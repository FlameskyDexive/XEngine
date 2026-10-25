// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Material = XEngine.Runtime.Resources.Material;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Dynamic irradiance probe volume (Surface Cache GI, probe tier): a camera-following snapped
/// grid of probes whose octahedral irradiance tiles live in a persistent RGBA16F atlas. A
/// round-robin batch of tiles is refreshed per frame by the SSGI shader's ProbeUpdate pass
/// (screen-space rays over the current frame's radiance, alpha-blended EMA), and lit shaders
/// sample the volume via <c>_GIMode 3</c> / <c>SampleGiProbes</c>. Grid params reach shaders
/// through a 2-texel RGBA32F data texture so no backend cbuffer ABI changes are needed.
/// v1: single volume owned by the last camera that prepared it (primary camera scenarios);
/// a world-space surface cache is the v2 upgrade path.
/// </summary>
public static class GiProbeVolume
{
    private const int CountX = 8;
    private const int CountY = 4;
    private const int CountZ = 8;
    private const float FastFillSeconds = 0.5f;

    /// <summary>Probe irradiance is sampled by lit shaders only while true.</summary>
    public static bool Active { get; private set; }

    public static int ProbeCount => CountX * CountY * CountZ;

    public static int TilesPerRow { get; private set; }
    public static int AtlasSize { get; private set; }

    private static RenderTexture _irradianceAtlas;
    private static Texture2D _probeData;
    private static Material _mat;
    private static Float3 _origin;
    private static float _spacing = 2f;
    private static int _cursor;
    private static float _fastFill;
    private static bool _stateDirty = true;
    private static Float4[] _dataTexels = new Float4[2];

    /// <summary>
    /// Per-camera frame prepare. Must run before <c>CollectRenderables</c> so per-object GI mode
    /// binding (<see cref="LightmapBinding"/>) observes <see cref="Active"/> for this frame.
    /// </summary>
    public static void PrepareFrame(Scene? scene, Camera camera, bool giCaptureActive)
    {
        var gi = scene?.Gi ?? default;
        bool active = giCaptureActive && gi.Enable && camera.GameObject != null;
        if (Active != active)
        {
            Active = active;
            _stateDirty = true;
        }
        if (!active)
            return;

        float spacing = Math.Clamp(gi.ProbeSpacing, 0.25f, 16f);
        if (spacing != _spacing)
        {
            _spacing = spacing;
            _stateDirty = true;
        }

        // Camera-following grid, snapped to the spacing lattice so texel data does not swim.
        Float3 cameraPos = camera.Transform.Position;
        float halfX = (CountX - 1) * _spacing * 0.5f;
        float halfY = (CountY - 1) * _spacing * 0.5f;
        float halfZ = (CountZ - 1) * _spacing * 0.5f;
        Float3 min = new(cameraPos.X - halfX, cameraPos.Y - halfY, cameraPos.Z - halfZ);
        Float3 snapped = new(
            MathF.Floor(min.X / _spacing) * _spacing,
            MathF.Floor(min.Y / _spacing) * _spacing,
            MathF.Floor(min.Z / _spacing) * _spacing);
        if (snapped != _origin)
        {
            _origin = snapped;
            _stateDirty = true;
            _fastFill = FastFillSeconds; // re-origin invalidates EMA contents: refill fast
        }
    }

    /// <summary>Binds the atlas + grid data texture as shader globals (before opaque draws).
    /// Strict backends (D3D12/Vulkan) require every reflected texture slot to be bound, so the
    /// inactive path pins dummies (ScreenSpaceShadows.BindDisabled convention): the black data
    /// texture's alpha also makes GiProbesActive() read false in-shader.</summary>
    public static void Bind(CommandBuffer cmd)
    {
        if (!Active)
        {
            BindDisabled(cmd);
            return;
        }
        EnsureResources();
        if (_stateDirty)
        {
            _dataTexels[0] = new Float4(_origin.X, _origin.Y, _origin.Z, _spacing);
            _dataTexels[1] = new Float4(CountX, CountY, CountZ, 1f);
            _probeData.SetData<Float4>(_dataTexels);
            _stateDirty = false;
        }
        cmd.SetGlobalTexture("_GiIrradianceAtlas", _irradianceAtlas.MainTexture);
        cmd.SetGlobalTexture("_GiProbeMoments", _irradianceAtlas.MainTexture);
        cmd.SetGlobalTexture("_GiProbeData", _probeData);
    }

    private static void BindDisabled(CommandBuffer cmd)
    {
        var white = Texture2D.LoadDefault(DefaultTexture.White);
        var black = Texture2D.LoadDefault(DefaultTexture.Emission);
        cmd.SetGlobalTexture("_GiIrradianceAtlas", white);
        cmd.SetGlobalTexture("_GiProbeMoments", white);
        cmd.SetGlobalTexture("_GiProbeData", black);
    }

