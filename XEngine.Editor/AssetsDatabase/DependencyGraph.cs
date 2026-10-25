using System;
using System.Collections.Generic;

namespace XEngine.Editor;

/// <summary>
/// One typed dependency edge: target asset GUID + the role it plays for the depending asset
/// (e.g. "Texture", "Material", "Prefab", "Config", "Script"). ★ Layer 0 (A3): previously edges were
/// pure binary (guid->guid), so a material referencing a texture and a prefab referencing a config
/// were indistinguishable. Importers emit the role where known; callers without a role use the
/// backward-compatible Guid-only overloads (role defaults to "References").
/// </summary>
public readonly record struct DependencyEdge(Guid Target, string Role);

/// <summary>
/// Tracks forward and reverse dependencies between assets.
/// Forward: asset -> what it depends on. Reverse: asset -> what depends on it.
/// </summary>
public class DependencyGraph
{
    private const string DefaultRole = "References";

    // Forward/reverse edges now carry a role. Backward-compatible Guid-only overloads wrap these.
    private readonly Dictionary<Guid, HashSet<DependencyEdge>> _forward = new();
    private readonly Dictionary<Guid, HashSet<DependencyEdge>> _reverse = new();

    /// <summary>Set dependencies WITHOUT a role (defaults to "References"). Backward compatible.</summary>
    public void SetDependencies(Guid asset, IEnumerable<Guid> dependencies)
    {
        var typed = new List<DependencyEdge>();
        foreach (Guid d in dependencies) typed.Add(new DependencyEdge(d, DefaultRole));
        SetDependenciesTyped(asset, typed);
    }

    /// <summary>★ A3 — Set dependencies WITH a role label. Lets an AI (or the asset-inspect tool)
    /// distinguish "references as texture" from "references as prefab".</summary>
    public void SetDependencies(Guid asset, IEnumerable<DependencyEdge> dependencies, bool typed)
    {
        // The 'typed' bool disambiguates from the Guid overload at the call site (same arity).
        _ = typed;
        SetDependenciesTyped(asset, dependencies);
    }

    private void SetDependenciesTyped(Guid asset, IEnumerable<DependencyEdge> dependencies)
    {
        // Remove old reverse links
        if (_forward.TryGetValue(asset, out HashSet<DependencyEdge>? oldDeps))
        {
            foreach (DependencyEdge dep in oldDeps)
            {
                _reverse.GetValueOrDefault(dep.Target)?.Remove(new DependencyEdge(asset, dep.Role));
            }
        }

        // Set new forward links
        var depSet = new HashSet<DependencyEdge>(dependencies);
        _forward[asset] = depSet;

        // Build reverse links (mirror the role so reverse lookups stay typed)
        foreach (DependencyEdge dep in depSet)
        {
            if (!_reverse.TryGetValue(dep.Target, out HashSet<DependencyEdge>? dependents))
            {
                dependents = new HashSet<DependencyEdge>();
                _reverse[dep.Target] = dependents;
            }
            dependents.Add(new DependencyEdge(asset, dep.Role));
        }
    }

    public void RemoveAsset(Guid asset)
    {
        if (_forward.TryGetValue(asset, out HashSet<DependencyEdge>? deps))
        {
            foreach (DependencyEdge dep in deps)
            {
                _reverse.GetValueOrDefault(dep.Target)?.Remove(new DependencyEdge(asset, dep.Role));
            }
            _forward.Remove(asset);
        }

        if (_reverse.TryGetValue(asset, out HashSet<DependencyEdge>? dependents))
        {
            foreach (DependencyEdge dep in dependents)
            {
                _forward.GetValueOrDefault(dep.Target)?.Remove(new DependencyEdge(asset, dep.Role));
            }
            _reverse.Remove(asset);
        }
    }

    /// <summary>What does this asset depend on? (Guid set — backward compatible)</summary>
    public IReadOnlySet<Guid> GetDependencies(Guid asset)
    {
        HashSet<DependencyEdge>? edges = _forward.GetValueOrDefault(asset);
        if (edges is null) return new HashSet<Guid>();
        var set = new HashSet<Guid>();
        foreach (DependencyEdge e in edges) set.Add(e.Target);
        return set;
    }

    /// <summary>What assets depend on this one? (Guid set — backward compatible)</summary>
    public IReadOnlySet<Guid> GetDependents(Guid asset)
    {
        HashSet<DependencyEdge>? edges = _reverse.GetValueOrDefault(asset);
        if (edges is null) return new HashSet<Guid>();
        var set = new HashSet<Guid>();
        foreach (DependencyEdge e in edges) set.Add(e.Target);
        return set;
    }

    /// <summary>★ A3 — Forward dependencies WITH roles. Returns the typed edges so a caller can tell
    /// a texture ref from a prefab ref.</summary>
    public IReadOnlyCollection<DependencyEdge> GetDependenciesWithRoles(Guid asset)
        => (IReadOnlyCollection<DependencyEdge>?)_forward.GetValueOrDefault(asset)
           ?? Array.Empty<DependencyEdge>();

    /// <summary>★ A3 — Reverse dependencies WITH roles.</summary>
    public IReadOnlyCollection<DependencyEdge> GetDependentsWithRoles(Guid asset)
        => (IReadOnlyCollection<DependencyEdge>?)_reverse.GetValueOrDefault(asset)
           ?? Array.Empty<DependencyEdge>();

    /// <summary>Get all assets that transitively depend on the given roots.</summary>
    public HashSet<Guid> GetTransitiveDependents(IEnumerable<Guid> roots)
    {
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>(roots);
        while (queue.Count > 0)
        {
            Guid current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            foreach (Guid dep in GetDependents(current))
                queue.Enqueue(dep);
        }
        return visited;
    }

    /// <summary>Get all assets that the given roots transitively depend on (forward walk).</summary>
    public HashSet<Guid> GetTransitiveDependencies(IEnumerable<Guid> roots)
    {
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>(roots);
        while (queue.Count > 0)
        {
            Guid current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            foreach (Guid dep in GetDependencies(current))
                queue.Enqueue(dep);
        }
        return visited;
    }

    public void Clear()
    {
        _forward.Clear();
        _reverse.Clear();
    }
}

