// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.XUI.Core;

/// <summary>
/// One authored attribute, kept in source order. The IR stores attributes as an ordered list rather
/// than a dictionary because <c>xui_patch</c> addresses them positionally and round-trip writes must
/// not reshuffle a hand-authored file.
/// </summary>
public sealed class XuiAttribute
{
    public XuiAttribute(string name, string value, XuiSourceSpan span = default)
    {
        Name = name;
        Value = value;
        Span = span;
    }

    public string Name { get; }

    /// <summary>Raw authored text. Interpretation (color token, size spec, anchor preset) is the
    /// applier's job, not the parser's — the IR stays lossless.</summary>
    public string Value { get; set; }

    public XuiSourceSpan Span { get; }

    public override string ToString() => Name + "=\"" + Value + "\"";
}
