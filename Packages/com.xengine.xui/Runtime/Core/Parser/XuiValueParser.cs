// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Globalization;

namespace XEngine.XUI.Core;

/// <summary>
/// Scalar attribute grammars shared by the parser, the lint rules, and the runtime applier. One
/// implementation means the offline validator and the engine can never disagree about whether
/// <c>size="_x44"</c> is legal.
/// </summary>
public static class XuiValueParser
{
    /// <summary>
    /// <c>WxH</c> where either axis may be <c>_</c> (leave that axis to the layout system).
    /// <c>480x360</c>, <c>400x_</c>, and <c>_x44</c> are all valid; a bare number is not, so a typo
    /// cannot silently mean "square".
    /// </summary>
    public static bool TryParseSize(string text, out XuiSize size)
    {
        size = default;
        if (string.IsNullOrEmpty(text)) return false;

        int split = text.IndexOf('x');
        if (split < 0) return false;
        // Exactly one separator: 'x' appearing twice means the author wrote something else entirely.
        if (text.IndexOf('x', split + 1) >= 0) return false;

        if (!TryParseAxis(text.AsSpan(0, split), out float? width)) return false;
        if (!TryParseAxis(text.AsSpan(split + 1), out float? height)) return false;

        size = new XuiSize(width, height);
        return true;
    }

    private static bool TryParseAxis(ReadOnlySpan<char> text, out float? value)
    {
        value = null;
        text = text.Trim();
        if (text.Length == 0) return false;
        if (text.Length == 1 && text[0] == '_') return true;
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            return false;
        value = parsed;
        return true;
    }

