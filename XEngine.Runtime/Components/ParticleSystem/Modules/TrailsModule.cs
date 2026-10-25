// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Utils;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem;

/// <summary>Unity TrailsModule mode: per-particle ribbons (Hovl uses this) or one shared ribbon.</summary>
public enum ParticleTrailMode
{
    PerParticle = 0,
    Ribbon = 1,
}

/// <summary>
/// Unity TrailsModule (Shuriken serialized name TrailModule), per-particle mode: every
/// emitting particle drags a camera-facing ribbon (same mesh approach as TrailRenderer).
/// Points are recorded in WORLD space each frame; width comes from WidthOverTrail × the
/// particle size, color from the particle color × ColorOverLifetime sampled by point age.
/// Ribbon mode is not implemented and falls back to per-particle (converter warns).
/// </summary>
public sealed class TrailsModule : ParticleSystemModule
{
    public ParticleTrailMode Mode = ParticleTrailMode.PerParticle;
    /// <summary>Fraction of particles (0-1) that grow trails; chosen deterministically by seed.</summary>
    public float Ratio = 0.5f;
    /// <summary>Trail length in seconds for a particle (sampled once at first record).</summary>
    public MinMaxCurve Lifetime = new(1f);
    public bool DieWithParticles = true;
    public bool InheritParticleColor = true;
    /// <summary>Multiplies the recorded point color by age.</summary>
    public MinMaxGradient ColorOverLifetime = new();
    public MinMaxCurve WidthOverTrail = new(1f);
    public bool SizeAffectsWidth = true;
    /// <summary>True = Unity textureMode Tile (not implemented; stretch used, converter warns).</summary>
    public bool TextureTiles;
    /// <summary>Optional dedicated trail material; falls back to the system material.</summary>
    public AssetRef<Material>? TrailMaterial;

    // ---- runtime state (not serialized) ------------------------------------

    internal sealed class ParticleTrail
    {
        public uint Seed;
        public bool Alive;
        public float LifeSeconds;
        /// <summary>Head-first points; pooled — see <see cref="TrailsModule._pool"/>.</summary>
        public List<TrailPoint> Points = new();
    }

    internal struct TrailPoint
    {
        public Float3 Position;
        public Float4 Color;
        public float Size;
        public float Age;
    }

    private const float MinPointDistance = 0.02f;

    private readonly Dictionary<uint, ParticleTrail> _trails = new();
    private readonly List<ParticleTrail> _pool = new();
    private readonly List<uint> _retired = new();

