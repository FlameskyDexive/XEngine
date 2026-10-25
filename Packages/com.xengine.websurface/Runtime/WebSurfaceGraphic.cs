using System;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.WebSurface;

/// <summary>
/// Draws the newest <see cref="WebSurfaceFrame"/> into a UI rect. Deliberately extends
/// <see cref="MaskableGraphic"/> (RawImage is sealed) so the surface participates in the
/// RectMask clip chain and stencil masks like every other UI drawable. Per-present work:
/// acquire the newest frame → upload into the retained texture → quad with the displayed UV
/// rect. A frame whose stride exceeds width·4 is copied row-wise during upload.
/// </summary>
public sealed class WebSurfaceGraphic : MaskableGraphic
{
    [SerializeField] private bool _maskable = true;
    [SerializeIgnore] private Texture2D? _texture;
    [SerializeIgnore] private WebSurfaceSession? _session;
    [SerializeIgnore] private int _uploadedGeneration = -1;
    [SerializeIgnore] private ulong _uploadedFrameNumber;
    [SerializeField] private bool _depthTestAgainstScene;

    private static Material? s_worldDepthMaterial;

    /// <summary>
    /// Shared material using the <c>Default/DefaultUIWorld</c> shader — identical to the default
    /// UI shader except the pass keeps scene depth test on (ZTest LEqual) with depth write off,
    /// so a world-space panel is occluded by scene geometry. Same cbuffer/properties as
    /// DefaultUI, so backend uniform packing and mask/stencil state are unchanged.
    /// </summary>
    public static Material SharedWorldDepthMaterial
        => s_worldDepthMaterial ??= new Material(Shader.LoadDefault(DefaultShader.DefaultUIWorld));

    /// <summary>UV sub-rect of the surface actually displayed (defaults to the full surface).</summary>
    [SerializeField] private Rect _uvRect = new(0f, 0f, 1f, 1f);

    public Rect UVRect
    {
        get => _uvRect;
        set { _uvRect = value; SetVerticesDirty(); }
    }

    public bool ClipToMask
    {
        get => _maskable;
        set => _maskable = value;
    }

    /// <summary>
    /// B7 — when true the panel renders with scene depth test on (ZTest LEqual, ZWrite Off):
    /// scene geometry occludes the panel, other UI stays on top. Designed for canvases in
    /// <see cref="RenderMode.WorldSpace"/>; screen-space overlays have no meaningful depth to
    /// test against. Clip (RectMask) and stencil mask behaviour are preserved.
    /// </summary>
    public bool DepthTestAgainstScene
    {
        get => _depthTestAgainstScene;
        set { _depthTestAgainstScene = value; SetMaterialDirty(); }
    }

    /// <summary>
    /// The world-depth material replaces the default UI material only when
    /// <see cref="DepthTestAgainstScene"/> is set; an explicit <see cref="Material"/> assignment
    /// still wins (mirrors <see cref="Graphic.GetMaterial"/>'s precedence).
    /// </summary>
    public override Material GetMaterial()
    {
        if (_depthTestAgainstScene && Material.Res is null)
            return SharedWorldDepthMaterial;
        return base.GetMaterial();
    }

    // ---------------------------------------------------------------------------------
    // B7b — world click gating: analytic ray hit (no collider), GPU visibility probe,
    // gated forwarding of the pointer events only when the panel was visible.
    // ---------------------------------------------------------------------------------

    private WebSurfaceClickGate? _clickGate;
    private WorldClickProbe? _activeProbe;
    private ulong _panelMeshGeneration;
    private Camera? _clickCamera;
    private Float2 _clickScreenSize;
    private (float X, float Y) _pendingClickLocal;
    private (float X, float Y) _pendingClickNdc;

    /// <summary>Diagnostics for the world click path (acceptance + live tooling).</summary>
    public int WorldClickIssuedProbes { get; private set; }
    public int WorldClickDelivered { get; private set; }
    public int WorldClickDropped { get; private set; }

    /// <summary>Bumped whenever the panel mesh is (re)populated (diagnostic counter; the click
    /// snapshot binds to the surface generation and the plane instead).</summary>
    public ulong PanelGeneration => _panelMeshGeneration;

    public void Bind(WebSurfaceSession session) => _session = session;

    public void Unbind()
    {
        _session = null;
        SetMaterialDirty();
    }

