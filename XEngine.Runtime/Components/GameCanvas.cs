// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using XEngine.Runtime.UI;

namespace XEngine.Runtime;

/// <summary>
/// Coordinates a UI hierarchy: owns layout, scale, and the destination surface.
/// Does not render anything itself - visible elements are <see cref="UIBehaviour"/>s
/// that produce <see cref="UIRenderItem"/>s into <see cref="Tree"/>, which the
/// pipeline then draws via <see cref="UIRenderTree.CollectFor"/>.
/// </summary>
[AddComponentMenu("UI/Game Canvas")]
[ExecuteAlways]
[ComponentIcon("")] // Image
public class GameCanvas : MonoBehaviour
{
    // ============================================================
    // REMOVED:
    //   private RenderTexture? _renderTexture;     // dead - never used
    //   private bool _initialized = false;          // dead - never read
    //   public  void DrawGUI() { ... }              // replaced by RebuildIfDirty
    //   private void BuildChildren(...)             // replaced by BuildRecursive
    // ============================================================

    /// <summary>When set, GameCanvas uses this size instead of the window framebuffer size.
    /// Used by the editor to render into off-screen render textures.</summary>
    public static Float2? ScreenSizeOverride
    {
        get => CanvasRegistry.ScreenSizeOverride;
        set => CanvasRegistry.ScreenSizeOverride = value;
    }

    /// <summary>
    /// When true, every canvas lays out and maps to world using its <b>world-space</b> representation
    /// (<see cref="ReferenceResolution"/> as the root rect + the GameObject transform), regardless of
    /// its actual <see cref="RenderMode"/>. The scene view enables this so screen-space UI can be
    /// viewed and edited in world space like any other GameObject, independent of screen/RT size.
    /// </summary>
    public static bool EditorWorldSpaceOverride { get; set; }

    /// <summary>Whether this canvas should currently be treated as world-space (its own mode, or the editor override).</summary>
    internal bool UseWorldSpace => EditorWorldSpaceOverride || RenderMode == RenderMode.WorldSpace;

    /// <summary>
    /// Design-pixels per world unit used when the scene view shows a screen-space canvas in 3D.
    /// Overlay canvases are authored in pixels (often 1280×720); mapping those 1:1 to world units
    /// produces a wall the editor camera sits inside. 100 matches typical UI pixels-per-unit.
    /// </summary>
    internal const float EditorOverlayPixelsPerUnit = 100f;

    // ----------------------------------------------------------------
    // CHANGED: every public field that affects layout is now a property
    //          with a backing field and dirty-marking setter.
    // ----------------------------------------------------------------

    [SerializeField] private RenderMode _renderMode = RenderMode.ScreenSpaceOverlay;
    /// <summary>The rendering mode of this GameCanvas.</summary>
    public RenderMode RenderMode
    {
        get => _renderMode;
        set => SetField(ref _renderMode, value, UIDirtyFlags.Layout | UIDirtyFlags.Hierarchy);
    }

    [SerializeField] private float _scaleFactor = 1f;
    /// <summary>Global scale factor applied to all elements.</summary>
    public float ScaleFactor
    {
        get => _scaleFactor;
        set => SetField(ref _scaleFactor, value, UIDirtyFlags.Layout);
    }

    [SerializeField] private bool _pixelPerfect;
    /// <summary>
    /// When true, overlay UI positions and verts snap to whole framebuffer pixels at
    /// <see cref="ScaleFactor"/>. World-space canvases ignore this flag.
    /// </summary>
    public bool PixelPerfect
    {
        get => _pixelPerfect;
        set => SetField(ref _pixelPerfect, value, UIDirtyFlags.Layout | UIDirtyFlags.Vertices);
    }

    /// <summary>True when overlay pixel snapping is currently applied.</summary>
    public bool PixelPerfectActive => CanvasPixelSnap.Applies(_pixelPerfect, UseWorldSpace);

    /// <summary>Snaps a canvas design-space point onto the pixel grid when <see cref="PixelPerfectActive"/>.</summary>
    public Float2 PixelAdjustPoint(Float2 designPoint)
        => PixelPerfectActive ? CanvasPixelSnap.Snap(designPoint, ScaleFactor) : designPoint;

    [SerializeField] private Camera? _renderCamera;
    /// <summary>
    /// Optional camera whose target RenderTexture supplies the canvas pixel size when
    /// <see cref="ScreenSizeOverride"/> is unset. Overlay drawing still goes through the UI queue.
    /// </summary>
    public Camera? RenderCamera
    {
        get => _renderCamera;
        set => SetField(ref _renderCamera, value, UIDirtyFlags.Layout);
    }

    [SerializeField] private int _targetRotationDegrees;
    /// <summary>
    /// Clockwise framebuffer rotation in degrees. 90 and 270 swap width/height so HUD relayouts
    /// for portrait/landscape. The OS size swap is usually already in the framebuffer; this is
    /// the explicit/test hook.
    /// </summary>
    public int TargetRotationDegrees
    {
        get => _targetRotationDegrees;
        set => SetField(ref _targetRotationDegrees, CanvasTargetSize.NormalizeDegrees(value), UIDirtyFlags.Layout);
    }

    [SerializeField] private int _sortOrder;
    /// <summary>Sort order relative to other screen-space canvases. Higher = on top.</summary>
    public int SortOrder
    {
        get => _sortOrder;
        set => SetField(ref _sortOrder, value, UIDirtyFlags.Hierarchy);
    }

    [SerializeField] private ScaleMode _uiScaleMode = ScaleMode.ConstantPixelSize;
    public ScaleMode UIScaleMode { get => _uiScaleMode; set => SetField(ref _uiScaleMode, value, UIDirtyFlags.Layout); }

    [SerializeField] private Float2 _referenceResolution = new(1280f, 720f);
    public Float2 ReferenceResolution { get => _referenceResolution; set => SetField(ref _referenceResolution, value, UIDirtyFlags.Layout); }

    [SerializeField] private float _matchWidthOrHeight = 0.5f;
    public float MatchWidthOrHeight { get => _matchWidthOrHeight; set => SetField(ref _matchWidthOrHeight, value, UIDirtyFlags.Layout); }

