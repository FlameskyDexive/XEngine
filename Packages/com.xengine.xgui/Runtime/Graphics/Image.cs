using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>Package Image; the existing UIImage remains the serialization compatibility type.</summary>
[AddComponentMenu("UI/Image")]
public class Image : MaskableGraphic, ISelectableSpriteGraphic
{
    [SerializeField] private AssetRef<Sprite> _sprite = new(XguiDefaultResources.ResolveDefaultSpriteGuid());
    // Keep the serialized names and display behavior in lockstep with Runtime.UI.UIImage so a
    // migration can retain authored geometry instead of merely preserving a subset of its fields.
    [SerializeField] private bool _preserveAspect;
    [SerializeField] private ImageType _type = ImageType.Sliced;
    [SerializeField] private float _pixelsPerUnitMultiplier = 1f;
    [SerializeField] private FillMethod _fillMethod = FillMethod.Radial360;
    [SerializeField] private int _fillOrigin;
    [SerializeField] private float _fillAmount = 1f;
    [SerializeField] private bool _fillClockwise;

    public AssetRef<Sprite> Sprite { get => _sprite; set => SetField(ref _sprite, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material); }
    public bool PreserveAspect { get => _preserveAspect; set => SetField(ref _preserveAspect, value, UIDirtyFlags.Vertices); }
    public ImageType Type { get => _type; set => SetField(ref _type, value, UIDirtyFlags.Vertices); }
    public float PixelsPerUnitMultiplier
    {
        get => _pixelsPerUnitMultiplier;
        set => SetField(ref _pixelsPerUnitMultiplier, Maths.Max(0.0001f, value), UIDirtyFlags.Vertices);
    }
    public FillMethod FillMethod { get => _fillMethod; set => SetField(ref _fillMethod, value, UIDirtyFlags.Vertices); }
    public int FillOrigin { get => _fillOrigin; set => SetField(ref _fillOrigin, value, UIDirtyFlags.Vertices); }
    public float FillAmount { get => _fillAmount; set => SetField(ref _fillAmount, Maths.Clamp(value, 0f, 1f), UIDirtyFlags.Vertices); }
    public bool FillClockwise { get => _fillClockwise; set => SetField(ref _fillClockwise, value, UIDirtyFlags.Vertices); }

    public override void SetNativeSize()
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null) return;
        if (!GraphicNativeSize.TryFromSprite(_sprite.Res, PixelsPerUnitMultiplier, out Float2 size))
            return;
        GraphicNativeSize.Apply(rt, size);
    }

    protected override void OnPopulateMesh(VertexHelper vertices)
    {
        UIMeshBuilder builder = vertices.Builder;
        UIContext context = PopulateContext;
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null) return;
        Rect rect = new(-rt.Pivot.X * rt.ComputedRect.Size.X, -rt.Pivot.Y * rt.ComputedRect.Size.Y,
            (1f - rt.Pivot.X) * rt.ComputedRect.Size.X, (1f - rt.Pivot.Y) * rt.ComputedRect.Size.Y);
        // Tint is supplied as a material property by Graphic so a color-only edit
        // does not rebuild or upload the local mesh.
        Color tint = Color.White;
        Sprite? sprite = _sprite.Res;
        if (sprite?.Texture.Res is Texture2D texture && texture.Width > 0 && texture.Height > 0)
        {
            float u0 = sprite.Rect.X / (float)texture.Width;
            float v0 = sprite.Rect.Y / (float)texture.Height;
            builder.SetUVRect(new Float2(u0, v0), new Float2(sprite.Rect.Width / (float)texture.Width, sprite.Rect.Height / (float)texture.Height));
        }
        else builder.SetUVRect(Float2.Zero, Float2.One);

        if (_preserveAspect && sprite != null)
            rect = FitAspect(rect, sprite.Rect.Width, sprite.Rect.Height);

        switch (Type)
        {
            case ImageType.Filled: builder.AddFilled(rect, tint, FillMethod, FillOrigin, FillAmount, FillClockwise); break;
            case ImageType.Tiled:
            {
                Float2 tileSize = sprite == null
                    ? new Float2(32f, 32f)
                    : new Float2(sprite.Rect.Width * _pixelsPerUnitMultiplier,
                        sprite.Rect.Height * _pixelsPerUnitMultiplier);
                builder.AddTiled(rect, tileSize, tint);
                break;
            }
            case ImageType.Sliced when sprite is { Border: var border }:
                border *= _pixelsPerUnitMultiplier;
                ScaleBordersToFit(ref border, rect.Size);
                Float4 uvBorders = new(
                    sprite.Border.X / Maths.Max(sprite.Rect.Width, 1),
                    sprite.Border.Y / Maths.Max(sprite.Rect.Height, 1),
                    sprite.Border.Z / Maths.Max(sprite.Rect.Width, 1),
                    sprite.Border.W / Maths.Max(sprite.Rect.Height, 1));
                ClampUvBorders(ref uvBorders);
                builder.AddNineSlice(rect, border, uvBorders, tint); break;
            default: builder.AddQuad(rect, tint, Float2.Zero, Float2.One); break;
        }
        PublishMesh(builder, context);
    }

    public override void PopulateProperties(PropertyState props, in UIContext context)
    {
        base.PopulateProperties(props, context);
        Texture2D texture = _sprite.Res?.Texture.Res ?? Texture2D.LoadDefault(DefaultTexture.White);
        props.SetTexture("_MainTex", texture);
        props.SetVector("_Tiling", new Float2(1f, 1f));
        props.SetVector("_Offset", Float2.Zero);
    }

    private static Rect FitAspect(Rect rect, float sourceWidth, float sourceHeight)
    {
        if (sourceWidth <= 0f || sourceHeight <= 0f || rect.Size.X <= 0f || rect.Size.Y <= 0f)
            return rect;

        float sourceAspect = sourceWidth / sourceHeight;
        float width = rect.Size.X;
        float height = rect.Size.Y;
        if (width / height > sourceAspect) width = height * sourceAspect;
        else height = width / sourceAspect;
        float centerX = (rect.Min.X + rect.Max.X) * 0.5f;
        float centerY = (rect.Min.Y + rect.Max.Y) * 0.5f;
        return new Rect(centerX - width * 0.5f, centerY - height * 0.5f,
            centerX + width * 0.5f, centerY + height * 0.5f);
    }

    private static void ScaleBordersToFit(ref Float4 border, Float2 size)
    {
        float scale = 1f;
        float horizontal = border.X + border.Z;
        float vertical = border.Y + border.W;
        if (horizontal > size.X && horizontal > 0f) scale = Maths.Min(scale, size.X / horizontal);
        if (vertical > size.Y && vertical > 0f) scale = Maths.Min(scale, size.Y / vertical);
        if (scale < 1f) border *= scale;
    }

    private static void ClampUvBorders(ref Float4 border)
    {
        float horizontal = border.X + border.Z;
        if (horizontal > 1f)
        {
            border.X /= horizontal;
            border.Z /= horizontal;
        }

        float vertical = border.Y + border.W;
        if (vertical > 1f)
        {
            border.Y /= vertical;
            border.W /= vertical;
        }
    }
}
