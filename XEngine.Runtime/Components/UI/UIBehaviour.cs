// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using XEngine.PaperUI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.UI;

/// <summary>
/// Abstract base class for every retained-mode UI component.
/// </summary>
/// <remarks>
/// UI components do not draw themselves. Instead, the owning <see cref="GameCanvas"/>
/// traverses the hierarchy in depth-first order and calls
/// <see cref="GenerateMesh"/> on each <see cref="UIBehaviour"/> whose mesh is dirty,
/// then bakes the result into <see cref="CachedMesh"/> for the pipeline to consume.
/// </remarks>
[RequireComponent(typeof(RectTransform))]
public abstract class UIBehaviour : MonoBehaviour
{
    /// <summary>
    /// This component's <see cref="RectTransform"/> (Unity's <c>UIBehaviour.rectTransform</c>).
    /// <see cref="RequireComponentAttribute"/> guarantees one exists; created defensively if a
    /// hand-built hierarchy skipped it. Derived UI controls read layout rects through this instead
    /// of routing through <see cref="GameObject.RectTransform"/>.
    /// </summary>
    public RectTransform RectTransform => GameObject.RectTransform ?? GameObject.EnsureRectTransform();

    [SerializeIgnore] internal Mesh? CachedMesh;
    [SerializeIgnore] internal Mesh? CachedSecondaryMesh;
    [SerializeIgnore] internal UIDirtyFlags DirtyFlags = UIDirtyFlags.All;

    // Geometry depends on the laid-out rect size. Inherited CanvasGroup alpha is a material
    // property and deliberately does not participate in this cache key: changing a group must not
    // reshape text or upload every descendant mesh.
    [SerializeIgnore] internal Float2 LastBakeSize = new(float.NaN, float.NaN);
    [SerializeIgnore] internal float LastBakePixelScale = float.NaN;
    [SerializeIgnore] private UIContext _renderContext = UIContext.Default;

    /// <summary>Latest inherited rendering context published by the owning Canvas.</summary>
    internal UIContext RenderContext => _renderContext;

    internal void SetRenderContext(in UIContext context) => _renderContext = context;

    public override void OnEnable()
    {
        GetCanvas()?.MarkDirty(UIDirtyFlags.Hierarchy);
        MarkDirty(UIDirtyFlags.Hierarchy);
        UIRaycastRegistry.Register(this);
    }

    public override void OnDisable()
    {
        UIRaycastRegistry.Unregister(this);
        GetCanvas()?.MarkDirty(UIDirtyFlags.Hierarchy);
        MarkDirty(UIDirtyFlags.Hierarchy);
    }

    public override void OnValidate()
    {
        // Run the OnValide only in the editor- During runtime, dirtying should be driven by the fields themselves.
        if (!Application.IsPlaying)
        {
            GetCanvas()?.MarkDirty(UIDirtyFlags.All);
            MarkDirty(UIDirtyFlags.All);
        }
    }

    public override void OnAddedToScene()
    {
        GetCanvas()?.MarkDirty(UIDirtyFlags.Hierarchy);
        MarkDirty(UIDirtyFlags.Hierarchy);
        UIRaycastRegistry.Register(this);
    }

    public override void OnRemovedFromScene()
    {
        UIRaycastRegistry.Unregister(this);
        GetCanvas()?.MarkDirty(UIDirtyFlags.Hierarchy);

        MarkDirty(UIDirtyFlags.Hierarchy);

        // Free the baked GPU buffers - the canvas will re-bake from scratch if this element
        // is ever re-added. Without this every created/destroyed UI element leaks its mesh.
        if (CachedMesh != null) CachedMesh.OnDispose();
        CachedMesh = null;
        if (CachedSecondaryMesh != null) CachedSecondaryMesh.OnDispose();
        CachedSecondaryMesh = null;
        DirtyFlags |= UIDirtyFlags.Vertices;
    }

    /// <summary>Subclasses fill <paramref name="builder"/> in canvas-local pixel space.</summary>
    public abstract void GenerateMesh(UIMeshBuilder builder, in UIContext context);

    /// <summary>
    /// Optional second mesh (different material/texture), e.g. rich-text inline sprites.
    /// Default produces no geometry.
    /// </summary>
    public virtual void GenerateSecondaryMesh(UIMeshBuilder builder, in UIContext context) { }

    /// <summary>Material for <see cref="CachedSecondaryMesh"/> when present.</summary>
    public virtual Material? GetSecondaryMaterial() => null;

    /// <summary>Property population for the secondary mesh item.</summary>
    public virtual void PopulateSecondaryProperties(PropertyState props, in UIContext context) { }

    /// <summary>Package-facing bridge for baking a pooled builder into this element's cached mesh.</summary>
    protected Mesh? BakeGeneratedMesh(UIMeshBuilder builder)
    {
        if (builder.IsEmpty)
        {
            if (CachedMesh != null) CachedMesh.OnDispose();
            CachedMesh = null;
            return null;
        }

        CachedMesh ??= new Mesh();
        builder.Bake(CachedMesh);
        return CachedMesh;
    }