    /// <summary>
    /// Refreshes this frame's round-robin probe batch. Called by the SSGI effect after its
    /// composite so the rays see the GI-composited scene colour of the current frame.
    /// </summary>
    public static void UpdateProbes(CommandBuffer cmd, Camera camera, Scene? scene, RenderTexture sceneColor, Texture2D depth)
    {
        if (!Active || _irradianceAtlas == null)
            return;

        var gi = scene?.Gi ?? default;
        int batch = Math.Clamp(gi.ProbesPerFrame, 1, ShaderConfig.MaxGiProbeUpdatesPerFrame);
        float rate = 0.15f;
        if (_fastFill > 0f)
        {
            // After (re)origin or activation: full-overwrite EMA and a doubled batch so the
            // volume converges in ~half a second instead of seconds.
            rate = 1f;
            batch = Math.Min(batch * 2, ProbeCount);
            _fastFill -= Time.UnscaledDeltaTime;
        }

        int first = _cursor;
        int count = Math.Min(batch, ProbeCount - first);
        if (count <= 0)
        {
            _cursor = 0;
            first = 0;
            count = Math.Min(batch, ProbeCount);
        }
        _cursor = first + count == ProbeCount ? 0 : first + count;
        LastUpdatedCount = count;

        _mat.SetFloat("_GiUpdateRate", rate);
        _mat.SetVector("_GiUpdateRange", new Float4(first, count, TilesPerRow, 0f));
        _mat.SetVector("_GiProbeOriginSpacing", new Float4(_origin.X, _origin.Y, _origin.Z, _spacing));
        _mat.SetVector("_GiProbeGridParams", new Float4(CountX, CountY, CountZ, 0f));
        var miss = scene?.Ambient ?? default;
        Float3 missColor = miss.UseHemisphere ? new Float3(miss.SkyColor.X, miss.SkyColor.Y, miss.SkyColor.Z) * miss.Strength
                                              : new Float3(miss.Color.X, miss.Color.Y, miss.Color.Z) * miss.Strength;
        _mat.SetVector("_GiMissColor", new Float4(missColor.X, missColor.Y, missColor.Z, 0f));
        cmd.SetGlobalTexture("_CameraDepthTexture", depth);
        cmd.Blit(sceneColor, _irradianceAtlas, _mat, 4); // Pass "ProbeUpdate"
        RenderStats.RecordGiPass();
        RenderStats.RecordGiProbesUpdated(count);
    }

    public static int LastUpdatedCount { get; private set; }

    /// <summary>
    /// Scene-view gizmo (debug view 4): the volume bounds plus a sparse lattice of probe points.
    /// Called from the pipeline's gizmo phase; CPU-side only, no GPU cost when not drawn.
    /// </summary>
    public static void DrawGizmos(bool show)
    {
        if (!Active || !show)
            return;
        float sizeX = (CountX - 1) * _spacing;
        float sizeY = (CountY - 1) * _spacing;
        float sizeZ = (CountZ - 1) * _spacing;
        Float3 center = new(_origin.X + sizeX * 0.5f, _origin.Y + sizeY * 0.5f, _origin.Z + sizeZ * 0.5f);
        Debug.DrawWireCube(center, new Float3(sizeX * 0.5f, sizeY * 0.5f, sizeZ * 0.5f), new Color(0.4f, 0.8f, 1f, 1f));
        // Sparse probe lattice: every other cell keeps the view readable.
        var color = new Color(0.55f, 0.85f, 1f, 1f);
        for (int z = 0; z < CountZ; z += 2)
        for (int y = 0; y < CountY; y += 2)
        for (int x = 0; x < CountX; x += 2)
            Debug.DrawLine(
                new Float3(_origin.X + x * _spacing, _origin.Y + y * _spacing, _origin.Z + z * _spacing),
                new Float3(_origin.X + x * _spacing + 0.05f, _origin.Y + y * _spacing, _origin.Z + z * _spacing),
                color);
    }

    private static void EnsureResources()
    {
        if (_irradianceAtlas != null)
            return;
        TilesPerRow = (int)Math.Ceiling(Math.Sqrt(ProbeCount));
        AtlasSize = TilesPerRow * ShaderConfig.GiProbeTileSize;
        _irradianceAtlas = new RenderTexture(AtlasSize, AtlasSize, false, [TextureImageFormat.Short4]);
        _probeData = new Texture2D(2, 1, false, TextureImageFormat.Float4);
        _mat = new Material(Shader.LoadDefault(DefaultShader.SSGI));
        _cursor = 0;
        _fastFill = FastFillSeconds;
    }

    /// <summary>Releases GPU resources (device shutdown / tests).</summary>
    public static void Reset()
    {
        _irradianceAtlas?.Dispose();
        _irradianceAtlas = null;
        _probeData?.Dispose();
        _probeData = null;
        _mat?.Dispose();
        _mat = null;
        Active = false;
        _cursor = 0;
        _stateDirty = true;
        LastUpdatedCount = 0;
    }
}
