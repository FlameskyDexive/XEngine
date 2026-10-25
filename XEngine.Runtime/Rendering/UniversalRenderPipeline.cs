// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.UI;
using XEngine.Runtime.Rendering.VirtualGeometry;
using XEngine.Vector;

using Material = XEngine.Runtime.Resources.Material;
using Mesh = XEngine.Runtime.Resources.Mesh;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

public struct ViewerData
{
    public Float3 Position;
    public Float3 Forward;
    public Float3 Up;
    public Float3 Right;

    // Camera projection data, used by screen-space and world-space UI canvases.
    public uint PixelWidth;
    public uint PixelHeight;
    public Float4x4 ViewMatrix;
    public Float4x4 ProjectionMatrix;
    public bool ProjectionFlipsWinding;

    public ViewerData(UniversalRenderPipeline.CameraSnapshot css) : this()
    {
        Position = css.CameraPosition;
        Forward = css.CameraForward;
        Up = css.CameraUp;
        Right = css.CameraRight;
        PixelWidth = css.PixelWidth;
        PixelHeight = css.PixelHeight;
        ViewMatrix = css.View;
        ProjectionMatrix = css.Projection;
        ProjectionFlipsWinding = css.ProjectionFlipsWinding;
    }

    public ViewerData(Float3 position, Float3 forward, Float3 right, Float3 up) : this()
    {
        Position = position;
        Forward = forward;
        Right = right;
        Up = up;
    }
}

/// <summary>
/// The engine's single render pipeline (Unity 6.7-aligned URP): standard forward rendering,
/// optional Deferred GBuffer lighting, post-processing effects, shadows, and debug visualization,
/// configured by a <see cref="UniversalRenderPipelineAsset"/>. Internal like Unity's URP class —
/// assignment flows through Graphics/Quality settings, never user code.
/// </summary>
internal sealed class UniversalRenderPipeline : RenderPipeline
{
    #region Static Resources

    private static Mesh s_quadMesh;
    private static Mesh s_skyDome;
    private static Material s_defaultMaterial;
    private static Material s_skybox;
    private static Material s_gradientSkybox;
    private static Material s_gizmo;
    private static Material? s_iconMaterial;
    private static Material? s_overdrawMaterial;
    private static Material? s_selectionMaskMaterial;
    private static Material? s_selectionOutlineMaterial;
    private readonly List<IRenderable> _selectionRenderables = new();

    /// <summary>Diagnostic: blit the raw selection mask instead of the outline composite.</summary>
    internal static bool DebugShowSelectionMask;

    private static Mesh? s_iconQuad;
    private static Mesh s_gridMesh;
    private static Material s_gridMaterial;
    private static Material? s_deferredLighting;
    private static readonly TextureImageFormat[] s_ldrColorFormat = [TextureImageFormat.Color4b];
    private static readonly TextureImageFormat[] s_hdrColorFormat = [TextureImageFormat.Short4];
    private static readonly TextureImageFormat[] s_prepassFormats =
    [
        TextureImageFormat.Color4b,
        TextureImageFormat.Short4,
    ];
    private static readonly TextureImageFormat[] s_gbufferFormats =
    [
        TextureImageFormat.Color4b,
        TextureImageFormat.Short4,
        TextureImageFormat.Short4,
        TextureImageFormat.Color4b,
    ];
    private static readonly TextureImageFormat[] s_giAlbedoFormats = [TextureImageFormat.Color4b];
    // Editor grid/gizmo depth copy target: zero color attachments, depth-only (Depth24f via
    // RenderTexture's hasDepthAttachment path). Supported by the GL/D3D12/Vulkan depth blits.
    private static readonly TextureImageFormat[] s_editorDepthFormats = [];

    internal static UniversalRenderPipeline Default { get; } = new();

    private readonly List<ImageEffect> _afterOpaqueEffects = new();
    private readonly List<ImageEffect> _postProcessEffects = new();
    private readonly List<ImageEffect> _allEffects = new();
    private readonly List<ImageEffect> _sourcedEffects = new();
    private readonly List<MonoBehaviour> _cameraBehaviours = new();
    private RenderFeatureRequirements _requiredFeatures;
    private bool _giCaptureActive;
    private RenderTexture? _giAlbedoRT;
    // Depth-only copy of the camera color RT depth, used by editor grid/gizmo shaders when no
    // prepass exists. Keeps sampled depth separate from the bound colorRT depth attachment,
    // which avoids a read/write feedback loop (undefined behavior on GL/D3D12/Vulkan).
    private RenderTexture? _editorDepthRT;
    private readonly List<IRenderable> _frameRenderables = new();
    private readonly List<IRenderableLight> _frameLights = new();
    private readonly RenderContext _afterOpaqueContext = new();
    private readonly RenderContext _postProcessContext = new();
    private FXAAEffect? _qualityFxaa;
    private SMAAEffect? _qualitySmaa;
    private TAAEffect? _pipelineTaa;
    private int _pipelineTaaCameraId;
    private BloomEffect? _volumeBloom;
    private TonemapperEffect? _volumeTonemap;
    private BokehDepthOfFieldEffect? _volumeDof;
    private MotionBlurEffect? _volumeMotionBlur;
    private CinematicEffects? _volumeCinematic;
    private ScreenSpaceGlobalIlluminationEffect? _volumeSsgi;
    private bool[] _mainCulledRenderableIndices = Array.Empty<bool>();
    private readonly Plane[] _cameraFrustumPlanes = new Plane[6];
    private readonly ScriptableRenderer _renderer = new();
    private readonly ScriptableRenderContext _frameContext = new();
    private readonly BuiltinForwardPass[] _builtInPasses;
    private readonly BuiltinForwardPass _gbufferPass;
    private readonly BuiltinForwardPass _deferredLightingPass;
    private readonly BuiltinForwardPass _copyColorPass;
    private readonly BuiltinForwardPass _copyDepthPass;
    private readonly List<RendererFeature> _rendererFeatures = new();
    private bool _rendererFeaturesCreated;
    // Per-camera RT handle systems. A single shared system meant every camera rendering at a
    // different resolution in the same frame (editor Scene View + Game View + previews) forced
    // a full release + reallocate of the camera color/prepass/opaque targets on every switch —
    // 2-4 RT create/destroy cycles per editor frame. Keyed weakly so a destroyed camera drops
    // its targets with the next GC (same pattern as _cameraHistories / s_lightSystems).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Camera, RTHandleSystem> _cameraRtHandles = new();
    private RTHandleSystem _rtHandles = new(); // current camera's system; initial value serves pre-render inspection
    private readonly ConditionalWeakTable<Camera, CameraHistory> _cameraHistories = new();

    private UniversalRenderPipelineAsset? _asset;

    internal UniversalRenderPipelineAsset? Asset
    {
        get => _asset;
        set
        {
            _asset = value;
            if (_asset != null) _asset.Renderer.EnsureLoaded();
        }
    }

    private RenderingPath? _renderingPathOverride;
    private RendererKind? _rendererKindOverride;
    private PipelineAntialiasingMode? _antialiasingOverride;
    private int? _msaaSamplesOverride;
    private PipelineUpscalingFilter? _upscalingFilterOverride;

    /// <summary>
    /// Forward (default), Deferred, ForwardPlus, or DeferredPlus. Reads
    /// <see cref="UniversalRenderData.RenderingPath"/> when an asset is assigned; tests may set this to
    /// override without Echo. Plus paths still use LightBVH.
    /// </summary>
    internal RenderingPath RenderingPath
    {
        get
        {
            if (_renderingPathOverride.HasValue)
                return _renderingPathOverride.Value;
            UniversalRenderData? renderer = Asset != null ? Asset.Renderer.Res : null;
            return renderer != null ? renderer.RenderingPath : RenderingPath.Forward;
        }
        set => _renderingPathOverride = value;
    }

    /// <summary>
    /// True for Deferred and DeferredPlus on Universal 3D. Overlay stacking is disabled.
    /// Renderer2D never uses GBuffer shading.
    /// </summary>
    internal bool UsesDeferredShading
        => RendererKind != RendererKind.Renderer2D
            && (RenderingPath == RenderingPath.Deferred || RenderingPath == RenderingPath.DeferredPlus);

    /// <summary>
    /// True on every Universal 3D path: LightBVH is the clustered additional-light loop.
    /// Renderer2D uses Light2D instead.
    /// </summary>
    internal bool UsesClusteredLighting => RendererKind != RendererKind.Renderer2D;

    /// <summary>
    /// Universal 3D (default) or Renderer2D. Reads <see cref="UniversalRenderData.Kind"/> when an asset is
    /// assigned; tests may set this to override without Echo.
    /// </summary>
    internal RendererKind RendererKind
    {
        get
        {
            if (_rendererKindOverride.HasValue)
                return _rendererKindOverride.Value;
            UniversalRenderData? renderer = Asset != null ? Asset.Renderer.Res : null;
            return renderer != null ? renderer.Kind : RendererKind.Universal;
        }
        set => _rendererKindOverride = value;
    }

    /// <summary>
    /// None / MSAA / TAA. Reads <see cref="UniversalRenderPipelineAsset.Antialiasing"/> when an asset is assigned;
    /// tests may set this to override without Echo. Default None preserves the M1 pixel floor.
    /// </summary>
    internal PipelineAntialiasingMode AntialiasingMode
    {
        get
        {
            if (_antialiasingOverride.HasValue)
                return _antialiasingOverride.Value;
            return Asset != null ? Asset.Antialiasing : PipelineAntialiasingMode.None;
        }
        set => _antialiasingOverride = value;
    }

    /// <summary>
    /// MSAA sample count when <see cref="AntialiasingMode"/> is MSAA; otherwise 1.
    /// </summary>
    internal int MsaaSampleCount
    {
        get
        {
            if (AntialiasingMode != PipelineAntialiasingMode.MSAA)
                return 1;
            int samples = _msaaSamplesOverride ?? (Asset != null ? Asset.MsaaSamples : 4);
            return UniversalRenderPipelineAsset.SnapMsaaSamples(samples);
        }
        set => _msaaSamplesOverride = value;
    }

    /// <summary>
    /// Auto / Linear / Point / FSR / STP. Reads <see cref="UniversalRenderPipelineAsset.UpscalingFilter"/> when an
    /// asset is assigned. STP injects the existing TAA history path (U17-M3).
    /// </summary>
    internal PipelineUpscalingFilter UpscalingFilter
    {
        get
        {
            if (_upscalingFilterOverride.HasValue)
                return _upscalingFilterOverride.Value;
            return Asset != null ? Asset.UpscalingFilter : PipelineUpscalingFilter.Auto;
        }
        set => _upscalingFilterOverride = value;
    }

    /// <summary>Keep destination alpha through post when HDR is on. Reads the pipeline asset.</summary>
    internal bool AllowPostProcessAlphaOutput => Asset != null && Asset.AllowPostProcessAlphaOutput;

    /// <summary>TAA mode or STP filter — both consume camera history.</summary>
    internal bool UsesTemporalUpscale
        => AntialiasingMode == PipelineAntialiasingMode.TAA
            || UpscalingFilter == PipelineUpscalingFilter.STP;

    /// <summary>Resolved filter: Auto becomes STP when temporal, otherwise Linear.</summary>
    internal PipelineUpscalingFilter GetEffectiveUpscalingFilter()
    {
        PipelineUpscalingFilter filter = UpscalingFilter;
        if (filter == PipelineUpscalingFilter.Auto)
            return UsesTemporalUpscale ? PipelineUpscalingFilter.STP : PipelineUpscalingFilter.Linear;
        return filter;
    }

    /// <summary>
    /// Always 1. MSAA used to be faked with a 2× internal scale (2× SSAA = 4× pixel-shading cost)
    /// that a downsampling blit resolved. The RHI now has real multisample render targets, so the
    /// camera renders at native resolution into a multisample target and resolves it — the sample
    /// buffers scale with the sample count, the shading cost does not. Kept as a method so callers
    /// and tests that asked for the stand-in scale keep compiling and observe the removal.
    /// </summary>
    internal float GetMsaaInternalScale() => 1f;

    /// <summary>
    /// Sample count actually used for this camera's color target, after clamping to what the
    /// device supports and excluding the configurations whose single-sample interop the RHI cannot
    /// serve. Returns 1 when MSAA is off or unusable, in which case the pipeline renders exactly as
    /// it did before.
    /// <para>
    /// MSAA is skipped when: the device reports no support for the requested count; the renderer is
    /// 2D or Deferred (both blit single-sample depth into the camera color target, which no API
    /// allows against a multisample attachment, and deferred MSAA additionally needs per-sample
    /// lighting); the clear flags are Depth/Nothing (they blit the previous camera's single-sample
    /// color in); the frame needs the editor depth copy (grid/gizmos without a prepass blit depth
    /// <em>out of</em> the camera target, equally illegal while multisampled); or an
    /// after-opaques image effect is active (SSAO/SSR/SSGI and friends sample SceneColor while the
    /// camera target is still the multisample one — the resolve only happens after transparents,
    /// so there is no single-sample copy for them to read).
    /// </para>
    /// </summary>
    internal int GetEffectiveMsaaSampleCount(CameraClearFlags clearFlags, bool needsEditorDepthCopy)
    {
        if (AntialiasingMode != PipelineAntialiasingMode.MSAA)
            return 1;
        if (RendererKind == RendererKind.Renderer2D || UsesDeferredShading)
            return 1;
        if (clearFlags is CameraClearFlags.Depth or CameraClearFlags.Nothing)
            return 1;
        if (needsEditorDepthCopy)
            return 1;
        if (_afterOpaqueEffects.Count > 0)
            return 1;

        int requested = MsaaSampleCount;
        if (requested <= 1)
            return 1;
        RHI.GraphicsDeviceCapabilities? caps = Graphics.Device?.Capabilities;
        return caps == null ? 1 : caps.ClampSampleCount(requested);
    }

    /// <summary>
    /// Project AA shader used when a camera has no explicit FXAA/SMAA/TAA effect.
    /// MSAA has no post shader. TAA uses <see cref="DefaultShader.TAA"/>.
    /// </summary>
    internal static DefaultShader? ResolveFallbackAntiAliasingShader(
        PipelineAntialiasingMode pipelineMode,
        QualitySettings.AntiAliasingMode quality,
        bool hasCameraAntiAliasing)
    {
        if (hasCameraAntiAliasing)
            return null;
        if (pipelineMode == PipelineAntialiasingMode.TAA)
            return DefaultShader.TAA;
        if (pipelineMode == PipelineAntialiasingMode.MSAA)
            return null;
        return quality switch
        {
            QualitySettings.AntiAliasingMode.FXAA => DefaultShader.FXAA,
            QualitySettings.AntiAliasingMode.SMAA => DefaultShader.SMAA,
            _ => null,
        };
    }

    /// <summary>Kind of the pipeline currently collecting / drawing. SpriteRenderer reads this.</summary>
    internal static RendererKind ActiveRendererKind { get; set; }

    /// <summary>Camera color / prepass handles (M4). Tests inspect reuse without running a scene.
    /// Returns the most recently rendered camera's system.</summary>
    internal RTHandleSystem RTHandles => _rtHandles;

    // One allocation per camera lifetime (amortized warm-up); kept outside the [HotPath]
    // Internal_Render so PR0007 doesn't flag the factory.
    private RTHandleSystem ResolveRtHandles(Camera camera)
        => _cameraRtHandles.GetValue(camera, static _ => new RTHandleSystem());