    /// <summary>Subclasses bind per-item shader properties (textures, scalars). Called every frame the item is visible.</summary>
    public virtual void PopulateProperties(PropertyState props, in UIContext context) { }

    /// <summary>The material this element draws with. Default returns the shared `DefaultUI` material.</summary>
    public virtual Material GetMaterial() => GameCanvas.SharedUIMaterial;

    /// <summary>Whether this UI element participates in pointer hit testing.</summary>
    public virtual bool IsRaycastTarget => true;

    /// <summary>
    /// Extra hit-test padding in design pixels: X left, Y bottom, Z right, W top.
    /// Positive values expand the pointer rectangle; negative values shrink it.
    /// </summary>
    public virtual Float4 RaycastPadding { get => default; set { } }

    /// <summary>Whether this behaviour owns drawable UI geometry for RenderStats and host diagnostics.</summary>
    public virtual bool IsGraphic => false;

    /// <summary>
    /// Whether this behaviour contributes a rectangular pointer candidate. Graphics are candidates by
    /// default; Selectable controls also opt in so legacy buttons without a separate background Graphic
    /// retain their historical hit area when the package raycaster is installed.
    /// </summary>
    public virtual bool IsRaycastCandidate => IsGraphic;

    public void MarkDirty(UIDirtyFlags flags)
    {
        DirtyFlags |= flags;
        OnDirtyFlagsChanged(flags);
        GetCanvas()?.MarkDirty(flags);
    }

    /// <summary>
    /// Extension hook for package-owned update registries. The Runtime host deliberately does not
    /// reference XGUI, but a package component can map the same dirty flags into its own queues.
    /// </summary>
    public virtual void OnDirtyFlagsChanged(UIDirtyFlags flags) { }

    /// <summary>
    /// Called by the Canvas host after a rebuild stage has consumed the specified flags. Package
    /// components use this to remove themselves from their de-duplicated dirty queues.
    /// </summary>
    public virtual void OnCanvasRebuildCompleted(UIDirtyFlags processedFlags) { }

    /// <summary>
    /// Called by a neutral <see cref="CanvasRenderer"/> when its render-order, clipping, or inherited
    /// raycast state changes. Package registries can mirror that state without making Runtime depend
    /// on a package assembly.
    /// </summary>
    public virtual void OnCanvasRaycastStateChanged() { }

    /// <summary>Clears host-owned dirty bits after an optional package update stage completes.</summary>
    public void ClearDirtyFlags(UIDirtyFlags processedFlags) => DirtyFlags &= ~processedFlags;

    /// <summary>
    /// Optional layout self-controller hook. The host invokes it before computing this object's
    /// RectTransform. Package-owned fitters can participate without making Runtime reference the
    /// package assembly.
    /// </summary>
    public virtual void ApplyLayout(in Rect parentRect) { }

    /// <summary>
    /// Backing-field setter used by every <see cref="UIBehaviour"/> property. Assigns
    /// <paramref name="value"/> only when it differs from <paramref name="field"/>, and
    /// marks <paramref name="flags"/> dirty when it does. Returns <c>true</c> if the value
    /// changed - callers can branch on it for additional work (see <c>CanvasGroup.Alpha</c>).
    /// </summary>
    /// <remarks>
    /// This is the single place the UI's value-change check lives. <see cref="EqualityComparer{T}.Default"/>
    /// resolves correctly for every property type in use: structs (<c>Color</c>, <c>Float2</c>)
    /// compare by value, and <see cref="EngineObject"/> references (<c>Texture2D</c>, <c>Material</c>,
    /// <c>FontAsset</c>) compare by reference via <see cref="EngineObject"/>'s overridden equality.
    /// </remarks>
    protected bool SetField<T>(ref T field, T value, UIDirtyFlags flags)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        MarkDirty(flags);
        return true;
    }

    public GameCanvas? GetCanvas() => GetComponentInParent<GameCanvas>(includeSelf: true);

    // ============================================================
    // Scene-view gizmos (RectTransform / UI gizmos)
    // ============================================================

    /// <summary>
    /// Nothing while unselected. UI rects live in design-pixel space (one pixel per world unit), so
    /// a screenful of unselected widgets buries the rest of the scene under rectangles an order of
    /// magnitude larger than it. The outline appears on selection, like Unity's.
    /// </summary>
    public override void DrawGizmos()
    {
    }

    /// <summary>
    /// Bold rect outline plus pivot + anchor handles when this element is selected, and the owning
    /// canvas's bounds behind them so the element can be read against the screen it belongs to.
    /// </summary>
    public override void DrawGizmosSelected()
    {
        if (GetCanvas() is { } canvas)
            UIGizmos.DrawCanvasRect(canvas, UIGizmos.UnselectedColor);
        UIGizmos.DrawRect(this, UIGizmos.SelectedColor, drawPivot: true, drawAnchors: true);
    }
}
