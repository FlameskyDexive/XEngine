using System.Collections.Generic;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

[AddComponentMenu("UI/Layout/Content Size Fitter")]
public sealed class ContentSizeFitter : UIBehaviour, ILayoutSelfController
{
    public enum FitMode { Unconstrained, MinSize, PreferredSize }
    [SerializeField] private FitMode _horizontalFit;
    [SerializeField] private FitMode _verticalFit;
    [SerializeIgnore] private readonly List<MonoBehaviour> _componentScratch = new(4);
    public FitMode HorizontalFit { get => _horizontalFit; set => SetField(ref _horizontalFit, value, UIDirtyFlags.Layout); }
    public FitMode VerticalFit { get => _verticalFit; set => SetField(ref _verticalFit, value, UIDirtyFlags.Layout); }
    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }
    public override void ApplyLayout(in Rect parentRect) => ApplyFit();

    public void ApplyFit()
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt is null || (_horizontalFit == FitMode.Unconstrained && _verticalFit == FitMode.Unconstrained)) return;
        Float2 size = rt.SizeDelta;
        Float2 min = XGuiLayoutUtility.GetMinSize(GameObject, _componentScratch);
        Float2 preferred = XGuiLayoutUtility.GetPreferredSize(GameObject, _componentScratch);
        if (_horizontalFit != FitMode.Unconstrained) size.X = _horizontalFit == FitMode.MinSize ? min.X : preferred.X;
        if (_verticalFit != FitMode.Unconstrained) size.Y = _verticalFit == FitMode.MinSize ? min.Y : preferred.Y;
        rt.SizeDelta = size;
    }
}
