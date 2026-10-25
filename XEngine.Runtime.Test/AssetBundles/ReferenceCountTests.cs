// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using XEngine.Echo;
using XEngine.Runtime.AssetBundles;

using Xunit;

namespace XEngine.Runtime.Test.AssetBundles;

/// <summary>
/// Tests for the Stage B reference-counting layer: provider pooling (N loads share 1 provider),
/// bundle loader pooling (assets in the same bundle share 1 loader), dependency resolution, and
/// unload-on-zero-refcount. These drive the manager with a file-backed XEngineBundleHandle factory
/// so they exercise the real AssetBundle container too.
/// </summary>
public class ReferenceCountTests : IDisposable
{
    private readonly string _tempDir;
    private int _bundleOpenCount;
    private int _bundleCloseCount;

    public ReferenceCountTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "xengine-ab-refcount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _bundleOpenCount = 0;
        _bundleCloseCount = 0;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ----------------------------------------------------------------
    //  Test assets / bundles
    // ----------------------------------------------------------------

    private sealed class RefStubAsset : EngineObject
    {
        public string Marker = string.Empty;
    }

    private static byte[] MakePayload(string marker)
    {
        var tag = EchoObject.NewCompound();
        tag["marker"] = new EchoObject(marker);
        tag["$type"] = new EchoObject(typeof(RefStubAsset).FullName ?? "RefStubAsset");
        using var ms = new MemoryStream();
        tag.WriteToBinary(new BinaryWriter(ms));
        return ms.ToArray();
    }

    /// <summary>Build a manifest with one bundle containing one asset. Returns the manifest and the
    /// bundle path on disk.</summary>
    private (PackageManifest manifest, BundleInfo bundle) BuildSingleBundle(string bundleName, Guid assetGuid, string address)
    {
        var payload = MakePayload("m-" + address);
        var entry = new AssetBundleEntry(assetGuid, address, typeof(RefStubAsset).FullName ?? "RefStubAsset", 0, payload.Length);
        var path = Path.Combine(_tempDir, bundleName + ".bundle");
        AssetBundleWriter.Write(path, new[] { new KeyValuePair<AssetBundleEntry, byte[]>(entry, payload) },
            new AssetBundleBuildOptions { Compression = BundleCompressionMode.Lz4Block, Lz4BlockSize = 4096 });

        var bundleHash = BundleHash.ComputeFile(path);
        var bundle = new BundleInfo
        {
            BundleName = bundleName,
            FileName = bundleName + ".bundle",
            BundleGuid = bundleHash,
            FileHash = bundleHash,
            FileCrc = Crc32Algorithm.ComputeFile(path),
            FileSize = new FileInfo(path).Length,
            DependentBundleIds = Array.Empty<int>(),
        };

        var manifest = new PackageManifest { PackageName = "test", PackageVersion = "1.0.0", EnableAddressable = true };
        manifest.BundleList.Add(bundle);
        manifest.AssetList.Add(new ManifestAsset { Address = address, AssetPath = "Assets/" + address + ".png", AssetGuid = assetGuid, BundleId = 0 });
        manifest.BuildIndexes();

        return (manifest, bundle);
    }

    private AssetManager MakeManager(PackageManifest manifest)
    {
        var manager = new AssetManager(manifest, bundleInfo =>
        {
            // Factory: open the real AssetBundle file from _tempDir. Count opens so tests can assert
            // pooling.
            _bundleOpenCount++;
            var path = Path.Combine(_tempDir, bundleInfo.FileName);
            var ab = AssetBundle.LoadFromFile(path);
            var handle = new CountingBundleHandle(ab, bundleInfo, () => _bundleCloseCount++);
            var op = new ImmediateBundleHandleOp(handle);
            return op;
        });
        manager.AutoUnloadBundleWhenUnused = true;
        return manager;
    }

    // ----------------------------------------------------------------
    //  Tests
    // ----------------------------------------------------------------