    /// <summary>
    /// Requests a click from a camera screen point (no collider needed): the ray is intersected
    /// with the panel's plane and only when it hits the panel's rect does the click enter the
    /// GPU visibility gate. Returns false when the ray misses the panel entirely.
    /// </summary>
    public bool RequestClickFromScreen(Camera camera, Float2 screenPoint, Float2 screenSize)
    {
        if (_session is null || camera is null)
            return false;

        Ray ray = camera.ScreenPointToRay(screenPoint, screenSize);
        if (!TryGetPanelWorldQuad(out Float3 corner, out Float3 edgeX, out Float3 edgeY))
            return false;

        // Ray-plane intersection against the panel's plane.
        Float3 normal = Float3.Normalize(Float3.Cross(edgeX, edgeY));
        float denominator = Float3.Dot(ray.Direction, normal);
        if (MathF.Abs(denominator) < 1e-6f)
            return false;
        float t = Float3.Dot(corner - ray.Origin, normal) / denominator;
        if (t < 0f)
            return false;
        Float3 hit = ray.Origin + ray.Direction * t;

        // Express the hit in the panel's basis: coordinates in [0,1]^2.
        Float3 local = hit - corner;
        float u = Float3.Dot(local, edgeX) / Float3.LengthSquared(edgeX);
        float v = Float3.Dot(local, edgeY) / Float3.LengthSquared(edgeY);
        if (u is < 0f or > 1f || v is < 0f or > 1f)
            return false;

        float width = _session.Asset?.Quality.Width ?? 1f;
        float height = _session.Asset?.Quality.Height ?? 1f;
        _pendingClickLocal = (Math.Clamp(u, 0f, 1f) * width, (1f - Math.Clamp(v, 0f, 1f)) * height);
        _clickCamera = camera;
        // The probe scissors at the click's NDC position, converted to the render target's pixel
        // space by the pipeline (which owns the actual viewport dimensions — the game view's
        // screenshot size and the render target size can differ, e.g. DPI scaling).
        _pendingClickNdc = (
            (screenPoint.X / MathF.Max(screenSize.X, 1f)) * 2f - 1f,
            1f - (screenPoint.Y / MathF.Max(screenSize.Y, 1f)) * 2f);
        _clickScreenSize = screenSize;

        _clickGate ??= new WebSurfaceClickGate();
        _clickGate.Submit(0, 0, BuildSnapshot(camera));
        return true;
    }

    internal (float X, float Y) ClickNdc => _pendingClickNdc;

    /// <summary>Cancels a pending world click (input cancellation / teardown).</summary>
    public void CancelWorldClick()
    {
        _clickGate?.Cancel();
        UnregisterProbe();
    }

    /// <summary>Advances the pending world click (called by <see cref="WebSurfaceDriver"/> each tick).</summary>
    internal void WorldClickTick(IWebSurfaceBackend? backend)
    {
        if (_clickGate is null || !_clickGate.HasPendingClick)
        {
            UnregisterProbe();
            return;
        }
        if (_clickCamera is null)
            return;

        WebSurfaceClickSnapshot snapshot = BuildSnapshot(_clickCamera);
        // The click's NDC is always known; the pipeline converts it to the viewport pixel.
        bool pixelAvailable = true;
        bool resultAvailable = false;
        bool passed = false;
        if (_activeProbe is not null && XEngine.Runtime.Graphics.Device is { } device)
            resultAvailable = device.TryGetOcclusionQueryResult(_activeProbe.QueryId, out passed);

        WebSurfaceClickGateTick tick = _clickGate.Tick(
            snapshot, pixelAvailable,
            (int)_pendingClickNdc.X, (int)_pendingClickNdc.Y,
            resultAvailable, passed);
        switch (tick.Action)
        {
            case WebSurfaceClickGateAction.IssueProbe:
                UnregisterProbe();
                _activeProbe = new WorldClickProbe(this, tick.QueryId);
                XEngine.Runtime.Rendering.WorldOcclusionProbes.Register(_activeProbe);
                WorldClickIssuedProbes++;
                break;

            case WebSurfaceClickGateAction.Deliver:
                UnregisterProbe();
                DeliverClick(backend);
                WorldClickDelivered++;
                break;

            case WebSurfaceClickGateAction.Drop:
                UnregisterProbe();
                WorldClickDropped++;
                break;
        }
    }

    private void UnregisterProbe()
    {
        if (_activeProbe is not null)
        {
            XEngine.Runtime.Rendering.WorldOcclusionProbes.Unregister(_activeProbe);
            _activeProbe = null;
        }
    }

