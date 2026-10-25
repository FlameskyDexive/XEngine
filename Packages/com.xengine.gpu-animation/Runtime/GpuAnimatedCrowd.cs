using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.GpuAnimation;

/// <summary>One crowd member: placement plus its own animation state.</summary>
public struct GpuAnimatedInstance
{
    public Float3 Position;
    public Quaternion Rotation;
    public Float3 Scale;
    /// <summary>Loop phase offset in [0,1) of the clip — desynchronizes the crowd.</summary>
    public float Phase;
    /// <summary>Playback speed multiplier (default 1).</summary>
    public float Speed;
    /// <summary>Clip index into the baked atlas.</summary>
    public int Clip;
    /// <summary>Per-instance tint (alpha multiplies the final color).</summary>
    public Float4 Tint;

    public static GpuAnimatedInstance Place(Float3 position, float phase = 0f, float speed = 1f, int clip = 0, float headingDegrees = 0f)
        => new()
        {
            Position = position,
            Rotation = Quaternion.FromEuler(0f, headingDegrees, 0f),
            Scale = new Float3(1f, 1f, 1f),
            Phase = phase,
            Speed = speed,
            Clip = clip,
            Tint = new Float4(1f, 1f, 1f, 1f),
        };
}

/// <summary>
/// GPU-instanced VAT crowd renderer. Feed it a <see cref="BakedGpuAnimation"/> asset plus its
/// UV2-encoded mesh, add instances, and it submits everything through the engine's instancing
/// pipeline in one batched draw chain. Playback is GPU-time driven — the only per-frame CPU
/// work is one float write.
/// </summary>
[AddComponentMenu("Rendering/GPU Animated Crowd")]
[ComponentIcon("\uf0c0")] // Users
public class GpuAnimatedCrowd : MonoBehaviour
{
    /// <summary>Baked vertex-animation-texture asset (from the GPU Animation Baker window).</summary>
    public AssetRef<BakedGpuAnimation> BakedAsset = default;

    /// <summary>UV2-encoded static mesh produced by the same bake.</summary>
    public AssetRef<Mesh> CrowdMesh = default;

    /// <summary>Use the lit (normal-mapped, directional lambert) variant instead of unlit.</summary>
    public bool Lit;

    /// <summary>Direct light direction (world space, will be normalized in the shader).</summary>
    public Float3 LightDirection = new(0.4f, 0.8f, 0.35f);

    public Float3 LightColor = new(0.95f, 0.92f, 0.85f);

    public Float3 AmbientColor = new(0.35f, 0.37f, 0.42f);

    /// <summary>When >= 0, shader time is pinned to this value (acceptance tests, scrubbing).</summary>
    public float TimeOverride = -1f;

    public List<GpuAnimatedInstance> Instances = new();

    /// <summary>Optional material asset (e.g. the .mat produced by the baker window). When set,
    /// it replaces the auto-built crowd material — textures are still bound per-crowd at
    /// runtime, so a shared .mat works across crowds with different baked assets.</summary>
    public AssetRef<Material> MaterialAsset = default;

    /// <summary>Optional runtime material override (wins over <see cref="MaterialAsset"/>).</summary>
    [NonSerialized] public Material? MaterialOverride;

    [NonSerialized] private Material? _material;
    [NonSerialized] private PropertyState? _properties;
    [NonSerialized] private Texture2D? _clipTable;
    [NonSerialized] private Texture2D? _whiteTexture;
    [NonSerialized] private int _clipTableCount = -1;
    [NonSerialized] private Float4x4[] _transforms = Array.Empty<Float4x4>();
    [NonSerialized] private Float4[] _colors = Array.Empty<Float4>();
    [NonSerialized] private Float4[] _custom = Array.Empty<Float4>();
    [NonSerialized] private AABB _bounds;
    [NonSerialized] private bool _dirty = true;
    [NonSerialized] private BakedGpuAnimation? _texturesBound;

    public void MarkInstancesDirty() => _dirty = true;

    /// <summary>Diagnostics: how many times OnRenderCollect has run (acceptance probes).</summary>
    [NonSerialized] public int DebugCollectCalls;

    /// <summary>Diagnostics: renderables submitted by the last OnRenderCollect (0 = early-out).</summary>
    [NonSerialized] public int DebugLastSubmitCount;

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        DebugCollectCalls++;
        int before = renderables.Count;
        BakedGpuAnimation? asset = BakedAsset.Res;
        Mesh? mesh = CrowdMesh.Res;
        if (asset == null || mesh == null || Instances.Count == 0) return;

        EnsureResources(asset);
        if (_dirty) RebuildInstances(asset);