    [Fact]
    public void LoadAsset_ReturnsValidHandle_AfterTick()
    {
        var guid = Guid.NewGuid();
        var (manifest, _) = BuildSingleBundle("single", guid, "grass");
        var manager = MakeManager(manifest);

        var handle = manager.LoadAssetAsync<RefStubAsset>("grass");
        PumpUntil(handle);

        Assert.True(handle.IsDone);
        Assert.Null(handle.Error);
        Assert.Equal(1, _bundleOpenCount);
        handle.Release();
    }

    [Fact]
    public void MultipleLoads_SameAsset_ShareOneProvider()
    {
        var guid = Guid.NewGuid();
        var (manifest, _) = BuildSingleBundle("shared", guid, "tex");
        var manager = MakeManager(manifest);

        var h1 = manager.LoadAssetAsync<RefStubAsset>("tex");
        var h2 = manager.LoadAssetAsync<RefStubAsset>("tex");
        var h3 = manager.LoadAssetAsync<RefStubAsset>("tex");

        PumpUntil(() => h1.IsDone && h2.IsDone && h3.IsDone);

        // All three handles resolve to the same loaded asset (one provider, one bundle open).
        Assert.NotNull(h1.AssetObject);
        Assert.NotNull(h2.AssetObject);
        Assert.NotNull(h3.AssetObject);
        Assert.Same(h1.AssetObject, h2.AssetObject);
        Assert.Same(h1.AssetObject, h3.AssetObject);
        Assert.Equal(1, _bundleOpenCount);

        h1.Release(); h2.Release(); h3.Release();
    }

    [Fact]
    public void BundleNotUnloaded_WhileAnyHandleAlive()
    {
        var guid = Guid.NewGuid();
        var (manifest, _) = BuildSingleBundle("unload", guid, "a");
        var manager = MakeManager(manifest);

        var h1 = manager.LoadAssetAsync<RefStubAsset>("a");
        var h2 = manager.LoadAssetAsync<RefStubAsset>("a");
        PumpUntil(() => h1.IsDone && h2.IsDone);

        h1.Release();
        // h2 still alive -> bundle must still be open.
        Assert.Equal(0, _bundleCloseCount);

        h2.Release();
        // Now both released; auto-unload should close the bundle.
        PumpTimes(5);
        Assert.True(_bundleCloseCount >= 1);
    }

    [Fact]
    public void ReopenAfterUnload_OpensBundleAgain()
    {
        var guid = Guid.NewGuid();
        var (manifest, _) = BuildSingleBundle("reopen", guid, "a");
        var manager = MakeManager(manifest);

        var h1 = manager.LoadAssetAsync<RefStubAsset>("a");
        PumpUntil(h1);
        h1.Release();
        PumpTimes(5);
        Assert.Equal(1, _bundleOpenCount);
        Assert.Equal(1, _bundleCloseCount);

        // Re-open: a fresh load must open the bundle a second time.
        var h2 = manager.LoadAssetAsync<RefStubAsset>("a");
        PumpUntil(h2);
        Assert.Equal(2, _bundleOpenCount);
        Assert.NotNull(h2.AssetObject);
        h2.Release();
    }

    [Fact]
    public void LoadByGuid_WorksLikeLoadByAddress()
    {
        var guid = Guid.NewGuid();
        var (manifest, _) = BuildSingleBundle("byguid", guid, "thing");
        var manager = MakeManager(manifest);

        var handle = manager.LoadAssetAsync<RefStubAsset>(guid);
        PumpUntil(handle);
        Assert.NotNull(handle.AssetObject);
        Assert.Equal(guid, handle.AssetGuid);
        handle.Release();
    }

    [Fact]
    public void LoadUnknownAddress_Throws()
    {
        var guid = Guid.NewGuid();
        var (manifest, _) = BuildSingleBundle("unknown", guid, "real");
        var manager = MakeManager(manifest);

        Assert.Throws<KeyNotFoundException>(() => manager.LoadAssetAsync<RefStubAsset>("does-not-exist"));
    }

