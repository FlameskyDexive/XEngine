// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XEngine.Ai.Host;

/// <summary>
/// Splices attribute and text edits directly into the document source.
/// </summary>
/// <remarks>
/// <para>
/// The obvious implementation — mutate an <see cref="XDocument"/> and save it — is not usable here.
/// <see cref="XmlWriter"/> has no setting to stop it writing <c>&lt;Color … /&gt;</c> where the author
/// wrote <c>&lt;Color …/&gt;</c>, so every patch would reformat every self-closing tag in the file and
/// bury the real change in an unreviewable diff.
/// </para>
/// <para>
/// So the XDocument is used only to <em>locate</em> things — <see cref="IXmlLineInfo"/> gives the exact
/// position of every element and attribute — and the edits are applied to the original text. Everything
/// the patch did not name comes out byte-for-byte unchanged.
/// </para>
/// </remarks>
internal sealed class XuiTextEdit
{
    private readonly string _text;
    private readonly int[] _lineStarts;
    private readonly List<Span> _edits = new();

    private readonly record struct Span(int Start, int Length, string Replacement);

    public XuiTextEdit(string text)
    {
        _text = text;
        _lineStarts = BuildLineIndex(text);
    }

    /// <summary>Sets an attribute, or appends it to the element's start tag when it is absent.</summary>
    public void SetAttribute(XElement element, string name, string value)
    {
        XAttribute? existing = element.Attribute(name);
        if (existing != null)
        {
            (int start, int length) = ValueSpan(existing);
            _edits.Add(new Span(start, length, Escape(value)));
            return;
        }

        int insertAt = StartTagEnd(element, out _);
        _edits.Add(new Span(insertAt, 0, " " + name + "=\"" + Escape(value) + "\""));
    }

    /// <summary>Removes an attribute along with the single space that separated it.</summary>
    public void RemoveAttribute(XElement element, string name)
    {
        XAttribute attribute = element.Attribute(name)
            ?? throw new KeyNotFoundException("No attribute '" + name + "' on <" + element.Name.LocalName + ">.");

        int nameStart = Offset(attribute);
        (int valueStart, int valueLength) = ValueSpan(attribute);
        int end = valueStart + valueLength + 1; // past the closing quote

        // Take the preceding whitespace with it, so removing an attribute does not leave a double space.
        int start = nameStart;
        while (start > 0 && (_text[start - 1] == ' ' || _text[start - 1] == '\t')) start--;

        _edits.Add(new Span(start, end - start, string.Empty));
    }

    /// <summary>
    /// Replaces an element's inline text. A self-closing element is expanded into an open/close pair,
    /// which is the only way to give it text at all.
    /// </summary>
    public void SetText(XElement element, string value)
    {
        int tagEnd = StartTagEnd(element, out bool selfClosing);

        if (selfClosing)
        {
            // "<Text id="x"/>" -> "<Text id="x">value</Text>"
            int closeStart = tagEnd;              // at '/'
            int closeEnd = _text.IndexOf('>', closeStart) + 1;
            _edits.Add(new Span(
                closeStart,
                closeEnd - closeStart,
                ">" + Escape(value) + "</" + element.Name.LocalName + ">"));
            return;
        }

        int contentStart = _text.IndexOf('>', tagEnd) + 1;
        int contentEnd = FindClosingTag(element, contentStart);
        _edits.Add(new Span(contentStart, contentEnd - contentStart, Escape(value)));
    }

    /// <summary>Applies every edit to the original text. Edits are keyed to the original offsets, so
    /// they are applied back-to-front and never shift each other.</summary>
    public string Apply()
    {
        if (_edits.Count == 0) return _text;

        _edits.Sort((a, b) => b.Start.CompareTo(a.Start));
        var builder = new StringBuilder(_text);
        for (int i = 0; i < _edits.Count; i++)
        {
            Span edit = _edits[i];
            builder.Remove(edit.Start, edit.Length);
            builder.Insert(edit.Start, edit.Replacement);
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------------
    //  Locating
    // ------------------------------------------------------------------

    /// <summary>Offset of the attribute's value, excluding the surrounding quotes.</summary>
    private (int Start, int Length) ValueSpan(XAttribute attribute)
    {
        int cursor = Offset(attribute) + attribute.Name.LocalName.Length;
        while (cursor < _text.Length && char.IsWhiteSpace(_text[cursor])) cursor++;
        if (cursor >= _text.Length || _text[cursor] != '=')
            throw new InvalidOperationException("Malformed attribute near offset " + cursor + ".");
        cursor++;
        while (cursor < _text.Length && char.IsWhiteSpace(_text[cursor])) cursor++;

        char quote = _text[cursor];
        if (quote != '"' && quote != '\'')
            throw new InvalidOperationException("Unquoted attribute value near offset " + cursor + ".");

        int start = cursor + 1;
        int end = _text.IndexOf(quote, start);
        if (end < 0) throw new InvalidOperationException("Unterminated attribute value near offset " + start + ".");
        return (start, end - start);
    }

    /// <summary>
    /// Offset of the character that closes the element's start tag — the <c>/</c> of <c>/&gt;</c>, or
    /// the <c>&gt;</c> itself. Quoted regions are skipped so a <c>&gt;</c> inside a value cannot fool it.
    /// </summary>
    private int StartTagEnd(XElement element, out bool selfClosing)
    {
        int cursor = Offset(element);
        char quote = '\0';

        while (cursor < _text.Length)
        {
            char c = _text[cursor];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
            }
            else if (c == '/' && cursor + 1 < _text.Length && _text[cursor + 1] == '>')
            {
                selfClosing = true;
                return cursor;
            }
            else if (c == '>')
            {
                selfClosing = false;
                return cursor;
            }
            cursor++;
        }

        throw new InvalidOperationException("Unterminated start tag for <" + element.Name.LocalName + ">.");
    }

    /// <summary>Offset of the element's closing tag, given where its content begins.</summary>
    private int FindClosingTag(XElement element, int contentStart)
    {
        string closing = "</" + element.Name.LocalName;
        int index = _text.IndexOf(closing, contentStart, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException("No closing tag for <" + element.Name.LocalName + ">.");
        return index;
    }

    private int Offset(XObject node)
    {
        if (node is not IXmlLineInfo info || !info.HasLineInfo())
            throw new InvalidOperationException("The document was not loaded with LoadOptions.SetLineInfo.");

        int line = info.LineNumber - 1;
        if (line < 0 || line >= _lineStarts.Length)
            throw new InvalidOperationException("Line " + info.LineNumber + " is outside the document.");

        return _lineStarts[line] + info.LinePosition - 1;
    }

    private static int[] BuildLineIndex(string text)
    {
        var starts = new List<int>(64) { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            starts.Add(i + 1);
        }
        return starts.ToArray();
    }

    /// <summary>
    /// Escapes only what must be escaped inside an attribute value or text node. Leaving <c>'</c> and
    /// <c>&gt;</c> alone keeps hand-written content looking the way it was written.
    /// </summary>
    private static string Escape(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
