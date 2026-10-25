// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>
/// Core Canvas rendering component. XGUI graphics use this component as their stable
/// host for mesh/material state; the M2 compatibility path still fills it from the
/// legacy UI render tree through <see cref="CanvasRenderNode"/>.
/// </summary>
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("UI/Canvas Renderer")]
public sealed class CanvasRenderer : MonoBehaviour
{
    [SerializeIgnore] private Mesh? _mesh;
    [SerializeIgnore] private Material? _material;
    [SerializeIgnore] private CanvasDirtyFlags _dirtyFlags = CanvasDirtyFlags.All;
    [SerializeIgnore] private CanvasClipState _clipState;
    [SerializeIgnore] private bool _blocksRaycasts = true;
    [SerializeIgnore] private Rect? _raycastRect;
    [SerializeIgnore] private long _sortKey;
    [SerializeIgnore] private bool _hasSortKey;
    [SerializeIgnore] private readonly CanvasRenderNode _node = new();
    [SerializeIgnore] private readonly CanvasRenderNode _unwindNode = new();
    [SerializeIgnore] private CanvasClipState _unwindClipState;
    [SerializeIgnore] private long _unwindSortKey;
    [SerializeIgnore] private bool _hasStencilUnwind;
    [SerializeIgnore] private readonly PropertyState _properties = new();
    [SerializeIgnore] private UIBehaviour? _owner;
    [SerializeIgnore] private UIContext _renderContext = UIContext.Default;

    public Mesh? Mesh => _mesh;
    public Material? Material => _material;
    public CanvasDirtyFlags DirtyFlags => _dirtyFlags;
    public CanvasRenderNode RenderNode => _node;
    internal CanvasRenderNode UnwindNode => _unwindNode;
    /// <summary>Current render-order key used by the host raycaster and package diagnostics.</summary>
    public long RaycastSortKey => _hasSortKey ? _sortKey : _node.SortKey;
    /// <summary>Current inherited raycast filter published by the owning Canvas rebuild.</summary>
    public bool BlocksRaycasts => _blocksRaycasts;
    /// <summary>Optional coarse rectangle filter in the canvas design-pixel space.</summary>
    public Rect? RaycastRect => _raycastRect;
    /// <summary>Current shader clip state shared with input filtering.</summary>
    public CanvasClipState ClipState => _clipState;
    internal UIBehaviour? Owner => _owner;
    internal bool IsRaycastTarget => _owner?.IsRaycastTarget == true;
    internal long HitSortKey => _node.SortKey;

    /// <summary>Associates this neutral host with its package-owned drawable component.</summary>
    public void SetOwner(UIBehaviour owner) => _owner = owner;

    public void SetMesh(Mesh? mesh)
    {
        if (ReferenceEquals(_mesh, mesh)) return;
        _mesh = mesh;
        _dirtyFlags |= CanvasDirtyFlags.Vertices;
    }

    public void SetMaterial(Material? material)
    {
        if (ReferenceEquals(_material, material)) return;
        _material = material;
        _dirtyFlags |= CanvasDirtyFlags.Material;
        RenderStats.RecordMaterialUpdate();
    }

    public void MarkPropertiesDirty() => _dirtyFlags |= CanvasDirtyFlags.Material;

    internal void SetRenderContext(in UIContext context) => _renderContext = context;

    public void SetColor(XEngine.Vector.Color color)
    {
        // Color is represented by the mesh/property payload in the first host slice.
        // Keep the operation as a material-category invalidation until Graphic owns it.
        _dirtyFlags |= CanvasDirtyFlags.Material;
    }

    public void SetTransformDirty() => _dirtyFlags |= CanvasDirtyFlags.Transform;
    public void SetClipDirty() => _dirtyFlags |= CanvasDirtyFlags.Clip;
    internal void SetClipState(CanvasClipState clip)
    {
        if (ClipEquals(_clipState, clip)) return;
        _clipState = clip;
        _dirtyFlags |= CanvasDirtyFlags.Clip;
        if (_owner != null) _owner.OnCanvasRaycastStateChanged();
    }

    private static bool ClipEquals(in CanvasClipState a, in CanvasClipState b)
        => a.Enabled == b.Enabled
        && a.HasRect == b.HasRect
        && a.UsesStencil == b.UsesStencil
        && a.IsStencilWrite == b.IsStencilWrite
        && a.SuppressStencilWriterColor == b.SuppressStencilWriterColor
        && a.StencilDepth == b.StencilDepth
        && a.StencilReference == b.StencilReference
        && a.ClipToLocal.Equals(b.ClipToLocal)
        && a.ClipRect.Equals(b.ClipRect)
        && a.CornerRadius.Equals(b.CornerRadius)
        && a.Softness.Equals(b.Softness);

    internal void SetHierarchySortKey(long sortKey)
    {
        if (_hasSortKey && _sortKey == sortKey) return;
        _sortKey = sortKey;
        _hasSortKey = true;
        _dirtyFlags |= CanvasDirtyFlags.Hierarchy;
        if (_owner != null) _owner.OnCanvasRaycastStateChanged();
    }

