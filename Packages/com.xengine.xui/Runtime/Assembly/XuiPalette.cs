// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// Resolves <c>color=</c> attributes. A document's <c>&lt;Theme&gt;</c> blocks seed the token table;
/// a runtime theme swap replaces the active table and every screen rebuilt afterwards picks up the new
/// values, which is why the IR keeps tokens unresolved instead of baking literals at parse time.
/// </summary>
public sealed class XuiPalette
{
    private readonly Dictionary<string, Color> _tokens = new(StringComparer.Ordinal);

    /// <summary>The palette used when a document declares no theme: literals only, no tokens.</summary>
    public static XuiPalette Empty { get; } = new();

    public string? ActiveTheme { get; private set; }

    /// <summary>
    /// Builds the token table for one theme, following <c>base=</c> first so a derived theme overrides
    /// only what it redeclares. Returns an empty palette when the name is unknown, because a missing
    /// theme is a lint error, not a reason to fail instantiation at runtime.
    /// </summary>
    public static XuiPalette FromDocument(XuiDocument document, string? themeName = null)
    {
        var palette = new XuiPalette();
        if (document.Themes.Count == 0) return palette;

        ThemeBlock? theme = null;
        if (themeName == null)
        {
            theme = document.Themes[0];
        }
        else
        {
            for (int i = 0; i < document.Themes.Count; i++)
            {
                if (!string.Equals(document.Themes[i].Name, themeName, StringComparison.Ordinal)) continue;
                theme = document.Themes[i];
                break;
            }
        }

        if (theme == null) return palette;

        palette.ActiveTheme = theme.Name;
        palette.Apply(document, theme, depth: 0);
        return palette;
    }

    private void Apply(XuiDocument document, ThemeBlock theme, int depth)
    {
        // A base chain that loops would otherwise hang the first screen that opens.
        if (depth > 8) return;

        if (theme.Base != null)
        {
            for (int i = 0; i < document.Themes.Count; i++)
            {
                if (!string.Equals(document.Themes[i].Name, theme.Base, StringComparison.Ordinal)) continue;
                Apply(document, document.Themes[i], depth + 1);
                break;
            }
        }

        for (int i = 0; i < theme.Colors.Count; i++)
        {
            ColorEntry entry = theme.Colors[i];
            if (XuiColorParser.TryParseLiteral(entry.Value, out XuiColor color))
                _tokens[entry.Name] = ToEngine(color);
        }
    }

    public bool TryGetToken(string name, out Color color) => _tokens.TryGetValue(name, out color);

    /// <summary>
    /// Resolves an authored color attribute. An unknown token falls back to <paramref name="fallback"/>
    /// rather than throwing — the screen still renders and lint names the missing token.
    /// </summary>
    public Color Resolve(string text, Color fallback)
    {
        XuiColor parsed = XuiColorParser.Parse(text);
        if (!parsed.IsToken) return ToEngine(parsed);
        return _tokens.TryGetValue(parsed.Token!, out Color token) ? token : fallback;
    }

    public static Color ToEngine(in XuiColor color) => new(color.R, color.G, color.B, color.A);
}
