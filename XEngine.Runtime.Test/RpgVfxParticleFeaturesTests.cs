// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.ParticleSystem;
using XEngine.Runtime.ParticleSystem.Modules;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// rpgvfx A2 — engine particle additions for the Hovl Studio port:
/// LimitVelocityOverLifetime (magnitude clamp / separate axes / drag), InheritVelocity
/// (emitter velocity folded into spawned particles), and the Mesh render mode (render mesh
/// selection + PARTICLE_MESH material keyword without the velocity row2 channel).
/// </summary>
public sealed class RpgVfxParticleFeaturesTests : RuntimeTestBase
{
    private static Particle SpawnParticle(float speed = 0f)
    {
        return new Particle
        {
            Position = Float3.Zero,
            Velocity = new Float3(0f, speed, 0f),
            Rotation = 0,
            RotationalSpeed = 0,
            StartSize = 1f,
            Size = 1f,
            StartColor = Color.White,
            Color = Color.White,
            StartLifetime = 1f,
            Lifetime = 1f,
            RandomSeed = 7,
            UVFrame = 0,
            TotalTime = 0f,
        };
    }

    [Fact]
    public void LimitVelocity_Magnitude_ClampsSpeedTowardLimit()
    {
        var module = new LimitVelocityOverLifetimeModule
        {
            Enabled = true,
            LimitMagnitude = new MinMaxCurve(2f),
            Dampen = 1f,
        };

        var particle = SpawnParticle(speed: 10f);
        module.OnParticleUpdate(ref particle, 1f / 60f);

        float afterOne = Float3.Length(particle.Velocity);
        Assert.True(afterOne < 10f, $"dampen must reduce excess speed, got {afterOne}");
        Assert.True(afterOne > 2f, "one frame should not fully reach the limit (exponential decay)");

        // Converges to the limit over time.
        for (int i = 0; i < 600; i++)
            module.OnParticleUpdate(ref particle, 1f / 60f);
        float settled = Float3.Length(particle.Velocity);
        Assert.InRange(settled, 1.9f, 2.1f);
    }

    [Fact]
    public void LimitVelocity_SeparateAxes_ClampsEachComponent()
    {
        var module = new LimitVelocityOverLifetimeModule
        {
            Enabled = true,
            SeparateAxes = true,
            LimitX = new MinMaxCurve(0.5f),
            LimitY = new MinMaxCurve(3f),
            LimitZ = new MinMaxCurve(0.5f),
            Dampen = 1f,
        };

        var particle = SpawnParticle();
        particle.Velocity = new Float3(5f, 5f, 5f);
        for (int i = 0; i < 600; i++)
            module.OnParticleUpdate(ref particle, 1f / 60f);

        Assert.InRange(particle.Velocity.X, -0.6f, 0.6f);
        Assert.InRange(particle.Velocity.Y, 2.9f, 3.1f); // Y limit is 3 — untouched
        Assert.InRange(particle.Velocity.Z, -0.6f, 0.6f);
    }

    [Fact]
    public void LimitVelocity_Drag_DecaysVelocityEvenBelowLimit()
    {
        var module = new LimitVelocityOverLifetimeModule
        {
            Enabled = true,
            LimitMagnitude = new MinMaxCurve(100f),
            Drag = 2f,
        };

        var particle = SpawnParticle(speed: 1f);
        for (int i = 0; i < 30; i++) // half a second at drag 2 → ~e^-1
            module.OnParticleUpdate(ref particle, 1f / 60f);
        Assert.InRange(Float3.Length(particle.Velocity), 0.3f, 0.45f);
    }

    [Fact]
    public void InheritVelocity_FoldsEmitterVelocityIntoSpawnedParticles()
    {
        var module = new InheritVelocityModule
        {
            Enabled = true,
            Curve = new MinMaxCurve(0.5f),
            EmitterVelocity = new Float3(0f, 10f, 0f),
        };

        var particle = SpawnParticle(); // starts at rest
        module.OnParticleSpawn(ref particle, new System.Random(1));
        Assert.Equal(5f, particle.Velocity.Y, 3);

        // Disabled module must not touch the velocity.
        module.Enabled = false;
        var untouched = SpawnParticle();
        module.OnParticleSpawn(ref untouched, new System.Random(1));
        Assert.Equal(0f, untouched.Velocity.Y, 5);
    }