    internal void SetRaycastState(bool blocksRaycasts, Rect? raycastRect)
    {
        if (_blocksRaycasts == blocksRaycasts && _raycastRect.Equals(raycastRect)) return;
        _blocksRaycasts = blocksRaycasts;
        _raycastRect = raycastRect;
        if (_owner != null) _owner.OnCanvasRaycastStateChanged();
    }
    public void ClearDirtyFlags() => _dirtyFlags = CanvasDirtyFlags.None;

    /// <summary>Publishes the complete neutral submission consumed by CanvasRegistry.</summary>
    public void SetRenderData(
        Mesh? mesh,
        Material? material,
        in Float4x4 model,
        long sortKey,
        UISurface surface,
        CanvasClipState clip = default)
    {
        bool materialChanged = !ReferenceEquals(_material, material);
        _mesh = mesh;
        _material = material;
        if (materialChanged) RenderStats.RecordMaterialUpdate();
        _node.ConfigureStandalone(mesh, material, model, sortKey, surface, clip);
        _dirtyFlags = CanvasDirtyFlags.None;
    }

    public override void OnAddedToScene()
        => GetComponentInParent<GameCanvas>(includeSelf: true)?.RegisterRenderer(this);

    public override void OnEnable()
        => GetComponentInParent<GameCanvas>(includeSelf: true)?.RegisterRenderer(this);

    public override void OnDisable()
        => GetComponentInParent<GameCanvas>(includeSelf: true)?.UnregisterRenderer(this);

    public override void OnRemovedFromScene()
        => GetComponentInParent<GameCanvas>(includeSelf: true)?.UnregisterRenderer(this);

    internal void PrepareForCanvas(GameCanvas canvas, int rendererIndex)
    {
        UIBehaviour? owner = _owner;
        if (owner == null)
            foreach (UIBehaviour candidate in GameObject.GetComponents<UIBehaviour>())
                if (candidate.CachedMesh != null) { owner = candidate; break; }
        if (owner == null)
        {
            _mesh = null;
            _material = null;
            _node.Detach();
            ClearStencilUnwind();
            return;
        }
        _owner = owner;
        _mesh = owner.CachedMesh;
        _properties.Clear();
        owner.PopulateProperties(_properties, _renderContext);
        Material? material = owner.GetMaterial();
        if (!ReferenceEquals(_material, material))
        {
            _material = material;
            _dirtyFlags |= CanvasDirtyFlags.Material;
            RenderStats.RecordMaterialUpdate();
        }
        if (_mesh == null || _material == null)
        {
            _mesh = null;
            _material = null;
            _node.Detach();
            ClearStencilUnwind();
            return;
        }

        long canvasDisc = canvas.InstanceID & 0x1FFFFF;
        long dfs = (uint)rendererIndex & 0x1FFFFF;
        long sortKey = _hasSortKey ? _sortKey : ((long)canvas.SortOrder << 42) + (canvasDisc << 21) + dfs;
        _node.ConfigureStandalone(_mesh, _material, canvas.BuildItemModel(owner), sortKey,
            UIRenderTree.ToSurface(canvas.RenderMode), _clipState, _properties);
        if (_hasStencilUnwind)
            _unwindNode.ConfigureStandalone(_mesh, _material, canvas.BuildItemModel(owner), _unwindSortKey,
                UIRenderTree.ToSurface(canvas.RenderMode), _unwindClipState, _properties);
        _dirtyFlags = CanvasDirtyFlags.None;
        owner.OnCanvasRebuildCompleted(UIDirtyFlags.Material | UIDirtyFlags.Hierarchy | UIDirtyFlags.Clip);
    }

    internal void PrepareStencilUnwind(GameCanvas canvas, long sortKey,
        CanvasClipState clip)
    {
        UIBehaviour? owner = _owner;
        if (owner == null)
        {
            foreach (UIBehaviour candidate in GameObject.GetComponents<UIBehaviour>())
            {
                if (candidate.CachedMesh == null) continue;
                owner = candidate;
                break;
            }
        }
        Mesh? mesh = owner?.CachedMesh;
        Material? material = owner?.GetMaterial();
        if (mesh == null || material == null || owner == null) return;
        _unwindClipState = clip;
        _unwindSortKey = sortKey;
        _hasStencilUnwind = true;
        _properties.Clear();
        owner.PopulateProperties(_properties, _renderContext);
        _unwindNode.ConfigureStandalone(mesh, material, canvas.BuildItemModel(owner), sortKey,
            UIRenderTree.ToSurface(canvas.RenderMode), clip, _properties);
    }

    internal void ClearStencilUnwind()
    {
        _hasStencilUnwind = false;
        _unwindSortKey = 0;
        _unwindClipState = CanvasClipState.None;
        _unwindNode.Detach();
    }
}
