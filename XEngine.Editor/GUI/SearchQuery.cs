// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Editor.GUI;

/// <summary>
/// Parsed asset-search query speaking Unity's Project-search dialect: bare words are
/// case-insensitive name substrings (AND), <c>t:Type</c> filters by type-name substring (AND),
/// <c>l:Label</c> filters by asset label (AND across filters, any-of within an asset's labels).
/// </summary>
public sealed class SearchQuery
{
    public readonly List<string> Names = new();
    public readonly List<string> Types = new();
    public readonly List<string> Labels = new();

    public bool IsEmpty => Names.Count == 0 && Types.Count == 0 && Labels.Count == 0;

    public static SearchQuery Parse(string? text)
    {
        var query = new SearchQuery();
        if (string.IsNullOrWhiteSpace(text)) return query;
        foreach (string raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
            {
                if (raw.Length > 2) query.Types.Add(raw[2..]);
            }
            else if (raw.StartsWith("l:", StringComparison.OrdinalIgnoreCase))
            {
                if (raw.Length > 2) query.Labels.Add(raw[2..]);
            }
            else
                query.Names.Add(raw);
        }
        return query;
    }

    public bool Matches(string name, string typeLabel, IReadOnlyList<string>? labels)
    {
        for (int i = 0; i < Names.Count; i++)
            if (!name.Contains(Names[i], StringComparison.OrdinalIgnoreCase))
                return false;

        for (int i = 0; i < Types.Count; i++)
            if (!typeLabel.Contains(Types[i], StringComparison.OrdinalIgnoreCase))
                return false;

        for (int i = 0; i < Labels.Count; i++)
        {
            bool any = false;
            if (labels != null)
                for (int j = 0; j < labels.Count; j++)
                    if (labels[j].Contains(Labels[i], StringComparison.OrdinalIgnoreCase)) { any = true; break; }
            if (!any) return false;
        }
        return true;
    }
}
