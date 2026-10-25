// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.XUI;

/// <summary>Which way a progress bar fills.</summary>
public enum XuiProgressDirection
{
    LeftToRight,
    RightToLeft,
    BottomToTop,
    TopToBottom,
}

/// <summary>
/// A read-only progress bar: a track with a fill sized to <see cref="Value"/>.
/// </summary>
/// <remarks>
/// Deliberately not a <c>Slider</c> with input disabled. A slider carries a handle, a selectable, and a
/// whole interaction model that a progress bar has no use for — and an author who reaches for a
/// disabled slider gets focus navigation landing on something the player can never operate.
/// </remarks>
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("UI/XUI/Progress")]
public sealed class XuiProgress : UIBehaviour
{
    [SerializeField] private float _value = 0f;
    [SerializeField] private XuiProgressDirection _direction = XuiProgressDirection.LeftToRight;
    [SerializeField] private RectTransform? _fillRect;

    /// <summary>Progress in 0..1. Values outside the range are clamped rather than distorting the bar.</summary>
    public float Value
    {
        get => _value;
        set
        {
            float clamped = Maths.Clamp(value, 0f, 1f);
            if (_value == clamped) return;
            _value = clamped;
            UpdateFill();
        }
    }

    public XuiProgressDirection Direction
    {
        get => _direction;
        set
        {
            _direction = value;
            UpdateFill();
        }
    }

    /// <summary>The rect that grows with the value. Assigned by the assembler.</summary>
    public RectTransform? FillRect
    {
        get => _fillRect;
        set
        {
            _fillRect = value;
            UpdateFill();
        }
    }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void OnEnable()
    {
        base.OnEnable();
        UpdateFill();
    }

    public override void ApplyLayout(in Rect parentRect) => UpdateFill();

    /// <summary>
    /// Sizes the fill by moving one anchor edge, so the bar works at any container size without the
    /// component needing to know its own pixel width.
    /// </summary>
    private void UpdateFill()
    {
        if (_fillRect is not { IsDisposed: false } fill) return;

        switch (_direction)
        {
            case XuiProgressDirection.LeftToRight:
                fill.AnchorMin = new Float2(0f, 0f);
                fill.AnchorMax = new Float2(_value, 1f);
                break;
            case XuiProgressDirection.RightToLeft:
                fill.AnchorMin = new Float2(1f - _value, 0f);
                fill.AnchorMax = new Float2(1f, 1f);
                break;
            case XuiProgressDirection.BottomToTop:
                fill.AnchorMin = new Float2(0f, 0f);
                fill.AnchorMax = new Float2(1f, _value);
                break;
            case XuiProgressDirection.TopToBottom:
                fill.AnchorMin = new Float2(0f, 1f - _value);
                fill.AnchorMax = new Float2(1f, 1f);
                break;
        }

        fill.SizeDelta = Float2.Zero;
        fill.AnchoredPosition = Float2.Zero;
        MarkDirty(UIDirtyFlags.Layout);
    }
}
