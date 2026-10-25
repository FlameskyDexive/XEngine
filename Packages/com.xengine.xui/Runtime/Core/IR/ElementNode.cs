// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>
/// One element in a screen or template body: a tag, its ordered attributes, its inline text, and its
/// children. This is the whole tree shape — there is no separate "control" node type, because the
/// assembler resolves a tag to XGUI components through <see cref="XuiTagLibrary"/> at build time.
/// </summary>
public sealed class ElementNode
{
    public ElementNode(string tag, XuiSourceSpan span = default)
    {
        Tag = tag;
        Span = span;
    }

    public string Tag { get; }

    public XuiSourceSpan Span { get; }

    /// <summary>Ordered attributes as authored.</summary>
    public List<XuiAttribute> Attributes { get; } = new();

    public List<ElementNode> Children { get; } = new();

    /// <summary>Inline text content (`&lt;Text&gt;Hello&lt;/Text&gt;`), trimmed. Null when absent.</summary>
    public string? Text { get; set; }

    /// <summary>Convenience accessor for the <c>id</c> attribute, the handle business code binds to.</summary>
    public string? Id => Attribute("id");

    public string? Attribute(string name)
    {
        for (int i = 0; i < Attributes.Count; i++)
        {
            if (string.Equals(Attributes[i].Name, name, StringComparison.Ordinal))
                return Attributes[i].Value;
        }
        return null;
    }

    public bool HasAttribute(string name) => Attribute(name) != null;

    public XuiAttribute? FindAttribute(string name)
    {
        for (int i = 0; i < Attributes.Count; i++)
        {
            if (string.Equals(Attributes[i].Name, name, StringComparison.Ordinal))
                return Attributes[i];
        }
        return null;
    }

    /// <summary>Sets an attribute in place, or appends it when absent. Used by <c>xui_patch</c>.</summary>
    public void SetAttribute(string name, string value)
    {
        XuiAttribute? existing = FindAttribute(name);
        if (existing != null)
        {
            existing.Value = value;
            return;
        }
        Attributes.Add(new XuiAttribute(name, value));
    }

    public bool RemoveAttribute(string name)
    {
        for (int i = 0; i < Attributes.Count; i++)
        {
            if (string.Equals(Attributes[i].Name, name, StringComparison.Ordinal))
            {
                Attributes.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    public override string ToString() => Id == null ? "<" + Tag + ">" : "<" + Tag + " id=\"" + Id + "\">";
}