    /// <summary>Releases every per-camera RT handle system plus the pre-render fallback.</summary>
    internal void ReleaseAllRtHandles()
    {
        _rtHandles.ReleaseAll();
        foreach (System.Collections.Generic.KeyValuePair<Camera, RTHandleSystem> pair in _cameraRtHandles)
            pair.Value.ReleaseAll();
    }

    /// <summary>
    /// Persistent history textures for one camera (U17-M2). Same instance while the camera lives.
    /// </summary>
    internal CameraHistory GetHistory(Camera camera)
    {
        if (camera == null)
            throw new ArgumentNullException(nameof(camera));
        return _cameraHistories.GetValue(camera, _ => new CameraHistory());
    }

    /// <summary>
    /// Camera scale multiplied by <see cref="UniversalRenderPipelineAsset.RenderScale"/> when an asset is assigned.
    /// With no asset this is exactly <see cref="Camera.RenderScale"/> (M3 must not change default pixels).
    /// </summary>
    internal float GetEffectiveRenderScale(Camera camera)
    {
        float cameraScale = camera != null ? camera.RenderScale : 1f;
        float scale = Asset == null
            ? cameraScale
            : Math.Clamp(Asset.RenderScale * cameraScale, 0.1f, 2f);
        // MSAA no longer inflates the render scale: the camera renders at native resolution into a
        // real multisample target (see GetEffectiveMsaaSampleCount) instead of the old 2x SSAA.
        return scale;
    }

    internal UniversalRenderPipeline()
    {
        _builtInPasses =
        [
            new BuiltinForwardPass(RenderPassEvent.BeforeRendering, ExecuteSetup, "Setup"),
            new BuiltinForwardPass(RenderPassEvent.BeforeRenderingShadows, ExecuteShadows, "Shadows"),
            new BuiltinForwardPass(RenderPassEvent.BeforeRenderingOpaques, ExecuteColor, "ColorPass"),
            new BuiltinForwardPass(RenderPassEvent.AfterRenderingOpaques, ExecuteAfterOpaques, "AfterOpaques"),
            new BuiltinForwardPass(RenderPassEvent.BeforeRenderingTransparents, ExecuteTransparents, "Transparents"),
            new BuiltinForwardPass(RenderPassEvent.BeforeRenderingPostProcessing, ExecutePostProcess, "PostProcess"),
            new BuiltinForwardPass(RenderPassEvent.AfterRendering, ExecuteFinal, "Final"),
        ];
        _copyColorPass = new BuiltinForwardPass(RenderPassEvent.AfterRenderingOpaques, ExecuteCopyColor, "CopyColor");
        _copyDepthPass = new BuiltinForwardPass(RenderPassEvent.AfterRenderingOpaques, ExecuteCopyDepth, "CopyDepth");
        _gbufferPass = new BuiltinForwardPass(RenderPassEvent.BeforeRenderingOpaques, ExecuteGBuffer, "GBufferPass");
        _deferredLightingPass = new BuiltinForwardPass(RenderPassEvent.AfterRenderingGbuffer, ExecuteDeferredLighting, "DeferredLighting");
        _frameContext.Renderables = _frameRenderables;
        _frameContext.Lights = _frameLights;
    }

    /// <summary>Append a feature that can enqueue extra passes each camera (M2).</summary>
    internal void AddRendererFeature(RendererFeature feature)
    {
        if (feature == null)
            throw new System.ArgumentNullException(nameof(feature));
        _rendererFeatures.Add(feature);
        if (_rendererFeaturesCreated)
            feature.Create();
    }

    internal int RendererFeatureCount => _rendererFeatures.Count;

    /// <summary>Enabled and disabled Features; M19 stripper reads this at player-build collect time.</summary>
    internal IReadOnlyList<RendererFeature> RendererFeatures => _rendererFeatures;

    /// <summary>Built-in pass events in enqueue order for the current renderer.</summary>
    internal RenderPassEvent[] GetBuiltInPassEvents()
    {
        if (RendererKind == RendererKind.Renderer2D)
        {
            var twoD = new RenderPassEvent[_builtInPasses.Length];
            for (int i = 0; i < _builtInPasses.Length; i++)
                twoD[i] = _builtInPasses[i].Event;
            return twoD;
        }

        if (UsesDeferredShading)
        {
            return
            [
                RenderPassEvent.BeforeRendering,
                RenderPassEvent.BeforeRenderingShadows,
                RenderPassEvent.BeforeRenderingOpaques,
                RenderPassEvent.AfterRenderingGbuffer,
                RenderPassEvent.AfterRenderingOpaques,
                RenderPassEvent.BeforeRenderingTransparents,
                RenderPassEvent.BeforeRenderingPostProcessing,
                RenderPassEvent.AfterRendering,
            ];
        }

        var events = new RenderPassEvent[_builtInPasses.Length];
        for (int i = 0; i < _builtInPasses.Length; i++)
            events[i] = _builtInPasses[i].Event;
        return events;
    }

    /// <summary>Overlay cameras are skipped on Deferred and DeferredPlus (URP stacking disable).</summary>
    internal bool ShouldRenderCamera(Camera camera)
        => camera.RenderType != CameraRenderType.Overlay || !UsesDeferredShading;

    // Per-Scene BVH state. Keyed weakly so it goes away with the scene without an explicit
    // unload hook from this side; a dropped scene drops its light textures on the next GC pass.
    private static readonly ConditionalWeakTable<Scene, SceneLightSystem> s_lightSystems = new();

    /// <summary>Get (or create) the light system bound to a scene. Creating one is cheap (no
    /// GPU allocation until the first <see cref="SceneLightSystem.Reconcile"/>).</summary>
    internal static SceneLightSystem GetOrCreateLightSystem(Scene scene)
    {
        if (scene == null) throw new ArgumentNullException(nameof(scene));
        return s_lightSystems.GetValue(scene, _ => new SceneLightSystem());
    }


    /// <summary>★ M7 — Flat white Unlit, used to stamp the selected objects' silhouette into the outline mask.</summary>
    private static Material SelectionMaskMaterial
    {
        get
        {
            if (s_selectionMaskMaterial != null) return s_selectionMaskMaterial;
            var material = new Material(Shader.LoadDefault(DefaultShader.Unlit)) { Name = "Selection Mask (Debug)" };
            material.SetColor("_MainColor", Color.White);
            s_selectionMaskMaterial = material;
            return material;
        }
    }

    /// <summary>★ M7 — Screen-space composite that turns the silhouette mask into an outline ring.</summary>
    private static Material SelectionOutlineMaterial
    {
        get
        {
            if (s_selectionOutlineMaterial != null) return s_selectionOutlineMaterial;
            s_selectionOutlineMaterial = new Material(Shader.LoadDefault(DefaultShader.SelectionOutline))
            {
                Name = "Selection Outline (Debug)",
            };
            return s_selectionOutlineMaterial;
        }
    }

    /// <summary>
    /// ★ M7 — Rings the selected objects (and their children) with Unity's orange selection outline:
    /// their silhouette is stamped into a temporary mask through the material override channel, then a
    /// full-screen composite draws the mask's edge over the scene color. Costs nothing when nothing is
    /// selected, so the player path never pays for it.
    /// </summary>
    private void RenderSelectionOutline(ScriptableRenderContext ctx)
    {
        var targets = ctx.RenderingData.SelectionOutlineTargets;
        RenderTexture? colorRT = ctx.ColorRT;
        if (targets == null || targets.Count == 0 || colorRT == null || !colorRT.IsValid())
            return;

        _selectionRenderables.Clear();
        List<IRenderable> all = ctx.Renderables;
        for (int i = 0; i < all.Count; i++)
        {
            if (IsSelected(targets, all[i].GetOwner()))
                _selectionRenderables.Add(all[i]);
        }
        if (_selectionRenderables.Count == 0)
            return;

        var mask = RenderTexture.GetTemporaryRT(colorRT.Width, colorRT.Height, false, [TextureImageFormat.Color4b]);
        var cmd = Graphics.GetCommandBuffer("SelectionOutline");

        cmd.SetRenderTarget(mask.frameBuffer);
        cmd.SetViewport(0, 0, (uint)mask.Width, (uint)mask.Height);
        cmd.ClearRenderTarget(ClearFlags.Color, Color.Black);
        DrawRenderables(cmd, _selectionRenderables, "RenderOrder", "Opaque",
            new ViewerData(ctx.Snapshot), null, false, null, overrideMaterial: SelectionMaskMaterial);

        Material composite = SelectionOutlineMaterial;

        cmd.SetRenderTarget(colorRT.frameBuffer);
        cmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);
        if (DebugShowSelectionMask)
            cmd.Blit(mask, colorRT);           // bisect: raw mask straight to screen
        else
            cmd.Blit(mask, colorRT, composite, 0);

