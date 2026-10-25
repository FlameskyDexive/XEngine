// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.ParticleSystem.Modules;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem;

/// <summary>
/// A GPU-instanced particle system component.
/// Particles are rendered using instanced rendering for optimal performance.
/// </summary>
[AddComponentMenu("Effects/Particle System")]
[ExecuteAlways]
[ComponentIcon("\ue2ca")] // WandMagicSparkles
public class ParticleSystemComponent : MonoBehaviour
{
    #region Configuration

    public AssetRef<Material> Material;
    public int MaxParticles = 1000;
    public float Duration = 5.0f;
    public bool Looping = true;
    public bool PlayOnEnable = true;
    public bool Prewarm = false;
    /// <summary>Nonzero seeds make Play/Simulate replays deterministic; zero uses automatic randomness.</summary>
    public uint RandomSeed;
    public SimulationSpace SimulationSpace = SimulationSpace.Local;

    // zonezero M9 additions:
    /// <summary>Seconds before the system starts emitting (Unity StartDelay).</summary>
    public float StartDelay = 0f;
    /// <summary>Billboard render mode: 0 = view-plane, 1 = velocity-stretched.</summary>
    public ParticleRenderMode RenderMode = ParticleRenderMode.Billboard;
    public ParticleRenderAlignment RenderAlignment = ParticleRenderAlignment.View;
    /// <summary>Meters pushed toward the camera for transparent sorting (Unity
    /// SortingFudge — bigger = drawn later/on top).</summary>
    public float SortingFudge = 0f;
    /// <summary>Velocity stretch factors (Unity stretched-billboard Velocity/LengthScale).</summary>
    public float VelocityScale = 1f;
    public float LengthScale = 1f;
    /// <summary>rpgvfx: Unity mesh render mode — instances draw this mesh instead of the quad
    /// (billboarding is replaced by the instance transform; material must set the
    /// PARTICLE_MESH keyword, applied automatically on render collect).</summary>
    public AssetRef<Mesh> RenderMesh;

    #endregion

    #region Modules

    public InitialModule Initial = new() { Enabled = true };
    public EmissionModule Emission = new() { Enabled = true };
    public SizeOverLifetimeModule SizeOverLifetime = new();
    public ColorOverLifetimeModule ColorOverLifetime = new();
    public RotationOverLifetimeModule RotationOverLifetime = new();
    public VelocityOverLifetimeModule VelocityOverLifetime = new();
    public ForceOverLifetimeModule ForceOverLifetime = new();
    public CollisionModule Collision = new();
    public UVModule UV = new();
    public LightModule Light = new();
    public SizeBySpeedModule SizeBySpeed = new();
    public NoiseModule Noise = new();
    // rpgvfx port additions:
    public LimitVelocityOverLifetimeModule LimitVelocity = new();
    public InheritVelocityModule InheritVelocity = new();
    public TrailsModule Trails = new();
    public SubEmitterModule SubEmitters = new();
    public ParticleCustomDataModule CustomData = new();

    #endregion

    #region State

    private List<Particle> _particles = new();
    private Random _random = new();
    private float _time = 0;
    private bool _isPlaying = false;
    private bool _emissionComplete;
    private float _delayRemaining = 0f;
    private SimulationSpace _prevSimulationSpace = SimulationSpace.Local;
    private PropertyState _properties = new();
    private Float3 _lastEmitterPosition;
    private Float3 _emitterStepStart;
    private Float3 _emitterWorldVelocity;

    // GPU instancing data - separate arrays for clean API
    private Mesh _quadMesh;
    private Float4x4[] _transforms = Array.Empty<Float4x4>();
    private Float4[] _colors = Array.Empty<Float4>();
    private Float4[] _customData = Array.Empty<Float4>();
    private AABB _bounds; // Computed bounds from particle positions + sizes
    private Material? _orientedMaterial;
    private Material? _orientedMaterialSource;
    private ulong _orientedMaterialState;
    private string? _orientedMaterialKeywords;

    // Stable light proxies, one per particle slot. Reusing the same proxy for the same slot
    // across frames means the BVH refits its leaf in place when the particle moves instead of
    // re-registering a fresh light every frame (which would dirty topology and force rebuilds).
    // Allocated lazily the first time the LightModule is enabled.
    private ParticleLightProxy[] _lightProxies = Array.Empty<ParticleLightProxy>();

    #endregion

    #region Lifecycle

    public override void OnEnable()
    {
        base.OnEnable();

        // Create quad mesh for particle rendering if not already created
        if (_quadMesh == null)
        {
            CreateQuadMesh();
        }

        if (PlayOnEnable && !_isPlaying)
        {
            Play();
        }
    }

