using XEngine.Echo;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>Constrains a RectTransform to an aspect ratio during the layout calculation stage.</summary>
[AddComponentMenu("UI/Layout/Aspect Ratio Fitter")]
public sealed class AspectRatioFitter : UIBehaviour, ILayoutSelfController
{
    public enum AspectMode { None, WidthControlsHeight, HeightControlsWidth, FitInParent, EnvelopeParent }

    [SerializeField] private AspectMode _aspectMode;
    [SerializeField] private float _aspectRatio = 1f;

    public AspectMode Mode { get => _aspectMode; set => SetField(ref _aspectMode, value, UIDirtyFlags.Layout); }
    public float AspectRatio
    {
        get => _aspectRatio;
        set => SetField(ref _aspectRatio, Maths.Max(0.0001f, value), UIDirtyFlags.Layout);
    }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void ApplyLayout(in Rect parentRect)
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null || Mode == AspectMode.None) return;

        Float2 size = rt.SizeDelta;
        float ratio = Maths.Max(0.0001f, AspectRatio);
        switch (Mode)
        {
            case AspectMode.WidthControlsHeight:
                size.Y = size.X / ratio;
                break;
            case AspectMode.HeightControlsWidth:
                size.X = size.Y * ratio;
                break;
            case AspectMode.FitInParent:
            case AspectMode.EnvelopeParent:
            {
                float parentW = Maths.Max(0f, parentRect.Size.X);
                float parentH = Maths.Max(0f, parentRect.Size.Y);
                float width = Mode == AspectMode.FitInParent
                    ? Maths.Min(parentW, parentH * ratio)
                    : Maths.Max(parentW, parentH * ratio);
                size = new Float2(width, width / ratio);
                break;
            }
        }
        rt.SizeDelta = size;
    }
}
