// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Resizes its own <see cref="RectTransform"/> to fit its content (its <see cref="IUILayoutElement"/>
/// sources - e.g. a child <see cref="UILayoutGroup"/> or a <see cref="UILayoutElement"/>). The canvas
/// applies the fit just before the element computes its rect, so the fitted size drives layout.
/// </summary>
[AddComponentMenu("UI/Layout/Content Size Fitter")]
[ComponentIcon("")] // ArrowsToDot
public class UIContentSizeFitter : UIBehaviour
{
    public enum FitMode { Unconstrained, MinSize, PreferredSize }

    [SerializeField] private FitMode _horizontalFit = FitMode.Unconstrained;
    [SerializeField] private FitMode _verticalFit = FitMode.Unconstrained;

    public FitMode HorizontalFit { get => _horizontalFit; set => SetField(ref _horizontalFit, value, UIDirtyFlags.Layout); }
    public FitMode VerticalFit { get => _verticalFit; set => SetField(ref _verticalFit, value, UIDirtyFlags.Layout); }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { /* no geometry */ }

    public override void ApplyLayout(in Rect parentRect) => ApplyFit();

    /// <summary>Writes the fitted size into the RectTransform's <see cref="RectTransform.SizeDelta"/>.
    /// Called by the canvas during rebuild before this element's rect is computed.</summary>
    public void ApplyFit()
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt is null) return;
        if (_horizontalFit == FitMode.Unconstrained && _verticalFit == FitMode.Unconstrained) return;

        Float2 sd = rt.SizeDelta;

        if (_horizontalFit != FitMode.Unconstrained)
            sd.X = _horizontalFit == FitMode.MinSize
                ? UILayoutUtility.GetMinSize(GameObject).X
                : UILayoutUtility.GetPreferredSize(GameObject).X;

        if (_verticalFit != FitMode.Unconstrained)
            sd.Y = _verticalFit == FitMode.MinSize
                ? UILayoutUtility.GetMinSize(GameObject).Y
                : UILayoutUtility.GetPreferredSize(GameObject).Y;

        // Setter is change-guarded, so a stable layout stops dirtying after it settles.
        rt.SizeDelta = sd;
    }
}