    [Fact]
    public void MeshRenderMode_UsesRenderMesh_AndSetsKeyword()
    {
        var go = new GameObject("MeshPs");
        var system = go.AddComponent<ParticleSystemComponent>();
        system.OnEnable(); // test harness has no player loop — create the quad mesh explicitly
        system.MaxParticles = 8;
        system.Looping = true;
        system.PlayOnEnable = false;
        system.Initial.StartLifetime = new MinMaxCurve(2f);
        system.Material = new AssetRef<Material>(new Material(
            Shader.LoadDefault(DefaultShader.Particle)));
        system.RenderMode = ParticleRenderMode.Mesh;
        system.RenderMesh = new AssetRef<Mesh>(Mesh.CreateCube(new Float3(0.2f, 0.2f, 0.2f)));
        system.Play();

        var cameraGo = new GameObject("Cam");
        var camera = cameraGo.AddComponent<XEngine.Runtime.Camera>();
        var renderables = new List<IRenderable>();
        var lights = new List<IRenderableLight>();

        try
        {
            Time.TimeStack.Push(new TimeData { DeltaTime = 1f / 60f });
            try
            {
                system.Emit(3);
                system.Update();
                system.OnRenderCollect(camera, renderables, lights);
            }
            finally
            {
                Time.TimeStack.Clear();
            }

            Assert.True(renderables.Count > 0,
                $"renderables={renderables.Count} particles={system.ParticleCount}");
            var material = renderables[0].GetMaterial();
            // Keyword is on and sticky; the emitted instance data must not carry the
            // stretched-billboard channel (row2 stays a plain Z-rotation row, w=0).
            Assert.True(IsKeywordSet(material, "PARTICLE_MESH"));
            Assert.False(IsKeywordSet(system.Material.Res!, "PARTICLE_MESH"));

            // Billboard mode keeps using the shared source material, without changing
            // the material already captured by the mesh draw earlier in this frame.
            renderables.Clear();
            system.RenderMode = ParticleRenderMode.Billboard;
            system.OnRenderCollect(camera, renderables, lights);
            Assert.False(IsKeywordSet(renderables[0].GetMaterial(), "PARTICLE_MESH"));
            Assert.True(IsKeywordSet(material, "PARTICLE_MESH"));
        }
        finally
        {
            cameraGo.Dispose();
            go.Dispose();
        }
    }

    [Fact]
    public void ConeShape_DirectionsStayWithinSpread_AboveHorizon()
    {
        // rpgvfx A2 regression: apex-born particles must not spray sideways (old code
        // normalized the spawn position, giving near-horizontal directions at h≈0).
        var emission = new EmissionModule
        {
            Enabled = true,
            Shape = EmissionShape.Cone,
            Radius = 0.4f, // small radius: the old bug dominates here
            ConeAngle = 16f,
        };
        var random = new System.Random(5);
        for (int i = 0; i < 2000; i++)
        {
            emission.GetShapePositionAndDirection(random, out Float3 position, out Float3 direction);
            float lateral = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
            Assert.True(direction.Y > 0f, $"direction must stay above horizon, got Y={direction.Y}");
            Assert.True(lateral <= MathF.Tan(16f * MathF.PI / 180f) + 0.01f,
                $"lateral {lateral} exceeds cone spread {MathF.Tan(16f * MathF.PI / 180f)}");
            Assert.InRange(position.Y, 0f, 0.4f);
        }
    }

    [Fact]
    public void ParticleTrails_RecordPoints_AgeOut_AndRender()
    {
        var trails = new TrailsModule
        {
            Enabled = true,
            Ratio = 1f,
            Lifetime = new MinMaxCurve(0.5f),
            WidthOverTrail = new MinMaxCurve(1f),
        };
        var random = new System.Random(3);

        // 30 frames of straight-line motion → a trail of points behind the head.
        for (int i = 0; i < 30; i++)
        {
            trails.Record(seed: 1, new Float3(i * 0.1f, 0f, 0f), size: 1f,
                new Float4(1f, 1f, 1f, 1f), trails.Lifetime);
            trails.Tick(1f / 60f);
        }
        int grown = CountPoints(trails, 1);
        Assert.True(grown >= 5, $"trail must accumulate points, got {grown}");

        // Aging: DieWithParticles=true drops the trail the moment the particle dies.
        trails.OnParticleDied(1);
        Assert.Equal(0, CountPoints(trails, 1));

        // DieWithParticles=false: points age out gradually instead of vanishing.
        var fade = new TrailsModule { Enabled = true, Ratio = 1f, Lifetime = new MinMaxCurve(0.5f), DieWithParticles = false };
        fade.Record(9, Float3.Zero, 1f, new Float4(1f, 1f, 1f, 1f), fade.Lifetime);
        fade.OnParticleDied(9);
        Assert.Equal(1, CountPoints(fade, 9));
        for (int i = 0; i < 40; i++)
            fade.Tick(1f / 60f); // 0.66s > 0.5s lifetime
        Assert.Equal(0, CountPoints(fade, 9));
    }