    private void DeliverClick(IWebSurfaceBackend? backend)
    {
        if (backend is null || _session is null)
            return;
        var input = new WebSurfaceInput(
            () => _session.Asset?.Quality.Width ?? 0,
            () => _session.Asset?.Quality.Height ?? 0,
            backend);
        var (localX, localY) = _pendingClickLocal;
        float width = _session.Asset?.Quality.Width ?? 1f;
        float height = _session.Asset?.Quality.Height ?? 1f;
        input.ForwardPointer(WebSurfacePointerKind.Down, localX, localY, width, height, pointerId: 0, buttons: 1);
        input.ForwardPointer(WebSurfacePointerKind.Up, localX, localY, width, height, pointerId: 0, buttons: 0);
    }

    private WebSurfaceClickSnapshot BuildSnapshot(Camera camera)
    {
        ulong surfaceGeneration = (ulong)(_session?.Pool.Generation ?? 0);
        long projectionStamp = (long)(camera.FieldOfView * 1000f)
            ^ ((long)_clickScreenSize.X << 12) ^ (long)_clickScreenSize.Y;
        long planeStamp = 0;
        if (TryGetPanelWorldQuad(out Float3 corner, out Float3 edgeX, out Float3 edgeY))
        {
            Float3 normal = Float3.Normalize(Float3.Cross(edgeX, edgeY));
            planeStamp = (long)(Float3.Dot(corner, normal) * 8192f)
                ^ ((long)(normal.X * 4096f) << 8) ^ ((long)(normal.Y * 4096f) << 16) ^ ((long)(normal.Z * 4096f) << 24);
        }
        return new WebSurfaceClickSnapshot(surfaceGeneration, camera.InstanceID, projectionStamp, planeStamp);
    }

    /// <summary>Panel quad in world space: the rect's min corner plus its two edge vectors.</summary>
    private bool TryGetPanelWorldQuad(out Float3 corner, out Float3 edgeX, out Float3 edgeY)
    {
        corner = default; edgeX = default; edgeY = default;
        RectTransform? rt = GameObject.RectTransform;
        GameCanvas? canvas = null;
        foreach (XEngine.Runtime.MonoBehaviour component in GameObject.GetComponentsInParent<GameCanvas>(true))
        {
            if (component is GameCanvas found) { canvas = found; break; }
        }
        if (rt == null || canvas is null)
            return false;

        Float4x4 canvasToWorld = canvas.CanvasToWorld;
        Rect rect = rt.ComputedRect;
        corner = Float4x4.TransformPoint(new Float3(rect.Min.X, rect.Min.Y, 0f), canvasToWorld);
        Float3 maxX = Float4x4.TransformPoint(new Float3(rect.Max.X, rect.Min.Y, 0f), canvasToWorld);
        Float3 maxY = Float4x4.TransformPoint(new Float3(rect.Min.X, rect.Max.Y, 0f), canvasToWorld);
        edgeX = maxX - corner;
        edgeY = maxY - corner;
        return Float3.LengthSquared(edgeX) > 1e-8f && Float3.LengthSquared(edgeY) > 1e-8f;
    }

    /// <summary>The model matrix the canvas assigns to this graphic's draw (probe parity with
    /// the regular panel draw).</summary>
    internal Float4x4 CanvasBuildItemModel()
    {
        foreach (XEngine.Runtime.MonoBehaviour component in GameObject.GetComponentsInParent<GameCanvas>(true))
        {
            if (component is GameCanvas found)
                return found.BuildItemModel(this);
        }
        return default;
    }

    internal Float3 GetPanelWorldCenter()
    {
        if (TryGetPanelWorldQuad(out Float3 corner, out Float3 edgeX, out Float3 edgeY))
            return corner + (edgeX + edgeY) * 0.5f;
        return Float4x4.TransformPoint(new Float3(0f, 0f, 0f), CanvasBasisMatrix);
    }

    private Float4x4 CanvasBasisMatrix
    {
        get
        {
            foreach (XEngine.Runtime.MonoBehaviour component in GameObject.GetComponentsInParent<GameCanvas>(true))
            {
                if (component is GameCanvas found)
                    return found.CanvasToWorld;
            }
            return default;
        }
    }

    /// <summary>B7b — the pipeline-facing probe for one pending click. The mesh is the canvas'
    /// baked panel quad (the exact geometry the regular draw uses). The pipeline calls
    /// TryGetProbePixel once per UI world pass: the first call computes the scissor pixel from
    /// the caller's view-projection and records the query; later calls only refresh the tracked
    /// pixel (the gate re-issues when the view moved it).</summary>
    private sealed class WorldClickProbe(WebSurfaceGraphic owner, ulong queryId) : IWorldOcclusionProbe
    {
        public ulong QueryId { get; } = queryId;

