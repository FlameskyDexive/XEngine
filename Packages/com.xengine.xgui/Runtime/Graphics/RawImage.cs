using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>Draws a texture directly into the Graphic rect without Sprite atlas remapping.</summary>
[AddComponentMenu("UI/Raw Image")]
public sealed class RawImage : MaskableGraphic
{
    [SerializeField] private AssetRef<Texture2D> _texture;
    [SerializeField] private Rect _uvRect = new(0f, 0f, 1f, 1f);

    // Runtime-only texture injection (e.g. video playback frames). Never serialized:
    // it overrides the serialized asset reference while set, without touching it.
    [SerializeIgnore] private Texture2D? _runtimeTexture;

    public AssetRef<Texture2D> Texture { get => _texture; set => SetField(ref _texture, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material); }
    public Rect UVRect { get => _uvRect; set => SetField(ref _uvRect, value, UIDirtyFlags.Vertices); }

    /// <summary>
    /// Non-serialized runtime texture shown instead of <see cref="Texture"/> while set.
    /// Cleared automatically when the source resets it to null (e.g. playback stopped).
    /// </summary>
    public Texture2D? RuntimeTexture
    {
        get => _runtimeTexture;
        set
        {
            if (_runtimeTexture == value) return;
            _runtimeTexture = value;
            MarkDirty(UIDirtyFlags.Material);
        }
    }

    public override void SetNativeSize()
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null) return;
        if (!GraphicNativeSize.TryFromTexture(_runtimeTexture ?? _texture.Res, _uvRect, out Float2 size))
            return;
        GraphicNativeSize.Apply(rt, size);
    }

    protected override void OnPopulateMesh(VertexHelper vertices)
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null) return;
        Float2 size = rt.ComputedRect.Size;
        Rect local = new(-rt.Pivot.X * size.X, -rt.Pivot.Y * size.Y,
            (1f - rt.Pivot.X) * size.X, (1f - rt.Pivot.Y) * size.Y);
        vertices.Builder.AddQuad(local, Color.White,
            new Float2(_uvRect.Min.X, _uvRect.Min.Y),
            new Float2(_uvRect.Max.X, _uvRect.Max.Y));
    }

    public override void PopulateProperties(PropertyState props, in UIContext context)
    {
        base.PopulateProperties(props, context);
        props.SetTexture("_MainTex", _runtimeTexture ?? _texture.Res ?? Texture2D.LoadDefault(DefaultTexture.White));
        props.SetVector("_Tiling", new Float2(1f, 1f));
        props.SetVector("_Offset", Float2.Zero);
    }
}
