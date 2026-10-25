// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// Turns the rect attributes (<c>anchor</c>, <c>size</c>, <c>pos</c>, <c>pivot</c>, <c>scale</c>) into
/// <see cref="RectTransform"/> state.
/// </summary>
/// <remarks>
/// The one rule worth knowing: <c>size</c> means different things per axis depending on the anchor.
/// On a pinned axis it is the width or height. On a stretched axis it is an inset delta — the XGUI
/// (and uGUI) <c>SizeDelta</c> semantics — so <c>anchor="stretch" size="-16x-16"</c> insets 8px on
/// every edge. Writing <c>_</c> for an axis leaves whatever the anchor already implies.
/// </remarks>
public static class XuiRectApplier
{
    public static void Apply(RectTransform rect, ElementNode node)
    {
        XuiAnchor anchor = XuiAnchor.None;
        string? anchorText = node.Attribute("anchor");
        if (anchorText != null)
            XuiValueParser.TryParseAnchor(anchorText, out anchor);

        if (anchor != XuiAnchor.None)
            ApplyAnchor(rect, anchor);

        string? sizeText = node.Attribute("size");
        if (sizeText != null && XuiValueParser.TryParseSize(sizeText, out XuiSize size))
        {
            Float2 current = rect.SizeDelta;
            rect.SizeDelta = new Float2(
                size.Width ?? DefaultAxis(rect.AnchorMin.X, rect.AnchorMax.X, current.X),
                size.Height ?? DefaultAxis(rect.AnchorMin.Y, rect.AnchorMax.Y, current.Y));
        }

        string? pivotText = node.Attribute("pivot");
        if (pivotText != null && XuiValueParser.TryParseFloat2(pivotText, out XuiFloat2 pivot))
            rect.Pivot = new Float2(pivot.X, pivot.Y);

        string? posText = node.Attribute("pos");
        if (posText != null && XuiValueParser.TryParseFloat2(posText, out XuiFloat2 pos))
            rect.AnchoredPosition = new Float2(pos.X, pos.Y);

        string? scaleText = node.Attribute("scale");
        if (scaleText != null && XuiValueParser.TryParseFloat2(scaleText, out XuiFloat2 scale))
            rect.GameObject.Transform.LocalScale = new Float3(scale.X, scale.Y, 1f);
    }

    /// <summary>
    /// A stretched axis with no authored size fills its anchor range (delta 0); a pinned axis keeps
    /// whatever it already had, so an element the author sized in only one direction is not reset.
    /// </summary>
    private static float DefaultAxis(float anchorMin, float anchorMax, float current)
        => anchorMin == anchorMax ? current : 0f;

    public static void ApplyAnchor(RectTransform rect, XuiAnchor anchor)
    {
        switch (anchor)
        {
            case XuiAnchor.Center: Set(rect, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f); break;
            case XuiAnchor.Top: Set(rect, 0.5f, 1f, 0.5f, 1f, 0.5f, 1f); break;
            case XuiAnchor.Bottom: Set(rect, 0.5f, 0f, 0.5f, 0f, 0.5f, 0f); break;
            case XuiAnchor.Left: Set(rect, 0f, 0.5f, 0f, 0.5f, 0f, 0.5f); break;
            case XuiAnchor.Right: Set(rect, 1f, 0.5f, 1f, 0.5f, 1f, 0.5f); break;
            case XuiAnchor.TopLeft: Set(rect, 0f, 1f, 0f, 1f, 0f, 1f); break;
            case XuiAnchor.TopRight: Set(rect, 1f, 1f, 1f, 1f, 1f, 1f); break;
            case XuiAnchor.BottomLeft: Set(rect, 0f, 0f, 0f, 0f, 0f, 0f); break;
            case XuiAnchor.BottomRight: Set(rect, 1f, 0f, 1f, 0f, 1f, 0f); break;
            case XuiAnchor.Stretch: Set(rect, 0f, 0f, 1f, 1f, 0.5f, 0.5f); break;
            case XuiAnchor.TopStretch: Set(rect, 0f, 1f, 1f, 1f, 0.5f, 1f); break;
            case XuiAnchor.BottomStretch: Set(rect, 0f, 0f, 1f, 0f, 0.5f, 0f); break;
            case XuiAnchor.LeftStretch: Set(rect, 0f, 0f, 0f, 1f, 0f, 0.5f); break;
            case XuiAnchor.RightStretch: Set(rect, 1f, 0f, 1f, 1f, 1f, 0.5f); break;
            default: break;
        }
    }

    private static void Set(
        RectTransform rect,
        float minX, float minY,
        float maxX, float maxY,
        float pivotX, float pivotY)
    {
        rect.AnchorMin = new Float2(minX, minY);
        rect.AnchorMax = new Float2(maxX, maxY);
        rect.Pivot = new Float2(pivotX, pivotY);
        // Entering a stretched axis, the previous fixed extent would read as a huge inset.
        Float2 delta = rect.SizeDelta;
        rect.SizeDelta = new Float2(
            minX == maxX ? delta.X : 0f,
            minY == maxY ? delta.Y : 0f);
        rect.AnchoredPosition = Float2.Zero;
    }
}