    [SerializeField] private ScreenMatchMode _screenMatchMode = ScreenMatchMode.MatchWidthOrHeight;
    public ScreenMatchMode ScreenMatchMode { get => _screenMatchMode; set => SetField(ref _screenMatchMode, value, UIDirtyFlags.Layout); }

    // ----------------------------------------------------------------
    // Shared default UI material - referenced by UIBehaviour.GetMaterial
    // ----------------------------------------------------------------

    private static Material? s_sharedUIMaterial;
    /// <summary>Shared <see cref="Material"/> using the <c>Default/DefaultUI</c> shader.
    /// Lazy-allocated; reused across every UI element that doesn't override
    /// <see cref="UIBehaviour.GetMaterial"/>.</summary>
    public static Material SharedUIMaterial
        => s_sharedUIMaterial ??= new Material(Shader.LoadDefault(DefaultShader.DefaultUI));

    private static Material? s_sharedTextMaterial;
    /// <summary>Shared <see cref="Material"/> using the <c>Default/DefaultText</c> shader, which
    /// reconstructs sharp coverage from the single-channel SDF glyph atlas. Reused by every text
    /// element (UI <c>TextComponent</c> and the 3D <c>TextMeshComponent</c>) that doesn't override
    /// its material.</summary>
    public static Material SharedTextMaterial
        => s_sharedTextMaterial ??= new Material(Shader.LoadDefault(DefaultShader.DefaultText));

    // ----------------------------------------------------------------
    // NEW: render tree + dirty state
    // ----------------------------------------------------------------

    // Read-only auto-property: not serialized by XEngine.Echo.
    internal UIRenderTree Tree { get; } = new();
    [SerializeIgnore] private bool _isDirty = true;
    [SerializeIgnore] private UIDirtyFlags _aggregateDirty = UIDirtyFlags.All;
    [SerializeIgnore] private long _nonConvergedLayoutFrame = -1;
    [SerializeIgnore] private readonly List<CanvasRenderer> _registeredRenderers = new(4);
    [SerializeIgnore] private readonly List<GameObject> _layoutRoots = new(4);
    [SerializeIgnore] private bool _hasLaidOut;
    [SerializeIgnore] private readonly List<MonoBehaviour> _layoutComponentScratch = new(4);
    // Separate from the layout scratch: the render walk holds its copy across EnsureBaked calls, which
    // can run layout code that would otherwise refill the buffer underneath it.
    [SerializeIgnore] private readonly List<UIBehaviour> _renderComponentScratch = new(4);

    /// <summary>
    /// Size of the surface this canvas was last built against (in real pixels).
    /// Tracked so that a resolution change - typical when the editor's game viewport resizes
    /// or when the rendering camera switches between targets - automatically forces a rebuild.
    /// Without this, layout uses the previous frame's resolution while the projection uses
    /// the current one, putting the UI off-center and at the wrong scale.
    /// </summary>
    [SerializeIgnore] private Float2 _lastBuildSize = Float2.Zero;
    [SerializeIgnore] private bool _lastBuiltWorldSpace;

    /// <summary>The canvas's root rect (design pixels) from the last rebuild. The canvas itself has no
    /// RectTransform; this is its layout extent, used by children and the bounds gizmo.</summary>
    [SerializeIgnore] private Rect _rootRect;
    public Rect RootRect => _rootRect;

    /// <summary>Called by descendants (or by property setters above) to request a rebuild.</summary>
    public void MarkDirty(UIDirtyFlags flags)
    {
        _aggregateDirty |= flags;
        _isDirty = true;
        CanvasRegistry.NotifyDirty(this);
    }

