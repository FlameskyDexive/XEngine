using System;
using System.Collections.Generic;

namespace XEngine.NodePlay;

/// <summary>
/// Concrete blackboard storage persisted inside graph assets (and reused in-memory by runner /
/// global scopes). Lookups walk <see cref="Parent"/> when a name misses here; names containing a
/// slash ("<c>Global/morale</c>") are routed to the named upstream scope first.
/// </summary>
public class NPBlackboardSource : INPBlackboard
{
    public const char ScopeSeparator = '/';

    public string Identifier { get; set; } = "";
    public List<NPVariable> Variables = new();

    public INPBlackboard? Parent { get; set; }

    /// <summary>Reserved seam for post-v1 network sync: raise this after Set writes (never persisted).</summary>
    public static event Action<NPBlackboardSource, NPVariable, object>? VariableWritten;

    private Dictionary<string, NPVariable>? _lookup;

    public override string ToString() => Identifier;

    public void OnValidate()
    {
        Variables ??= new List<NPVariable>();
        RebuildLookup();
    }

    public IReadOnlyList<NPVariable> All => Variables;

    public NPVariable Add(string name, object initial, bool isPublic = false)
    {
        var variable = new NPVariable
        {
            Name = name,
            Value = initial ?? "",
            IsPublic = isPublic,
        };
        Remove(name);
        Variables.Add(variable);
        Index(variable);
        return variable;
    }

    public bool Remove(string name)
    {
        NPVariable? existing = GetLocal(name);
        if (existing == null)
            return false;
        Variables.Remove(existing);
        _lookup?.Remove(name);
        return true;
    }

    public NPVariable? Get(string name)
    {
        if (TrySplitScope(name, out string? scope, out string local))
            return ResolveScope(scope).Get(local);

        NPVariable own = GetLocal(name);
        if (own != null)
            return own;
        return Parent?.Get(name);
    }

    public T GetValue<T>(string name, T fallback)
    {
        NPVariable? v = Get(name);
        return v != null ? v.GetValue(fallback) : fallback;
    }

    public void SetValue(string name, object value)
    {
        NPVariable? target = GetLocalForWrite(name);
        if (target == null)
            return;
        if (!target.TrySetObject(value))
            return;
        VariableWritten?.Invoke(this, target, value);
    }

    /// <summary>Returns the variable by name, creating it here when absent ("_x" dynamics included).</summary>
    public NPVariable Ensure(string name, object seedValue)
    {
        if (TrySplitScope(name, out string? scope, out string local))
        {
            var targetScope = ResolveScope(scope) as NPBlackboardSource
                              ?? throw new InvalidOperationException($"scope '{scope}' is not writable storage");
            return targetScope.Ensure(local, seedValue);
        }

        NPVariable? existing = GetLocal(name);
        return existing ?? Add(name, seedValue);
    }

    // ---- internals ----------------------------------------------------------------------

    private NPVariable? GetLocal(string name)
    {
        if (_lookup == null)
            RebuildLookup();
        return _lookup!.GetValueOrDefault(name);
    }

    private NPVariable? GetLocalForWrite(string name)
    {
        if (TrySplitScope(name, out string? scope, out string local))
            return ResolveScope(scope).Get(local);

        NPVariable local1 = GetLocal(name);
        if (local1 != null)
            return local1;

        // Writing an unknown "_"-prefixed name auto-promotes it into this scope ("dynamic params").
        if (name.StartsWith("_"))
            return Add(name, "");
        return Parent?.Get(name);
    }

    private static bool TrySplitScope(string fullName, out string? scope, out string local)
    {
        int sep = fullName.IndexOf(ScopeSeparator);
        if (sep <= 0 || sep >= fullName.Length - 1)
        {
            scope = null;
            local = fullName;
            return false;
        }

        scope = fullName[..sep];
        local = fullName[(sep + 1)..];
        return true;
    }

    private INPBlackboard ResolveScope(string scope)
    {
        INPBlackboard? current = this as INPBlackboard;
        while (current != null)
        {
            if (string.Equals(current.Identifier, scope, StringComparison.OrdinalIgnoreCase))
                return current;
            current = current.Parent;
        }

        // Named non-parent scopes resolve through the global registry ("Global/x").
        return NPGlobalBlackboards.Ensure(scope);
    }

    private void RebuildLookup()
    {
        _lookup = new Dictionary<string, NPVariable>(StringComparer.Ordinal);
        for (int i = 0; i < Variables.Count; i++)
            Index(Variables[i]);
    }

    private void Index(NPVariable variable)
    {
        _lookup ??= new Dictionary<string, NPVariable>(StringComparer.Ordinal);
        _lookup[variable.Name] = variable;
    }
}