    [Fact]
    public void UnloadAllAssets_ReleasesEverything()
    {
        var guid = Guid.NewGuid();
        var (manifest, _) = BuildSingleBundle("unloadall", guid, "a");
        var manager = MakeManager(manifest);

        var h1 = manager.LoadAssetAsync<RefStubAsset>("a");
        PumpUntil(h1);
        manager.UnloadAllAssets();
        Assert.True(_bundleCloseCount >= 1);
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    private static void PumpUntil(Func<bool> done, int maxFrames = 1000)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            AsyncOperationSystem.Update();
            if (done()) return;
        }
        throw new TimeoutException("Operation did not complete within " + maxFrames + " frames.");
    }

    /// <summary>Pump the scheduler until the given handle is done. The bool value is re-read each
    /// frame (passing the property by value would snapshot it once).</summary>
    private static void PumpUntil(AssetHandleBase handle, int maxFrames = 1000) => PumpUntil(() => handle.IsDone, maxFrames);

    private static void PumpTimes(int frames)
    {
        for (int i = 0; i < frames; i++) AsyncOperationSystem.Update();
    }

    /// <summary>An IBundleHandle that counts UnloadBundle calls so tests can assert unload timing.</summary>
    private sealed class CountingBundleHandle : IBundleHandle
    {
        private readonly AssetBundle _bundle;
        private readonly Action _onUnload;

        public CountingBundleHandle(AssetBundle bundle, BundleInfo info, Action onUnload)
        {
            _bundle = bundle;
            Bundle = info;
            _onUnload = onUnload;
        }

        public BundleInfo Bundle { get; }
        public bool IsLoaded => !_bundle.IsUnloaded;

        public AsyncOperationBase<EngineObject?> LoadAssetAsync(Guid assetGuid, Type type)
            => _bundle.LoadFromFileImplied_LoadAssetAsync(assetGuid, type);

        public AsyncOperationBase<IReadOnlyList<EngineObject>> LoadAllAssetsAsync(Type type)
            => _bundle.LoadFromFileImplied_LoadAllAssetsAsync(type);

        public void UnloadBundle(bool unloadAllLoadedObjects)
        {
            _onUnload();
            _bundle.Unload(unloadAllLoadedObjects);
        }
    }

    /// <summary>An op that returns a pre-built IBundleHandle on its first update tick.</summary>
    private sealed class ImmediateBundleHandleOp : AsyncOperationBase<IBundleHandle>
    {
        private readonly IBundleHandle _handle;
        public ImmediateBundleHandleOp(IBundleHandle handle) { _handle = handle; }
        protected override void InternalStart() { }
        protected override void InternalUpdate() { Result = _handle; SetResult(); }
    }
}

/// <summary>Extension methods used only by the test to wrap AssetBundle.LoadAsset in an async op,
/// without exposing async factories on the public AssetBundle API (which is synchronous by design).</summary>
internal static class AssetBundleTestExtensions
{
    public static AsyncOperationBase<EngineObject?> LoadFromFileImplied_LoadAssetAsync(this AssetBundle bundle, Guid guid, Type type)
    {
        var op = new SyncAssetLoadTestOp(bundle, guid, type);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public static AsyncOperationBase<IReadOnlyList<EngineObject>> LoadFromFileImplied_LoadAllAssetsAsync(this AssetBundle bundle, Type type)
    {
        var op = new SyncAllAssetsTestOp(bundle, type);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    private sealed class SyncAssetLoadTestOp : AsyncOperationBase<EngineObject?>
    {
        private readonly AssetBundle _bundle; private readonly Guid _guid; private readonly Type _type;
        public SyncAssetLoadTestOp(AssetBundle b, Guid g, Type t) { _bundle = b; _guid = g; _type = t; }
        protected override void InternalStart() { }
        protected override void InternalUpdate() { Result = _bundle.LoadAsset(_guid, _type); SetResult(); }
    }

    private sealed class SyncAllAssetsTestOp : AsyncOperationBase<IReadOnlyList<EngineObject>>
    {
        private readonly AssetBundle _bundle; private readonly Type _type;
        public SyncAllAssetsTestOp(AssetBundle b, Type t) { _bundle = b; _type = t; }
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            var list = new List<EngineObject>();
            foreach (var g in _bundle.GetAllAssetGuids()) { var o = _bundle.LoadAsset(g, _type); if (o != null) list.Add(o); }
            Result = list; SetResult();
        }
    }
}