    public override void Update() => Simulate(Time.DeltaTime);

    /// <summary>Advances the same simulation used by Update, without depending on wall-clock time.</summary>
    public void Simulate(float deltaTime)
    {
        // Migrate particles when simulation space changes
        if (_prevSimulationSpace != SimulationSpace)
        {
            MigrateParticleSpace(_prevSimulationSpace, SimulationSpace);
            _prevSimulationSpace = SimulationSpace;
        }

        if (!_isPlaying || deltaTime <= 0f || !float.IsFinite(deltaTime))
            return;

        // rpgvfx: track the emitter's world velocity (transform delta per frame) so the
        // InheritVelocity module can hand it to freshly spawned particles. Runs before any
        // early-out so the velocity is valid on the first frame after Play().
        if (deltaTime > 0f)
        {
            Float3 position = (Float3)Transform.Position;
            InheritVelocity.EmitterVelocity = (position - _lastEmitterPosition) / deltaTime;
            _emitterStepStart = _lastEmitterPosition;
            _emitterWorldVelocity = InheritVelocity.EmitterVelocity;
            _lastEmitterPosition = position;
        }

        if (_delayRemaining > 0f)
        {
            float delayStep = MathF.Min(_delayRemaining, deltaTime);
            _delayRemaining -= delayStep;
            deltaTime -= delayStep;
            _emitterStepStart += _emitterWorldVelocity * delayStep;
        }

        while (deltaTime > 0f)
        {
            bool emitting = !_emissionComplete && Duration > 0f;
            float step = emitting ? MathF.Min(deltaTime, Duration - _time) : deltaTime;
            if (step > 0f)
            {
                if (emitting)
                    _time += step;
                SimulateStep(step, emitting);
                deltaTime -= step;
            }

            if (emitting && _time >= Duration)
            {
                if (Looping)
                {
                    _time = 0f;
                    Emission.RestartCycle();
                }
                else
                    _emissionComplete = true;
            }
            else if (!emitting)
                _emissionComplete = true;
        }

        if (_emissionComplete && _particles.Count == 0 && !Trails.HasLiveTrails)
            _isPlaying = false;
    }

    private void SimulateStep(float deltaTime, bool emitting)
    {
        if (VelocityOverLifetime.Enabled)
            VelocityOverLifetime.ConfigureSpace(Transform.LocalToWorldMatrix, Transform.WorldToLocalMatrix, SimulationSpace);
        if (ForceOverLifetime.Enabled)
            ForceOverLifetime.ConfigureSpace(Transform.Rotation, SimulationSpace);
        Noise.AdvanceTime(deltaTime, Duration > 0f ? _time / Duration : 0f, RandomSeed);
        // Emit new particles
        int emitCount = emitting ? Emission.CalculateEmitCount(deltaTime, _time, _random, Duration) : 0;
        for (int i = 0; i < emitCount && _particles.Count < MaxParticles; i++)
        {
            EmitParticle();
        }
        Float3 emitterStepEnd = _emitterStepStart + _emitterWorldVelocity * deltaTime;
        if (emitting)
        {
            int distanceCount = Emission.CalculateDistanceEmitCount(
                Float3.Length(emitterStepEnd - _emitterStepStart), _time / Duration, _random,
                out float firstFraction, out float fractionStep);
            for (int i = 0; i < distanceCount && _particles.Count < MaxParticles; i++)
            {
                Float3 emissionPosition = _emitterStepStart + (emitterStepEnd - _emitterStepStart) *
                    Math.Clamp(firstFraction + i * fractionStep, 0f, 1f);
                EmitParticle(emissionPosition);
            }
        }
        _emitterStepStart = emitterStepEnd;

        // Update existing particles
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var particle = _particles[i];

            // Update lifetime
            particle.Lifetime -= deltaTime;
            particle.TotalTime += deltaTime;

            if (particle.Lifetime <= 0)
            {
                // rpgvfx: death hooks — sub-emitter Death trigger + trail retirement.
                if (SubEmitters.Enabled && SubEmitters.HasTriggers(SubEmitterTrigger.Death))
                {
                    ToWorldSpace(particle.Position, particle.Velocity,
                        out Float3 deathPos, out Float3 deathVel);
                    SubEmitters.Trigger(SubEmitterTrigger.Death, this, deathPos, deathVel,
                        particle.Color, particle.Size, 0f, _random);
                }
                if (Trails.Enabled)
                    Trails.OnParticleDied(particle.RandomSeed);
                SubEmitters.OnParticleDied(particle.RandomSeed);

                _particles.RemoveAt(i);
                continue;
            }

            // Update particle through modules
            Noise.RemoveLastContribution(ref particle);
            Initial.OnParticleUpdate(ref particle, deltaTime);
            SizeOverLifetime.OnParticleUpdate(ref particle, deltaTime);
            SizeBySpeed.OnParticleUpdate(ref particle, deltaTime);
            ColorOverLifetime.OnParticleUpdate(ref particle, deltaTime);
            RotationOverLifetime.OnParticleUpdate(ref particle, deltaTime);
            ForceOverLifetime.OnParticleUpdate(ref particle, deltaTime);
            VelocityOverLifetime.OnParticleUpdate(ref particle, deltaTime);
            Noise.OnParticleUpdate(ref particle, deltaTime);
            LimitVelocity.OnParticleUpdate(ref particle, deltaTime);
            UV.OnParticleUpdate(ref particle, deltaTime);
            CustomData.OnParticleUpdate(ref particle, deltaTime);

            // Update position
            particle.Position += particle.Velocity * deltaTime;
            if (SubEmitters.Enabled)
            {
                ToWorldSpace(particle.Position, particle.Velocity, out Float3 parentPosition, out Float3 parentVelocity);
                SubEmitters.UpdateParent(particle.RandomSeed, parentPosition, parentVelocity,
                    particle.Color, particle.Size, particle.Lifetime);
            }

            // rpgvfx: trail recording happens after integration (world-space sample).
            if (Trails.Enabled)
            {
                ToWorldSpace(particle.Position, particle.Velocity,
                    out Float3 trailPos, out _);
                Trails.Record(particle.RandomSeed, trailPos, particle.Size,
                    particle.Color, Trails.Lifetime);
            }

            _particles[i] = particle;
        }

