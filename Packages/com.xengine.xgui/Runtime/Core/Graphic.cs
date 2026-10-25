using System;
using System.Collections.Generic;
using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Package-facing retained-mode drawable. Its mesh is submitted through CanvasRenderer.</summary>
[RequireComponent(typeof(CanvasRenderer))]
public abstract class Graphic : UIBehaviour, ISelectableGraphic
{
    [SerializeField] private Color _color = Color.White;
    [SerializeField] private bool _raycastTarget = true;
    [SerializeField] private Float4 _raycastPadding;
    [SerializeField] private AssetRef<Material> _material;

    [SerializeIgnore] private CanvasRenderer? _renderer;
    [SerializeIgnore] private UIContext _populateContext;
    [SerializeIgnore] private IMeshModifier[]? _meshModifiers;
    [SerializeIgnore] private IMaterialModifier[]? _materialModifiers;

    public Color Color
    {
        get => _color;
        set => SetField(ref _color, value, UIDirtyFlags.Material);
    }

    public bool RaycastTarget
    {
        get => _raycastTarget;
        set => SetField(ref _raycastTarget, value, UIDirtyFlags.Hierarchy);
    }

    public override Float4 RaycastPadding
    {
        get => _raycastPadding;
        set => SetField(ref _raycastPadding, value, UIDirtyFlags.Hierarchy);
    }

    /// <summary>Snaps a canvas design-space point when the owning overlay Canvas has PixelPerfect set.</summary>
    public Float2 PixelAdjustPoint(Float2 point)
    {
        GameCanvas? canvas = GetCanvas();
        return canvas != null ? canvas.PixelAdjustPoint(point) : point;
    }

    public AssetRef<Material> Material
    {
        get => _material;
        set => SetField(ref _material, value, UIDirtyFlags.Material);
    }

    public CanvasRenderer CanvasRenderer
        => _renderer ??= GameObject.GetComponent<CanvasRenderer>() ?? GameObject.AddComponent<CanvasRenderer>();

    public void SetVerticesDirty() => MarkDirty(UIDirtyFlags.Vertices);
    public void SetMaterialDirty() => MarkDirty(UIDirtyFlags.Material);

    /// <summary>Resizes the RectTransform to the drawable's native pixel size when one exists.</summary>
    public virtual void SetNativeSize() { }

    public override Material GetMaterial()
    {
        Material material = _material.Res ?? base.GetMaterial();
        IMaterialModifier[] modifiers = _materialModifiers ??= Array.Empty<IMaterialModifier>();
        for (int i = 0; i < modifiers.Length; i++)
            material = modifiers[i].GetModifiedMaterial(material) ?? material;
        return material;
    }

    public override void OnEnable()
    {
        base.OnEnable();
        _renderer = GameObject.GetComponent<CanvasRenderer>();
        _renderer?.SetOwner(this);
        RefreshModifierCache();
        GraphicRegistry.RegisterGraphic(this);
    }

    public override void OnAddedToScene()
    {
        base.OnAddedToScene();
        RefreshModifierCache();
        GraphicRegistry.RegisterGraphic(this);
    }

    public override void OnDirtyFlagsChanged(UIDirtyFlags flags)
    {
        CanvasUpdateRegistry.Register(this, flags);
        if ((flags & (UIDirtyFlags.Material | UIDirtyFlags.Hierarchy)) != 0)
            _renderer?.MarkPropertiesDirty();
    }

    public override bool IsRaycastTarget => _raycastTarget;
    public override bool IsGraphic => true;

    public override void OnCanvasRebuildCompleted(UIDirtyFlags processedFlags)
    {
        CanvasUpdateRegistry.Unregister(this, processedFlags);
    }

    public override void OnCanvasRaycastStateChanged()
        => GraphicRegistry.NotifyRaycastStateChanged(this);

    protected virtual void OnPopulateMesh(VertexHelper vh) { }

    public override void PopulateProperties(PropertyState props, in UIContext context)
    {
        props.SetColor("_MainColor", _color * new Color(1f, 1f, 1f, context.Alpha));
    }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context)
    {
        _populateContext = context;
        OnPopulateMesh(new VertexHelper(builder));

        IMeshModifier[] modifiers = _meshModifiers ??= Array.Empty<IMeshModifier>();
        VertexHelper vertices = new(builder);
        for (int i = 0; i < modifiers.Length; i++)
            modifiers[i].ModifyMesh(vertices);
    }

    protected UIContext PopulateContext => _populateContext;

    internal long RaycastSortKey => _renderer?.RaycastSortKey ?? long.MinValue;

    private void RefreshModifierCache()
    {
        List<IMeshModifier>? meshes = null;
        List<IMaterialModifier>? materials = null;
        foreach (UIBehaviour component in GameObject.GetComponents<UIBehaviour>())
        {
            if (component is IMeshModifier mesh)
                (meshes ??= new List<IMeshModifier>(2)).Add(mesh);
            if (component is IMaterialModifier material)
                (materials ??= new List<IMaterialModifier>(1)).Add(material);
        }
        _meshModifiers = meshes?.ToArray() ?? Array.Empty<IMeshModifier>();
        _materialModifiers = materials?.ToArray() ?? Array.Empty<IMaterialModifier>();
    }

    protected void PublishMesh(UIMeshBuilder builder, in UIContext context)
        => CanvasRenderer.SetMaterial(GetMaterial());

    public override void OnDisable()
    {
        GraphicRegistry.UnregisterGraphic(this);
        base.OnDisable();
    }

    public override void OnRemovedFromScene()
    {
        GraphicRegistry.UnregisterGraphic(this);
        base.OnRemovedFromScene();
    }
}
