// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Editor.GUI;

/// <summary>
/// Memoized UI element-id concatenation. Editor panels build Paper element ids per rendered
/// frame from a stable prefix plus a per-object key (e.g. <c>$"hier_name_{goId}"</c>).
/// Naive interpolation allocates a fresh string for every visible row on every frame, which
/// keeps Gen0 GC pressure high and shows up as periodic editor stutter. Paper only hashes the
/// id string (it is not retained), so returning the same cached instance across frames is safe
/// and also lets hash lookups hit reference-equality fast paths.
/// </summary>
public static class UiId
{
    // (prefix, key) -> cached "prefix + key". Bounded by object counts, not frame count;
    // cleared wholesale when the cache grows past the safety cap (scene churn).
    private const int MaxEntries = 65536;

    private static readonly Dictionary<(string Prefix, string Key), string> _byString = new();
    private static readonly Dictionary<(string Prefix, long Key), string> _byLong = new();
    private static readonly Dictionary<(string Prefix, int Key), string> _byInt = new();
    private static readonly Dictionary<(string Prefix, Guid Key), string> _byGuid = new();

    /// <summary>Cached <c>prefix + key</c> for string keys (GameObject guids, asset paths...).</summary>
    public static string Cat(string prefix, string key)
    {
        var k = (prefix, key);
        if (_byString.TryGetValue(k, out string? cached)) return cached;
        if (_byString.Count >= MaxEntries) _byString.Clear();
        return _byString[k] = prefix + key;
    }

    /// <summary>Cached <c>prefix + key</c> for long keys (instance ids, component hashes...).</summary>
    public static string Cat(string prefix, long key)
    {
        var k = (prefix, key);
        if (_byLong.TryGetValue(k, out string? cached)) return cached;
        if (_byLong.Count >= MaxEntries) _byLong.Clear();
        return _byLong[k] = prefix + key.ToString();
    }

    /// <summary>Cached <c>prefix + key</c> for int keys.</summary>
    public static string Cat(string prefix, int key)
    {
        var k = (prefix, key);
        if (_byInt.TryGetValue(k, out string? cached)) return cached;
        if (_byInt.Count >= MaxEntries) _byInt.Clear();
        return _byInt[k] = prefix + key.ToString();
    }

    /// <summary>Cached <c>prefix + key</c> for <see cref="Guid"/> keys (GameObject/component
    /// identifiers, asset guids). Uses the default "D" format, matching <c>$"{guid}"</c> interpolation.</summary>
    public static string Cat(string prefix, Guid key)
    {
        var k = (prefix, key);
        if (_byGuid.TryGetValue(k, out string? cached)) return cached;
        if (_byGuid.Count >= MaxEntries) _byGuid.Clear();
        return _byGuid[k] = prefix + key.ToString();
    }

    /// <summary>Cached <c>prefix + "_" + suffix</c>; separate namespace from <see cref="Cat"/>.</summary>
    public static string Underscore(string prefix, string suffix)
    {
        var k = (prefix, suffix);
        if (_byString.TryGetValue(k, out string? cached)) return cached;
        if (_byString.Count >= MaxEntries) _byString.Clear();
        return _byString[k] = prefix + "_" + suffix;
    }

    /// <summary>Drops all cached ids (scene switch, project switch).</summary>
    public static void Clear()
    {
        _byString.Clear();
        _byLong.Clear();
        _byInt.Clear();
        _byGuid.Clear();
    }
}
