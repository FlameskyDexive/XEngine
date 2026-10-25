// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>
/// The art a document names. The importer walks this to turn keys into GUIDs (and to register build
/// dependencies), and lint walks it to report keys that resolve to nothing.
/// </summary>
public sealed class XuiDocumentReferences
{
    private readonly SortedSet<string> _sprites = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _fonts = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Sprites => _sprites;

    public IReadOnlyCollection<string> Fonts => _fonts;

    /// <summary>
    /// Collects from screens and templates alike. Template bodies are scanned unexpanded, so a key
    /// only reachable through a parameter default is still seen — better to over-collect a dependency
    /// than to ship a build missing a sprite that one code path opens.
    /// </summary>
    public static XuiDocumentReferences Collect(XuiDocument document)
    {
        var references = new XuiDocumentReferences();

        for (int i = 0; i < document.Screens.Count; i++)
            references.Walk(document.Screens[i].Root);
        for (int i = 0; i < document.Templates.Count; i++)
            references.Walk(document.Templates[i].Root);

        return references;
    }

    private void Walk(ElementNode node)
    {
        Add(_sprites, node.Attribute("sprite"));
        Add(_fonts, node.Attribute("font"));

        for (int i = 0; i < node.Children.Count; i++)
            Walk(node.Children[i]);
    }

    private static void Add(SortedSet<string> set, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        // A key that still contains a placeholder is only knowable after expansion; resolving the
        // literal text would register a dependency on an asset path that does not exist.
        if (value.IndexOf('{') >= 0) return;
        set.Add(value);
    }
}