    /// <summary>True when Ribbon mode fell back (converter/diagnostics flag).</summary>
    public bool RibbonFallback { get; private set; }
    internal bool HasLiveTrails => _trails.Count > 0;

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        // Recording happens in the component loop (needs transform + world position).
    }

    private bool HasTrail(uint seed) =>
        Ratio >= 1f || (seed % 100u) / 100f < Ratio;

    /// <summary>Records one particle sample (called from the component's update loop).</summary>
    public void Record(uint seed, Float3 worldPosition, float size, Float4 color, MinMaxCurve lifetime)
    {
        if (!HasTrail(seed))
            return;

        if (!_trails.TryGetValue(seed, out ParticleTrail? trail))
        {
            trail = RentTrail(seed);
            trail.LifeSeconds = MathF.Max(0.05f, lifetime.EvaluateInitial(null));
        }
        trail.Alive = true;

        List<TrailPoint> points = trail.Points;
        if (points.Count == 0 || Float3.Length(points[0].Position - worldPosition) >= MinPointDistance)
        {
            points.Insert(0, new TrailPoint
            {
                Position = worldPosition,
                Color = InheritParticleColor ? color : new Float4(1f, 1f, 1f, 1f),
                Size = SizeAffectsWidth ? size : 1f,
                Age = 0f,
            });
        }
    }

    /// <summary>Called when a particle dies (lifetime expiry or kill).</summary>
    public void OnParticleDied(uint seed)
    {
        if (!_trails.TryGetValue(seed, out ParticleTrail? trail)) return;
        trail.Alive = false;
        if (DieWithParticles)
        {
            _trails.Remove(seed);
            trail.Points.Clear();
            _pool.Add(trail);
        }
        // DieWithParticles=false: keep aging — the trail fades out via point age-out below.
        // The dead trail's points stop being refreshed and eventually retire in Prune.
    }

    /// <summary>Ages every trail, retires expired points and trails of dead particles.</summary>
    public void Tick(float deltaTime)
    {
        _retired.Clear();
        foreach (KeyValuePair<uint, ParticleTrail> kv in _trails)
        {
            ParticleTrail trail = kv.Value;
            List<TrailPoint> points = trail.Points;
            int alive = 0;
            for (int i = 0; i < points.Count; i++)
            {
                TrailPoint point = points[i];
                point.Age += deltaTime;
                // Retired trails keep the authored lifetime rather than a separate
                // hardcoded duration, then release their per-particle record.
                if (point.Age < trail.LifeSeconds)
                {
                    points[alive++] = point;
                }
            }
            if (alive != points.Count)
            {
                points.RemoveRange(alive, points.Count - alive);
            }
            if (!trail.Alive && points.Count == 0)
            {
                _retired.Add(kv.Key);
            }
        }
        for (int i = 0; i < _retired.Count; i++)
        {
            ParticleTrail trail = _trails[_retired[i]];
            _trails.Remove(_retired[i]);
            _pool.Add(trail);
        }
    }

    /// <summary>Revives trails whose particle survived into a new Play()/session.</summary>
    public void Clear()
    {
        foreach (KeyValuePair<uint, ParticleTrail> kv in _trails)
        {
            kv.Value.Points.Clear();
            _pool.Add(kv.Value);
        }
        _trails.Clear();
    }

    private ParticleTrail RentTrail(uint seed)
    {
        ParticleTrail trail;
        int last = _pool.Count - 1;
        if (last >= 0)
        {
            trail = _pool[last];
            _pool.RemoveAt(last);
        }
        else
        {
            trail = new ParticleTrail();
        }
        trail.Seed = seed;
        trail.Alive = true;
        _trails[seed] = trail;
        return trail;
    }

    // ---- rendering ---------------------------------------------------------

    private Mesh? _mesh;
    private Float3[] _vertices = Array.Empty<Float3>();
    private Color[] _colors = Array.Empty<Color>();
    private Float2[] _uvs = Array.Empty<Float2>();
    private uint[] _indices = Array.Empty<uint>();

    /// <summary>Builds the combined ribbon mesh for all live trails and submits one
    /// renderable. Called from the component's OnRenderCollect.</summary>
    public void SubmitRenderables(Camera camera, List<IRenderable> renderables, Material? systemMaterial, int layerIndex)
    {
        Material? material = TrailMaterialResolution(systemMaterial);
        if (material == null || _trails.Count == 0 || !material.PrepareRenderAssets())
            return;

        Float3 cameraPosition = camera.Transform.Position;

        int vertexCount = 0;
        int indexCount = 0;
        foreach (KeyValuePair<uint, ParticleTrail> kv in _trails)
        {
            List<TrailPoint> points = kv.Value.Points;
            if (points.Count < 2) continue;
            vertexCount += points.Count * 2;
            indexCount += (points.Count - 1) * 6;
        }
        if (vertexCount == 0)
            return;

        if (_vertices.Length != vertexCount)
        {
            _vertices = new Float3[vertexCount];
            _colors = new Color[vertexCount];
            _uvs = new Float2[vertexCount];
        }
        if (_indices.Length != indexCount)
            _indices = new uint[indexCount];

        int v = 0, idx = 0;
        foreach (KeyValuePair<uint, ParticleTrail> kv in _trails)
        {
            List<TrailPoint> points = kv.Value.Points;
            int count = points.Count;
            if (count < 2) continue;

            for (int i = 0; i < count; i++)
            {
                TrailPoint point = points[i];
                Float3 tangent = i == 0
                    ? points[0].Position - points[1].Position
                    : i == count - 1
                        ? points[count - 2].Position - points[count - 1].Position
                        : points[i - 1].Position - points[i + 1].Position;

                Float3 toCamera = cameraPosition - point.Position;
                Float3 right = Float3.NormalizeSafe(Float3.Cross(toCamera, tangent), Float3.UnitX);

                float t = (float)i / (count - 1);
                float width = 0.5f * point.Size * Maths.Max(0f, WidthOverTrail.Evaluate(t, null));
                float age01 = Maths.Clamp(point.Age / MathF.Max(0.05f, kv.Value.LifeSeconds), 0f, 1f);
                Float4 tint = ColorOverLifetime.Evaluate(age01, null);
                float r = point.Color.X * tint.X;
                float g = point.Color.Y * tint.Y;
                float b = point.Color.Z * tint.Z;
                float a = point.Color.W * tint.W;

                _vertices[v] = point.Position - right * width;
                _vertices[v + 1] = point.Position + right * width;
                _colors[v] = new Color(r, g, b, a);
                _colors[v + 1] = _colors[v];
                _uvs[v] = new Float2(t, 0f);
                _uvs[v + 1] = new Float2(t, 1f);
                v += 2;
            }

            int baseVertex = v - count * 2;
            for (int s = 0; s < count - 1; s++)
            {
                uint b = (uint)(baseVertex + s * 2);
                _indices[idx++] = b;
                _indices[idx++] = b + 1;
                _indices[idx++] = b + 2;
                _indices[idx++] = b + 1;
                _indices[idx++] = b + 3;
                _indices[idx++] = b + 2;
            }
        }

        _mesh ??= new Mesh { Name = "ParticleTrails" };
        _mesh.Vertices = _vertices;
        _mesh.UV = _uvs;
        _mesh.Colors = _colors;
        _mesh.Indices = _indices;
        _mesh.RecalculateBounds();
        _mesh.Upload();

        renderables.Add(new MeshRenderable(_mesh, material, Float4x4.Identity, layerIndex));
    }

    internal Material? TrailMaterialResolution(Material? systemMaterial)
    {
        // An authored material which is still streaming is not an absent override. Drawing
        // with the particle material here can expose a different opaque texture/blend mode.
        return TrailMaterial is { } reference && !reference.IsExplicitNull
            ? reference.Res
            : systemMaterial;
    }

    public void DisposeMesh()
    {
        _mesh?.Dispose();
        _mesh = null;
    }
}