        Material drawMaterial = _material ?? MaterialOverride ?? MaterialAsset.Res!;
        if (drawMaterial == null) return;
        InstancedMeshRenderable.CreateBatched(
            renderables, mesh, drawMaterial, _transforms,
            _bounds.Center,
            _colors, _custom,
            properties: _properties,
            bounds: _bounds);
        DebugLastSubmitCount = renderables.Count - before;
    }

    private void EnsureResources(BakedGpuAnimation asset)
    {
        if (_properties == null) _properties = new PropertyState();

        // Auto material only when the user didn't supply one (runtime override or .mat asset).
        Material? target = MaterialOverride ?? MaterialAsset.Res;
        if (_material == null && target == null)
            _material = new Material(Lit ? GpuAnimationShaderLibrary.LoadLitShader() : GpuAnimationShaderLibrary.LoadUnlitShader());
        target ??= _material;

        // Textures ride the shared PropertyState (object block — bound in the VERTEX stage like
        // the SkinnedInstanceBatch bone palette); scalars ride the Material (the instance
        // property path uploads no arbitrary floats on Vulkan).
        if (_texturesBound != asset)
        {
            var positionTexture = asset.GetPositionTexture();
            if (positionTexture != null) _properties.SetTexture("_PosTex", positionTexture);
            if (Lit)
            {
                var normalTexture = asset.GetNormalTexture();
                if (normalTexture != null) _properties.SetTexture("_NrmTex", normalTexture);
            }
            _texturesBound = asset;
        }
        if (Lit && target != null)
        {
            target.SetVector("_GpuAnimLightDir", LightDirection);
            target.SetVector("_GpuAnimLightColor", LightColor);
            target.SetVector("_GpuAnimAmbient", AmbientColor);
        }

        // 1 texel per clip: (frameStart, frameCount, animLength, 0) — the shader's clip table.
        if (_clipTable == null || _clipTableCount != asset.Clips.Count)
        {
            _clipTable?.Dispose();
            var data = new Float4[asset.Clips.Count];
            for (int i = 0; i < asset.Clips.Count; i++)
                data[i] = new Float4(asset.Clips[i].FrameStart, asset.Clips[i].FrameCount, asset.Clips[i].AnimLength, 0f);
            _clipTable = new Texture2D(1, (uint)Math.Max(1, asset.Clips.Count), false, TextureImageFormat.Float4);
            _clipTable.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
            Graphics.SetWrapS(_clipTable.Handle, TextureWrap.ClampToEdge);
            Graphics.SetWrapT(_clipTable.Handle, TextureWrap.ClampToEdge);
            unsafe { fixed (Float4* ptr = data) _clipTable.SetDataPtr(ptr, 0, 0, 1, (uint)data.Length); }
            _clipTableCount = asset.Clips.Count;
        }
        _properties.SetTexture("_ClipTex", _clipTable);

        if (_whiteTexture == null)
        {
            _whiteTexture = new Texture2D(1, 1, false, TextureImageFormat.Color4b);
            _whiteTexture.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
            Graphics.SetWrapS(_whiteTexture.Handle, TextureWrap.ClampToEdge);
            Graphics.SetWrapT(_whiteTexture.Handle, TextureWrap.ClampToEdge);
            var white = new byte[] { 255, 255, 255, 255 };
            unsafe { fixed (byte* ptr = white) _whiteTexture.SetDataPtr(ptr, 0, 0, 1, 1); }
        }
        _properties.SetTexture("_MainTex", _whiteTexture);
    }

    private void RebuildInstances(BakedGpuAnimation asset)
    {
        (_transforms, _colors, _custom, _bounds) = BuildInstancePayloads(asset, Instances);
        _dirty = false;
    }

    /// <summary>Pure per-instance data mapping (device-free, unit-testable): TRS rows, tint, and
    /// CustomData = (clipIndex, phase, speed, 0), plus the crowd's world AABB.</summary>
    internal static (Float4x4[] Transforms, Float4[] Colors, Float4[] Custom, AABB Bounds) BuildInstancePayloads(
        BakedGpuAnimation asset, List<GpuAnimatedInstance> instances)
    {
        int count = instances.Count;
        var transforms = new Float4x4[count];
        var colors = new Float4[count];
        var custom = new Float4[count];

        asset.GetUnionBounds(out Float3 min, out Float3 max);
        AABB localBounds = new(min, max); // animated-space local bounds from the bake

        Float3 worldMin = new(float.MaxValue);
        Float3 worldMax = new(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            GpuAnimatedInstance inst = instances[i];
            Float4x4 trs = Float4x4.CreateTRS(inst.Position, inst.Rotation, inst.Scale);
            transforms[i] = trs;
            colors[i] = inst.Tint;
            custom[i] = new Float4(inst.Clip, inst.Phase, inst.Speed, 0f);

            AABB instanceBounds = localBounds.TransformBy(trs);
            worldMin = new Float3(
                Maths.Min(worldMin.X, instanceBounds.Min.X),
                Maths.Min(worldMin.Y, instanceBounds.Min.Y),
                Maths.Min(worldMin.Z, instanceBounds.Min.Z));
            worldMax = new Float3(
                Maths.Max(worldMax.X, instanceBounds.Max.X),
                Maths.Max(worldMax.Y, instanceBounds.Max.Y),
                Maths.Max(worldMax.Z, instanceBounds.Max.Z));
        }
        return (transforms, colors, custom, new AABB(worldMin, worldMax));
    }

    /// <summary>Clip table texels (one per clip): (frameStart, frameCount, animLength, 0).</summary>
    internal static Float4[] BuildClipTableData(BakedGpuAnimation asset)
    {
        var data = new Float4[asset.Clips.Count];
        for (int i = 0; i < asset.Clips.Count; i++)
        {
            GpuAnimationClipInfo clip = asset.Clips[i];
            data[i] = new Float4(clip.FrameStart, clip.FrameCount, clip.AnimLength, 0f);
        }
        return data;
    }

    /// <summary>Explicitly releases GPU resources (material, clip table, white texture). Safe to
    /// call repeatedly; OnRenderCollect lazily rebuilds them on the next collect.</summary>
    public void ReleaseGpuResources()
    {
        _texturesBound = null;
        _material?.Dispose();
        _material = null;
        _properties = null;
        _clipTable?.Dispose();
        _clipTable = null;
        _clipTableCount = -1;
        _whiteTexture?.Dispose();
        _whiteTexture = null;
        _dirty = true;
    }

    public override void OnDisable()
    {
        ReleaseGpuResources();
    }
}