    /// <summary>
    /// Backing-field setter for this canvas's properties: assigns only on a real change and
    /// marks <paramref name="flags"/> dirty when it does. Mirrors <see cref="UIBehaviour.SetField{T}"/>
    /// so code, inspector edits, and undo all funnel value-change detection through one place.
    /// </summary>
    private bool SetField<T>(ref T field, T value, UIDirtyFlags flags)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        MarkDirty(flags);
        return true;
    }

    /// <summary>
    /// Float overload that compares with an epsilon. Layout-driving values (scale factor,
    /// pixels-per-unit) are recomputed every rebuild from float math; an exact compare would
    /// let sub-ULP jitter re-trigger rebuilds, so we treat near-equal values as unchanged.
    /// </summary>
    private bool SetField(ref float field, float value, UIDirtyFlags flags, float epsilon = 1e-6f)
    {
        if (Maths.Abs(field - value) < epsilon) return false;
        field = value;
        MarkDirty(flags);
        return true;
    }

    // ============================================================
    // Lifecycle (mostly unchanged from the original)
    // ============================================================

    /// <summary>Ensures this GameCanvas's GameObject and all descendants use RectTransform.</summary>
    public override void OnAddedToScene()
    {
        // The canvas is the layout root and needs no RectTransform itself; its children do.
        EnsureChildRectTransforms(GameObject);
        MarkDirty(UIDirtyFlags.All);   // first build is always dirty
        CanvasRegistry.Register(this);
    }

    // CHANGED: OnEnable / OnDisable bodies - both were empty in the original
    public override void OnEnable()
    {
        MarkDirty(UIDirtyFlags.All);
        CanvasRegistry.Register(this);
    }

    public override void OnDisable() => CanvasRegistry.Unregister(this);

    public override void OnRemovedFromScene() => CanvasRegistry.Unregister(this);

    /// <summary>
    /// Drops baked UI meshes after a GPU device loss so the next <see cref="RebuildIfDirty"/>
    /// uploads geometry to the restored device.
    /// </summary>
    internal void DiscardGpuMeshes()
    {
        DiscardGpuMeshesRecursive(GameObject);
        MarkDirty(UIDirtyFlags.All);
    }

    private void DiscardGpuMeshesRecursive(GameObject node)
    {
        _layoutComponentScratch.Clear();
        node.GetComponents(_layoutComponentScratch);
        for (int i = 0; i < _layoutComponentScratch.Count; i++)
        {
            if (_layoutComponentScratch[i] is not UIBehaviour ui) continue;
            bool hadMesh = ui.CachedMesh != null || ui.CachedSecondaryMesh != null;
            if (ui.CachedMesh != null)
            {
                ui.CachedMesh.OnDispose();
                ui.CachedMesh = null;
            }
            if (ui.CachedSecondaryMesh != null)
            {
                ui.CachedSecondaryMesh.OnDispose();
                ui.CachedSecondaryMesh = null;
            }
            if (hadMesh)
            {
                ui.DirtyFlags |= UIDirtyFlags.Vertices;
                ui.LastBakeSize = new Float2(float.NaN, float.NaN);
            }
        }

        List<GameObject> children = node.Children;
        for (int i = 0; i < children.Count; i++)
            DiscardGpuMeshesRecursive(children[i]);
    }

    public void RegisterRenderer(CanvasRenderer renderer)
    {
        for (int i = 0; i < _registeredRenderers.Count; i++)
            if (ReferenceEquals(_registeredRenderers[i], renderer)) return;
        _registeredRenderers.Add(renderer);
        MarkDirty(UIDirtyFlags.Hierarchy);
    }

    public void UnregisterRenderer(CanvasRenderer renderer)
    {
        if (_registeredRenderers.Remove(renderer))
            MarkDirty(UIDirtyFlags.Hierarchy);
    }

    internal void AppendRegisteredRendererNodes(UISurface surface, List<CanvasRenderNode> destination)
    {
        for (int i = 0; i < _registeredRenderers.Count; i++)
        {
            CanvasRenderer renderer = _registeredRenderers[i];
            renderer.PrepareForCanvas(this, i);
            CanvasRenderNode node = renderer.RenderNode;
            if (node.IsAttached && node.Surface == surface)
                destination.Add(node);
            CanvasRenderNode unwind = renderer.UnwindNode;
            if (unwind.IsAttached && unwind.Surface == surface)
                destination.Add(unwind);
        }
    }

    internal void AppendRegisteredRendererNodes(UISurface surface, List<IRenderable> destination)
    {
        for (int i = 0; i < _registeredRenderers.Count; i++)
        {
            CanvasRenderer renderer = _registeredRenderers[i];
            renderer.PrepareForCanvas(this, i);
            CanvasRenderNode node = renderer.RenderNode;
            if (node.IsAttached && node.Surface == surface)
                destination.Add(node);
            CanvasRenderNode unwind = renderer.UnwindNode;
            if (unwind.IsAttached && unwind.Surface == surface)
                destination.Add(unwind);
        }
    }

    internal IReadOnlyList<CanvasRenderer> RegisteredRenderers => _registeredRenderers;

    /// <summary>Marks hierarchy topology dirty without exposing the legacy dirty enum.</summary>
    public void MarkHierarchyDirty() => MarkDirty(UIDirtyFlags.Hierarchy);

    /// <summary>
    /// Collects this canvas through the Runtime host contract. The old tree remains the
    /// compatibility producer; callers receive only neutral CanvasRenderNode instances.
    /// </summary>
    public int CollectRenderNodes(UISurface surface, List<CanvasRenderNode> destination)
    {
        if (!EnabledInHierarchy || UIRenderTree.ToSurface(RenderMode) != surface) return 0;
        int before = destination.Count;
        RebuildIfDirty();
        Tree.RefreshTransforms();
        Tree.AppendNodes(destination);
        AppendRegisteredRendererNodes(surface, destination);
        int added = destination.Count - before;
        RenderStats.RecordCanvasCollection(added);
        return added;
    }

    // Update() is intentionally not overridden. ScaleFactor must be computed against the
    // active render-target size, which is only known inside RebuildIfDirty (where the
    // pipeline has pushed GameCanvas.ScreenSizeOverride). Computing it here would use the
    // stale framebuffer size and fight the per-camera value.

    /// <summary>Resolves the effective screen-pixel size for this canvas, in real pixels.</summary>
    private Float2 ResolveScreenSize()
    {
        Float2? cameraTarget = null;
        if (_renderCamera?.Target is { } rt && rt.IsValid() && rt.Width > 0 && rt.Height > 0)
            cameraTarget = new Float2(rt.Width, rt.Height);

        Float2 framebuffer = Float2.Zero;
        if (Window.InternalWindow != null)
            framebuffer = new Float2(Window.InternalWindow.FramebufferSize.X, Window.InternalWindow.FramebufferSize.Y);

        return CanvasTargetSize.Resolve(ScreenSizeOverride, cameraTarget, framebuffer, _targetRotationDegrees);
    }

    // ============================================================
    // NEW: WorldSpace IRenderable plumbing
    // ============================================================

    /// <summary>
    /// For <see cref="RenderMode.WorldSpace"/> canvases, adds every item in <see cref="Tree"/>
    /// to the scene's main renderable list so they participate in #13 Transparent + UI passes.
    /// Overlay/Camera canvases ignore this hook - they are pulled by
    /// <see cref="UIRenderTree.CollectFor"/> from the pipeline directly.
    /// </summary>
    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> _)
    {
        if (RenderMode != RenderMode.WorldSpace) return;
        RebuildIfDirty();
        Tree.RefreshTransforms();
        Tree.AppendNodes(renderables);
        AppendRegisteredRendererNodes(UISurface.World, renderables);
    }

    // ============================================================
    // NEW: rebuild driver - replaces the old DrawGUI
    // ============================================================

    /// <summary>
    /// Walks the hierarchy under this canvas, computes layout, generates meshes for any
    /// <see cref="UIBehaviour"/> with dirty vertices, and produces a fresh <see cref="Tree"/>.
    /// Cheap when <see cref="_isDirty"/> is false (single boolean check + return).
    /// </summary>
    public void RebuildIfDirty()
    {
        // Detect a render-target size change (window resize, editor viewport change, switch
        // between cameras with different RT sizes). The pipeline pushes the active surface size
        // via GameCanvas.ScreenSizeOverride before calling here, so a mismatch forces a rebuild.
        Float2 currentSize = ResolveScreenSize();
        bool worldSpaceChanged = _lastBuiltWorldSpace != UseWorldSpace;
        bool rootRectChanged = !_lastBuildSize.Equals(currentSize) || worldSpaceChanged;
        if (rootRectChanged)
        {
            _isDirty = true;
            _lastBuildSize = currentSize;
            _lastBuiltWorldSpace = UseWorldSpace;
        }

        if (!_isDirty || _nonConvergedLayoutFrame == Time.FrameCount) return;

        // Recompute scale factor against the current screen size *before* layout - Update()
        // can't do this reliably because it runs without ScreenSizeOverride set. Assign the backing
        // field directly: going through the property setter would MarkDirty(Layout) and re-trigger
        // this rebuild every frame in ScaleWithScreenSize mode.
        _scaleFactor = ComputeScaleFactor();

        Rect rootRect = ComputeRootRect();
        _rootRect = rootRect; // the canvas has no RectTransform; children lay out against this directly

        bool needsLayout = rootRectChanged
            || !_hasLaidOut
            || (_aggregateDirty & (UIDirtyFlags.Layout | UIDirtyFlags.Hierarchy)) != 0;
        if (needsLayout)
        {
            CanvasUpdateDispatcher.Execute(this, CanvasUpdatePhase.PreLayout);

            // An already laid-out canvas can rebuild only the package-derived highest roots. Full
            // traversal remains mandatory after topology/root-rect changes because descendants may
            // not yet have a valid ComputedRect for a partial subtree to anchor against.
            bool rebuildLayoutRoots = !rootRectChanged
                && _hasLaidOut
                && (_aggregateDirty & UIDirtyFlags.Layout) != 0
                && (_aggregateDirty & UIDirtyFlags.Hierarchy) == 0
                && CanvasUpdateDispatcher.TryCollectLayoutRoots(this, _layoutRoots);
            if ((_aggregateDirty & UIDirtyFlags.Layout) != 0 || !_hasLaidOut || rootRectChanged)
                RenderStats.RecordDirtyLayoutRoot(rebuildLayoutRoots ? _layoutRoots.Count : 1);

            // Fitters can change SizeDelta while their parent is calculating preferred sizes. Permit
            // two deterministic child-to-parent/parent-to-child passes to converge, then stop and keep
            // a diagnostic instead of allowing a user-authored cycle to rebuild forever.
            ulong lastVersion = RectTransform.GlobalLayoutVersion;
            bool converged = false;
            int pass;
            for (pass = 0; pass < 2; pass++)
            {
                CanvasUpdateDispatcher.Execute(this, CanvasUpdatePhase.LayoutCalculation);
                if (rebuildLayoutRoots)
                    BuildLayoutRoots(rootRect);
                else
                    BuildLayoutRecursive(GameObject, rootRect);
                CanvasUpdateDispatcher.Execute(this, CanvasUpdatePhase.LayoutControl);
                RenderStats.RecordLayoutRebuild();
                ulong currentVersion = RectTransform.GlobalLayoutVersion;
                if (currentVersion == lastVersion)
                {
                    converged = true;
                    break;
                }
                lastVersion = currentVersion;
            }
            if (!converged)
            {
                if (!CanvasUpdateDispatcher.TryReportNonConvergentLayout(this, pass))
                    Runtime.Debug.LogWarning($"[XGUI] Layout did not converge after 2 passes on Canvas '{GameObject.Name}'. Check fitter/group dependencies.");
                // Keep the canvas dirty for the next frame, but never spin through another pair of
                // passes while the same frame is collecting multiple UI surfaces.
                _isDirty = true;
                _aggregateDirty |= UIDirtyFlags.Layout;
                _nonConvergedLayoutFrame = Time.FrameCount;
            }
            else
            {
                _isDirty = false;
                _aggregateDirty = UIDirtyFlags.None;
                _nonConvergedLayoutFrame = -1;
                _hasLaidOut = true;
            }
        }
        else
        {
            _isDirty = false;
            _aggregateDirty = UIDirtyFlags.None;
            _nonConvergedLayoutFrame = -1;
        }

        CanvasUpdateDispatcher.Execute(this, CanvasUpdatePhase.ClipUpdate);
        Tree.Clear();
        for (int i = 0; i < _registeredRenderers.Count; i++)
            _registeredRenderers[i].ClearStencilUnwind();
        int dfs = 0;
        BuildRenderRecursive(GameObject, rootRect, UIContext.Default, canvasScissor: null, activeClip: null,
            activeStencilDepth: 0, ref dfs);
        CanvasUpdateDispatcher.Execute(this, CanvasUpdatePhase.GraphicRebuild);
        CanvasUpdateDispatcher.Execute(this, CanvasUpdatePhase.MaterialUpdate);
        Tree.SortHierarchical();

    }

    private Rect ComputeRootRect()
    {
        // World-space canvases have a fixed design size (their ReferenceResolution) and don't track the
        // screen. Screen-space canvases fill the surface (in design pixels, after the scale factor).
        if (UseWorldSpace)
            return new Rect(0, 0, Maths.Max(ReferenceResolution.X, 1f), Maths.Max(ReferenceResolution.Y, 1f));

        Float2 surfaceSize = ResolveScreenSize();
        float rawW = surfaceSize.X;
        float rawH = surfaceSize.Y;
        float screenW = rawW / Maths.Max(ScaleFactor, 0.001f);
        float screenH = rawH / Maths.Max(ScaleFactor, 0.001f);
        return new Rect(0, 0, screenW, screenH);
    }

    private void BuildLayoutRecursive(GameObject parent, Rect parentRect)
    {
        ILayoutGroupHost? layout = FindActiveLayoutGroup(parent);
        bool arrangingChildren = layout != null;
        if (layout != null) layout.Arrange(parentRect);

        foreach (GameObject child in parent.Children)
        {
            if (!child.EnabledInHierarchy) continue;

            // Nested GameCanvas - skip; it manages its own tree.
            GameCanvas? nested = child.GetComponent<GameCanvas>();
            if (nested != null && nested != this) continue;

            Rect childRect = BuildLayoutChild(child, parentRect, arrangingChildren);

            BuildLayoutRecursive(child, childRect);
        }
    }

    private void BuildLayoutRoots(Rect rootRect)
    {
        for (int i = 0; i < _layoutRoots.Count; i++)
        {
            GameObject root = _layoutRoots[i];
            if (!root.EnabledInHierarchy) continue;

            if (ReferenceEquals(root, GameObject))
            {
                BuildLayoutRecursive(GameObject, rootRect);
                return;
            }

            if (!root.IsChildOf(GameObject)) continue;
            if (root.GetComponent<GameCanvas>() is { } nested && !ReferenceEquals(nested, this)) continue;

            Rect parentRect = root.Parent?.RectTransform?.ComputedRect ?? rootRect;
            Rect layoutRootRect = BuildLayoutChild(root, parentRect, parentArrangesChildren: false);
            BuildLayoutRecursive(root, layoutRootRect);
        }
    }

    private Rect BuildLayoutChild(GameObject child, Rect parentRect, bool parentArrangesChildren)
    {
        if (child.RectTransform is not { } rt) return parentRect;

        bool ignoredByLayout = false;
        _layoutComponentScratch.Clear();
        child.GetComponents(_layoutComponentScratch);

        if (parentArrangesChildren)
        {
            for (int i = 0; i < _layoutComponentScratch.Count; i++)
            {
                MonoBehaviour candidate = _layoutComponentScratch[i];
                if (candidate is ILayoutElementHost element && candidate.EnabledInHierarchy && element.IgnoreLayout)
                {
                    ignoredByLayout = true;
                    break;
                }
            }
        }

        // A parent group owns the final child rect, but self controllers still need a chance to
        // update their authored size. The following bounded layout pass then lets that new size
        // feed back into the group's preferred-size calculation without a third traversal.
        ApplyLayoutControllers(parentRect);

        if (parentArrangesChildren && !ignoredByLayout)
            return rt.ComputedRect;

        return rt.ComputeRect(parentRect);
    }

    private ILayoutGroupHost? FindActiveLayoutGroup(GameObject gameObject)
    {
        _layoutComponentScratch.Clear();
        gameObject.GetComponents(_layoutComponentScratch);
        for (int i = 0; i < _layoutComponentScratch.Count; i++)
        {
            MonoBehaviour candidate = _layoutComponentScratch[i];
            if (candidate is ILayoutGroupHost group && candidate.EnabledInHierarchy)
                return group;
        }
        return null;
    }

    private void ApplyLayoutControllers(in Rect parentRect)
    {
        for (int i = 0; i < _layoutComponentScratch.Count; i++)
        {
            if (_layoutComponentScratch[i] is UIBehaviour controller && controller.EnabledInHierarchy)
                controller.ApplyLayout(parentRect);
        }
    }

    private void BuildRenderRecursive(GameObject parent, Rect parentRect, UIContext ctx, Rect? canvasScissor,
        UIClip? activeClip, uint activeStencilDepth, ref int dfsIndex)
    {
        foreach (GameObject child in parent.Children)
        {
            if (!child.EnabledInHierarchy) continue;

            // Nested GameCanvas - skip; it manages its own tree.
            GameCanvas? nested = child.GetComponent<GameCanvas>();
            if (nested != null && nested != this) continue;

            UIContext childCtx = ctx;
            CanvasGroup? grp = child.GetComponent<CanvasGroup>();
            if (grp != null && grp.EnabledInHierarchy)
                childCtx = grp.ApplyTo(ctx);

            Rect childRect = child.RectTransform?.ComputedRect ?? parentRect;

            // ---- RectMask: coarse AABB cull (canvas-space rect) + precise per-fragment clip. ----
            Rect? childScissor = canvasScissor;
            UIClip? childClip = activeClip;
            RectMask? rectMask = child.GetComponent<RectMask>();
            if (rectMask != null && rectMask.EnabledInHierarchy && child.RectTransform != null)
            {
                Rect mr = rectMask.GetClipRectInCanvasPixels();
                childScissor = canvasScissor is null ? mr : IntersectRect(canvasScissor.Value, mr);
                // Empty scissor -> whole subtree contributes nothing. Skip it.
                if (childScissor!.Value.Size.X <= 0f || childScissor.Value.Size.Y <= 0f)
                    continue;
                // The innermost mask supplies the shader clip; nested masks still intersect for the cull.
                childClip = ComputeClip(rectMask);
            }

            // Coarse cull: if the layout rect can't intersect the active mask rect, skip the subtree.
            if (childScissor is { } cs && !RectsIntersect(cs, childRect))
                continue;

            CanvasRenderer? hostRenderer = child.GetComponent<CanvasRenderer>();

            // Copy the components out before walking them: baking a graphic below can attach a
            // component (a CanvasRenderer for a first-time graphic), which would invalidate a lazy
            // enumerator over the GameObject's live component list.
            child.GetComponents(_renderComponentScratch);

            ICanvasStencilMask? stencilMask = null;
            bool hasStencilMaskGraphic = false;
            for (int i = 0; i < _renderComponentScratch.Count; i++)
            {
                UIBehaviour candidate = _renderComponentScratch[i];
                if (!candidate.EnabledInHierarchy) continue;
                if (candidate is ICanvasStencilMask mask) stencilMask = mask;
                if (candidate.IsGraphic) hasStencilMaskGraphic = true;
            }

            // A mask writes a new value only when its GameObject has a package Graphic whose
            // CanvasRenderer can submit the same mesh. A malformed mask remains non-destructive:
            // descendants continue with the parent stencil state instead of becoming invisible.
            bool writesStencil = stencilMask != null && hostRenderer != null && hasStencilMaskGraphic
                && activeStencilDepth < byte.MaxValue;
            uint childStencilDepth = writesStencil ? activeStencilDepth + 1 : activeStencilDepth;

            // (Re)bake every UIBehaviour that produces geometry, then add a UIRenderItem.
            bool rendererPrepared = false;
            for (int i = 0; i < _renderComponentScratch.Count; i++)
            {
                UIBehaviour ui = _renderComponentScratch[i];
                if (!ui.EnabledInHierarchy) continue;

                ui.SetRenderContext(childCtx);
                EnsureBaked(ui, childCtx);
                long sortKey = BuildSortKey(dfsIndex);
                // A package Graphic owns a CanvasRenderer and is submitted through the
                // neutral host below. Do not also emit the legacy compatibility item or
                // the same primitive would be drawn twice. Legacy UI components have no
                // CanvasRenderer and continue through the old pooled tree unchanged.
                if (hostRenderer != null && ui.IsGraphic)
                {
                    if (!rendererPrepared)
                    {
                        CanvasClipState clipState = BuildCanvasClipState(childClip, activeStencilDepth,
                            writesStencil, childStencilDepth, stencilMask?.ShowMaskGraphic != true);
                        hostRenderer.SetClipState(clipState);
                        hostRenderer.SetHierarchySortKey(sortKey);
                        hostRenderer.SetRaycastState(childCtx.BlocksRaycasts, childScissor);
                        hostRenderer.SetRenderContext(childCtx);
                        UIRaycastRegistry.UpdateState(ui, sortKey, childCtx.BlocksRaycasts, clipState, childScissor);
                        rendererPrepared = true;
                        dfsIndex++;
                    }
                    continue;
                }
                if (ui.CachedMesh is { } mesh)
                    EmitItem(ui, mesh, dfsIndex++, childClip, childCtx, secondary: false);
                if (ui.CachedSecondaryMesh is { } secondaryMesh && ui.GetSecondaryMaterial() is { } secondaryMat)
                    EmitItem(ui, secondaryMesh, dfsIndex++, childClip, childCtx, secondary: true, secondaryMat);
                if (ui.IsRaycastCandidate)
                {
                    CanvasClipState clipState = BuildCanvasClipState(childClip, activeStencilDepth,
                        writesStencil, childStencilDepth, stencilMask?.ShowMaskGraphic != true);
                    UIRaycastRegistry.UpdateState(ui, sortKey, childCtx.BlocksRaycasts, clipState, childScissor);
                    if (ui.CachedMesh == null && ui.CachedSecondaryMesh == null)
                        dfsIndex++;
                }
            }

            BuildRenderRecursive(child, childRect, childCtx, childScissor, childClip, childStencilDepth, ref dfsIndex);

            if (writesStencil && hostRenderer != null)
            {
                CanvasClipState rectState = childClip is { } rect
                    ? CanvasClipState.Rect(BuildItemModel(rect.Source).Invert(), rect.Rect,
                        rect.Radius, rect.Softness)
                    : CanvasClipState.None;
                CanvasClipState unwindState = CanvasClipState.StencilUnwind(activeStencilDepth,
                    childStencilDepth, rectState);
                hostRenderer.PrepareStencilUnwind(this, BuildSortKey(dfsIndex++), unwindState);
            }
        }
    }

    private CanvasClipState BuildCanvasClipState(UIClip? clip, uint activeStencilDepth,
        bool writesStencil, uint writeDepth, bool suppressWriterColor)
    {
        CanvasClipState rectState = clip is { } rect
            ? CanvasClipState.Rect(BuildItemModel(rect.Source).Invert(), rect.Rect, rect.Radius, rect.Softness)
            : CanvasClipState.None;
        if (writesStencil)
            return CanvasClipState.StencilWrite(writeDepth, activeStencilDepth, suppressWriterColor, rectState);
        return activeStencilDepth > 0 ? CanvasClipState.WithStencil(rectState, activeStencilDepth) : rectState;
    }

    /// <summary>Builds the shader clip region for a <see cref="RectMask"/>: its rect in the mask's own
    /// pivot-centered local pixel space (the space the item's inverse-mask matrix maps into) plus radius
    /// and softness. The world->local matrix itself is derived per item from the mask's model.</summary>
    private static UIClip ComputeClip(RectMask mask)
    {
        RectTransform rt = mask.GameObject.RectTransform!;
        Rect cr = rt.ComputedRect;
        Float2 pivot = rt.Pivot;
        float w = cr.Size.X, h = cr.Size.Y;
        Float4 pad = mask.Padding;   // (left, top, right, bottom)

        float minX = -pivot.X * w + pad.X;
        float maxX = (1f - pivot.X) * w - pad.Z;
        float minY = -pivot.Y * h + pad.W;
        float maxY = (1f - pivot.Y) * h - pad.Y;

        float radius = Maths.Clamp(mask.CornerRadius, 0f, 0.5f * Maths.Min(maxX - minX, maxY - minY));
        return new UIClip(mask, new Float4(minX, minY, maxX, maxY), radius, mask.Softness);
    }

    private void EnsureBaked(UIBehaviour ui, in UIContext childCtx)
    {
        // The baked geometry is a function of Vertices-dirty properties and the laid-out size.
        // Inherited CanvasGroup alpha is applied by the renderer property block and must not force
        // a mesh rebuild or text shaping pass. Pixel-perfect overlay snapping also keys off scale
        // so a scaler change rebakes verts onto the new pixel grid.
        Float2 size = ui.GameObject.RectTransform?.ComputedRect.Size ?? Float2.Zero;
        float bakeScale = PixelPerfectActive ? ScaleFactor : 0f;
        bool needsBake = ui.CachedMesh is null
                      || (ui.DirtyFlags & UIDirtyFlags.Vertices) != 0
                      || !ui.LastBakeSize.Equals(size)
                      || ui.LastBakePixelScale != bakeScale;
        if (!needsBake) return;

        UIMeshBuilder builder = UIMeshBuilder.Rent();
        try
        {
            if (ui.IsGraphic)
                RenderStats.RecordGraphicRebuild();
            if (PixelPerfectActive)
                builder.SetPixelSnapScale(ScaleFactor);
            ui.GenerateMesh(builder, childCtx);
            if (builder.IsEmpty)
            {
                // Element produced no geometry (e.g. empty text): drop the old mesh, disposing its
                // GPU buffers rather than orphaning them.
                ui.CachedMesh?.OnDispose();
                ui.CachedMesh = null;
            }
            else
            {
                ui.CachedMesh ??= new Mesh();
                builder.Bake(ui.CachedMesh);
            }
        }
        finally { UIMeshBuilder.Return(builder); }

        UIMeshBuilder secondaryBuilder = UIMeshBuilder.Rent();
        try
        {
            if (PixelPerfectActive)
                secondaryBuilder.SetPixelSnapScale(ScaleFactor);
            ui.GenerateSecondaryMesh(secondaryBuilder, childCtx);
            if (secondaryBuilder.IsEmpty || ui.GetSecondaryMaterial() == null)
            {
                ui.CachedSecondaryMesh?.OnDispose();
                ui.CachedSecondaryMesh = null;
            }
            else
            {
                ui.CachedSecondaryMesh ??= new Mesh();
                secondaryBuilder.Bake(ui.CachedSecondaryMesh);
            }
        }
        finally { UIMeshBuilder.Return(secondaryBuilder); }

        ui.DirtyFlags &= ~UIDirtyFlags.Vertices;
        ui.LastBakeSize = size;
        ui.LastBakePixelScale = bakeScale;
        ui.OnCanvasRebuildCompleted(UIDirtyFlags.Vertices);
    }

    private void EmitItem(UIBehaviour ui, Mesh mesh, int dfsIndex, UIClip? clip, in UIContext context,
        bool secondary = false, Material? secondaryMaterial = null)
    {
        UIRenderItem item = Tree.RentItem();
        item.Initialize(
            owner:    ui,
            canvas:   this,
            mesh:     mesh,
            material: secondary ? secondaryMaterial! : ui.GetMaterial(),
            model:    BuildItemModel(ui),
            sortKey:  BuildSortKey(dfsIndex),
            surface:  UIRenderTree.ToSurface(RenderMode),
            clip:     clip,
            context:  context,
            useSecondaryProperties: secondary);
        Tree.Add(item);
    }

    /// <summary>
    /// Global draw-order key: <c>SortOrder</c> (dominant) then a stable per-canvas discriminator then
    /// the depth-first index. A 64-bit key gives <c>SortOrder</c> its own high field (no more sign-bit
    /// overflow past 127) and 21 bits each for the canvas id and DFS index. The canvas discriminator
    /// keeps two equal-<c>SortOrder</c> canvases as contiguous layers instead of interleaving them.
    /// </summary>
    internal long BuildSortKey(int dfsIndex)
    {
        long canvasDisc = InstanceID & 0x1FFFFF;
        long dfs = (uint)dfsIndex & 0x1FFFFF;
        return ((long)SortOrder << 42) + (canvasDisc << 21) + dfs;
    }

    private static Rect IntersectRect(Rect a, Rect b)
    {
        float minX = MathF.Max(a.Min.X, b.Min.X);
        float minY = MathF.Max(a.Min.Y, b.Min.Y);
        float maxX = MathF.Min(a.Max.X, b.Max.X);
        float maxY = MathF.Min(a.Max.Y, b.Max.Y);
        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;
        return new Rect(minX, minY, maxX, maxY);
    }

    private static bool RectsIntersect(Rect a, Rect b)
        => a.Max.X > b.Min.X && b.Max.X > a.Min.X && a.Max.Y > b.Min.Y && b.Max.Y > a.Min.Y;

    /// <summary>
    /// Base canvas -> world transform without per-element pivot / rotation / scale. Per-mode:
    ///   - WorldSpace -> <see cref="Transform.LocalToWorldMatrix"/> (design pixels map 1:1 to world units)
    ///   - ScreenSpaceOverlay -> scale(<see cref="ScaleFactor"/>)
    /// </summary>
    /// <remarks>
    /// Exposed so the scene-view editor and gizmos stay in sync with the actual rendering by
    /// consuming the same matrix.
    /// </remarks>
    public Float4x4 CanvasToWorld => UseWorldSpace
        ? OverlayWorldMatrix
        : Float4x4.CreateScale(Maths.Max(ScaleFactor, 0.001f));

    private Float4x4 OverlayWorldMatrix
    {
        get
        {
            // True WorldSpace canvases map design pixels 1:1 through the transform.
            // Scene-view overlay uses 100 px/unit so a 1280×720 HUD is ~13 units across.
            if (EditorWorldSpaceOverride && RenderMode != RenderMode.WorldSpace)
                return Transform.LocalToWorldMatrix * Float4x4.CreateScale(1f / EditorOverlayPixelsPerUnit);
            return Transform.LocalToWorldMatrix;
        }
    }

    /// <summary>World-space AABB of the canvas root, using the same matrix as rendering.</summary>
    internal bool TryGetWorldAabb(out Float3 min, out Float3 max)
    {
        RebuildIfDirty();
        return TryTransformRectAabb(_rootRect, CanvasToWorld, out min, out max);
    }

    /// <summary>World-space AABB of a child element under this canvas.</summary>
    internal bool TryGetWorldAabb(UIBehaviour ui, out Float3 min, out Float3 max)
    {
        min = default;
        max = default;
        RectTransform? rt = ui.GameObject.RectTransform;
        if (rt == null) return false;
        return TryGetWorldAabb(rt, out min, out max);
    }

    /// <summary>World-space AABB of a RectTransform laid out under this canvas (covers grouping
    /// nodes that carry a RectTransform but no UIBehaviour).</summary>
    internal bool TryGetWorldAabb(RectTransform rt, out Float3 min, out Float3 max)
    {
        min = default;
        max = default;
        if (rt == null) return false;
        RebuildIfDirty();
        return TryTransformRectAabb(rt.ComputedRect, CanvasToWorld, out min, out max);
    }

    private static bool TryTransformRectAabb(Rect rect, Float4x4 matrix, out Float3 min, out Float3 max)
    {
        min = default;
        max = default;
        if (rect.Size.X <= 0f || rect.Size.Y <= 0f) return false;

        Float3 a = Float4x4.TransformPoint(new Float3(rect.Min.X, rect.Min.Y, 0f), matrix);
        Float3 b = Float4x4.TransformPoint(new Float3(rect.Max.X, rect.Min.Y, 0f), matrix);
        Float3 c = Float4x4.TransformPoint(new Float3(rect.Max.X, rect.Max.Y, 0f), matrix);
        Float3 d = Float4x4.TransformPoint(new Float3(rect.Min.X, rect.Max.Y, 0f), matrix);
        min = new Float3(
            Maths.Min(Maths.Min(a.X, b.X), Maths.Min(c.X, d.X)),
            Maths.Min(Maths.Min(a.Y, b.Y), Maths.Min(c.Y, d.Y)),
            Maths.Min(Maths.Min(a.Z, b.Z), Maths.Min(c.Z, d.Z)));
        max = new Float3(
            Maths.Max(Maths.Max(a.X, b.X), Maths.Max(c.X, d.X)),
            Maths.Max(Maths.Max(a.Y, b.Y), Maths.Max(c.Y, d.Y)),
            Maths.Max(Maths.Max(a.Z, b.Z), Maths.Max(c.Z, d.Z)));
        return true;
    }

    /// <summary>
    /// Returns the model matrix for a UI element under this canvas. Equivalent to
    /// <see cref="CanvasToWorld"/> x <see cref="BuildRectModel(RectTransform)"/>.
    /// </summary>
    internal Float4x4 BuildItemModel(UIBehaviour b)
        => CanvasToWorld * BuildRectModel(b.GameObject.RectTransform!);

    /// <summary>
    /// Builds the canvas-design-pixel space matrix that places a RectTransform's pivot-centered
    /// mesh into the canvas frame, threading <b>parent rotation and scale</b> down the chain.
    /// </summary>
    internal Float4x4 BuildRectModel(RectTransform rt)
    {
        Rect cr = rt.ComputedRect;
        Float2 pivot = rt.Pivot;

        // Element's pivot in canvas-axis-aligned design pixels.
        float pivotX = cr.Min.X + pivot.X * cr.Size.X;
        float pivotY = cr.Min.Y + pivot.Y * cr.Size.Y;
        if (PixelPerfectActive)
        {
            pivotX = CanvasPixelSnap.Snap(pivotX, ScaleFactor);
            pivotY = CanvasPixelSnap.Snap(pivotY, ScaleFactor);
        }

        // Element TRS: rotation/scale apply around the pivot (mesh is pivot-centered),
        // then the pivot is placed at its layout position.
        Float4x4 model = Float4x4.CreateTRS(
            new Float3(pivotX, pivotY, 0),
            rt.LocalRotation,
            rt.LocalScale);

        RectTransform? canvasRT = GameObject.RectTransform;
        GameObject? cur = rt.GameObject.Parent;
        while (cur != null)
        {
            RectTransform? prt = cur.RectTransform;
            if (prt == null || prt == canvasRT) break;

            Rect pcr = prt.ComputedRect;
            float pPivotX = pcr.Min.X + prt.Pivot.X * pcr.Size.X;
            float pPivotY = pcr.Min.Y + prt.Pivot.Y * pcr.Size.Y;
            if (PixelPerfectActive)
            {
                pPivotX = CanvasPixelSnap.Snap(pPivotX, ScaleFactor);
                pPivotY = CanvasPixelSnap.Snap(pPivotY, ScaleFactor);
            }
            Float3 pPivot = new Float3(pPivotX, pPivotY, 0);

            Float4x4 wrap = Float4x4.CreateTRS(pPivot, prt.LocalRotation, prt.LocalScale)
                          * Float4x4.CreateTranslation(-pPivot);
            model = wrap * model;

            cur = cur.Parent;
        }

        return model;
    }

    // ============================================================
    // Unchanged from the original - preserved verbatim
    // ============================================================

    private static void EnsureChildRectTransforms(GameObject parent)
    {
        foreach (GameObject child in parent.Children)
        {
            child.EnsureRectTransform();
            EnsureChildRectTransforms(child);
        }
    }

    private float ComputeScaleFactor()
    {
        foreach (MonoBehaviour component in GameObject.GetComponents<MonoBehaviour>())
        {
            if (component is ICanvasScaler scaler && component.EnabledInHierarchy)
                return scaler.ComputeScaleFactor(ResolveScreenSize(), _scaleFactor);
        }

        switch (UIScaleMode)
        {
            case ScaleMode.ConstantPixelSize:    return Maths.Max(ScaleFactor, 0.001f);
            case ScaleMode.ScaleWithScreenSize:  return ComputeScreenSizeScale();
            default:                              return 1f;
        }
    }

    // ============================================================
    // Scene-view gizmos
    // ============================================================

    /// <summary>
    /// Nothing while unselected. A canvas rect is one design pixel per world unit, so a 1280x720
    /// canvas is a 1280x720-unit wireframe — in a scene whose content is a few units across its
    /// edges sweep the whole viewport. Unity likewise only shows the rect once the canvas (or a
    /// UI element inside it) is selected, which is where the outline is actually useful.
    /// </summary>
    public override void DrawGizmos()
    {
    }

    public override void DrawGizmosSelected()
    {
        UIGizmos.DrawCanvasRect(this, UIGizmos.SelectedColor);
    }

    private float ComputeScreenSizeScale()
        => ComputeScreenSizeScale(ResolveScreenSize());

    public float ComputeScreenSizeScale(Float2? screenSizeOverride)
    {
        Float2 size = screenSizeOverride ?? ResolveScreenSize();
        float screenW = size.X;
        float screenH = size.Y;
        if (screenW <= 0 || screenH <= 0) return 1f;

        float refW = Maths.Max(ReferenceResolution.X, 1f);
        float refH = Maths.Max(ReferenceResolution.Y, 1f);

        float logWidth  = MathF.Log2(screenW / refW);
        float logHeight = MathF.Log2(screenH / refH);

        float logScale = ScreenMatchMode switch
        {
            ScreenMatchMode.MatchWidthOrHeight =>
                Maths.Lerp(logWidth, logHeight, Maths.Clamp(MatchWidthOrHeight, 0f, 1f)),
            ScreenMatchMode.Expand => Maths.Min(logWidth, logHeight),
            ScreenMatchMode.Shrink => Maths.Max(logWidth, logHeight),
            _ => 0f,
        };
        return Maths.Max(Maths.Pow(2f, logScale), 0.001f);
    }
}
