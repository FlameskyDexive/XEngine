// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Regressions for the engine-side roots that kept the Zonezero script AssemblyLoadContext alive
/// through a hot-reload (gcdump after 10 failed GC attempts, recorded under
/// docs/benchmarks/editor-performance/2026-09-14-incremental-ui-n4/lifecycle). Each test pins the
/// specific retention that was observed, not the reload itself.
/// </summary>
public sealed class HotReloadPinningTests
{
    private sealed class TickingBehaviour : MonoBehaviour
    {
        public override void Start() { }
        public override void Update() { }
    }

    private static object Channel(Scene scene, string channelField)
    {
        object registry = typeof(Scene).GetField("_componentRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        return registry.GetType().GetField(channelField, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
    }

    /// <summary>The stored sorted array, without rebuilding it (what a never-iterated scene retains).</summary>
    private static MonoBehaviour[] SortedSnapshot(Scene scene, string channelField)
    {
        object channel = Channel(scene, channelField);
        return (MonoBehaviour[])channel.GetType().GetField("_sorted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(channel)!;
    }

    /// <summary>Iterates the channel the way the frame loop does (Snapshot rebuilds the sorted array).</summary>
    private static MonoBehaviour[] Iterate(Scene scene, string channelField)
    {
        object channel = Channel(scene, channelField);
        return (MonoBehaviour[])channel.GetType().GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.Public)!.Invoke(channel, null)!;
    }

    [Fact]
    public void RegistryChannelDropsItsSortedSnapshotWhenTheLastComponentLeaves()
    {
        // The snapshot is rebuilt lazily on the next iteration. A scene that is never iterated again
        // (unloaded, but still referenced by a panel) kept every removed component in the stale array.
        using var scene = new Scene();
        scene.Enable();
        var go = new GameObject("Ticker");
        TickingBehaviour ticker = go.AddComponent<TickingBehaviour>();
        scene.Add(go);

        Assert.Contains(ticker, Iterate(scene, "_update"));
        Assert.Contains(ticker, SortedSnapshot(scene, "_update"));

        scene.Remove(go);
        Assert.Empty(SortedSnapshot(scene, "_update"));
    }

    [Fact]
    public void DisposingASceneClearsItsRegistrySnapshots()
    {
        var scene = new Scene();
        scene.Enable();
        var first = new GameObject("A"); first.AddComponent<TickingBehaviour>(); scene.Add(first);
        var second = new GameObject("B"); second.AddComponent<TickingBehaviour>(); scene.Add(second);
        Assert.Equal(2, Iterate(scene, "_update").Length);
        Assert.Equal(2, Iterate(scene, "_start").Length);

        scene.Dispose();
        Assert.Empty(SortedSnapshot(scene, "_update"));
        Assert.Empty(SortedSnapshot(scene, "_start"));
    }

    private static Assembly LoadCollectibleEchoCopy()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Echo.dll"));
        var alc = new AssemblyLoadContext("HotReloadPinningTests-copy", isCollectible: true);
        Assembly copy = alc.LoadFromStream(new MemoryStream(bytes));
        Assert.True(copy.IsCollectible);
        return copy;
    }

    [Fact]
    public void BuiltInAssetsForgetRegistrationsWhoseLoaderLivesInACollectibleAssembly()
    {
        // Package [OnAssemblyLoad] hooks register shader libraries with a loader lambda declared in
        // the package assembly; one such delegate in the static entry table pinned the whole context.
        Assembly copy = LoadCollectibleEchoCopy();
        Type echoObjectType = copy.GetType("XEngine.Echo.EchoObject", throwOnError: true)!;
        // A closed-static delegate whose Target is an instance of a type instantiated over a
        // collectible type - the same strong reference into the context a package lambda carries.
        object collectibleTarget = Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(echoObjectType))!;
        MethodInfo loaderMethod = typeof(HotReloadPinningTests).GetMethod(nameof(NeverLoaded), BindingFlags.Static | BindingFlags.NonPublic)!;
        var collectibleLoader = (Func<EngineObject>)Delegate.CreateDelegate(typeof(Func<EngineObject>), collectibleTarget, loaderMethod);
        var engineLoader = (Func<EngineObject>)Delegate.CreateDelegate(typeof(Func<EngineObject>), new System.Collections.Generic.List<int>(), loaderMethod);
        Assert.True(XEngine.Async.Internal.CollectibleBinding.IsBound(collectibleLoader));
        Assert.False(XEngine.Async.Internal.CollectibleBinding.IsBound(engineLoader));

        const string collectiblePath = "Tests/HotReloadPinning/collectible.asset";
        const string enginePath = "Tests/HotReloadPinning/engine.asset";
        Guid collectibleGuid = BuiltInAssets.DeterministicGuid(collectiblePath);
        Guid engineGuid = BuiltInAssets.DeterministicGuid(enginePath);
        try
        {
            BuiltInAssets.Register(collectiblePath, "collectible", typeof(EngineObject), collectibleLoader);
            BuiltInAssets.Register(enginePath, "engine", typeof(EngineObject), engineLoader);
            Assert.True(BuiltInAssets.IsBuiltIn(collectibleGuid));
            Assert.True(BuiltInAssets.IsBuiltIn(engineGuid));

            BuiltInAssets.RemoveCollectibleRegistrations();

            Assert.False(BuiltInAssets.IsBuiltIn(collectibleGuid));
            Assert.True(BuiltInAssets.IsBuiltIn(engineGuid));
        }
        finally
        {
            RemoveEntry(collectibleGuid);
            RemoveEntry(engineGuid);
        }
    }

    /// <summary>Never invoked: the entry is removed, not loaded.</summary>
    private static EngineObject NeverLoaded(object target) => throw new InvalidOperationException("loader must not run");

    private static void RemoveEntry(Guid guid)
    {
        var entries = (System.Collections.IDictionary)typeof(BuiltInAssets).GetField("_entries", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        entries.Remove(guid);
    }
}
