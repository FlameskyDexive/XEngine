// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Scribe;

readonly struct LayoutCacheKey : IEquatable<LayoutCacheKey>
{
    public readonly string Text;
    public readonly int PxQ, LsQ, WsQ, LhQ, MwQ; // exact float bits; subpixel changes can alter wrapping
    public readonly int TabSize;
    public readonly FontFile? Font;
    public readonly Func<int, FontFile>? Selector;
    public readonly long? SelectorRevision;
    public readonly TextWrapMode Wrap;
    public readonly TextAlignment Align;
    // Quality selects a distinct atlas rasterization, so two layouts differing only in quality
    // must not share a cache entry (their glyphs resolve to different atlas regions).
    public readonly FontQuality Quality;
    public LayoutCacheKey(
        string text, float pixelSize, float letterSpacing, float wordSpacing, float lineHeight,
        int tabSize, TextWrapMode wrap, TextAlignment align, float maxWidth, FontFile? font,
        FontQuality quality, Func<int, FontFile>? selector, long? selectorRevision)
    {
        Text = text ?? string.Empty;
        PxQ = BitConverter.SingleToInt32Bits(pixelSize);
        LsQ = BitConverter.SingleToInt32Bits(letterSpacing);
        WsQ = BitConverter.SingleToInt32Bits(wordSpacing);
        LhQ = BitConverter.SingleToInt32Bits(lineHeight);
        MwQ = BitConverter.SingleToInt32Bits(maxWidth);
        TabSize = tabSize; Wrap = wrap; Align = align; Font = font;
        Quality = quality;
        Selector = selector; SelectorRevision = selectorRevision;
    }

    public bool Equals(LayoutCacheKey o) =>
        (ReferenceEquals(Text, o.Text) || (Text?.Equals(o.Text) ?? o.Text is null))
        && PxQ == o.PxQ && LsQ == o.LsQ && WsQ == o.WsQ && LhQ == o.LhQ && MwQ == o.MwQ
        && TabSize == o.TabSize && Wrap == o.Wrap && Align == o.Align && ReferenceEquals(Font, o.Font)
        && Quality == o.Quality && ReferenceEquals(Selector, o.Selector) && SelectorRevision == o.SelectorRevision;

    public override bool Equals(object obj) => obj is LayoutCacheKey o && Equals(o);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = Text?.GetHashCode() ?? 0;
            h = (h * 397) ^ PxQ; h = (h * 397) ^ LsQ; h = (h * 397) ^ WsQ; h = (h * 397) ^ LhQ; h = (h * 397) ^ MwQ;
            h = (h * 397) ^ TabSize; h = (h * 397) ^ (int)Wrap; h = (h * 397) ^ (int)Align;
            h = (h * 397) ^ (Font is null ? 0 : RuntimeHelpers.GetHashCode(Font));
            h = (h * 397) ^ (int)Quality;
            h = (h * 397) ^ (Selector is null ? 0 : RuntimeHelpers.GetHashCode(Selector));
            h = (h * 397) ^ SelectorRevision.GetHashCode();
            return h;
        }
    }
}