        private bool _recorded;

        public Mesh ProbeMesh => owner.CachedMesh ?? Mesh.GetFullscreenQuad();

        public Material ProbeMaterial => WebSurfaceGraphic.SharedWorldDepthMaterial;

        public int ProbePass
        {
            get
            {
                var passes = ProbeMaterial.Shader.GetPassesWithTag("RenderOrder", "UI");
                return passes.Count > 0 ? passes[0] : 0;
            }
        }

        public Float4x4 ProbeModel => owner.CanvasBuildItemModel();

        public bool TryGetProbePixel(in Float4x4 viewProjection, int viewportWidth, int viewportHeight, out int px, out int py)
        {
            // The click's NDC converted into THIS viewer's render-target pixels — the same
            // scissor space the UI world pass draws in.
            px = (int)Math.Round(((owner.ClickNdc.X * 0.5f + 0.5f) * viewportWidth));
            py = (int)Math.Round(((1f - (owner.ClickNdc.Y * 0.5f + 0.5f)) * viewportHeight));
            if (_recorded)
                return false;
            _recorded = true;
            return true;
        }
    }

    /// Presents the newest committed frame, if any. Returns the presented frame number
    /// (ulong.MaxValue when nothing new was available — the previous frame stays on screen,
    /// which is the correct behaviour for a paused/stalled surface).
    /// </summary>
    public ulong Present()
    {
        if (_session is null)
            return ulong.MaxValue;

        using WebSurfaceFrame? frame = _session.TryAcquireLatest();
        if (frame is null)
            return _uploadedFrameNumber;

        EnsureTexture(frame);
        UploadPixels(frame);
        _uploadedGeneration = frame.Generation;
        _uploadedFrameNumber = frame.FrameNumber;
        MarkDirty(UIDirtyFlags.Vertices | UIDirtyFlags.Material);
        return frame.FrameNumber;
    }

    private void EnsureTexture(WebSurfaceFrame frame)
    {
        if (_texture is not null && _uploadedGeneration == frame.Generation
            && _texture.Width == frame.Width && _texture.Height == frame.Height)
        {
            return;
        }

        _texture?.Dispose();
        _texture = new Texture2D((uint)frame.Width, (uint)frame.Height);
        MarkDirty(UIDirtyFlags.Vertices | UIDirtyFlags.Material);
    }

    private void UploadPixels(WebSurfaceFrame frame)
    {
        if (_texture is null)
            return;
        if (frame.Stride == frame.Width * 4)
        {
            _texture.SetData<byte>(frame.Pixels);
            return;
        }

        // Padded rows: copy row-wise into a tight buffer.
        var tight = new byte[frame.Width * frame.Height * 4];
        for (int y = 0; y < frame.Height; y++)
        {
            Buffer.BlockCopy(frame.Pixels, y * frame.Stride, tight, y * frame.Width * 4, frame.Width * 4);
        }
        _texture.SetData<byte>(tight);
    }

    protected override void OnPopulateMesh(VertexHelper vertices)
    {
        unchecked { _panelMeshGeneration++; }
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null)
            return;

        Float2 size = rt.ComputedRect.Size;
        var local = new Rect(
            -rt.Pivot.X * size.X, -rt.Pivot.Y * size.Y,
            (1f - rt.Pivot.X) * size.X, (1f - rt.Pivot.Y) * size.Y);

        // Frames arrive top-left origin (the web contract); the engine samples texture row 0 at
        // the quad's bottom (v=0), so the V axis flips to put the page's top at the quad's top.
        vertices.Builder.AddQuad(local, Color.White,
            new Float2(_uvRect.Min.X, _uvRect.Max.Y),
            new Float2(_uvRect.Max.X, _uvRect.Min.Y));
    }

    public override void PopulateProperties(PropertyState props, in UIContext context)
    {
        base.PopulateProperties(props, context);
        // Straight alpha from the producer: the standard UI material composes with the alpha
        // channel as-is; opaque surfaces render identical (alpha=255).
        props.SetTexture("_MainTex", _texture ?? Texture2D.LoadDefault(DefaultTexture.White));
        props.SetVector("_Tiling", new Float2(1f, 1f));
        props.SetVector("_Offset", Float2.Zero);
    }
}