        Graphics.Submit(cmd);
        RenderTexture.ReleaseTemporaryRT(mask);
    }

    /// <summary>True when <paramref name="go"/> is one of the selected objects, or a descendant of one.</summary>
    private static bool IsSelected(IReadOnlyList<GameObject> targets, GameObject? go)
    {
        for (GameObject? cursor = go; cursor != null; cursor = cursor.Parent)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (ReferenceEquals(targets[i], cursor)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// ★ M7 — The material every renderable draws with in the Overdraw view: an Unlit constant color,
    /// one step of the ramp per fragment layer once <see cref="CommandBuffer.ForceAdditiveNoDepth"/>
    /// makes the blending additive. Built on the built-in Unlit shader, which ships both GLSL and
    /// Slang programs, so the view works on every backend that can rasterize.
    /// </summary>
    private static Material OverdrawMaterial
    {
        get
        {
            if (s_overdrawMaterial != null) return s_overdrawMaterial;
            var shader = Shader.LoadDefault(DefaultShader.Unlit);
            var material = new Material(shader) { Name = "Overdraw (Debug)" };
            // ~8 layers to saturate: dark enough to read a single layer, bright where geometry stacks.
            material.SetColor("_MainColor", new Color(0.14f, 0.06f, 0.03f, 1f));
            s_overdrawMaterial = material;
            return material;
        }
    }

    #endregion

    #region Resource Management

    private static void ValidateDefaults()
    {
        if (s_quadMesh is null) s_quadMesh = Mesh.GetFullscreenQuad();
        if (s_defaultMaterial is null) s_defaultMaterial = new Material(Shader.LoadDefault(DefaultShader.Standard));
        if (s_skybox is null) s_skybox = new Material(Shader.LoadDefault(DefaultShader.ProceduralSkybox));
        if (s_gizmo is null) s_gizmo = new Material(Shader.LoadDefault(DefaultShader.Gizmos));

        if (s_skyDome.IsNotValid())
        {
            using var stream = EmbeddedResources.GetStream("Assets/Defaults/SkyDome.obj");
            var skyImport = new AssetImporting.ModelImporter().Import(stream, "SkyDome.obj");
            s_skyDome = skyImport.Meshes.Count > 0 ? skyImport.Meshes[0] : new Resources.Mesh { Name = "SkyDome" };
        }

        // Pre-compute and upload BRDF integration LUT for PBR
        BRDFLutGenerator.UploadGlobal();
        ReflectionProbeManager.Instance.PrepareAndUpload(Float3.Zero);
        LightCookieManager.Instance.UploadDefaults();
    }

    internal static void DisposeDefaultResources()
    {
        Default.DisposeQualityEffects();
        Default.ReleaseAllRtHandles();
        if (s_quadMesh != null)
            s_quadMesh.Dispose();
        if (s_skyDome != null)
            s_skyDome.Dispose();
        if (s_defaultMaterial != null)
            s_defaultMaterial.Dispose();
        if (s_skybox != null)
            s_skybox.Dispose();
        if (s_gradientSkybox != null)
            s_gradientSkybox.Dispose();
        if (s_gizmo != null)
            s_gizmo.Dispose();
        if (s_iconMaterial != null)
            s_iconMaterial.Dispose();
        if (s_overdrawMaterial != null)
            s_overdrawMaterial.Dispose();
        if (s_selectionMaskMaterial != null)
            s_selectionMaskMaterial.Dispose();
        if (s_selectionOutlineMaterial != null)
            s_selectionOutlineMaterial.Dispose();
        if (s_iconQuad != null)
            s_iconQuad.Dispose();
        if (s_gridMesh != null)
            s_gridMesh.Dispose();
        if (s_gridMaterial != null)
            s_gridMaterial.Dispose();

        s_quadMesh = null!;
        s_skyDome = null!;
        s_defaultMaterial = null!;
        s_skybox = null!;
        s_gradientSkybox = null!;
        s_gizmo = null!;
        s_iconMaterial = null;
        s_overdrawMaterial = null;
        s_selectionMaskMaterial = null;
        s_selectionOutlineMaterial = null;
        s_iconQuad = null;
        s_gridMesh = null!;
        s_gridMaterial = null!;
        if (s_deferredLighting != null)
            s_deferredLighting.Dispose();
        s_deferredLighting = null;
        Renderer2D.DisposeResources();
        GiProbeVolume.Reset();
        ReflectionProbeManager.Instance.ResetForTests();
        LightCookieManager.Instance.ResetForTests();
        DecalSystem.Instance.ResetForTests();
        Light2DSystem.Instance.ResetForTests();
        ShadowCaster2DSystem.Instance.ResetForTests();
        ScreenSpaceDecalPass.DisposeResources();
        ScreenSpaceShadowsRendererFeature.DisposeResources();
    }

    #endregion

    #region Main Rendering

    private static readonly Profiling.ProfilerMarker s_renderCameraMarker = new("Render Camera");
    private static readonly Profiling.ProfilerMarker s_shadowsMarker = new("Shadows");
    private static readonly Profiling.ProfilerMarker s_colorPassMarker = new("Opaque + Color");
    private static readonly Profiling.ProfilerMarker s_transparentsMarker = new("Transparents");

    internal override void Render(Camera camera, in RenderingData data)
    {
        if (!ShouldRenderCamera(camera))
            return;

        ValidateDefaults();

        using var renderScope = s_renderCameraMarker.Auto();

        // Main rendering with correct order of operations. The OpenGL command executor
        // keeps its own GL state mirror and skips redundant binds, so we no
        // longer need a per-render "reset to defaults" call here the first state
        // change in the pipeline's CBs picks up wherever GL is.
        Internal_Render(camera, data);

        PropertyState.ClearGlobals();

        base.Render(camera, in data);
    }

    private void GatherImageEffects(Camera camera)
    {
        _afterOpaqueEffects.Clear();
        _postProcessEffects.Clear();
        _allEffects.Clear();
        _requiredFeatures = RenderFeatureRequirements.None;
        _giCaptureActive = false;

        UniversalAdditionalCameraData? cameraData = UniversalAdditionalCameraData.Find(camera);
        if (cameraData != null && cameraData.RequiresDepthTexture)
            _requiredFeatures |= RenderFeatureRequirements.Depth;

        if (!QualitySettings.ImageEffectsEnabled)
        {
            int skipped = 0;
            foreach (ImageEffect effect in camera.Effects)
            {
                if (effect != null && effect.Enabled)
                    skipped++;
            }
            RenderStats.RecordImageEffectsSkipped(skipped);
            return;
        }

        bool renderPost = UniversalAdditionalCameraData.ShouldRenderPostProcessing(camera);
        bool hasCameraAntiAliasing = false;
        bool hasBloom = false;
        bool hasTonemap = false;
        bool hasDof = false;
        bool hasMotionBlur = false;
        bool hasCinematic = false;
        bool hasSsgi = false;
        bool featureGtao = TryGetEnabledFeature(out GTAORendererFeature gtaoFeature);
        bool featureSsr = TryGetEnabledFeature(out ScreenSpaceReflectionRendererFeature ssrFeature);
        bool featureSsgi = TryGetEnabledFeature(out ScreenSpaceGlobalIlluminationRendererFeature ssgiFeature);
        foreach (ImageEffect effect in camera.Effects)
        {
            if (effect == null || !effect.Enabled) continue;
            // Feature owns the same ImageEffect type — skip the camera copy so it is not applied twice.
            if (featureGtao && effect is GTAOEffect) continue;
            if (featureSsr && effect is ScreenSpaceReflectionEffect) continue;
            if (featureSsgi && effect is ScreenSpaceGlobalIlluminationEffect) continue;
            if (effect.Stage == RenderStage.PostProcess && !renderPost)
                continue;
            if (effect is FXAAEffect or SMAAEffect or TAAEffect)
                hasCameraAntiAliasing = true;
            if (effect is BloomEffect) hasBloom = true;
            if (effect is TonemapperEffect) hasTonemap = true;
            if (effect is BokehDepthOfFieldEffect) hasDof = true;
            if (effect is MotionBlurEffect) hasMotionBlur = true;
            if (effect is CinematicEffects) hasCinematic = true;
            if (effect is ScreenSpaceGlobalIlluminationEffect) hasSsgi = true;
            switch (effect.Stage)
            {
                case RenderStage.AfterOpaques:
                    _afterOpaqueEffects.Add(effect);
                    _allEffects.Add(effect);
                    break;
                case RenderStage.PostProcess:
                    _postProcessEffects.Add(effect);
                    _allEffects.Add(effect);
                    break;
            }
        }

        CollectSourcedImageEffects(camera, _sourcedEffects);
        for (int i = 0; i < _sourcedEffects.Count; i++)
        {
            ImageEffect effect = _sourcedEffects[i];
            if (effect == null || !effect.Enabled) continue;
            if (featureGtao && effect is GTAOEffect) continue;
            if (featureSsr && effect is ScreenSpaceReflectionEffect) continue;
            if (featureSsgi && effect is ScreenSpaceGlobalIlluminationEffect) continue;
            if (effect.Stage == RenderStage.PostProcess && !renderPost)
                continue;
            if (effect is FXAAEffect or SMAAEffect or TAAEffect)
            {
                if (hasCameraAntiAliasing) continue;
                hasCameraAntiAliasing = true;
            }
            if (effect is BloomEffect)
            {
                if (hasBloom) continue;
                hasBloom = true;
            }
            if (effect is TonemapperEffect)
            {
                if (hasTonemap) continue;
                hasTonemap = true;
            }
            if (effect is BokehDepthOfFieldEffect)
            {
                if (hasDof) continue;
                hasDof = true;
            }
            if (effect is MotionBlurEffect)
            {
                if (hasMotionBlur) continue;
                hasMotionBlur = true;
            }
            if (effect is CinematicEffects)
            {
                if (hasCinematic) continue;
                hasCinematic = true;
            }
            if (effect is ScreenSpaceGlobalIlluminationEffect) hasSsgi = true;
            switch (effect.Stage)
            {
                case RenderStage.AfterOpaques:
                    _afterOpaqueEffects.Add(effect);
                    _allEffects.Add(effect);
                    break;
                case RenderStage.PostProcess:
                    _postProcessEffects.Add(effect);
                    _allEffects.Add(effect);
                    break;
            }
        }

        if (renderPost && !hasCameraAntiAliasing && cameraData != null
            && cameraData.Antialiasing != CameraAntialiasing.None)
        {
            hasCameraAntiAliasing = true;
            switch (cameraData.Antialiasing)
            {
                case CameraAntialiasing.FXAA:
                    _qualityFxaa ??= new FXAAEffect();
                    _postProcessEffects.Add(_qualityFxaa);
                    _allEffects.Add(_qualityFxaa);
                    break;
                case CameraAntialiasing.SMAA:
                    _qualitySmaa ??= new SMAAEffect();
                    _postProcessEffects.Add(_qualitySmaa);
                    _allEffects.Add(_qualitySmaa);
                    break;
                case CameraAntialiasing.TAA:
                    if (camera.RenderType == CameraRenderType.Base)
                    {
                        TAAEffect taa = GetPipelineTaa(camera);
                        _postProcessEffects.Add(taa);
                        _allEffects.Add(taa);
                    }
                    break;
            }
        }

        if (featureGtao)
        {
            _afterOpaqueEffects.Add(gtaoFeature.Effect);
            _allEffects.Add(gtaoFeature.Effect);
        }

        if (featureSsr)
        {
            _afterOpaqueEffects.Add(ssrFeature.Effect);
            _allEffects.Add(ssrFeature.Effect);
        }

        if (featureSsgi)
        {
            _afterOpaqueEffects.Add(ssgiFeature.Effect);
            _allEffects.Add(ssgiFeature.Effect);
        }

        // Volume-driven realtime GI: a global/blendable Volume OR the scene-level Gi params
        // (EnvironmentPanel) enables SSGI without a camera effect or renderer feature
        // (Bloom's EnqueueVolumeDrivenEffect pattern, AfterOpaques).
        bool wantsSceneGi = camera.RenderScene?.Gi.Enable == true;
        EnqueueVolumeDrivenEffect(hasSsgi || featureSsgi,
            wantsSceneGi || VolumeManager.Instance.Get<GlobalIlluminationVolumeComponent>().WantsEffect,
            ref _volumeSsgi, RenderStage.AfterOpaques);

        if (renderPost)
        {
            EnqueueVolumeDrivenEffect(hasBloom, VolumeManager.Instance.Get<BloomVolumeComponent>().WantsEffect,
                ref _volumeBloom, RenderStage.PostProcess);
            EnqueueVolumeDrivenEffect(hasTonemap, VolumeManager.Instance.Get<TonemapVolumeComponent>().WantsEffect,
                ref _volumeTonemap, RenderStage.PostProcess);
            EnqueueVolumeDrivenEffect(hasDof, VolumeManager.Instance.Get<DepthOfFieldVolumeComponent>().WantsEffect,
                ref _volumeDof, RenderStage.PostProcess);
            EnqueueVolumeDrivenEffect(hasMotionBlur, VolumeManager.Instance.Get<MotionBlurVolumeComponent>().WantsEffect,
                ref _volumeMotionBlur, RenderStage.PostProcess);
            bool wantsCinematic = VolumeManager.Instance.Get<VignetteVolumeComponent>().WantsEffect
                || VolumeManager.Instance.Get<ColorAdjustmentsVolumeComponent>().WantsEffect;
            EnqueueVolumeDrivenEffect(hasCinematic, wantsCinematic, ref _volumeCinematic, RenderStage.PostProcess);
        }

        // Pipeline AA / STP fills cameras that did not opt into a camera-specific effect.
        // TAA and STP are per-camera (history must not be shared). Overlay cameras skip both.
        if (renderPost && !hasCameraAntiAliasing)
        {
            if (UsesTemporalUpscale
                && camera.RenderType == CameraRenderType.Base)
            {
                TAAEffect taa = GetPipelineTaa(camera);
                _postProcessEffects.Add(taa);
                _allEffects.Add(taa);
            }
            else if (AntialiasingMode == PipelineAntialiasingMode.None)
            {
                switch (QualitySettings.AntiAliasing)
                {
                    case QualitySettings.AntiAliasingMode.FXAA:
                        _qualityFxaa ??= new FXAAEffect();
                        _postProcessEffects.Add(_qualityFxaa);
                        _allEffects.Add(_qualityFxaa);
                        break;
                    case QualitySettings.AntiAliasingMode.SMAA:
                        _qualitySmaa ??= new SMAAEffect();
                        _postProcessEffects.Add(_qualitySmaa);
                        _allEffects.Add(_qualitySmaa);
                        break;
                }
            }
        }

        for (int i = 0; i < _allEffects.Count; i++)
            _requiredFeatures |= _allEffects[i].Requirements;

        // Realtime GI active this camera: the albedo capture pass and _GiAlbedoTexture bind
        // below key off this instead of re-scanning the effect list in ExecuteColor.
        for (int i = 0; i < _afterOpaqueEffects.Count; i++)
        {
            if (_afterOpaqueEffects[i] is ScreenSpaceGlobalIlluminationEffect)
            {
                _giCaptureActive = true;
                break;
            }
        }
    }

    private void CollectSourcedImageEffects(Camera camera, List<ImageEffect> destination)
    {
        destination.Clear();
        if (camera.GameObject == null)
            return;

        camera.GameObject.GetComponents(_cameraBehaviours);
        for (int i = 0; i < _cameraBehaviours.Count; i++)
        {
            MonoBehaviour behaviour = _cameraBehaviours[i];
            if (behaviour == null || !behaviour.Enabled)
                continue;
            if (behaviour is not ICameraImageEffectSource source)
                continue;
            source.Prepare(camera);
            source.CollectImageEffects(destination);
        }
        // Scratch only: retaining the last camera's components pinned scene objects (and, for a
        // user-script camera behaviour, the collectible script context) on the pipeline singleton.
        _cameraBehaviours.Clear();
    }

    private void EnqueueVolumeDrivenEffect<T>(bool cameraAlreadyHas, bool wants, ref T? instance, RenderStage stage)
        where T : ImageEffect, new()
    {
        if (cameraAlreadyHas || !wants)
            return;
        instance ??= new T();
        if (stage == RenderStage.AfterOpaques)
            _afterOpaqueEffects.Add(instance);
        else
            _postProcessEffects.Add(instance);
        _allEffects.Add(instance);
    }

    private TAAEffect GetPipelineTaa(Camera camera)
    {
        int id = camera.InstanceID;
        if (_pipelineTaa != null && _pipelineTaaCameraId != id)
        {
            _pipelineTaa.OnDisable();
            _pipelineTaa = null;
        }

        _pipelineTaa ??= new TAAEffect();
        _pipelineTaaCameraId = id;
        return _pipelineTaa;
    }

    private void ConfigurePipelineTaaUpscale(Camera camera)
    {
        if (_pipelineTaa == null)
            return;
        _pipelineTaa.UpscaleWidth = (int)camera.PixelRectWidth;
        _pipelineTaa.UpscaleHeight = (int)camera.PixelRectHeight;
    }

    private void DisposeQualityEffects()
    {
        _qualityFxaa?.OnDisable();
        _qualitySmaa?.OnDisable();
        _pipelineTaa?.OnDisable();
        _volumeBloom?.OnDisable();
        _volumeTonemap?.OnDisable();
        _volumeDof?.OnDisable();
        _volumeMotionBlur?.OnDisable();
        _volumeCinematic?.OnDisable();
        _qualityFxaa = null;
        _qualitySmaa = null;
        _pipelineTaa = null;
        _pipelineTaaCameraId = 0;
        _volumeBloom = null;
        _volumeTonemap = null;
        _volumeDof = null;
        _volumeMotionBlur = null;
        _volumeCinematic = null;
        DisposeRendererFeatureEffects();
    }

    private void DisposeRendererFeatureEffects()
    {
        for (int i = 0; i < _rendererFeatures.Count; i++)
        {
            if (_rendererFeatures[i] is GTAORendererFeature gtao)
                gtao.Effect.OnDisable();
            else if (_rendererFeatures[i] is ScreenSpaceReflectionRendererFeature ssr)
                ssr.Effect.OnDisable();
            else if (_rendererFeatures[i] is ScreenSpaceGlobalIlluminationRendererFeature ssgi)
                ssgi.Effect.OnDisable();
            else if (_rendererFeatures[i] is ScreenSpaceShadowsRendererFeature ssShadows)
                ssShadows.ReleaseTransient();
        }
    }

    private bool TryGetEnabledFeature<T>(out T feature) where T : RendererFeature
    {
        for (int i = 0; i < _rendererFeatures.Count; i++)
        {
            if (_rendererFeatures[i] is T typed && typed.Enabled)
            {
                feature = typed;
                return true;
            }
        }

        feature = null!;
        return false;
    }

    private void ExecuteImageEffects(RenderContext context, List<ImageEffect> effects)
    {
        if (effects == null || effects.Count == 0)
            return;

        foreach (var effect in effects)
        {
            try { effect.OnRenderEffect(context); }
            catch (Exception ex) { LogEffectSkipped(effect, "OnRenderEffect", ex); }
            RenderStats.AddImageEffect();
        }
    }

    // A broken image effect (bad shader or variant, disposed RT, divide by zero on a zero size viewport,
    // or a bug in a user ImageEffect) must never take down the whole frame. Skip it and log it.
    private static void LogEffectSkipped(ImageEffect effect, string stage, Exception ex)
        => Debug.LogError($"[ImageEffect] {effect.GetType().Name}.{stage} threw and was skipped: {ex.Message}");

    #endregion

    #region Scene Rendering

    [HotPath]
    private void Internal_Render(Camera camera, in RenderingData data)
    {
        // Pass list follows existing CommandBuffer submit boundaries so pixels stay identical.
        _rtHandles = ResolveRtHandles(camera);
        ActiveRendererKind = RendererKind;
        VirtualGeometrySystem.FrameTick(); // budget/LRU maintenance for virtual geometry residency
        _frameContext.Camera = camera;
        _frameContext.RenderingData = data;
        _renderer.Clear();
        bool capturing = FrameDebugger.IsCapturing;
        if (capturing)
            FrameDebugger.BeginCameraGroup(camera, this);
        EnqueueBuiltinPasses();
        EnqueueRendererFeatures();
        if ((_renderer.RequestedInputs & ScriptableRenderPassInput.Color) == 0)
            _rtHandles.ReleaseOpaque();
        _renderer.Execute(_frameContext, _copyColorPass, _copyDepthPass);
        _frameContext.ClearFrameTargets();
        if (capturing)
            FrameDebugger.EndGroup();
    }

    [HotPath]
    private void EnqueueBuiltinPasses()
    {
        if (RendererKind == RendererKind.Renderer2D)
        {
            for (int i = 0; i < _builtInPasses.Length; i++)
                _renderer.EnqueuePass(_builtInPasses[i]);
            return;
        }

        _renderer.EnqueuePass(_builtInPasses[0]);
        _renderer.EnqueuePass(_builtInPasses[1]);
        if (UsesDeferredShading)
        {
            _renderer.EnqueuePass(_gbufferPass);
            _renderer.EnqueuePass(_deferredLightingPass);
        }
        else
            _renderer.EnqueuePass(_builtInPasses[2]);
        for (int i = 3; i < _builtInPasses.Length; i++)
            _renderer.EnqueuePass(_builtInPasses[i]);
    }

    private void EnqueueRendererFeatures()
    {
        if (!_rendererFeaturesCreated)
        {
            for (int i = 0; i < _rendererFeatures.Count; i++)
                _rendererFeatures[i].Create();
            _rendererFeaturesCreated = true;
        }

        for (int i = 0; i < _rendererFeatures.Count; i++)
        {
            RendererFeature feature = _rendererFeatures[i];
            if (!feature.Enabled)
                continue;
            feature.AddRenderPasses(_renderer, _frameContext);
            if (feature.InputRequirements != ScriptableRenderPassInput.None)
                _renderer.AddRequestedInputs(feature.InputRequirements);
        }
    }

    /// <summary>Enqueue built-in + feature passes without executing (unit tests, no GPU).</summary>
    internal int EnqueueBuiltInAndFeaturesForTest()
    {
        _renderer.Clear();
        EnqueueBuiltinPasses();
        EnqueueRendererFeatures();
        EnqueueCopyPasses();
        return _renderer.EnqueuedCount;
    }

    /// <summary>Record + compile the execute-path DAG (copies are graph nodes, not queue entries).</summary>
    internal void CompileRenderGraphForTest(out int culled, out int executed)
    {
        _renderer.Clear();
        EnqueueBuiltinPasses();
        EnqueueRendererFeatures();
        _renderer.CompileGraph(_copyColorPass, _copyDepthPass);
        culled = _renderer.GraphCulledCount;
        executed = _renderer.GraphExecutedCount;
    }

    internal ScriptableRenderPassInput RequestedInputsForTest => _renderer.RequestedInputs;

    private void EnqueueCopyPasses()
    {
        ScriptableRenderPassInput inputs = _renderer.RequestedInputs;
        if ((inputs & ScriptableRenderPassInput.Color) != 0)
            _renderer.EnqueuePass(_copyColorPass);
        else
            _rtHandles.ReleaseOpaque();

        if ((inputs & ScriptableRenderPassInput.Depth) != 0)
            _renderer.EnqueuePass(_copyDepthPass);
    }

    internal static RenderFeatureRequirements MapPassInputs(ScriptableRenderPassInput inputs)
    {
        RenderFeatureRequirements requirements = RenderFeatureRequirements.None;
        if ((inputs & ScriptableRenderPassInput.Depth) != 0)
            requirements |= RenderFeatureRequirements.Depth;
        if ((inputs & ScriptableRenderPassInput.Normal) != 0)
            requirements |= RenderFeatureRequirements.Normals;
        if ((inputs & ScriptableRenderPassInput.Motion) != 0)
            requirements |= RenderFeatureRequirements.MotionVectors;
        return requirements;
    }

    internal int GatherEffectsForTest(Camera camera)
    {
        VolumeManager.Instance.Update(camera.Transform.Position);
        GatherImageEffects(camera);
        return _allEffects.Count;
    }

    internal int CountEffectsForTest<T>() where T : ImageEffect
    {
        int count = 0;
        for (int i = 0; i < _allEffects.Count; i++)
        {
            if (_allEffects[i] is T)
                count++;
        }

        return count;
    }

    internal bool HasEffectForTest<T>() where T : ImageEffect
        => CountEffectsForTest<T>() > 0;

    internal int CopyEnqueuedEventsForTest(System.Span<RenderPassEvent> destination)
        => _renderer.CopyEnqueuedEvents(destination);

    internal int CopyLastPassEvents(System.Span<RenderPassEvent> destination)
        => _renderer.CopyLastPassEvents(destination);

    private void ExecuteSetup(ScriptableRenderContext ctx)
    {
        Camera camera = ctx.Camera;
        RenderingData data = ctx.RenderingData;

        VolumeManager.Instance.Update(camera.Transform.Position);
        Asset?.ApplyQuality();
        ReflectionProbeManager.Instance.PrepareAndUpload(camera.Transform.Position);
        GatherImageEffects(camera);
        // Realtime GI probe volume state must be settled before CollectRenderables so per-object
        // GI mode binding (LightmapBinding) observes it for this camera frame.
        GiProbeVolume.PrepareFrame(camera.RenderScene, camera, _giCaptureActive);
        camera.UpdateImageEffectLifecycle(_allEffects);
        ctx.RequiredFeatures = _requiredFeatures | MapPassInputs(_renderer.RequestedInputs);

        float previousScale = camera.RenderScale;
        float scale = GetEffectiveRenderScale(camera);
        ctx.IsHdr = camera.HDR || (Asset != null && Asset.Hdr);
        try
        {
            PixelPerfectCamera.TryApply(camera);
            if (PixelPerfectCamera.TryGetUpscaleRenderScale(camera, out float upscale))
                scale = Math.Clamp(scale * upscale, 0.1f, 2f);
            camera.RenderScale = scale;
            ctx.Target = camera.UpdateRenderData();
            ConfigurePipelineTaaUpscale(camera);
        }
        finally
        {
            camera.RenderScale = previousScale;
        }

        foreach (ImageEffect effect in _allEffects)
            try { effect.OnPreCull(camera); }
            catch (Exception ex) { LogEffectSkipped(effect, "OnPreCull", ex); }

        ctx.Snapshot = new CameraSnapshot(camera, _cameraFrustumPlanes);
        CameraSnapshot css = ctx.Snapshot;
        if (Graphics.BackendDevice?.Backend == GraphicsBackend.Metal)
        {
            // Metal's presentation path uses a Y-reflected camera projection. Apply the same
            // projection to RenderTexture cameras so front-face winding/culling is independent
            // of whether the camera renders to the drawable or an offscreen target. The final
            // target blit restores the RenderTexture's established display orientation below.
            Float4x4 presentationY = Float4x4.CreateScale(1f, -1f, 1f);
            css.Projection = presentationY * css.Projection;
            css.NonJitteredProjection = presentationY * css.NonJitteredProjection;
            css.ProjectionFlipsWinding = true;
            if (css.HasPreviousViewProj)
                css.PreviousViewProj = presentationY * css.PreviousViewProj;
            ctx.Snapshot = css;
        }
        SetupGlobalUniforms(css);

        CollectRenderables(camera.RenderScene, camera, _frameRenderables, _frameLights);

        // ★ M7 — Scene lighting toggle. Dropping the collected lights before culling is what Unity's
        // light-bulb button does: shadow passes, the light UBOs and the clustered/BVH build all see an
        // empty set, so shading falls back to ambient + GI with albedo intact. Nothing shader-side has
        // to know about the mode, which keeps every custom shader working.
        if (data.DebugView == DebugViewMode.Unlit)
            _frameLights.Clear();

        InvalidateWorldBounds();

        // Editor grid is no longer injected as an opaque renderable: it forced a full-scene
        // prepass just to feed _CameraDepthTexture. ExecuteColor draws it after the opaque
        // pass instead, sampling a depth-only copy of the color RT depth (see ExecuteColor).

        ctx.CulledRenderableIndices = CullRenderables(
            ctx.Renderables, css.WorldFrustum, css.CullingMask, ref _mainCulledRenderableIndices);

        RenderStats.AddCamera();

        int dirCount = 0, pointCount = 0, spotCount = 0, shadowCount = 0;
        List<IRenderableLight> lights = ctx.Lights;
        for (int i = 0; i < lights.Count; i++)
        {
            IRenderableLight l = lights[i];
            switch (l.GetLightType())
            {
                case LightType.Directional: dirCount++; break;
                case LightType.Point: pointCount++; break;
                case LightType.Spot: spotCount++; break;
            }
            if (l.DoCastShadows()) shadowCount++;
        }
        RenderStats.AddLightCounts(lights.Count, dirCount, pointCount, spotCount, shadowCount);

        foreach (ImageEffect effect in _allEffects)
            try { effect.OnPreRender(camera); }
            catch (Exception ex) { LogEffectSkipped(effect, "OnPreRender", ex); }
    }

    private void ExecuteShadows(ScriptableRenderContext ctx)
    {
        using var shadowScope = s_shadowsMarker.Auto();
        if (RendererKind == RendererKind.Renderer2D)
        {
            ExecuteShadows2D(ctx);
            return;
        }

        CameraSnapshot css = ctx.Snapshot;
        List<IRenderable> renderables = ctx.Renderables;
        List<IRenderableLight> lights = ctx.Lights;

        SceneLightSystem lightSystem = GetOrCreateLightSystem(css.Scene);
        ctx.LightSystem = lightSystem;
        lightSystem.Reconcile(lights, css.CameraPosition, css.CullingMask);

        bool needsShadowAtlas = lightSystem.NeedsShadowAtlas
            && UniversalAdditionalCameraData.ShouldRenderShadows(ctx.Camera);
        ulong shadowCacheKey = 0;
        bool shadowCacheHit = false;
        if (needsShadowAtlas)
        {
            ShadowAtlas.TryInitialize();
            shadowCacheKey = ComputeShadowCacheKey(lightSystem, css.CameraPosition, renderables);
            shadowCacheHit = ShadowAtlas.IsShadowCacheValid(shadowCacheKey);
        }

        if (needsShadowAtlas && !shadowCacheHit)
        {
            // Layered path: a scene with BOTH static and dynamic casters renders the static
            // layer once per static key, then per frame restores it with one depth blit and
            // re-draws only the dynamic casters into the same tiles. All-static scenes keep the
            // whole-atlas cache above; scenes with no static casters keep the plain full path.
            bool hasStaticCaster = false;
            bool hasDynamicCaster = false;
            for (int i = 0; i < renderables.Count; i++)
            {
                if (renderables[i].IsStaticForShadows) hasStaticCaster = true;
                else hasDynamicCaster = true;
                if (hasStaticCaster && hasDynamicCaster) break;
            }

            if (hasStaticCaster && hasDynamicCaster)
            {
                ulong staticKey = ComputeShadowCacheKey(lightSystem, css.CameraPosition, renderables, staticSubsetOnly: true);
                RenderStats.BeginShadowPass();
                if (!ShadowAtlas.IsStaticCacheValid(staticKey))
                {
                    ShadowAtlas.Clear();
                    ShadowAtlas.TryInitialize();
                    RenderStats.RecordShadowAtlasClear(ShadowAtlas.GetSize());

                    {
                        using var shadowSetup = Graphics.GetCommandBuffer("ShadowAtlasClear");
                        shadowSetup.SetRenderTarget(ShadowAtlas.GetAtlas()!.frameBuffer);
                        shadowSetup.ClearRenderTarget(ClearFlags.Depth | ClearFlags.Stencil, new Color(0, 0, 0, 1));
                        Graphics.Submit(shadowSetup);
                    }

                    lightSystem.RenderShadows(this, css.CameraPosition, renderables, ShadowCasterSet.StaticOnly);
                    BlitShadowAtlasDepth(ShadowAtlas.GetAtlas()!, ShadowAtlas.EnsureStaticCache(), "ShadowStaticCacheStore");
                    ShadowAtlas.MarkStaticCacheValid(staticKey);
                }
                else
                {
                    BlitShadowAtlasDepth(ShadowAtlas.GetStaticCacheAtlas()!, ShadowAtlas.GetAtlas()!, "ShadowStaticCacheRestore");
                }

                lightSystem.RenderShadows(this, css.CameraPosition, renderables, ShadowCasterSet.DynamicOnly, reuseTiles: true);
                RenderStats.EndShadowPass();
                // The whole-atlas cache stays invalid: dynamic casters re-render every frame.
            }
            else
            {
                ShadowAtlas.Clear();
                ShadowAtlas.TryInitialize();
                RenderStats.RecordShadowAtlasClear(ShadowAtlas.GetSize());

                {
                    using var shadowSetup = Graphics.GetCommandBuffer("ShadowAtlasClear");
                    shadowSetup.SetRenderTarget(ShadowAtlas.GetAtlas()!.frameBuffer);
                    shadowSetup.ClearRenderTarget(ClearFlags.Depth | ClearFlags.Stencil, new Color(0, 0, 0, 1));
                    Graphics.Submit(shadowSetup);
                }

                RenderStats.BeginShadowPass();
                lightSystem.RenderShadows(this, css.CameraPosition, renderables);
                RenderStats.EndShadowPass();
                ShadowAtlas.MarkShadowCacheValid(shadowCacheKey);
            }
        }
        else if (shadowCacheHit)
        {
            RenderStats.RecordShadowPassCached();
        }
        else
        {
            RenderStats.RecordShadowPassSkipped();
        }

        AssignCameraMatrices(css.View, css.Projection);
        // Camera-aware overload: builds this camera's clustered light list (P3.2) before binding
        // the light globals, so forward + deferred lighting take the cluster lookup path.
        lightSystem.UploadGlobalUniforms(in css);
        UploadEnvironmentUniforms(css.Scene);
    }

    private void ExecuteShadows2D(ScriptableRenderContext ctx)
    {
        CameraSnapshot css = ctx.Snapshot;
        AssignCameraMatrices(css.View, css.Projection);
        UploadEnvironmentUniforms(css.Scene);

        _rtHandles.SetReferenceSize((int)css.PixelWidth, (int)css.PixelHeight);
        if (!Renderer2D.NeedsShadowMask())
        {
            RenderStats.RecordShadowPassSkipped();
            var skip = Graphics.GetCommandBuffer("Shadow2DSkip");
            Renderer2D.BindWhiteLight(skip);
            skip.SetGlobalTexture(Renderer2D.ShadowMaskName, Texture2D.LoadDefault(DefaultTexture.White));
            Graphics.Submit(skip);
            return;
        }

        RenderTexture shadowRt = _rtHandles.AllocShadow2D(s_ldrColorFormat);
        var cmd = Graphics.GetCommandBuffer("Shadow2D");
        Renderer2D.ExecuteShadows(cmd, shadowRt);
        Graphics.Submit(cmd);
        RenderStats.BeginShadowPass();
        RenderStats.EndShadowPass();
    }

    private void ExecuteRenderer2DColor(ScriptableRenderContext ctx)
    {
        Camera camera = ctx.Camera;
        RenderingData data = ctx.RenderingData;
        CameraSnapshot css = ctx.Snapshot;
        RenderTexture? target = ctx.Target;

        TextureImageFormat[] colorFormat = ctx.IsHdr ? s_hdrColorFormat : s_ldrColorFormat;
        _rtHandles.SetReferenceSize((int)css.PixelWidth, (int)css.PixelHeight);
        RenderTexture colorRT = _rtHandles.AllocCameraColor(true, colorFormat);
        ctx.ColorRT = colorRT;

        // 2D renderer: grid is never drawn by this path and the 2D prepass was never rendered
        // nor bound to _CameraDepthTexture, so editor grid/gizmos must not force one here.
        // (Gizmo depth for this path is bound in ExecuteFinal.)
        bool requiresPrepass = data.StackShareColor
            ? RequiresPrepass(CameraClearFlags.SolidColor, ctx.RequiredFeatures)
            : RequiresPrepass(camera.ClearFlags, ctx.RequiredFeatures);
        if (requiresPrepass)
        {
            ctx.Prepass = _rtHandles.AllocCameraPrepass(s_prepassFormats);
            RenderStats.RecordPrepassSubmitted();
        }
        else
        {
            _rtHandles.ReleasePrepass();
            ctx.Prepass = null;
            RenderStats.RecordPrepassSkipped();
        }

        var mainCmd = Graphics.GetCommandBuffer("ColorPass2D");
        mainCmd.SetRenderTarget(colorRT.frameBuffer);
        mainCmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);
        if (data.StackShareColor)
        {
            if (camera.ClearFlags != CameraClearFlags.Nothing)
                mainCmd.ClearRenderTarget(ClearFlags.Depth, camera.ClearColor);
        }
        else
        {
            Color clear = camera.ClearFlags == CameraClearFlags.Skybox
                ? (css.Scene.Skybox.Mode == Scene.SkyboxMode.SolidColor ? css.Scene.Skybox.SolidColor : camera.ClearColor)
                : camera.ClearColor;
            if (camera.ClearFlags == CameraClearFlags.Nothing || camera.ClearFlags == CameraClearFlags.Depth)
            {
                if (target.IsValid())
                {
                    mainCmd.SetRenderTargets(colorRT.frameBuffer, target.frameBuffer);
                    mainCmd.BlitFramebuffer(0, 0, target.Width, target.Height,
                                            0, 0, colorRT.Width, colorRT.Height,
                                            ClearFlags.Color, BlitFilter.Nearest);
                    mainCmd.SetRenderTarget(colorRT.frameBuffer);
                }
            }
            else
                mainCmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, clear);
        }

        RenderTexture lightRt = _rtHandles.AllocLight2D(ctx.IsHdr ? s_hdrColorFormat : s_ldrColorFormat);
        Texture2D? shadowMask = _rtHandles.Shadow2D.RenderTexture is { IsDisposed: false } shadowRt
            ? shadowRt.MainTexture
            : null;
        Renderer2D.ExecuteLights(mainCmd, lightRt, shadowMask);
        mainCmd.SetRenderTarget(colorRT.frameBuffer);
        mainCmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);
        RenderStats.BeginColorPass();
        Graphics.Submit(mainCmd);
    }

    private void ExecuteColor(ScriptableRenderContext ctx)
    {
        using var colorScope = s_colorPassMarker.Auto();
        if (RendererKind == RendererKind.Renderer2D)
        {
            ExecuteRenderer2DColor(ctx);
            return;
        }

        _rtHandles.ReleaseLight2D();
        Camera camera = ctx.Camera;
        RenderingData data = ctx.RenderingData;
        CameraSnapshot css = ctx.Snapshot;
        List<IRenderable> renderables = ctx.Renderables;
        List<IRenderableLight> lights = ctx.Lights;
        bool[] culledRenderableIndices = ctx.CulledRenderableIndices;
        RenderTexture? target = ctx.Target;

        TextureImageFormat[] colorFormat = ctx.IsHdr ? s_hdrColorFormat : s_ldrColorFormat;
        _rtHandles.SetReferenceSize((int)css.PixelWidth, (int)css.PixelHeight);

        // Editor grid/gizmos no longer force a prepass: grid is drawn right after the opaque
        // pass with a depth-only copy of the color RT depth bound as _CameraDepthTexture
        // (see the EditorGrid block at the end of this method), and FinalBlit gizmos reuse
        // the same binding. Prepass is now required only by clear flags / feature inputs.
        bool requiresPrepass = data.StackShareColor
            ? RequiresPrepass(CameraClearFlags.SolidColor, ctx.RequiredFeatures)
            : RequiresPrepass(camera.ClearFlags, ctx.RequiredFeatures);

        // That editor depth copy blits depth out of the camera target, which is illegal while the
        // target is multisampled, so it is one of the conditions that turns MSAA off for the frame.
        bool needsEditorDepthCopy = !requiresPrepass &&
            (data.DisplayGrid || (data.DisplayGizmos && Debug.HasGizmos));
        CameraClearFlags msaaClearFlags = data.StackShareColor ? CameraClearFlags.SolidColor : camera.ClearFlags;
        int msaaSamples = GetEffectiveMsaaSampleCount(msaaClearFlags, needsEditorDepthCopy);

        RenderTexture colorRT = _rtHandles.AllocCameraColor(true, colorFormat, msaaSamples);
        ctx.ColorRT = colorRT;

        RenderTexture? prepass;
        if (requiresPrepass)
            prepass = _rtHandles.AllocCameraPrepass(s_prepassFormats);
        else
        {
            _rtHandles.ReleasePrepass();
            prepass = null;
        }
        ctx.Prepass = prepass;
        if (requiresPrepass)
            RenderStats.RecordPrepassSubmitted();
        else
            RenderStats.RecordPrepassSkipped();

        var mainCmd = Graphics.GetCommandBuffer("ColorPass");

        // Opaques (and the prepass) draw approximately front-to-back, Unity's quantized approach:
        // DrawRenderables batches by key so batching is unchanged; batch discovery order and
        // within-batch order become near-to-far, cutting overdraw for the per-fragment lighting.
        List<IRenderable> opaqueOrder = SortRenderables(
            renderables, culledRenderableIndices, css.CameraPosition, SortMode.FrontToBack);

        if (prepass != null)
        {
            mainCmd.SetRenderTarget(prepass.frameBuffer);
            mainCmd.SetViewport(0, 0, (uint)prepass.Width, (uint)prepass.Height);
            mainCmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0, 0, 0, 0));
            DrawRenderables(mainCmd, opaqueOrder, "LightMode", "Prepass", new ViewerData(css), null, true);

            mainCmd.SetGlobalTexture("_CameraDepthTexture", prepass.InternalDepth);
            mainCmd.SetGlobalTexture("_CameraNormalsTexture", prepass.InternalTextures[0]);
            mainCmd.SetGlobalTexture("_CameraMotionVectorsTexture", prepass.InternalTextures[1]);

            // Seed the color RT depth with the prepass depth so opaques get early-z for free.
            // A multisample depth attachment cannot receive a single-sample copy (no API allows
            // it, and depth has no resolve), so the MSAA path clears instead and lets the opaque
            // draws write their own depth — same image, only the early-z shortcut is lost.
            if (colorRT.IsMultisampled)
            {
                mainCmd.SetRenderTarget(colorRT.frameBuffer);
                mainCmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);
                mainCmd.ClearRenderTarget(ClearFlags.Depth, default);
            }
            else
            {
                mainCmd.SetRenderTargets(colorRT.frameBuffer, prepass.frameBuffer);
                mainCmd.BlitFramebuffer(0, 0, prepass.Width, prepass.Height,
                                         0, 0, colorRT.Width, colorRT.Height,
                                         ClearFlags.Depth, BlitFilter.Nearest);
            }
        }

        // Realtime GI albedo capture (forward): one extra opaque geometry pass into a dedicated
        // 1-MRT target so the SSGI composite can re-modulate irradiance by receiver albedo/AO.
        // Deferred binds the GBuffer albedo attachment instead (BindGBufferGlobals).
        if (_giCaptureActive && prepass != null)
        {
            _giAlbedoRT = RenderTexture.GetTemporaryRT(prepass.Width, prepass.Height, true, s_giAlbedoFormats);
            mainCmd.SetRenderTarget(_giAlbedoRT.frameBuffer);
            mainCmd.SetViewport(0, 0, (uint)_giAlbedoRT.Width, (uint)_giAlbedoRT.Height);
            mainCmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0, 0, 0, 0));
            DrawRenderables(mainCmd, opaqueOrder, "LightMode", "GiAlbedo", new ViewerData(css), null, true);
            mainCmd.SetGlobalTexture("_GiAlbedoTexture", _giAlbedoRT.MainTexture);
            RenderStats.RecordGiAlbedoPass();
        }

        // Probe-volume globals (atlas + grid data texture) must be bound before opaque draws
        // sample _GIMode 3; re-bound every camera frame.
        GiProbeVolume.Bind(mainCmd);

        mainCmd.SetRenderTarget(colorRT.frameBuffer);
        mainCmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);

        if (data.StackShareColor)
        {
            // Keep the Base color; Overlay typically clears depth only.
            if (camera.ClearFlags != CameraClearFlags.Nothing)
                mainCmd.ClearRenderTarget(ClearFlags.Depth, camera.ClearColor);
        }
        else if (data.DebugView == DebugViewMode.Overdraw)
        {
            // Overdraw accumulates onto black and skips the skybox: the ramp is the layer count.
            mainCmd.ClearRenderTarget(ClearFlags.Color | (prepass == null ? ClearFlags.Depth : 0), Color.Black);
        }
        else
        {
            switch (camera.ClearFlags)
            {
                case CameraClearFlags.Skybox:
                {
                    var skyColor = css.Scene.Skybox.Mode == Scene.SkyboxMode.SolidColor
                        ? css.Scene.Skybox.SolidColor : camera.ClearColor;
                    mainCmd.ClearRenderTarget(ClearFlags.Color | (prepass == null ? ClearFlags.Depth : 0), skyColor);
                    RenderSkybox(mainCmd, css, lights);
                    break;
                }
                case CameraClearFlags.SolidColor:
                    mainCmd.ClearRenderTarget(ClearFlags.Color | (prepass == null ? ClearFlags.Depth : 0), camera.ClearColor);
                    break;
                case CameraClearFlags.Depth:
                    if (target.IsValid())
                    {
                        mainCmd.SetRenderTargets(colorRT.frameBuffer, target.frameBuffer);
                        mainCmd.BlitFramebuffer(0, 0, target.Width, target.Height,
                                                0, 0, colorRT.Width, colorRT.Height,
                                                ClearFlags.Color, BlitFilter.Nearest);
                        mainCmd.SetRenderTarget(colorRT.frameBuffer);
                    }
                    break;
                case CameraClearFlags.Nothing:
                    if (target.IsValid())
                    {
                        mainCmd.SetRenderTargets(colorRT.frameBuffer, target.frameBuffer);
                        mainCmd.BlitFramebuffer(0, 0, target.Width, target.Height,
                                                0, 0, colorRT.Width, colorRT.Height,
                                                ClearFlags.Color, BlitFilter.Nearest);
                        mainCmd.SetRenderTarget(colorRT.frameBuffer);
                    }
                    break;
            }
        }

        BindScreenSpaceShadows(mainCmd, prepass, colorRT);

        RenderStats.BeginColorPass();
        // Wireframe / Overdraw debug views apply to scene geometry only; grid/gizmos/UI stay solid.
        bool overdraw = data.DebugView == DebugViewMode.Overdraw;
        mainCmd.ForceWireframeFill = data.DebugView == DebugViewMode.Wireframe;
        mainCmd.ForceAdditiveNoDepth = overdraw;
        DrawRenderables(mainCmd, opaqueOrder, "RenderOrder", "Opaque", new ViewerData(css), null, false, colorRT,
            overrideMaterial: overdraw ? OverdrawMaterial : null);
        mainCmd.ForceWireframeFill = false;
        mainCmd.ForceAdditiveNoDepth = false;

        // EditorGrid: draw the editor grid after opaques (still before transparents, matching
        // the old opaque-queue ordering) without a full-scene prepass. Grid/gizmo shaders do a
        // manual depth compare against _CameraDepthTexture, so when no prepass exists we blit
        // the color RT depth into a depth-only copy and bind that instead. Rendering into
        // colorRT while sampling its own depth attachment would be a feedback loop (undefined
        // behavior on OpenGL/D3D12/Vulkan); the copy breaks the loop at one full-frame depth
        // blit instead of a full-scene depth draw. Backends: the blit reuses the same
        // depth-blit path as the prepass->colorRT copy (glBlitFramebuffer DEPTH_BIT on GL,
        // CopyTextureRegion on D3D12, image layout transitions on Vulkan), and depth-only
        // framebuffers are accepted by every backend's CreateFramebuffer validation.
        if (prepass == null && (data.DisplayGrid || (data.DisplayGizmos && Debug.HasGizmos)))
            _editorDepthRT = BindEditorDepthCopy(mainCmd, colorRT);

        if (data.DisplayGrid)
        {
            EnsureGridResources();
            if (s_gridMesh != null && s_gridMaterial != null)
            {
                mainCmd.SetRenderTarget(colorRT.frameBuffer);
                mainCmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);
                float cx = MathF.Round(css.CameraPosition.X);
                float cz = MathF.Round(css.CameraPosition.Z);
                mainCmd.DrawMesh(s_gridMesh, s_gridMaterial, 0,
                    Float4x4.CreateTranslation(new Float3(cx, 0, cz)));
            }
        }

        Graphics.Submit(mainCmd);
    }

    private void ExecuteGBuffer(ScriptableRenderContext ctx)
    {
        Camera camera = ctx.Camera;
        RenderingData data = ctx.RenderingData;
        CameraSnapshot css = ctx.Snapshot;
        List<IRenderable> renderables = ctx.Renderables;
        bool[] culledRenderableIndices = ctx.CulledRenderableIndices;
        RenderTexture? target = ctx.Target;

        TextureImageFormat[] colorFormat = ctx.IsHdr ? s_hdrColorFormat : s_ldrColorFormat;
        _rtHandles.SetReferenceSize((int)css.PixelWidth, (int)css.PixelHeight);
        RenderTexture colorRT = _rtHandles.AllocCameraColor(true, colorFormat);
        ctx.ColorRT = colorRT;

        RenderTexture prepass = _rtHandles.AllocCameraPrepass(s_gbufferFormats);
        ctx.Prepass = prepass;
        RenderStats.RecordPrepassSubmitted();

        var mainCmd = Graphics.GetCommandBuffer("GBufferPass");
        mainCmd.SetRenderTarget(prepass.frameBuffer);
        mainCmd.SetViewport(0, 0, (uint)prepass.Width, (uint)prepass.Height);
        mainCmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0, 0, 0, 0));
        DrawRenderables(mainCmd, renderables, "LightMode", "GBuffer", new ViewerData(css), culledRenderableIndices, true);

        BindGBufferGlobals(mainCmd, prepass);
        GiProbeVolume.Bind(mainCmd);

        mainCmd.SetRenderTargets(colorRT.frameBuffer, prepass.frameBuffer);
        mainCmd.BlitFramebuffer(0, 0, prepass.Width, prepass.Height,
                                 0, 0, colorRT.Width, colorRT.Height,
                                 ClearFlags.Depth, BlitFilter.Nearest);

        mainCmd.SetRenderTarget(colorRT.frameBuffer);
        mainCmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);

        if (data.StackShareColor)
        {
            if (camera.ClearFlags != CameraClearFlags.Nothing)
                mainCmd.ClearRenderTarget(ClearFlags.Depth, camera.ClearColor);
        }
        else
        {
            switch (camera.ClearFlags)
            {
                case CameraClearFlags.Skybox:
                {
                    var skyColor = css.Scene.Skybox.Mode == Scene.SkyboxMode.SolidColor
                        ? css.Scene.Skybox.SolidColor : camera.ClearColor;
                    mainCmd.ClearRenderTarget(ClearFlags.Color, skyColor);
                    RenderSkybox(mainCmd, css, ctx.Lights);
                    break;
                }
                case CameraClearFlags.SolidColor:
                    mainCmd.ClearRenderTarget(ClearFlags.Color, camera.ClearColor);
                    break;
                case CameraClearFlags.Depth:
                    if (target.IsValid())
                    {
                        mainCmd.SetRenderTargets(colorRT.frameBuffer, target.frameBuffer);
                        mainCmd.BlitFramebuffer(0, 0, target.Width, target.Height,
                                                0, 0, colorRT.Width, colorRT.Height,
                                                ClearFlags.Color, BlitFilter.Nearest);
                        mainCmd.SetRenderTarget(colorRT.frameBuffer);
                    }
                    break;
                case CameraClearFlags.Nothing:
                    if (target.IsValid())
                    {
                        mainCmd.SetRenderTargets(colorRT.frameBuffer, target.frameBuffer);
                        mainCmd.BlitFramebuffer(0, 0, target.Width, target.Height,
                                                0, 0, colorRT.Width, colorRT.Height,
                                                ClearFlags.Color, BlitFilter.Nearest);
                        mainCmd.SetRenderTarget(colorRT.frameBuffer);
                    }
                    break;
            }
        }

        Graphics.Submit(mainCmd);
    }

    private void ExecuteDeferredLighting(ScriptableRenderContext ctx)
    {
        RenderTexture? colorRT = ctx.ColorRT;
        RenderTexture? prepass = ctx.Prepass;
        if (colorRT == null || prepass == null || prepass.IsDisposed)
            return;

        CameraSnapshot css = ctx.Snapshot;
        var cmd = Graphics.GetCommandBuffer("DeferredLighting");
        BindGBufferGlobals(cmd, prepass);
        BindScreenSpaceShadows(cmd, prepass, colorRT);

        s_deferredLighting ??= new Material(Shader.LoadDefault(DefaultShader.DeferredLighting));
        cmd.Blit(colorRT, s_deferredLighting);

        RenderStats.BeginColorPass();
        List<IRenderable> forwardOpaqueOrder = SortRenderables(
            ctx.Renderables, ctx.CulledRenderableIndices, css.CameraPosition, SortMode.FrontToBack);
        DrawRenderables(cmd, forwardOpaqueOrder, "RenderOrder", "Opaque", new ViewerData(css), null, false, colorRT, skipGBufferMaterials: true);
        Graphics.Submit(cmd);
    }

    private static void BindGBufferGlobals(CommandBuffer cmd, RenderTexture prepass)
    {
        cmd.SetGlobalTexture(RTHandleNames.CameraPrepass, prepass.InternalDepth);
        cmd.SetGlobalTexture("_CameraDepthTexture", prepass.InternalDepth);
        cmd.SetGlobalTexture("_CameraNormalsTexture", prepass.InternalTextures[0]);
        cmd.SetGlobalTexture("_CameraMotionVectorsTexture", prepass.InternalTextures[1]);
        if (prepass.InternalTextures.Length > 2)
        {
            cmd.SetGlobalTexture(RTHandleNames.GBufferAlbedo, prepass.InternalTextures[2]);
            // Deferred already has albedo+AO in the GBuffer; alias it for the SSGI composite.
            cmd.SetGlobalTexture("_GiAlbedoTexture", prepass.InternalTextures[2]);
        }
        if (prepass.InternalTextures.Length > 3)
            cmd.SetGlobalTexture(RTHandleNames.GBufferEmission, prepass.InternalTextures[3]);
    }

    private void BindScreenSpaceShadows(CommandBuffer cmd, RenderTexture? prepass, RenderTexture colorRT)
    {
        if (TryGetEnabledFeature(out ScreenSpaceShadowsRendererFeature feature) && prepass != null)
            feature.Execute(cmd, prepass, colorRT.Width, colorRT.Height);
        else
            ScreenSpaceShadowsRendererFeature.BindDisabled(cmd);

        cmd.SetRenderTarget(colorRT.frameBuffer);
        cmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);
    }

    private void ExecuteAfterOpaques(ScriptableRenderContext ctx)
    {
        RenderStats.BeginPostFx();
        if (_afterOpaqueEffects.Count > 0)
        {
            CameraSnapshot css = ctx.Snapshot;
            RenderTexture? prepass = ctx.Prepass;
            _afterOpaqueContext.Dispose();
            _afterOpaqueContext.DepthNormals = prepass!;
            _afterOpaqueContext.MotionVectors = prepass == null ? null! : prepass.InternalTextures[1];
            _afterOpaqueContext.GiAlbedoTexture = _giCaptureActive && _giAlbedoRT != null ? _giAlbedoRT.MainTexture : null;
            _afterOpaqueContext.SceneColor = ctx.ColorRT;
            _afterOpaqueContext.Camera = ctx.Camera;
            _afterOpaqueContext.Width = (int)css.PixelWidth;
            _afterOpaqueContext.Height = (int)css.PixelHeight;
            _afterOpaqueContext.CurrentStage = RenderStage.AfterOpaques;
            ExecuteImageEffects(_afterOpaqueContext, _afterOpaqueEffects);
            _afterOpaqueContext.Dispose();
        }
        RenderStats.EndPostFx();
    }

    private void ExecuteCopyColor(ScriptableRenderContext ctx)
    {
        RenderTexture? colorRT = ctx.ColorRT;
        if (colorRT == null)
            return;

        TextureImageFormat[] formats = ctx.IsHdr ? s_hdrColorFormat : s_ldrColorFormat;
        RenderTexture copy = _rtHandles.AllocCameraOpaque(formats);
        var cmd = Graphics.GetCommandBuffer("CopyColor");
        // The opaque copy is same-size, so with MSAA the resolve *is* the copy — one
        // fixed-function pass instead of a full-screen blit, and the only legal way to read a
        // multisample target into a sampleable texture.
        if (colorRT.IsMultisampled && copy.Width == colorRT.Width && copy.Height == colorRT.Height)
            cmd.ResolveTexture(colorRT, copy);
        else
            cmd.Blit(colorRT, copy);
        cmd.SetGlobalTexture(RTHandleNames.CameraOpaque, copy.MainTexture);
        Graphics.Submit(cmd);
        ctx.OpaqueColor = copy;
        RenderStats.RecordCopyColor();
    }

    private void ExecuteCopyDepth(ScriptableRenderContext ctx)
    {
        // Fallback to the camera target's own depth only while it is single-sample: a multisample
        // depth attachment cannot be sampled, and depth has no resolve path in either API.
        Texture2D? depth = ctx.Prepass != null && !ctx.Prepass.IsDisposed
            ? ctx.Prepass.InternalDepth
            : ctx.ColorRT is { IsDisposed: false, IsMultisampled: false } ? ctx.ColorRT.InternalDepth : null;
        if (depth == null)
            return;

        var cmd = Graphics.GetCommandBuffer("CopyDepth");
        cmd.SetGlobalTexture(RTHandleNames.CameraPrepass, depth);
        Graphics.Submit(cmd);
        RenderStats.RecordCopyDepth();
    }

    private void ExecuteTransparents(ScriptableRenderContext ctx)
    {
        using var transparentsScope = s_transparentsMarker.Auto();
        CameraSnapshot css = ctx.Snapshot;
        RenderTexture colorRT = ctx.ColorRT!;
        List<IRenderable> sortBackToFront = SortRenderables(
            ctx.Renderables, ctx.CulledRenderableIndices, css.CameraPosition, SortMode.BackToFront);
        // No candidates -> no CB at all (previously an empty Transparents CB was recorded and
        // submitted every frame even for scenes with zero renderables).
        if (sortBackToFront.Count > 0)
        {
            var transparentCmd = Graphics.GetCommandBuffer("Transparents");
            transparentCmd.SetRenderTarget(colorRT.frameBuffer);
            transparentCmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);
            bool overdrawPass = ctx.RenderingData.DebugView == DebugViewMode.Overdraw;
            transparentCmd.ForceWireframeFill = ctx.RenderingData.DebugView == DebugViewMode.Wireframe;
            transparentCmd.ForceAdditiveNoDepth = overdrawPass;
            DrawRenderables(transparentCmd, sortBackToFront, "RenderOrder", "Transparent", new ViewerData(css), null, false, colorRT,
                overrideMaterial: overdrawPass ? OverdrawMaterial : null);
            transparentCmd.ForceWireframeFill = false;
            transparentCmd.ForceAdditiveNoDepth = false;
            Graphics.Submit(transparentCmd);
        }

        RenderSelectionOutline(ctx);

        RenderUIQueue(css, colorRT, UISurface.World, ctx.RenderingData);
        RenderStats.EndColorPass();
        MaybeResolveMsaa(ctx);
    }

    /// <summary>
    /// Resolve the multisample camera color into a same-size single-sample target and hand that to
    /// every downstream consumer (post processing, final blit). Everything after this point samples
    /// the camera color as a texture, which a multisample attachment cannot serve.
    /// <para>
    /// This replaces the 2× SSAA stand-in's downsampling blit: the source is now native resolution,
    /// so the resolve is a same-size fixed-function sample average rather than a filtered
    /// downscale of a 4×-pixel image.
    /// </para>
    /// </summary>
    private void MaybeResolveMsaa(ScriptableRenderContext ctx)
    {
        RenderTexture? color = ctx.ColorRT;
        if (color == null)
            return;

        RenderStats.RecordMsaaSamples(color.SampleCount);
        if (!color.IsMultisampled)
            return;

        TextureImageFormat[] formats = ctx.IsHdr ? s_hdrColorFormat : s_ldrColorFormat;
        RenderTexture resolved = _rtHandles.AllocCameraResolve(color.Width, color.Height, formats);
        var cmd = Graphics.GetCommandBuffer("MsaaResolve");
        cmd.ResolveTexture(color, resolved);
        Graphics.Submit(cmd);
        ctx.ColorRT = resolved;
    }

    private void ExecutePostProcess(ScriptableRenderContext ctx)
    {
        RenderStats.BeginPostFx();
        if (_postProcessEffects.Count > 0)
        {
            CameraSnapshot css = ctx.Snapshot;
            RenderTexture? prepass = ctx.Prepass;
            _postProcessContext.Dispose();
            _postProcessContext.DepthNormals = prepass!;
            _postProcessContext.MotionVectors = prepass == null ? null! : prepass.InternalTextures[1];
            _postProcessContext.SceneColor = ctx.ColorRT;
            _postProcessContext.Camera = ctx.Camera;
            _postProcessContext.Width = (int)css.PixelWidth;
            _postProcessContext.Height = (int)css.PixelHeight;
            _postProcessContext.CurrentStage = RenderStage.PostProcess;

            ExecuteImageEffects(_postProcessContext, _postProcessEffects);

            var replacedRTs = _postProcessContext.GetReplacedRTs();
            if (replacedRTs.Count > 0)
            {
                ctx.ColorRT = _postProcessContext.SceneColor;
                for (int i = 0; i < replacedRTs.Count; i++)
                {
                    RenderTexture oldRT = replacedRTs[i];
                    if (_rtHandles.CameraColor.Owns(oldRT))
                        _rtHandles.CameraColor.Release();
                    else
                        RenderTexture.ReleaseTemporaryRT(oldRT);
                }
                if (ctx.ColorRT != null)
                    _rtHandles.CameraColor.Adopt(ctx.ColorRT, pooled: true);
            }
            _postProcessContext.Dispose();
        }
        RenderStats.EndPostFx();
    }

    private void ExecuteFinal(ScriptableRenderContext ctx)
    {
        Camera camera = ctx.Camera;
        RenderingData data = ctx.RenderingData;
        CameraSnapshot css = ctx.Snapshot;
        RenderTexture colorRT = ctx.ColorRT!;
        RenderTexture? target = ctx.Target;
        RenderTexture? prepass = ctx.Prepass;

        var finalCmd = Graphics.GetCommandBuffer("FinalBlit");
        if (data.DisplayGizmos)
        {
            // 3D binds _CameraDepthTexture either to the prepass depth (prepass exists) or to
            // the editor depth copy made in ExecuteColor. The 2D renderer produces no depth
            // producer at all (its prepass was never rendered or bound), so fall back to a
            // white texture: depth.r == 1 == far plane, i.e. "nothing occludes the gizmos",
            // instead of sampling a stale binding left over from a previous camera.
            if (prepass == null && _editorDepthRT == null)
                finalCmd.SetGlobalTexture("_CameraDepthTexture", Texture2D.LoadDefault(DefaultTexture.White));

            finalCmd.SetRenderTarget(colorRT.frameBuffer);
            finalCmd.SetViewport(0, 0, (uint)colorRT.Width, (uint)colorRT.Height);
            RenderGizmos(finalCmd, css);
            GiProbeVolume.DrawGizmos(css.Scene?.Gi.DebugView == 4);
        }

        if (data.StackSkipFinalBlit)
        {
            RenderUIQueue(css, colorRT, UISurface.Overlay, data, finalCmd);
        }
        else
        {
            // Vulkan's swapchain has no depth/stencil attachment. Compose masks/cooldowns on
            // the camera target, which owns stencil, before presenting the combined image.
            bool overlayBeforePresent = target == null && Graphics.BackendDevice?.Backend == GraphicsBackend.Vulkan;
            RenderTexture presentSource = colorRT;
            if (overlayBeforePresent)
            {
                presentSource = _rtHandles.AllocPresentation((int)css.ViewportWidth, (int)css.ViewportHeight, s_ldrColorFormat);
                Material presentationCopy = RenderPipeline.GetBlitMaterial();
                presentationCopy.SetFloat("_FlipY", 1f);
                finalCmd.Blit(colorRT, presentSource, material: presentationCopy);
                RenderUIQueue(css, presentSource, UISurface.Overlay, data, finalCmd);
            }
            float targetFlipY = ResolveOffscreenCameraPresentationFlipY(
                Graphics.BackendDevice?.Backend ?? GraphicsBackend.Null);
            if (target != null && targetFlipY != 0f)
            {
                // Scene cameras now share the drawable's projection winding. RenderTexture
                // consumers keep their previous upright image orientation by undoing that Y
                // reflection at the final camera blit; screen-space overlays are drawn after it.
                Material targetPresentation = RenderPipeline.GetBlitMaterial();
                targetPresentation.SetFloat("_FlipY", targetFlipY);
                finalCmd.Blit(
                    presentSource,
                    target,
                    css.ViewportX,
                    css.ViewportY,
                    css.ViewportWidth,
                    css.ViewportHeight,
                    targetPresentation,
                    0,
                    false,
                    false);
            }
            else
            {
                finalCmd.Blit(
                    presentSource,
                    target,
                    css.ViewportX,
                    css.ViewportY,
                    css.ViewportWidth,
                    css.ViewportHeight,
                    null,
                    0,
                    false,
                    false);
            }

            if (!overlayBeforePresent)
                RenderUIQueue(css, target, UISurface.Overlay, data, finalCmd);
        }
        Graphics.Submit(finalCmd);
        // No trailing AssignCameraMatrices: nothing consumes GlobalUniforms between here and the
        // next camera's ExecuteSetup assign (gizmos draw earlier in this pass; PipelineReset only
        // touches RT/viewport), and the restore cost 3 matrix inversions + one UBO upload CB.

        var resetCmd = Graphics.GetCommandBuffer("PipelineReset");
        resetCmd.SetRenderTarget(null);
        uint resetWidth = css.PixelWidth;
        uint resetHeight = css.PixelHeight;
        if (Window.InternalWindow != null)
        {
            resetWidth = (uint)Window.InternalWindow.FramebufferSize.X;
            resetHeight = (uint)Window.InternalWindow.FramebufferSize.Y;
        }
        resetCmd.SetViewport(0, 0, resetWidth, resetHeight);
        if (_editorDepthRT != null)
        {
            ReleaseEditorDepthCopy(resetCmd, _editorDepthRT);
            _editorDepthRT = null;
        }
        Graphics.Submit(resetCmd);

        camera.SavePreviousViewProjectionMatrix();

        foreach (ImageEffect effect in _allEffects)
            try { effect.OnPostRender(camera); }
            catch (Exception ex) { LogEffectSkipped(effect, "OnPostRender", ex); }

        // Camera color/prepass stay in RTHandle across frames while size and format match.
        if (prepass != null && !_rtHandles.CameraPrepass.Owns(prepass))
            RenderTexture.ReleaseTemporaryRT(prepass);
        if (!_rtHandles.CameraColor.Owns(colorRT) && !_rtHandles.CameraResolve.Owns(colorRT))
            RenderTexture.ReleaseTemporaryRT(colorRT);
        if (_giAlbedoRT != null)
        {
            RenderTexture.ReleaseTemporaryRT(_giAlbedoRT);
            _giAlbedoRT = null;
        }
    }

    /// <summary>
    /// Undo the Metal camera-projection Y reflection when the final image is written to a
    /// RenderTexture. Intermediate blits keep their backend-default orientation policy.
    /// </summary>
    internal static float ResolveOffscreenCameraPresentationFlipY(GraphicsBackend backend) =>
        backend == GraphicsBackend.Metal ? 1f : 0f;

    /// <summary>
    /// Blit the color RT depth into a depth-only temporary and bind it as _CameraDepthTexture
    /// for the editor grid/gizmo shaders (see the EditorGrid comment in ExecuteColor). Must
    /// leave colorRT as the active draw target: the blit binds the depth-only FBO (zero color
    /// attachments) as the draw framebuffer, and the gizmos-only path (no grid) records no
    /// SetRenderTarget of its own before the color pass is submitted.
    /// </summary>
    internal static RenderTexture BindEditorDepthCopy(CommandBuffer cmd, RenderTexture colorRT)
    {
        RenderTexture editorDepthRT = RenderTexture.GetTemporaryRT(
            colorRT.Width, colorRT.Height, true, s_editorDepthFormats);
        cmd.SetRenderTargets(editorDepthRT.frameBuffer, colorRT.frameBuffer);
        cmd.BlitFramebuffer(0, 0, colorRT.Width, colorRT.Height,
                            0, 0, editorDepthRT.Width, editorDepthRT.Height,
                            ClearFlags.Depth, BlitFilter.Nearest);
        cmd.SetRenderTarget(colorRT.frameBuffer);
        cmd.SetGlobalTexture("_CameraDepthTexture", editorDepthRT.InternalDepth);
        return editorDepthRT;
    }

    /// <summary>
    /// Drop the _CameraDepthTexture global binding made by <see cref="BindEditorDepthCopy"/>
    /// before returning the depth copy to the temporary pool, so a later pass or camera cannot
    /// sample the recycled texture.
    /// </summary>
    internal static void ReleaseEditorDepthCopy(CommandBuffer cmd, RenderTexture editorDepthRT)
    {
        cmd.ClearGlobalTexture("_CameraDepthTexture");
        RenderTexture.ReleaseTemporaryRT(editorDepthRT);
    }

    /// <summary>
    /// A prepass (full-scene depth/normals/motion draw) is required when the camera clear flags
    /// cannot initialize the color RT depth in-place (Depth/Nothing need a blit source) or when
    /// renderer features request prepass buffers. Editor grid/gizmos no longer force a prepass;
    /// they consume a depth-only copy of the color RT depth instead (see ExecuteColor).
    /// </summary>
    internal static bool RequiresPrepass(
        CameraClearFlags clearFlags,
        RenderFeatureRequirements requirements)
        => clearFlags is CameraClearFlags.Depth or CameraClearFlags.Nothing
            || requirements != RenderFeatureRequirements.None;

    /// <summary>Full-atlas depth copy between the shadow atlas and its static-layer cache.</summary>
    private static void BlitShadowAtlasDepth(RenderTexture source, RenderTexture destination, string name)
    {
        int size = ShadowAtlas.GetSize();
        using var cmd = Graphics.GetCommandBuffer(name);
        cmd.SetRenderTargets(destination.frameBuffer, source.frameBuffer);
        cmd.BlitFramebuffer(0, 0, size, size, 0, 0, size, size, ClearFlags.Depth, BlitFilter.Nearest);
        Graphics.Submit(cmd);
    }

    private static ulong ComputeShadowCacheKey(SceneLightSystem lightSystem, Float3 cameraPosition,
        IReadOnlyList<IRenderable> renderables, bool staticSubsetOnly = false)
    {
        // Dynamic or custom renderables cannot prove their shadow geometry is immutable, so the
        // whole-atlas key bails whenever one is present. The static-subset mode instead skips
        // them: it keys only the static layer, which the layered cache renders once and then
        // composites under the per-frame dynamic casters.
        ulong hash = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        int keyedCount = 0;
        for (int i = 0; i < renderables.Count; i++)
        {
            IRenderable renderable = renderables[i];
            if (!renderable.IsStaticForShadows)
            {
                if (staticSubsetOnly)
                    continue;
                return 0;
            }
            hash = MixShadowCacheKey(hash, renderable.ShadowCasterVersion, prime);
            keyedCount++;
        }

        hash = MixShadowCacheKey(hash, (ulong)keyedCount, prime);
        // Camera position enters the key only quantized: cascade fit drifts continuously with
        // the camera, but a stale fit inside one bucket is visually indistinguishable, and the
        // exact float key defeated the cache on every scene-view camera move (the dominant
        // editor case). Bucket = shadowDistance/16 so error stays a small fraction of a cascade.
        float shadowDistance = lightSystem.Directional is DirectionalLight dir ? dir.ShadowDistance : 150f;
        float bucket = MathF.Max(0.5f, shadowDistance / 16f);
        hash = MixShadowCacheKey(hash, (ulong)(int)MathF.Floor(cameraPosition.X / bucket), prime);
        hash = MixShadowCacheKey(hash, (ulong)(int)MathF.Floor(cameraPosition.Y / bucket), prime);
        hash = MixShadowCacheKey(hash, (ulong)(int)MathF.Floor(cameraPosition.Z / bucket), prime);
        hash = MixShadowCacheKey(hash, (ulong)ShadowAtlas.GetSize(), prime);
        hash = MixShadowCacheKey(hash, (ulong)QualitySettings.Tier, prime);
        hash = MixShadowCacheKey(hash, (ulong)QualitySettings.Version, prime);

        if (lightSystem.Directional is not Light directional)
        {
            hash = MixShadowCacheKey(hash, 0, prime);
        }
        else
        {
            hash = MixLightShadowCacheKey(hash, directional, prime);
        }

        IReadOnlyList<IRenderableLight> casters = lightSystem.ShadowCasters;
        hash = MixShadowCacheKey(hash, (ulong)casters.Count, prime);
        for (int i = 0; i < casters.Count; i++)
        {
            if (casters[i] is not Light light)
                return 0;
            hash = MixLightShadowCacheKey(hash, light, prime);
        }

        return hash;
    }

    private static ulong MixLightShadowCacheKey(ulong hash, Light light, ulong prime)
    {
        hash = MixShadowCacheKey(hash, (ulong)light.GetLightID(), prime);
        hash = MixShadowCacheKey(hash, light.Transform.Version, prime);
        hash = MixShadowCacheKey(hash, light.DoCastShadows() ? 1UL : 0UL, prime);
        hash = MixShadowCacheKey(hash, (ulong)light.ShadowQuality, prime);
        hash = MixShadowCacheKey(hash, (ulong)BitConverter.SingleToInt32Bits(light.ShadowBias), prime);
        hash = MixShadowCacheKey(hash, (ulong)BitConverter.SingleToInt32Bits(light.ShadowNormalBias), prime);
        hash = MixShadowCacheKey(hash, (ulong)BitConverter.SingleToInt32Bits(light.ShadowStrength), prime);

        switch (light)
        {
            case DirectionalLight directional:
                hash = MixShadowCacheKey(hash, (ulong)directional.ShadowResolution, prime);
                hash = MixShadowCacheKey(hash, (ulong)directional.Cascades, prime);
                hash = MixShadowCacheKey(hash, (ulong)BitConverter.SingleToInt32Bits(directional.ShadowDistance), prime);
                break;
            case PointLight point:
                hash = MixShadowCacheKey(hash, (ulong)point.ShadowResolution, prime);
                hash = MixShadowCacheKey(hash, (ulong)BitConverter.SingleToInt32Bits(point.Range), prime);
                break;
            case SpotLight spot:
                hash = MixShadowCacheKey(hash, (ulong)spot.ShadowResolution, prime);
                hash = MixShadowCacheKey(hash, (ulong)BitConverter.SingleToInt32Bits(spot.Range), prime);
                hash = MixShadowCacheKey(hash, (ulong)BitConverter.SingleToInt32Bits(spot.SpotAngle), prime);
                break;
        }

        return hash;
    }

    private static ulong MixShadowCacheKey(ulong hash, ulong value, ulong prime) => (hash ^ value) * prime;

    // Fog + ambient are nine global-uniform writes. Routing each through its own one-op
    // CommandBuffer (the PropertyState.SetGlobalX helpers) cost 9 rent/submit cycles per
    // camera per frame; encode them into a single buffer and submit once (same shape as
    // SceneLightSystem.UploadGlobalUniforms).
    private static void UploadEnvironmentUniforms(Scene scene)
    {
        using var cmd = Graphics.GetCommandBuffer("EnvironmentUniforms");

        Scene.FogParams fog = scene.Fog;
        Float4 fogParams = Float4.Zero;
        fogParams.X = fog.Density / 1.2011224f;
        fogParams.Y = fog.Density / 0.693147181f;
        float fogRange = fog.End - fog.Start;
        if (MathF.Abs(fogRange) < 0.0001f) fogRange = 0.0001f;
        fogParams.Z = -1.0f / fogRange;
        fogParams.W = fog.End / fogRange;

        cmd.SetGlobalColor("_FogColor", fog.Color);
        cmd.SetGlobalVector("_FogParams", fogParams);
        cmd.SetGlobalVector("_FogStates", new Float3(
            fog.Mode == Scene.FogParams.FogMode.Linear ? 1 : 0,
            fog.Mode == Scene.FogParams.FogMode.Exponential ? 1 : 0,
            fog.Mode == Scene.FogParams.FogMode.ExponentialSquared ? 1 : 0
        ));

        Scene.AmbientLightParams ambient = scene.Ambient;
        cmd.SetGlobalVector("_AmbientMode", new Float2(
            ambient.Mode == Scene.AmbientLightParams.AmbientMode.Uniform ? 1 : 0,
            ambient.Mode == Scene.AmbientLightParams.AmbientMode.Hemisphere ? 1 : 0
        ));
        cmd.SetGlobalColor("_AmbientColor", ambient.Color);
        cmd.SetGlobalColor("_AmbientSkyColor", ambient.SkyColor);
        cmd.SetGlobalColor("_AmbientGroundColor", ambient.GroundColor);
        cmd.SetGlobalFloat("_AmbientStrength", (float)ambient.Strength);

        Graphics.Submit(cmd);
    }

    // ─────────────────────── UI ───────────────────────

    private static readonly List<CanvasRenderNode> s_uiTmp = new(64);
    private readonly UIBatchComposer _uiOverlayBatches = new();
    private readonly UIBatchComposer _uiWorldBatches = new();

    /// <summary>
    /// Draws the screen-space UI for one surface (Camera or Overlay) of a camera. Collects the scene's
    /// UI render items, sorts them by canvas/hierarchy order, sets an orthographic screen projection, and
    /// draws each item's baked mesh with its material's UI pass through a command buffer. World-space
    /// canvases are not handled here (they render alongside the scene transparents).
    /// </summary>
    private void RenderUIQueue(
        CameraSnapshot css,
        RenderTexture? targetRT,
        UISurface surface,
        in RenderingData data,
        CommandBuffer? recordedCommandBuffer = null)
    {
        RenderStats.BeginUIPass();
        try
        {
            RenderUIQueueCore(css, targetRT, surface, data, recordedCommandBuffer);
        }
        finally
        {
            RenderStats.EndUIPass();
        }
    }

    private void RenderUIQueueCore(
        CameraSnapshot css,
        RenderTexture? targetRT,
        UISurface surface,
        in RenderingData data,
        CommandBuffer? recordedCommandBuffer = null)
    {
        if (surface == UISurface.World) { RenderUIWorldCore(css, targetRT, data); return; }
        // Screen-space UI is game-view only. In the scene view every canvas is drawn world-space instead
        // (see RenderUIWorld), so it can be seen and edited in 3D.
        if (data.SkipUI || data.IsSceneView) return;

        // Tell every screen-space GameCanvas the surface size so its design-pixel layout matches the
        // orthographic projection built below; canvases rebuild themselves when this changes.
        Float2? prevOverride = CanvasRegistry.ScreenSizeOverride;
        bool presentation = _rtHandles.IsPresentation(targetRT)
            || (targetRT == null && Graphics.BackendDevice?.Backend == GraphicsBackend.Metal);
        uint uiWidth = css.ViewportWidth > 0 ? css.ViewportWidth : css.PixelWidth;
        uint uiHeight = css.ViewportHeight > 0 ? css.ViewportHeight : css.PixelHeight;
        CanvasRegistry.ScreenSizeOverride = new Float2(uiWidth, uiHeight);
        try
        {
            s_uiTmp.Clear();
            CanvasRegistry.CollectRenderNodes(css.Scene, surface, s_uiTmp);
            if (s_uiTmp.Count == 0) return;

            // Items arrive in per-canvas hierarchy order; stable-sort by SortKey across canvases.
            s_uiTmp.Sort(static (a, b) => a.SortKey.CompareTo(b.SortKey));

            // RectTransform layout is bottom-left / +Y-up, while the final target is addressed
            // top-left on Vulkan. Reverse the orthographic Y range so a positive anchored Y
            // still moves a screen-space Canvas element toward the top of the presented image.
            Float4x4 uiProjection = presentation
                ? Float4x4.CreateOrthoOffCenter(0, uiWidth, 0, uiHeight, -1000f, 1000f)
                : BuildScreenOrtho(uiWidth, uiHeight);
            AssignCameraMatrices(Float4x4.Identity, uiProjection);

            CommandBuffer cmd = recordedCommandBuffer ?? Graphics.GetCommandBuffer("UI");
            if (targetRT != null)
            {
                cmd.SetRenderTarget(targetRT.frameBuffer);
                if (css.ViewportWidth > 0 && css.ViewportHeight > 0)
                    cmd.SetViewport(css.ViewportX, css.ViewportY, css.ViewportWidth, css.ViewportHeight);
                else
                    cmd.SetViewport(0, 0, (uint)targetRT.Width, (uint)targetRT.Height);
            }
            else
            {
                cmd.SetRenderTarget(null);
                if (css.ViewportWidth > 0 && css.ViewportHeight > 0)
                    cmd.SetViewport(css.ViewportX, css.ViewportY, css.ViewportWidth, css.ViewportHeight);
                else
                    cmd.SetViewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
            }

            DrawUIItems(cmd, s_uiTmp, new ViewerData(css), _uiOverlayBatches);
            if (recordedCommandBuffer == null)
            {
                Graphics.Submit(cmd);
                // A separately submitted UI command buffer has already been queued. It is safe
                // to restore the game camera now because the later UBO upload follows the draw.
                AssignCameraMatrices(css.View, css.Projection);
            }
        }
        finally
        {
            CanvasRegistry.ScreenSizeOverride = prevOverride;
        }
    }

    /// <summary>
    /// Draws world-space UI canvases. Their render items carry world-space model matrices, so they use
    /// the camera's own view/projection (already current after the transparents pass) and render into the
    /// scene color. Shown in the scene view too, since they live in the world.
    /// </summary>
    private void RenderUIWorldCore(CameraSnapshot css, RenderTexture? targetRT, in RenderingData data)
    {
        if (data.SkipUI) return;

        // In the scene view, screen-space canvases lay out against the viewport size so their world rect
        // matches the UISceneEditor handles (which push the same override while editing).
        Float2? prevOverride = CanvasRegistry.ScreenSizeOverride;
        if (data.IsSceneView)
            CanvasRegistry.ScreenSizeOverride = new Float2(css.PixelWidth, css.PixelHeight);
        try
        {
            s_uiTmp.Clear();
            CanvasRegistry.CollectRenderNodes(css.Scene, UISurface.World, s_uiTmp);
            if (data.IsSceneView)
            {
                // The scene view shows every canvas in world space, regardless of RenderMode.
                CanvasRegistry.CollectRenderNodes(css.Scene, UISurface.Overlay, s_uiTmp);
            }
            if (s_uiTmp.Count == 0) return;

            s_uiTmp.Sort(static (a, b) => a.SortKey.CompareTo(b.SortKey));

            var cmd = Graphics.GetCommandBuffer("UIWorld");
            if (targetRT != null)
            {
                cmd.SetRenderTarget(targetRT.frameBuffer);
                cmd.SetViewport(0, 0, (uint)targetRT.Width, (uint)targetRT.Height);
            }
            DrawUIItems(cmd, s_uiTmp, new ViewerData(css), _uiWorldBatches);
            Graphics.Submit(cmd);
            RecordWorldOcclusionProbes(css, targetRT);
        }
        finally
        {
            CanvasRegistry.ScreenSizeOverride = prevOverride;
        }
    }

    /// <summary>
    /// B7b — records one occlusion probe per pending world-surface click: the panel's quad is
    /// re-drawn into the same depth attachment with a single-pixel scissor at the click point,
    /// color writes blended out (Src=Zero, Dst=One), depth write off and depth test LEqual —
    /// the wrapped occlusion query then answers "would the panel be visible at this pixel"
    /// with the scene's depth/stencil/clip/MSAA state fully in effect. Runs after the regular
    /// UI draws in its own command buffer so a frame without UI items still probes.
    /// </summary>
    private void RecordWorldOcclusionProbes(CameraSnapshot css, RenderTexture? targetRT)
    {
        var device = Graphics.Device;
        if (device is null || !device.SupportsOcclusionQueries)
            return;
        IWorldOcclusionProbe[] probes = WorldOcclusionProbes.Snapshot();
        if (probes.Length == 0)
            return;

        var viewer = new ViewerData(css);
        var viewProjection = viewer.ProjectionMatrix * viewer.ViewMatrix;
        int viewportWidth = targetRT?.Width ?? (int)viewer.PixelWidth;
        int viewportHeight = targetRT?.Height ?? (int)viewer.PixelHeight;

        var cmd = Graphics.GetCommandBuffer("UIWorldOcclusionProbes");
        if (targetRT != null)
        {
            cmd.SetRenderTarget(targetRT.frameBuffer);
            cmd.SetViewport(0, 0, (uint)targetRT.Width, (uint)targetRT.Height);
        }

        foreach (IWorldOcclusionProbe probe in probes)
        {
            if (!probe.TryGetProbePixel(viewProjection, viewportWidth, viewportHeight, out int pixelX, out int pixelY))
            {
                Runtime.Debug.Log($"[OcclusionProbe] id={probe.QueryId} no-pixel (off-screen?)");
                continue;
            }
            Runtime.Debug.Log($"[OcclusionProbe] id={probe.QueryId} pixel=({pixelX},{pixelY}) viewport={viewportWidth}x{viewportHeight} meshVT={probe.ProbeMesh?.VertexCount ?? -1} pass={probe.ProbePass}");

            pixelX = Math.Clamp(pixelX, 0, Math.Max(0, viewportWidth - 1));
            pixelY = Math.Clamp(pixelY, 0, Math.Max(0, viewportHeight - 1));
            cmd.SetScissor(pixelX, pixelY, 1, 1);
            cmd.BeginOcclusionQuery(probe.QueryId);
            // The raster override keeps the probe a pure depth visibility test: the fragment's
            // contribution is blended to exactly zero (Src=Zero/Dst=One/Add leaves the target
            // unchanged), depth is tested LEqual but never written, and nothing touches stencil.
            var raster = new RasterizerState
            {
                DepthTest = true,
                DepthWrite = false,
                Depth = RasterizerState.DepthMode.Lequal,
                // Color contribution blended to exactly zero (Src=Zero/Dst=One/Add): the pass
                // disables color writes without a dedicated write-mask knob.
                DoBlend = true,
                BlendSrc = RasterizerState.Blending.Zero,
                BlendDst = RasterizerState.Blending.One,
                Blend = RasterizerState.BlendMode.Add,
                CullFace = RasterizerState.PolyFace.None,
                StencilEnabled = false,
            };
            cmd.DrawMesh(probe.ProbeMesh, probe.ProbeMaterial, probe.ProbePass, probe.ProbeModel,
                rasterOverride: raster);
            cmd.EndOcclusionQuery(probe.QueryId);
            cmd.DisableScissor();
        }
        Graphics.Submit(cmd);
    }

    private void DrawUIItems(CommandBuffer cmd, IReadOnlyList<CanvasRenderNode> items, ViewerData viewer, UIBatchComposer composer)
    {
        composer.Compose(items, viewer);

        // GetPassesWithTag allocates a fresh list per call, and UI items are drawn back-to-back with the
        // same shared material - so memoize the resolved pass index for the last shader seen instead of
        // re-querying (and allocating) for every one of potentially hundreds of items each frame.
        Shader? cachedShader = null;
        int cachedPass = -1;
        CanvasClipState lastClip = CanvasClipState.None;
        bool haveClip = false;

        bool usesStencil = false;
        IReadOnlyList<UIBatchDraw> draws = composer.Draws;
        for (int i = 0; i < draws.Count; i++)
        {
            if (!draws[i].Clip.UsesStencil) continue;
            usesStencil = true;
            break;
        }
        if (usesStencil)
            cmd.ClearRenderTarget(ClearFlags.Stencil, new Color(0f, 0f, 0f, 0f));

        for (int i = 0; i < draws.Count; i++)
        {
            UIBatchDraw item = draws[i];
            Material material = item.Material;
            if (material == null || material.Shader.IsNotValid()) continue;

            PropertyState properties = item.Properties;
            Mesh mesh = item.Mesh;
            Float4x4 model = item.Model;
            if (mesh == null || mesh.VertexCount <= 0) continue;

            // UI materials carry a single pass tagged RenderOrder=UI.
            int uiPass;
            if (ReferenceEquals(material.Shader, cachedShader))
                uiPass = cachedPass;
            else
            {
                var uiPasses = material.Shader.GetPassesWithTag("RenderOrder", "UI");
                uiPass = uiPasses.Count > 0 ? uiPasses[0] : -1;
                cachedShader = material.Shader;
                cachedPass = uiPass;
            }
            if (uiPass < 0) continue;

            // Rect clipping remains per-fragment so it follows rotation, scale and rounded corners.
            // Stencil masks alter only the backend-neutral raster state for this draw; the UI shader
            // and Canvas node remain package-agnostic.
            if (item.Clip.UsesStencil)
            {
                CanvasClipState itemClip = item.Clip;
                RasterizerState raster = material.Shader.GetPass(uiPass).State;
                ConfigureUIStencilState(in itemClip, ref raster);
                cmd.DrawMesh(mesh, material, uiPass, model, properties, rasterOverride: raster);
            }
            else
            {
                cmd.DrawMesh(mesh, material, uiPass, model, properties);
            }
            RenderStats.RecordUIDraw(item.VertexCount, item.IndexCount);
            RenderStats.RecordUIBatch();
            if (!haveClip || !lastClip.Equals(item.Clip))
            {
                RenderStats.RecordClipStateChange(item.Clip.UsesStencil);
                lastClip = item.Clip;
                haveClip = true;
            }
        }

        // Leave the scissor test off so the next command buffer isn't clipped.
        cmd.DisableScissor();
    }

    private static void ConfigureUIStencilState(in CanvasClipState clip, ref RasterizerState raster)
    {
        raster.StencilEnabled = true;
        raster.StencilReadMask = byte.MaxValue;
        raster.StencilFailOp = RasterizerState.StencilOp.Keep;
        raster.StencilZFailOp = RasterizerState.StencilOp.Keep;
        if (clip.IsStencilWrite)
        {
            // A nested mask increments only fragments that already carry its parent's value. The
            // ensuing descendants test the incremented value, giving deterministic nested clipping
            // without reserving one stencil bit per nesting level.
            raster.StencilFunc = clip.StencilReference == 0
                ? RasterizerState.StencilFunction.Always
                : RasterizerState.StencilFunction.Equal;
            raster.StencilRef = checked((int)clip.StencilReference);
            raster.StencilWriteMask = byte.MaxValue;
            raster.StencilPassOp = RasterizerState.StencilOp.Incr;
            if (clip.SuppressStencilWriterColor)
            {
                // The stencil writer must retain its geometry but not affect the color target.
                // Source=0, destination=1 is portable across all three backends and leaves every
                // color channel unchanged without introducing a backend-specific color-write API.
                raster.DoBlend = true;
                raster.BlendSrc = RasterizerState.Blending.Zero;
                raster.BlendDst = RasterizerState.Blending.One;
                raster.Blend = RasterizerState.BlendMode.Add;
            }
            return;
        }

        if (clip.IsStencilUnwind)
        {
            raster.StencilFunc = RasterizerState.StencilFunction.Equal;
            raster.StencilRef = checked((int)clip.StencilReference);
            raster.StencilWriteMask = byte.MaxValue;
            raster.StencilPassOp = RasterizerState.StencilOp.Decr;
            raster.DoBlend = true;
            raster.BlendSrc = RasterizerState.Blending.Zero;
            raster.BlendDst = RasterizerState.Blending.One;
            raster.Blend = RasterizerState.BlendMode.Add;
            return;
        }

        raster.StencilFunc = RasterizerState.StencilFunction.Equal;
        raster.StencilRef = checked((int)clip.StencilDepth);
        raster.StencilWriteMask = 0;
        raster.StencilPassOp = RasterizerState.StencilOp.Keep;
    }

    internal static Float4x4 BuildScreenOrtho(uint width, uint height)
        => Float4x4.CreateOrthoOffCenter(0, width, height, 0, -1000f, 1000f);

    private void RenderSkybox(CommandBuffer cmd, CameraSnapshot css, List<IRenderableLight> lights)
    {
        var skyParams = css.Scene.Skybox;

        switch (skyParams.Mode)
        {
            case Scene.SkyboxMode.Procedural:
            {
                // PR0001-allowlisted: FirstOrDefault(static ...) over a concrete List<T> was
                // 1.3-2x faster than the manual scan with zero per-call allocation (see
                // docs/benchmarks/2026-07-23-linq-migration). Predicate must stay non-capturing.
                IRenderableLight? sun = lights.FirstOrDefault(
                    static l => l is IRenderableLight rl && rl.GetLightType() == LightType.Directional);
                var sunDir = sun != null ? sun.GetLightDirection() : Float3.Normalize(new Float3(0.5f, 0.7f, 0.5f));
                s_skybox.SetVector("_SunDir", sunDir);
                cmd.DrawMesh(s_skyDome, s_skybox, 0, Float4x4.Identity);
                break;
            }

            case Scene.SkyboxMode.SolidColor:
                // Camera clear already filled with color nothing more to do.
                break;

            case Scene.SkyboxMode.Gradient:
            {
                s_gradientSkybox ??= new Material(Shader.LoadDefault(DefaultShader.GradientSkybox));
                s_gradientSkybox.SetColor("_TopColor", skyParams.GradientTop);
                s_gradientSkybox.SetColor("_BottomColor", skyParams.GradientBottom);
                s_gradientSkybox.SetFloat("_Exponent", skyParams.GradientExponent);
                cmd.DrawMesh(s_skyDome, s_gradientSkybox, 0, Float4x4.Identity);
                break;
            }

            case Scene.SkyboxMode.Material:
            {
                var customMat = skyParams.CustomMaterial.Res;
                if (customMat != null)
                    cmd.DrawMesh(s_skyDome, customMat, 0, Float4x4.Identity);
                else
                    cmd.DrawMesh(s_skyDome, s_skybox, 0, Float4x4.Identity);
                break;
            }
        }
    }

    /// <summary>
    /// CPU copy of <c>TransformSkyboxClip</c> in XEngineCG. Ortho must expand the unit dome so
    /// NDC covers the far plane; perspective already does via the projection.
    /// </summary>
    internal static Float4 TransformSkyboxClip(Float3 objectPos, in Float4x4 view, in Float4x4 projection)
    {
        Float4 viewDir4 = Float4x4.TransformPoint(new Float4(objectPos.X, objectPos.Y, objectPos.Z, 0f), view);
        Float3 viewDir = new(viewDir4.X, viewDir4.Y, viewDir4.Z);
        Float4 clip = Float4x4.TransformPoint(new Float4(viewDir.X, viewDir.Y, viewDir.Z, 1f), projection);
        Float4 origin = Float4x4.TransformPoint(new Float4(0f, 0f, 0f, 1f), projection);
        if (MathF.Abs(origin.W) > 0.5f)
        {
            clip.X = viewDir.X * 4f * origin.W;
            clip.Y = viewDir.Y * 4f * origin.W;
        }
        clip.Z = clip.W;
        // Ortho flattens the dome along view Z, so with ZTest Off the behind-camera hemisphere
        // lands on the same pixels as the sky and submission order decides each pixel.
        // Perspective is safe (w = -viewZ hardware-clips it); here push those verts past the
        // far plane so the clipper removes them.
        if (MathF.Abs(origin.W) > 0.5f && viewDir.Z > 0f)
            clip.Z = 2f * MathF.Abs(clip.W) + 1f;
        return clip;
    }

    private static void EnsureGridResources()
    {
        if (s_gridMesh == null)
        {
            const float e = 500f;
            s_gridMesh = new Mesh();
            s_gridMesh.Vertices = [new(-e, 0, -e), new(e, 0, -e), new(e, 0, e), new(-e, 0, e)];
            s_gridMesh.UV = [new(-e, -e), new(e, -e), new(e, e), new(-e, e)];
            s_gridMesh.Normals = [Float3.UnitY, Float3.UnitY, Float3.UnitY, Float3.UnitY];
            s_gridMesh.Indices = [0, 2, 1, 0, 3, 2];
            s_gridMesh.RecalculateBounds();
            s_gridMesh.Upload();
        }

        if (s_gridMaterial == null)
        {
            var shader = Shader.LoadDefault(DefaultShader.Grid);
            if (shader != null)
            {
                s_gridMaterial = new Material(shader);
                s_gridMaterial.SetColor("_GridColor", new Color(0.5f, 0.5f, 0.5f, 0.3f));
                s_gridMaterial.SetFloat("_PrimaryGridSize", 1f);
                s_gridMaterial.SetFloat("_SecondaryGridSize", 0.25f);
                s_gridMaterial.SetFloat("_LineWidth", 0.02f);
                s_gridMaterial.SetFloat("_Falloff", 1.5f);
                s_gridMaterial.SetFloat("_MaxDist", 500f);
            }
        }
    }

    private void RenderGizmos(CommandBuffer cmd, CameraSnapshot css)
    {
        // Post-process / UI passes may have replaced the global VP with a screen ortho.
        // Gizmo shaders read XENGINE_MATRIX_VP, so restore the camera before recording draws.
        AssignCameraMatrices(css.View, css.Projection);
        (Mesh? wire, Mesh? solid) = Debug.GetGizmoDrawData();

        if (wire.IsValid() || solid.IsValid())
        {
            if (wire.IsValid()) cmd.DrawMesh(wire, s_gizmo);
            if (solid.IsValid()) cmd.DrawMesh(solid, s_gizmo);
        }

        var icons = Debug.GetGizmoIcons();
        if (icons.Count > 0)
        {
            s_iconMaterial ??= new Material(Shader.LoadDefault(DefaultShader.GizmoIcon));
            s_iconQuad ??= Mesh.GetFullscreenQuad();

            foreach (var icon in icons)
            {
                if (icon.Texture == null || icon.Texture.IsDisposed) continue;
                s_iconMaterial.SetTexture("_MainTex", icon.Texture);
                s_iconMaterial.SetVector("_IconCenter", icon.Center);
                s_iconMaterial.SetFloat("_IconScale", icon.Scale);
                s_iconMaterial.SetVector("_IconColor", new Float4(icon.Color.R, icon.Color.G, icon.Color.B, icon.Color.A));
                cmd.DrawMesh(s_iconQuad, s_iconMaterial);
            }
        }
    }

    #endregion
}