        // rpgvfx: age trail points / retire dead trails.
        if (Trails.Enabled)
            Trails.Tick(deltaTime);
        SubEmitters.TickAttachedEmitters(deltaTime, _random);

        // Update collisions (bulk operation for efficiency)
        if (Collision.Enabled)
        {
            Collision.UpdateCollisions(_particles, GameObject.Scene?.Physics, deltaTime, Transform, SimulationSpace);

            // rpgvfx: sub-emitter Collision trigger, driven by the collision sink.
            if (SubEmitters.Enabled && SubEmitters.HasTriggers(SubEmitterTrigger.Collision))
            {
                IReadOnlyList<uint> seeds = Collision.CollidedSeeds;
                for (int s = 0; s < seeds.Count; s++)
                {
                    for (int i = _particles.Count - 1; i >= 0; i--)
                    {
                        if (_particles[i].RandomSeed != seeds[s])
                            continue;
                        Particle hitParticle = _particles[i];
                        ToWorldSpace(hitParticle.Position, hitParticle.Velocity,
                            out Float3 hitPos, out Float3 hitVel);
                        SubEmitters.Trigger(SubEmitterTrigger.Collision, this, hitPos, hitVel,
                            hitParticle.Color, hitParticle.Size, hitParticle.Lifetime, _random);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Converts a particle's stored (simulation-space) position/velocity to world.</summary>
    private void ToWorldSpace(Float3 position, Float3 velocity, out Float3 worldPosition, out Float3 worldVelocity)
    {
        if (SimulationSpace == SimulationSpace.Local)
        {
            var p = Transform.LocalToWorldMatrix * new Float4(position, 1.0f);
            var v = Transform.LocalToWorldMatrix * new Float4(velocity, 0.0f);
            worldPosition = new Float3(p.X, p.Y, p.Z);
            worldVelocity = new Float3(v.X, v.Y, v.Z);
        }
        else
        {
            worldPosition = position;
            worldVelocity = velocity;
        }
    }

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        // Trails own an independent material slot and remain renderable without a
        // particle material, renderer, or living parent particles.
        if (Trails.Enabled)
            Trails.SubmitRenderables(camera, renderables, Material.Res, GameObject.LayerIndex);
        if (Light.Enabled)
            CollectParticleLights(lights);
        var material = Material.Res;
        if (material == null || _quadMesh == null || _particles.Count <= 0 || RenderMode == ParticleRenderMode.None)
            return;
        // A particle texture carries its shape/alpha, not just its surface color. White
        // fallback during async streaming turns an entire billboard into a visible square.
        if (!material.PrepareRenderAssets())
            return;

        // rpgvfx: mesh render mode swaps the quad for the authored mesh and flips the
        // material's PARTICLE_MESH keyword so the vertex stage skips billboarding. SetKeyword
        // early-outs when unchanged, so this is a dictionary read per frame in steady state.
        Mesh renderMesh = _quadMesh;
        bool meshMode = RenderMode == ParticleRenderMode.Mesh;
        if (meshMode && RenderMesh.Res == null) return; // Never replace a streaming mesh with a quad.
        if (meshMode) renderMesh = RenderMesh.Res!;
        bool orientedQuad = RenderMode == ParticleRenderMode.Billboard &&
            (RenderAlignment == ParticleRenderAlignment.Local || RenderAlignment == ParticleRenderAlignment.World);
        Material renderMaterial = meshMode || orientedQuad ? GetOrientedMaterial(material) : material;

        // Update instance data from particles
        UpdateInstanceData();

        // Setup per-object properties
        _properties.Clear();
        _properties.SetInt("_ObjectID", InstanceID);

        // Create batched instanced renderables. SortingFudge (M9): bias the sort position
        // toward the camera so overlapping transparents draw in a stable, authored order.
        Float3 toCamera = camera.Transform.Position - Transform.Position;
        float toCameraLength = Float3.Length(toCamera);
        Float3 sortPosition = Transform.Position;
        if (SortingFudge != 0f && toCameraLength > 0.001f)
            sortPosition += toCamera * (SortingFudge / toCameraLength);
        InstancedMeshRenderable.CreateBatched(
            renderables,
            renderMesh,
            renderMaterial,
            _transforms,
            sortPosition,
            _colors,
            _customData,
            GameObject.LayerIndex,
            _properties,
            _bounds
        );

    }

    private Material GetOrientedMaterial(Material source)
    {
        ulong state = source.GetStateHash();
        string keywords = source.KeywordKey;
        if (_orientedMaterial == null || !ReferenceEquals(_orientedMaterialSource, source) ||
            !ReferenceEquals(_orientedMaterial.Shader, source.Shader) ||
            _orientedMaterialState != state || _orientedMaterialKeywords != keywords)
        {
            _orientedMaterial?.Dispose();
            _orientedMaterial = source.Clone();
            _orientedMaterial.SetKeyword("PARTICLE_MESH", true);
            _orientedMaterialSource = source;
            _orientedMaterialState = state;
            _orientedMaterialKeywords = keywords;
        }
        return _orientedMaterial;
    }

    /// <summary>
    /// Push one IRenderableLight per alive particle into <paramref name="lights"/>. Proxies
    /// are pooled per particle index so the dynamic BVH refits the same leaf when a particle
    /// moves instead of seeing a brand-new light each frame.
    /// </summary>
    private void CollectParticleLights(List<IRenderableLight> lights)
    {
        if (_lightProxies.Length < _particles.Count)
        {
            int newSize = Math.Max(_lightProxies.Length * 2, _particles.Count);
            var grown = new ParticleLightProxy[newSize];
            Array.Copy(_lightProxies, grown, _lightProxies.Length);
            for (int i = _lightProxies.Length; i < newSize; i++)
                grown[i] = new ParticleLightProxy(this, i);
            _lightProxies = grown;
        }

        for (int i = 0; i < _particles.Count; i++)
        {
            if (!_particles[i].IsAlive) continue;
            lights.Add(_lightProxies[i]);
        }
    }

    /// <summary>Build the per-frame ForwardLightData for a particle at <paramref name="index"/>.
    /// Called by the proxy once per BVH-touched fragment, but the data itself is computed once
    /// per frame via the BVH's slot-cache (SlotMatches short-circuits identical updates).</summary>
    internal ForwardLightData GetParticleLightData(int index)
    {
        var p = _particles[index];

        // Position: bring local-space particles into world space so the BVH's world AABB is
        // valid. World-space particles are already there.
        Float3 worldPos = p.Position;
        if (SimulationSpace == SimulationSpace.Local)
        {
            var w = Transform.LocalToWorldMatrix * new Float4(worldPos, 1.0f);
            worldPos = new Float3(w.X, w.Y, w.Z);
        }

        Color rgb = Light.UseParticleColor ? (p.Color * Light.Color) : Light.Color;

        float intensity = Light.Intensity;
        if (Light.FadeWithLifetime)
            intensity *= 1.0f - p.NormalizedLifetime;

        float range = Light.Range;
        if (Light.ScaleRangeByParticleSize && p.StartSize > 0.0001f)
            range *= p.Size / p.StartSize;

        return new ForwardLightData
        {
            Type = LightType.Point,
            Position = worldPos,
            Direction = Float3.UnitY,
            Color = new Float3((float)rgb.R, (float)rgb.G, (float)rgb.B),
            Intensity = intensity,
            Range = range,
            ShadowEnabled = false,
            ShadowBias = 0f,
            ShadowNormalBias = 0f,
            ShadowStrength = 0f,
            ShadowQuality = 0f,
        };
    }

    /// <summary>Stable IRenderableLight wrapper around one particle slot. Identity is per-slot,
    /// not per-particle: when the slot recycles to a new particle the BVH sees an Update on the
    /// same proxy reference (cheap refit) rather than a remove + add (topology rebuild).</summary>
    private sealed class ParticleLightProxy : IRenderableLight
    {
        private readonly ParticleSystemComponent _system;
        private readonly int _index;

        public ParticleLightProxy(ParticleSystemComponent system, int index)
        {
            _system = system;
            _index = index;
        }

        // Mix in the slot index so collisions across systems / slots don't share an ID.
        public int GetLightID() => unchecked(_system.InstanceID * 397) ^ _index;
        public int GetLayer() => _system.GameObject.LayerIndex;
        public LightType GetLightType() => LightType.Point;
        public Float3 GetLightPosition() => _system.GetParticleLightData(_index).Position;
        public Float3 GetLightDirection() => Float3.UnitY;
        public bool DoCastShadows() => false;
        public ForwardLightData GetForwardLightData() => _system.GetParticleLightData(_index);
    }

    public override void OnDisable()
    {
        base.OnDisable();
        Stop();

        // Clean up resources
        _quadMesh?.Dispose();
        _quadMesh = null;
        _orientedMaterial?.Dispose();
        _orientedMaterial = null;
        _orientedMaterialSource = null;
        // rpgvfx: trail state + GPU mesh.
        Trails.Clear();
        Trails.DisposeMesh();
        SubEmitters.ClearCache();
    }

    public override void DrawGizmos()
    {
        var icon = Texture2D.LoadDefault(DefaultTexture.IconParticleSystem);
        if (icon != null) Debug.DrawIcon(icon, Transform.Position, 0.5f, Color.White);
    }

    #endregion

    #region Particle System Control

    /// <summary>zonezero M9 — Unity Emit(count): spawns count particles immediately
    /// (bypassing rate/burst scheduling), respecting MaxParticles.</summary>
    public void Emit(int count)
    {
        for (int i = 0; i < count && _particles.Count < MaxParticles; i++)
            EmitParticle();
    }

    public void Play()
    {
        _isPlaying = true;
        _emissionComplete = false;
        _time = 0;
        _delayRemaining = MathF.Max(0f, StartDelay);
        _lastEmitterPosition = (Float3)Transform.Position;
        _emitterStepStart = _lastEmitterPosition;
        _emitterWorldVelocity = Float3.Zero;
        _prevSimulationSpace = SimulationSpace;
        if (RandomSeed != 0)
            _random = new Random(unchecked((int)RandomSeed));
        Emission.Reset();
        Noise.Reset();

        if (Prewarm && Looping)
        {
            PrewarmSystem();
        }
    }

    public void Stop()
    {
        _isPlaying = false;
        _time = 0;
        Clear();
    }

    public void Pause()
    {
        _isPlaying = false;
    }

    public void Clear()
    {
        _particles.Clear();
        Noise.Reset();
        Trails.Clear();
        SubEmitters.ClearCache();
    }

    private void MigrateParticleSpace(SimulationSpace from, SimulationSpace to)
    {
        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];

            if (from == SimulationSpace.Local && to == SimulationSpace.World)
            {
                p.Position = (Float3)Transform.Position + (Transform.Rotation * p.Position);
                p.Velocity = Transform.Rotation * p.Velocity;
            }
            else if (from == SimulationSpace.World && to == SimulationSpace.Local)
            {
                var invRot = Quaternion.Inverse(Transform.Rotation);
                p.Position = invRot * (p.Position - (Float3)Transform.Position);
                p.Velocity = invRot * p.Velocity;
            }

            _particles[i] = p;
        }
    }

    public bool IsPlaying => _isPlaying;
    public int ParticleCount => _particles.Count;

    #endregion

    #region Particle Emission

    private void EmitParticle(Float3? emitterPosition = null)
    {
        // Get spawn position and direction from emission shape
        Float3 spawnPosition = Float3.Zero;
        Float3 spawnDirection = new Float3(0, 1, 0);

        if (Emission.Enabled)
        {
            Emission.GetShapePositionAndDirection(_random, out spawnPosition, out spawnDirection);
        }

        var particle = new Particle
        {
            Position = spawnPosition,
            Velocity = spawnDirection * 1.0f, // Default velocity, will be modified by modules
            Rotation = 0,
            RotationalSpeed = 0,
            StartSize = 1,
            Size = 1,
            StartColor = Color.White,
            Color = Color.White,
            StartLifetime = 1,
            Lifetime = 1,
            RandomSeed = (uint)_random.Next(),
            UVFrame = 0,
            TotalTime = 0
        };

        // Initialize through modules
        Initial.OnParticleSpawn(ref particle, _random);
        InheritVelocity.OnParticleSpawn(ref particle, _random);
        RotationOverLifetime.OnParticleSpawn(ref particle, _random);
        UV.OnParticleSpawn(ref particle, _random);
        CustomData.OnParticleSpawn(ref particle, _random);

        // Transform to world space if needed
        if (SimulationSpace == SimulationSpace.World)
        {
            // Apply the GameObject's rotation to position offset and velocity direction
            var rotation = Transform.Rotation;
            Float4 worldPosition = Transform.LocalToWorldMatrix * new Float4(particle.Position, 1f);
            particle.Position = new Float3(worldPosition.X, worldPosition.Y, worldPosition.Z);
            particle.Velocity = rotation * particle.Velocity;
            particle.SpawnRotation = rotation;
        }
        if (emitterPosition.HasValue)
        {
            Float3 displacement = emitterPosition.Value - (Float3)Transform.Position;
            if (SimulationSpace == SimulationSpace.Local)
            {
                Float4 local = Transform.WorldToLocalMatrix * new Float4(displacement, 0f);
                displacement = new Float3(local.X, local.Y, local.Z);
            }
            particle.Position += displacement;
        }

        // rpgvfx: birth hooks — sub-emitter Birth trigger.
        if (SubEmitters.Enabled && SubEmitters.HasTriggers(SubEmitterTrigger.Birth))
        {
            ToWorldSpace(particle.Position, particle.Velocity,
                out Float3 birthPos, out Float3 birthVel);
            SubEmitters.Trigger(SubEmitterTrigger.Birth, this, birthPos, birthVel,
                particle.Color, particle.Size, particle.Lifetime, _random, particle.RandomSeed);
        }

        _particles.Add(particle);
    }

    /// <summary>
    /// rpgvfx: spawns one particle for a sub-emitter event at an explicit world position.
    /// The target's Initial module provides its own lifetime/speed/size/color baseline; the
    /// caller's inheritance flags override velocity direction, size, color and lifetime.
    /// </summary>
    internal void SpawnSubParticle(Float3 worldPosition, Float3 velocity, float sizeScale,
        Float4 color, bool overrideColor, float lifetimeOverride, Random random)
    {
        if (_particles.Count >= MaxParticles)
            return;

        var particle = new Particle
        {
            Position = Float3.Zero,
            Velocity = velocity,
            Rotation = 0,
            RotationalSpeed = 0,
            StartSize = 1,
            Size = 1,
            StartColor = Color.White,
            Color = Color.White,
            StartLifetime = 1,
            Lifetime = 1,
            RandomSeed = (uint)random.Next(),
            UVFrame = 0,
            TotalTime = 0
        };

        Initial.OnParticleSpawn(ref particle, random);
        RotationOverLifetime.OnParticleSpawn(ref particle, random);
        UV.OnParticleSpawn(ref particle, random);
        CustomData.OnParticleSpawn(ref particle, random);

        particle.Velocity = velocity;
        if (lifetimeOverride > 0f)
        {
            particle.StartLifetime = lifetimeOverride;
            particle.Lifetime = lifetimeOverride;
        }
        if (sizeScale != 1f)
        {
            particle.StartSize *= sizeScale;
            particle.Size *= sizeScale;
            particle.StartSize3D *= sizeScale;
            particle.Size3D *= sizeScale;
        }
        if (overrideColor)
        {
            particle.StartColor = color;
            particle.Color = color;
        }

        if (SimulationSpace == SimulationSpace.Local)
        {
            var local = Transform.WorldToLocalMatrix * new Float4(worldPosition, 1.0f);
            particle.Position = new Float3(local.X, local.Y, local.Z);
            var localVel = Transform.WorldToLocalMatrix * new Float4(velocity, 0.0f);
            particle.Velocity = new Float3(localVel.X, localVel.Y, localVel.Z);
        }
        else
        {
            particle.Position = worldPosition;
            particle.SpawnRotation = Transform.Rotation;
        }

        if (!_isPlaying)
        {
            _isPlaying = true;
            _emissionComplete = true;
            _delayRemaining = 0f;
        }
        _particles.Add(particle);
    }

    private Float3 RandomDirection()
    {
        // Random direction on unit sphere
        float theta = (float)(_random.NextDouble() * Maths.PI * 2);
        float phi = (float)(Maths.Acos(2.0 * _random.NextDouble() - 1.0));

        return new Float3(
            (float)(Maths.Sin(phi) * Maths.Cos(theta)),
            (float)(Maths.Sin(phi) * Maths.Sin(theta)),
            (float)Maths.Cos(phi)
        );
    }

    #endregion

    #region Prewarm

    private void PrewarmSystem()
    {
        if (!Looping || Duration <= 0)
            return;

        // Prewarm uses exactly the live module order, trails and death hooks.
        float delay = _delayRemaining;
        _delayRemaining = 0f;
        float remaining = Duration;
        while (remaining > 0f)
        {
            float step = MathF.Min(0.02f, remaining);
            Simulate(step);
            remaining -= step;
        }
        _delayRemaining = delay;
    }

    #endregion


    #region GPU Instancing

    private void CreateQuadMesh()
    {
        // Create a simple quad mesh for particle rendering
        // Vertices are in local space, centered at origin
        Float3[] vertices = new Float3[]
        {
            new Float3(-0.5f, -0.5f, 0),
            new Float3( 0.5f, -0.5f, 0),
            new Float3( 0.5f,  0.5f, 0),
            new Float3(-0.5f,  0.5f, 0),
        };

        Float2[] uvs = new Float2[]
        {
            new Float2(0, 0),
            new Float2(1, 0),
            new Float2(1, 1),
            new Float2(0, 1),
        };

        uint[] indices = new uint[] { 0, 1, 2, 0, 2, 3 };

        _quadMesh = new Mesh();
        _quadMesh.Vertices = vertices;
        _quadMesh.UV = uvs;
        _quadMesh.Indices = indices;
        _quadMesh.RecalculateBounds();
        _quadMesh.Upload();
    }

    private void UpdateInstanceData()
    {
        // Resize arrays if needed
        if (_transforms.Length != _particles.Count)
        {
            _transforms = new Float4x4[_particles.Count];
            _colors = new Float4[_particles.Count];
            _customData = new Float4[_particles.Count];
        }

        // Initialize bounds tracking
        Float3 min = new Float3(float.MaxValue);
        Float3 max = new Float3(float.MinValue);

        Float4x4 emitterMatrix = Transform.LocalToWorldMatrix;
        Float3 emitterScale = Transform.LossyScale;
        bool meshMode = RenderMode == ParticleRenderMode.Mesh;
        bool orientedQuad = RenderMode == ParticleRenderMode.Billboard &&
            (RenderAlignment == ParticleRenderAlignment.Local || RenderAlignment == ParticleRenderAlignment.World);

        // Fill separate arrays from particles
        for (int i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];

            // Create transform matrix for this particle
            Float3 position = particle.Position;
            float rotation = particle.Rotation;
            Float3 particleSize = particle.Size3D;
            // Also supports particles supplied by older callers that only set scalar size.
            if (particle.StartSize3D == Float3.Zero)
                particleSize = new Float3(particle.Size);
            Float3 scaledSize = particleSize * emitterScale;

            // Transform to world space if in local simulation space
            if (SimulationSpace == SimulationSpace.Local)
            {
                var worldPos = Transform.LocalToWorldMatrix * new Float4(position, 1.0f);
                position = new Float3(worldPos.X, worldPos.Y, worldPos.Z);
            }

            // Update bounds - expand by particle size (radius = size * 0.5)
            float radius = Float3.Length(scaledSize) * 0.5f;
            if (meshMode && RenderMesh.Res != null)
                radius *= MathF.Max(1f, Float3.Length(RenderMesh.Res.bounds.Size));
            Float3 particleMin = new Float3(position.X - radius, position.Y - radius, position.Z - radius);
            Float3 particleMax = new Float3(position.X + radius, position.Y + radius, position.Z + radius);
            Float3 stretchVelocity = Float3.Zero;
            float stretchLength = 0f;
            if (RenderMode == ParticleRenderMode.StretchedBillboard)
            {
                ToWorldSpace(particle.Position, particle.Velocity, out _, out stretchVelocity);
                float speed = Float3.Length(stretchVelocity);
                stretchLength = MathF.Max(0f, scaledSize.X * LengthScale + speed * VelocityScale);
                if (speed > 0.0001f)
                {
                    Float3 tail = position - stretchVelocity * (stretchLength / speed);
                    particleMin = new Float3(Maths.Min(particleMin.X, tail.X - radius),
                        Maths.Min(particleMin.Y, tail.Y - radius), Maths.Min(particleMin.Z, tail.Z - radius));
                    particleMax = new Float3(Maths.Max(particleMax.X, tail.X + radius),
                        Maths.Max(particleMax.Y, tail.Y + radius), Maths.Max(particleMax.Z, tail.Z + radius));
                }
            }
            min = new Float3(
                Maths.Min(min.X, particleMin.X),
                Maths.Min(min.Y, particleMin.Y),
                Maths.Min(min.Z, particleMin.Z)
            );
            max = new Float3(
                Maths.Max(max.X, particleMax.X),
                Maths.Max(max.Y, particleMax.Y),
                Maths.Max(max.Z, particleMax.Z)
            );

            // Particle angular values follow Unity's radians contract; the engine's
            // general-purpose FromEuler API accepts degrees.
            Quaternion rotation3D = Quaternion.FromEuler(particle.Rotation3D.X * Maths.Rad2Deg,
                particle.Rotation3D.Y * Maths.Rad2Deg, rotation * Maths.Rad2Deg);
            if (meshMode || orientedQuad)
            {
                bool localOrientation = meshMode || RenderAlignment == ParticleRenderAlignment.Local;
                Quaternion worldOrientation = meshMode ? particle.SpawnRotation
                    : localOrientation ? Transform.Rotation : Quaternion.Identity;
                _transforms[i] = SimulationSpace == SimulationSpace.Local && localOrientation
                    ? emitterMatrix * Float4x4.CreateTRS(particle.Position, rotation3D, particleSize)
                    : Float4x4.CreateTRS(position, worldOrientation * rotation3D, scaledSize);
            }
            else
            {
                _transforms[i] = Float4x4.CreateTRS(position,
                    Quaternion.FromEuler(0f, 0f, rotation * Maths.Rad2Deg), scaledSize);
                // InstanceData's legacy ModelRow names carry COLUMNS. Writing a row
                // here overwrites position.Z and corrupts the right/up basis vectors.
                if (RenderMode == ParticleRenderMode.StretchedBillboard)
                {
                    // The baked Unity quad extends from UV.x=0 at the particle towards
                    // UV.x=1 at its tail. Carry the authored total length with the mode
                    // marker, without borrowing Custom1 or flipbook channels.
                    _transforms[i].c2 = new Float4(stretchVelocity, 1f + stretchLength);
                }
                else
                    _transforms[i].c2 = new Float4(particle.RandomSeed * (1f / int.MaxValue), 0f, 0f, 0f);
            }

            // The selected Custom1XY stream extends UV0.xy to UV0.xyzw. These two
            // unused column W values preserve the legacy stretch flag and flipbook
            // channels; particle shaders compute positions from column XYZ only.
            if (CustomData.Enabled && CustomData.Custom1ToUV0ZW)
            {
                _transforms[i].c0.W = particle.Custom1.X;
                _transforms[i].c1.W = particle.Custom1.Y;
            }

            // Get UV tile info if UV module is enabled
            Float4 uvTileInfo = UV.Enabled ? UV.GetUVTileInfo(particle) : new Float4(0, 0, 1, 1);
            // c3.xyz carries translation; particle shaders do not use c3.w for
            // positions, so retain the independent flipbook Y/X scale ratio here.
            _transforms[i].c3.W = uvTileInfo.W / uvTileInfo.Z;

            // Store color and custom data
            _colors[i] = particle.Color;

            // CustomData: X=normalized lifetime, Y=UV offsetX, Z=UV offsetY, W=UV scale
            _customData[i] = new Float4(particle.NormalizedLifetime, uvTileInfo.X, uvTileInfo.Y, uvTileInfo.Z);
        }

        // Store computed bounds
        _bounds = _particles.Count > 0 ? new AABB(min, max) : new AABB(Float3.Zero, Float3.Zero);
    }

    #endregion
}

public enum SimulationSpace
{
    Local,
    World
}

/// <summary>Billboard render mode (M9). Stretch aligns the quad with the velocity.
/// rpgvfx: Mesh renders the authored RenderMesh through the instance transform.</summary>
public enum ParticleRenderMode
{
    Billboard = 0,
    StretchedBillboard = 1,
    Mesh = 2,
    /// <summary>Unity ParticleSystemRenderMode.None — the system simulates but renders
    /// nothing (Hovl uses it for helper/logic emitters; 84 systems in the pack).</summary>
    None = 3,
}

public enum ParticleRenderAlignment
{
    View = 0,
    World = 1,
    Local = 2,
    Facing = 3,
    Velocity = 4,
}
