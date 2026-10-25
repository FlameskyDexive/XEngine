// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>
/// The translations for one locale, indexed for lookup.
/// </summary>
/// <remarks>
/// Separate from <see cref="PoCatalog"/> on purpose: that one is the editable file, with comments,
/// references, and obsolete entries a translator needs; this one is what the running game asks, and it
/// holds only what a lookup requires.
/// </remarks>
public sealed class XuiCatalog
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    public XuiCatalog(string locale)
    {
        Locale = locale ?? string.Empty;
    }

    /// <summary>BCP-47-ish locale tag, e.g. <c>zh-Hans</c>. Matched case-insensitively.</summary>
    public string Locale { get; }

    public int Count => _entries.Count;

    /// <summary>
    /// Builds a lookup from a parsed <c>.po</c>. Obsolete entries and empty translations are dropped —
    /// both mean "fall back to the source string", and keeping them would only cost lookups.
    /// </summary>
    public static XuiCatalog FromPo(PoCatalog po, string locale)
    {
        var catalog = new XuiCatalog(locale);
        if (po == null) return catalog;

        for (int i = 0; i < po.Entries.Count; i++)
        {
            PoEntry entry = po.Entries[i];
            if (entry.IsObsolete || entry.Translation.Length == 0) continue;
            catalog._entries[entry.Key] = entry.Translation;
        }
        return catalog;
    }

    public void Add(string? context, string id, string translation)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(translation)) return;
        _entries[MakeKey(context, id)] = translation;
    }

    /// <summary>
    /// Translates, falling back to the source string.
    /// </summary>
    /// <remarks>
    /// Never returns a placeholder like <c>??key??</c>. A missing translation should leave the UI
    /// readable in the source language; a screen full of markers is how a shipped build ends up
    /// unusable for the one locale nobody tested.
    /// </remarks>
    public string Translate(string source, string? context = null)
    {
        if (string.IsNullOrEmpty(source)) return source;
        return _entries.TryGetValue(MakeKey(context, source), out string? translation) ? translation : source;
    }

    public bool Has(string source, string? context = null)
        => !string.IsNullOrEmpty(source) && _entries.ContainsKey(MakeKey(context, source));

    /// <summary>
    /// The lookup key. A context and the source string are joined by U+0004, which is what
    /// gettext itself uses, so a context never collides with a source string containing the separator.
    /// </summary>
    public static string MakeKey(string? context, string id)
        => string.IsNullOrEmpty(context) ? id : context + "\u0004" + id;
}
