// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// XAssets - the global entry point for the AssetBundles subsystem. Manages named packages and
// drives the AsyncOperationSystem once per frame via Tick (wired into the Game.cs main loop in
// Stage G).
//
// Independent rewrite of YooAsset's YooAssets static facade (Apache-2.0).

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Global facade for the AssetBundles subsystem. Initialize once at engine startup, then create
/// packages, load manifests, and load assets. Call <see cref="Tick"/> every frame from the main loop.
/// </summary>
public static class XAssets
{
    private static readonly Dictionary<string, AssetPackage> s_packages = new(StringComparer.Ordinal);
    private static bool s_initialized;

    /// <summary>Initialize the subsystem (creates the async-operation scheduler state). Idempotent.</summary>
    public static void Initialize()
    {
        if (s_initialized) return;
        AsyncOperationSystem.Initialize();
        s_initialized = true;
    }

    /// <summary>Create a named package. Throws if the name already exists.</summary>
    public static AssetPackage CreatePackage(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
        if (s_packages.ContainsKey(name))
            throw new InvalidOperationException($"Package '{name}' already exists.");
        var pkg = new AssetPackage(name);
        s_packages[name] = pkg;
        return pkg;
    }

    public static AssetPackage? TryGetPackage(string name)
        => s_packages.TryGetValue(name, out var p) ? p : null;

    public static AssetPackage GetPackage(string name)
        => s_packages.TryGetValue(name, out var p) ? p : throw new KeyNotFoundException($"Package not found: {name}");

    public static bool ContainsPackage(string name) => s_packages.ContainsKey(name);

    public static bool RemovePackage(string name)
    {
        if (!s_packages.TryGetValue(name, out var p)) return false;
        p.Destroy();
        s_packages.Remove(name);
        return true;
    }

    /// <summary>Cap the per-frame time slice the scheduler spends on async operations. Default 2 ms.</summary>
    public static void SetAsyncOperationMaxTimeSlice(TimeSpan slice) => AsyncOperationSystem.MaxTimeSlice = slice;

    /// <summary>Per-frame pump. Must be called every frame from the engine main loop (Game.cs).</summary>
    public static void Tick() => AsyncOperationSystem.Update();

    /// <summary>Tear down: destroy every package and clear the scheduler.</summary>
    public static void Shutdown()
    {
        foreach (var kv in s_packages.Values) kv.Destroy();
        s_packages.Clear();
        AsyncOperationSystem.Clear();
        s_initialized = false;
    }
}