    /// <summary><c>x,y</c> — used by <c>pos</c>, <c>pivot</c>, <c>scale</c>, and animation offsets.</summary>
    public static bool TryParseFloat2(string text, out XuiFloat2 value)
    {
        value = default;
        if (string.IsNullOrEmpty(text)) return false;

        int split = text.IndexOf(',');
        if (split < 0) return false;
        if (text.IndexOf(',', split + 1) >= 0) return false;

        if (!float.TryParse(text.AsSpan(0, split).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
            return false;
        if (!float.TryParse(text.AsSpan(split + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            return false;

        value = new XuiFloat2(x, y);
        return true;
    }

    /// <summary>
    /// A transition's <c>from:to</c> offsets, in pixels relative to wherever layout put the element.
    /// </summary>
    /// <remarks>
    /// The colon is required and either side may be left empty to mean rest, so
    /// <c>move="0,-24:"</c> enters from below and <c>move=":0,-24"</c> leaves downward. Writing one
    /// point with no colon would be ambiguous in exactly the way that is invisible in a screenshot —
    /// the element would animate in the wrong direction and still look plausible in a still frame.
    /// </remarks>
    public static bool TryParseTweenOffset(string text, out XuiFloat2 from, out XuiFloat2 to)
        => TryParseTween(text, new XuiFloat2(0f, 0f), uniformAllowed: false, out from, out to);

    /// <summary>
    /// A transition's <c>from:to</c> scale. Rest is <c>1,1</c>, and a side may be a single number to
    /// scale both axes, so <c>scale="0.9:"</c> pops up to normal size.
    /// </summary>
    public static bool TryParseTweenScale(string text, out XuiFloat2 from, out XuiFloat2 to)
        => TryParseTween(text, new XuiFloat2(1f, 1f), uniformAllowed: true, out from, out to);

    /// <summary>A transition's <c>from:to</c> alpha. Rest is fully opaque, so <c>fade="0:"</c> fades in.</summary>
    public static bool TryParseTweenAlpha(string text, out float from, out float to)
    {
        from = to = 1f;
        if (!Split(text, out string left, out string right)) return false;
        if (!TryParseAlpha(left, out from)) return false;
        return TryParseAlpha(right, out to);
    }

    private static bool TryParseAlpha(string text, out float value)
    {
        if (text.Length == 0) { value = 1f; return true; }
        return TryParseFloat(text, out value) && value >= 0f && value <= 1f;
    }

    private static bool TryParseTween(
        string text, XuiFloat2 rest, bool uniformAllowed, out XuiFloat2 from, out XuiFloat2 to)
    {
        from = to = rest;
        if (!Split(text, out string left, out string right)) return false;
        if (!TryParseTweenPoint(left, rest, uniformAllowed, out from)) return false;
        return TryParseTweenPoint(right, rest, uniformAllowed, out to);
    }

    private static bool TryParseTweenPoint(
        string text, XuiFloat2 rest, bool uniformAllowed, out XuiFloat2 value)
    {
        if (text.Length == 0) { value = rest; return true; }
        if (TryParseFloat2(text, out value)) return true;
        if (uniformAllowed && TryParseFloat(text, out float uniform))
        {
            value = new XuiFloat2(uniform, uniform);
            return true;
        }
        value = rest;
        return false;
    }

    private static bool Split(string text, out string left, out string right)
    {
        left = right = string.Empty;
        if (text == null) return false;

        int colon = text.IndexOf(':');
        if (colon < 0) return false;
        if (text.IndexOf(':', colon + 1) >= 0) return false;

        left = text.AsSpan(0, colon).Trim().ToString();
        right = text.AsSpan(colon + 1).Trim().ToString();
        return left.Length > 0 || right.Length > 0;
    }

    /// <summary>
    /// Edge insets as <c>T,R,B,L</c>, or a single number applied to all four edges. Used by
    /// <c>padding</c> and <c>margin</c>.
    /// </summary>
    public static bool TryParseInsets(string text, out float top, out float right, out float bottom, out float left)
    {
        top = right = bottom = left = 0f;
        if (string.IsNullOrEmpty(text)) return false;

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float uniform))
        {
            top = right = bottom = left = uniform;
            return true;
        }

        ReadOnlySpan<char> span = text.AsSpan();
        Span<Range> parts = stackalloc Range[5];
        int count = SplitInto(span, ',', parts);
        if (count != 4) return false;

        return TryEdge(span[parts[0]], out top)
            && TryEdge(span[parts[1]], out right)
            && TryEdge(span[parts[2]], out bottom)
            && TryEdge(span[parts[3]], out left);
    }

    private static bool TryEdge(ReadOnlySpan<char> text, out float value)
        => float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static int SplitInto(ReadOnlySpan<char> text, char separator, Span<Range> ranges)
    {
        int count = 0;
        int start = 0;
        for (int i = 0; i < text.Length && count < ranges.Length; i++)
        {
            if (text[i] != separator) continue;
            ranges[count++] = new Range(start, i);
            start = i + 1;
        }
        if (count < ranges.Length)
            ranges[count++] = new Range(start, text.Length);
        return count;
    }

    public static bool TryParseAnchor(string text, out XuiAnchor anchor)
    {
        anchor = XuiAnchor.None;
        switch (text)
        {
            case "center": anchor = XuiAnchor.Center; return true;
            case "top": anchor = XuiAnchor.Top; return true;
            case "bottom": anchor = XuiAnchor.Bottom; return true;
            case "left": anchor = XuiAnchor.Left; return true;
            case "right": anchor = XuiAnchor.Right; return true;
            case "top-left": anchor = XuiAnchor.TopLeft; return true;
            case "top-right": anchor = XuiAnchor.TopRight; return true;
            case "bottom-left": anchor = XuiAnchor.BottomLeft; return true;
            case "bottom-right": anchor = XuiAnchor.BottomRight; return true;
            case "stretch": anchor = XuiAnchor.Stretch; return true;
            case "top-stretch": anchor = XuiAnchor.TopStretch; return true;
            case "bottom-stretch": anchor = XuiAnchor.BottomStretch; return true;
            case "left-stretch": anchor = XuiAnchor.LeftStretch; return true;
            case "right-stretch": anchor = XuiAnchor.RightStretch; return true;
            default: return false;
        }
    }

    public static bool TryParseCanvasMode(string text, out XuiCanvasMode mode)
    {
        switch (text)
        {
            case "overlay": mode = XuiCanvasMode.Overlay; return true;
            case "camera": mode = XuiCanvasMode.Camera; return true;
            case "world": mode = XuiCanvasMode.World; return true;
            default: mode = XuiCanvasMode.Overlay; return false;
        }
    }

    public static bool TryParseLayer(string text, out XuiLayerKind layer)
    {
        switch (text)
        {
            case "Normal": layer = XuiLayerKind.Normal; return true;
            case "Fixed": layer = XuiLayerKind.Fixed; return true;
            case "PopUp": layer = XuiLayerKind.PopUp; return true;
            case "Other": layer = XuiLayerKind.Other; return true;
            default: layer = XuiLayerKind.Normal; return false;
        }
    }

    public static bool TryParseScaleMode(string text, out XuiScaleMode mode)
    {
        switch (text)
        {
            case "auto": mode = XuiScaleMode.Auto; return true;
            case "pixel": mode = XuiScaleMode.Pixel; return true;
            default: mode = XuiScaleMode.Auto; return false;
        }
    }

    public static bool TryParseImageFit(string text, out XuiImageFit fit)
    {
        switch (text)
        {
            case "simple": fit = XuiImageFit.Simple; return true;
            case "sliced": fit = XuiImageFit.Sliced; return true;
            case "tiled": fit = XuiImageFit.Tiled; return true;
            case "filled": fit = XuiImageFit.Filled; return true;
            case "contain": fit = XuiImageFit.Contain; return true;
            default: fit = XuiImageFit.Auto; return false;
        }
    }

    public static bool TryParseEase(string text, out XuiEase ease) => XuiEasing.TryParse(text, out ease);

    public static bool TryParseProgressDirection(string text, out XuiProgressFill direction)
    {
        switch (text)
        {
            case "left-to-right": direction = XuiProgressFill.LeftToRight; return true;
            case "right-to-left": direction = XuiProgressFill.RightToLeft; return true;
            case "bottom-to-top": direction = XuiProgressFill.BottomToTop; return true;
            case "top-to-bottom": direction = XuiProgressFill.TopToBottom; return true;
            default: direction = XuiProgressFill.LeftToRight; return false;
        }
    }

    public static bool TryParseTextAlign(string text, out XuiTextAlign align)
    {
        switch (text)
        {
            case "left": align = XuiTextAlign.Left; return true;
            case "center": align = XuiTextAlign.Center; return true;
            case "right": align = XuiTextAlign.Right; return true;
            case "justify": align = XuiTextAlign.Justify; return true;
            default: align = XuiTextAlign.Left; return false;
        }
    }

    /// <summary>
    /// <c>true</c> / <c>false</c> only. Accepting <c>1</c> or <c>yes</c> here would make the lint rules
    /// ambiguous for no authoring benefit.
    /// </summary>
    public static bool TryParseBool(string text, out bool value)
    {
        switch (text)
        {
            case "true": value = true; return true;
            case "false": value = false; return true;
            default: value = false; return false;
        }
    }

    public static bool TryParseFloat(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
