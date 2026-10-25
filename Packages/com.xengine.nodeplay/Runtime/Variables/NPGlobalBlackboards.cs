using System;
using System.Collections.Generic;

namespace XEngine.NodePlay;

/// <summary>
/// Engine-level named scopes sitting above scene objects — the default being "Global". Namespaced
/// access from any graph uses "<c>&lt;scope&gt;/&lt;var&gt;</c>". Instances persist nothing here:
/// runtimes that need durable globals serialize their own <see cref="NPBlackboardSource"/> asset.
/// </summary>
public static class NPGlobalBlackboards
{
    public const string DefaultScopeName = "Global";

    private static readonly Dictionary<string, NPBlackboardSource> Scopes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the named global scope, creating it on first use.</summary>
    public static NPBlackboardSource Ensure(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultScopeName;
        if (!Scopes.TryGetValue(name, out NPBlackboardSource? scope))
        {
            scope = new NPBlackboardSource { Identifier = name };
            Scopes[name] = scope;
        }

        return scope;
    }

    public static NPBlackboardSource Default => Ensure(DefaultScopeName);

    /// <summary>All live scope identifiers, parent-most ordering not guaranteed.</summary>
    public static IEnumerable<string> ScopeNames => Scopes.Keys;

    /// <summary>Drops every scope — called on play-mode enter/exit so separate runs start clean.</summary>
    public static void ResetAll() => Scopes.Clear();
}