    private static int CountPoints(TrailsModule module, uint seed)
    {
        // TrailsModule's runtime types are internal — count via reflection (test-only).
        var field = typeof(TrailsModule).GetField("_trails",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var dict = (System.Collections.IDictionary)field!.GetValue(module)!;
        if (!dict.Contains(seed))
            return 0;
        var trail = dict[seed]!;
        var points = (System.Collections.ICollection)trail.GetType()
            .GetField("Points")!.GetValue(trail)!;
        return points.Count;
    }

    [Theory]
    [InlineData(ParticleRenderMode.Billboard)]
    [InlineData(ParticleRenderMode.None)]
    public void TrailOnlySystem_RendersOwnMaterialWithoutParticleMaterialOrLivingParticles(ParticleRenderMode mode)
    {
        using var go = new GameObject("TrailOnly");
        using var cameraGo = new GameObject("Camera");
        using var material = new Material(Shader.LoadDefault(DefaultShader.Particle));
        var system = go.AddComponent<ParticleSystemComponent>();
        system.RenderMode = mode;
        system.Trails.Enabled = true;
        system.Trails.TrailMaterial = new AssetRef<Material>(material);
        system.Trails.Record(7, Float3.Zero, 1f, new Float4(1f), system.Trails.Lifetime);
        system.Trails.Record(7, Float3.UnitX, 1f, new Float4(1f), system.Trails.Lifetime);
        cameraGo.Transform.Position = new Float3(0f, 0f, -5f);
        var renderables = new List<IRenderable>();
        var lights = new List<IRenderableLight>();

        system.OnRenderCollect(cameraGo.AddComponent<Camera>(), renderables, lights);

        Assert.Single(renderables);
        Assert.IsType<MeshRenderable>(renderables[0]);
    }

    [Fact]
    public void SubEmitters_BirthTrigger_SpawnsInTarget_AtParentPosition()
    {
        var rootGo = new GameObject("FxRoot");
        var parentGo = new GameObject("Core");
        var targetGo = new GameObject("Sparks");
        parentGo.SetParent(rootGo, false);
        targetGo.SetParent(rootGo, false);

        var parent = parentGo.AddComponent<ParticleSystemComponent>();
        parent.OnEnable();
        var target = targetGo.AddComponent<ParticleSystemComponent>();
        target.OnEnable();
        target.MaxParticles = 50;
        target.Initial.StartSpeed = new MinMaxCurve(2f);
        target.Initial.StartLifetime = new MinMaxCurve(1f);
        target.PlayOnEnable = false;

        parent.SubEmitters.Enabled = true;
        parent.SubEmitters.Emitters.Add(new SubEmitterSetting
        {
            TargetPath = "Sparks",
            Trigger = SubEmitterTrigger.Birth,
            Probability = 1f,
            InheritVelocityDirection = true,
        });

        try
        {
            // Deterministic parent: no shape (default +Y direction), speed 5 → the sub
            // particle inherits the +Y direction at the target's own speed 2.
            parent.Emission.Enabled = false;
            parentGo.Transform.Position = new Float3(5f, 0f, 0f);
            parent.Emit(1);

            Assert.Equal(1, target.ParticleCount);
            // The sub particle's velocity follows the parent's (+Y) at the target's speed 2.
            var field = typeof(ParticleSystemComponent).GetField("_particles",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var list = (List<Particle>)field!.GetValue(target)!;
            Assert.InRange(list[0].Velocity.Y, 1.9f, 2.1f);
            Assert.InRange(MathF.Abs(list[0].Velocity.X), 0f, 0.1f);
            // Spawned at the parent's world position (5,0,0); the target sits at the origin,
            // so its local space equals world space here.
            Assert.InRange(list[0].Position.X, 4.9f, 5.1f);
        }
        finally
        {
            targetGo.Dispose();
            parentGo.Dispose();
            rootGo.Dispose();
        }
    }

    private static bool IsKeywordSet(Material material, string keyword)
    {
        // Material._localKeywords is internal; reflect once per call (test-only path).
        var field = typeof(Material).GetField("_localKeywords",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var dict = (System.Collections.Generic.Dictionary<string, bool>)field!.GetValue(material)!;
        return dict.TryGetValue(keyword, out bool value) && value;
    }
}
