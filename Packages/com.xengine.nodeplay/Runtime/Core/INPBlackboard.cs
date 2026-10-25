namespace XEngine.NodePlay;

/// <summary>
/// Scoped variable store contract implemented by <see cref="NPBlackboardSource"/>. Tasks and nodes
/// talk to this interface so graph-local / gameobject / global scoping stays transparent to
/// authors — lookups walk the chain, writes auto-promote "_" dynamics.
/// </summary>
public interface INPBlackboard
{
    /// <summary>Scope label ("Global", an object name, or a subgraph key).</summary>
    string Identifier { get; }

    /// <summary>Next scope searched when a lookup misses here; null ends the chain.</summary>
    INPBlackboard? Parent { get; set; }

    /// <summary>Chain-aware lookup ("<c>Scope/var</c>" syntax included); null when nowhere defined.</summary>
    NPVariable? Get(string name);

    /// <summary>Typed read with converter fallback and per-scope default.</summary>
    T GetValue<T>(string name, T fallback);

    /// <summary>Write-through set; unknown dynamic ("_x") names self-register.</summary>
    void SetValue(string name, object value);

    /// <summary>Get-or-create semantics used by setter nodes and mappings.</summary>
    NPVariable Ensure(string name, object seedValue);
}
