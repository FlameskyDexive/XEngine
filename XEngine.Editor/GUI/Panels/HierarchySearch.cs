// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Editor.GUI.Panels;

/// <summary>Unity-style Hierarchy search: name substring plus <c>t:Type</c> component filters.</summary>
public readonly struct HierarchySearchQuery
{
    public HierarchySearchQuery(string name, string[] typeFilters)
    {
        Name = name ?? "";
        TypeFilters = typeFilters ?? Array.Empty<string>();
    }

    public string Name { get; }
    public string[] TypeFilters { get; }
    public bool IsEmpty => Name.Length == 0 && TypeFilters.Length == 0;
}

public static class HierarchySearch
{
    private static readonly List<MonoBehaviour> s_compScratch = new();

    public static HierarchySearchQuery Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new HierarchySearchQuery("", Array.Empty<string>());

        string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var types = new List<string>();
        var nameParts = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length >= 2 && (part[0] == 't' || part[0] == 'T') && part[1] == ':')
            {
                string type = part.Length > 2 ? part[2..] : "";
                if (type.Length > 0)
                    types.Add(type);
                continue;
            }
            nameParts.Add(part);
        }

        string name = nameParts.Count == 0 ? "" : string.Join(' ', nameParts);
        return new HierarchySearchQuery(name, types.Count == 0 ? Array.Empty<string>() : types.ToArray());
    }

    public static bool Matches(GameObject go, in HierarchySearchQuery query)
    {
        if (go == null) return false;
        if (query.IsEmpty) return true;
        if (query.Name.Length > 0
            && (go.Name == null || go.Name.IndexOf(query.Name, StringComparison.OrdinalIgnoreCase) < 0))
            return false;
        return MatchesTypes(go, query.TypeFilters);
    }

    public static bool MatchesSelfOrDescendant(GameObject go, in HierarchySearchQuery query)
    {
        if (Matches(go, query)) return true;
        if (go?.Children == null) return false;
        for (int i = 0; i < go.Children.Count; i++)
        {
            if (MatchesSelfOrDescendant(go.Children[i], query))
                return true;
        }
        return false;
    }

    public static bool MatchesTypes(GameObject go, string[] typeFilters)
    {
        if (typeFilters == null || typeFilters.Length == 0) return true;
        if (go == null) return false;
        go.GetComponents(s_compScratch);
        for (int t = 0; t < typeFilters.Length; t++)
        {
            string filter = typeFilters[t];
            bool found = false;
            for (int i = 0; i < s_compScratch.Count; i++)
            {
                var comp = s_compScratch[i];
                if (comp == null) continue;
                string typeName = comp.GetType().Name;
                if (typeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }
}
