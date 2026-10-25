// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.AssetBundles;

using Xunit;

namespace XEngine.Runtime.Test.AssetBundles;

/// <summary>
/// End-to-end tests for the Offline play mode: build a bundle + manifest to a temp "builtin" root,
/// wire up an AssetPackage with a BuiltinStorage, load the manifest, and load assets through the
/// public AssetPackage API. This exercises the full Stage A+B+C stack together.
/// </summary>
public class OfflinePlayModeTests : IDisposable
{
    private readonly string _tempDir;

    public OfflinePlayModeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "xengine-ab-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        XAssets.Initialize();
    }

    public void Dispose()
    {
        XAssets.Shutdown();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class OfflineAsset : EngineObject
    {
        public string Marker = string.Empty;
    }

    private static byte[] MakePayload(string marker)
    {
        var tag = EchoObject.NewCompound();
        tag["marker"] = new EchoObject(marker);
        tag["$type"] = new EchoObject(typeof(OfflineAsset).FullName ?? "OfflineAsset");
        using var ms = new MemoryStream();
        tag.WriteToBinary(new BinaryWriter(ms));
        return ms.ToArray();
    }

    /// <summary>Read the BundleHash a writer stamped into a bundle file's 84-byte header. Mirrors the
    /// header read in BuiltinBundleStore.InitializeAsync.</summary>
    private static BundleHash ReadBundleHashFromHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        br.ReadUInt32(); // magic
        br.ReadUInt16(); // version
        br.ReadByte();   // platform
        br.ReadByte();   // compression
        br.ReadInt32();  // entryCount
        br.ReadUInt32(); // flags
        br.ReadUInt32(); // headerCrc
        br.ReadInt64();  // indexOffset
        br.ReadInt64();  // indexLength
        br.ReadInt64();  // dataOffset
        br.ReadInt64();  // dataLength
        br.ReadInt64();  // manifestOffset
        br.ReadInt64();  // manifestLength
        return BundleHash.ReadBigEndian(br.ReadBytes(16));
    }

    /// <summary>Build bundles + manifest into <paramref name="root"/> and return the manifest
    /// (already populated with correct bundle GUIDs/hashes/CRCs by reading the written files).</summary>
    private PackageManifest BuildPackage(string root, string packageName, string version,
        params (Guid guid, string address, string marker)[] assets)
    {
        Directory.CreateDirectory(root);

        // Put every asset in its own bundle (simplest grouping for the test).
        var bundleList = new List<BundleInfo>();
        var assetList = new List<ManifestAsset>();

        for (int i = 0; i < assets.Length; i++)
        {
            var (guid, address, marker) = assets[i];
            var payload = MakePayload(marker);
            var entry = new AssetBundleEntry(guid, address, typeof(OfflineAsset).FullName ?? "OfflineAsset", 0, payload.Length);
            string bundleName = $"bundle_{i}";
            string path = Path.Combine(root, bundleName + ".bundle");
            AssetBundleWriter.Write(path, new[] { new KeyValuePair<AssetBundleEntry, byte[]>(entry, payload) },
                new AssetBundleBuildOptions { Compression = BundleCompressionMode.Lz4Block, Lz4BlockSize = 4096 });

            // The bundle GUID equals the hash the writer stamps in the header (NOT a whole-file hash).
            // The BuiltinBundleStore indexes bundles by reading that same header hash, so they must
            // agree. Read it back from the file.
            var bundleHash = ReadBundleHashFromHeader(path);
            var info = new BundleInfo
            {
                BundleName = bundleName,
                FileName = bundleName + ".bundle",
                BundleGuid = bundleHash,
                FileHash = bundleHash,
                FileCrc = Crc32Algorithm.ComputeFile(path),
                FileSize = new FileInfo(path).Length,
                DependentBundleIds = Array.Empty<int>(),
            };
            bundleList.Add(info);
            assetList.Add(new ManifestAsset { Address = address, AssetPath = "Assets/" + address, AssetGuid = guid, BundleId = i });
        }

        var manifest = new PackageManifest
        {
            PackageName = packageName,
            PackageVersion = version,
            BuildTarget = AssetBundlePlatform.Any,
            EnableAddressable = true,
            Compression = BundleCompressionMode.Lz4Block,
        };
        manifest.BundleList.AddRange(bundleList);
        manifest.AssetList.AddRange(assetList);
        manifest.BuildIndexes();

        // Write the manifest + version file in the format the BuiltinStorage expects.
        PackageManifestSerializer.SerializeToFile(manifest, Path.Combine(root, PackageFileNames.ManifestFile(packageName, version)));
        File.WriteAllText(Path.Combine(root, PackageFileNames.VersionFile(packageName)), version);

        return manifest;
    }

    [Fact]
    public void Offline_LoadAssetByAddress_ReturnsValidHandle()
    {
        var guid = Guid.NewGuid();
        BuildPackage(_tempDir, "offlinepkg", "1.0.0", (guid, "hero", "hero-marker"));

        var pkg = XAssets.CreatePackage("offlinepkg");
        var storage = new BuiltinStorage();
        storage.OnCreateAsync("offlinepkg", _tempDir).Wait();
        storage.InitializeStores(AssetBundlePlatform.Any).Wait();
        pkg.Initialize(AssetPlayMode.Offline, new[] { (IAssetStorage)storage });

        var verOp = pkg.RequestPackageVersionAsync();
        PumpUntil(() => verOp.IsDone);
        Assert.Equal("1.0.0", verOp.Result);

        var manOp = pkg.LoadPackageManifestAsync(verOp.Result!);
        PumpUntil(() => manOp.IsDone);
        Assert.Null(manOp.Error);
        Assert.NotNull(pkg.ActiveManifest);

        var handle = pkg.LoadAssetAsync<OfflineAsset>("hero");
        PumpUntil(handle);

        Assert.True(handle.IsValid, $"handle error: {handle.Error}");
        var asset = handle.GetAssetObject<OfflineAsset>();
        Assert.NotNull(asset);
        Assert.Equal(guid, asset!.AssetID);
        handle.Release();
        PumpTimes(3);
    }

    [Fact]
    public void Offline_IsLocationValid_AndGetAssetInfo_Work()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        BuildPackage(_tempDir, "querypkg", "2.0.0", (g1, "a", "ma"), (g2, "b", "mb"));

        var pkg = XAssets.CreatePackage("querypkg");
        var storage = new BuiltinStorage();
        storage.OnCreateAsync("querypkg", _tempDir).Wait();
        storage.InitializeStores(AssetBundlePlatform.Any).Wait();
        pkg.Initialize(AssetPlayMode.Offline, new[] { (IAssetStorage)storage });

        var manOp = pkg.LoadPackageManifestAsync("2.0.0");
        PumpUntil(() => manOp.IsDone);

        Assert.True(pkg.IsLocationValid("a"));
        Assert.True(pkg.IsLocationValid("b"));
        Assert.False(pkg.IsLocationValid("missing"));
        var info = pkg.GetAssetInfo("a");
        Assert.NotNull(info);
        Assert.Equal(g1, info!.AssetGuid);
    }

    [Fact]
    public void Offline_LoadAssetSync_BlocksUntilComplete()
    {
        var guid = Guid.NewGuid();
        BuildPackage(_tempDir, "syncpkg", "3.0.0", (guid, "sync", "m"));

        var pkg = XAssets.CreatePackage("syncpkg");
        var storage = new BuiltinStorage();
        storage.OnCreateAsync("syncpkg", _tempDir).Wait();
        storage.InitializeStores(AssetBundlePlatform.Any).Wait();
        pkg.Initialize(AssetPlayMode.Offline, new[] { (IAssetStorage)storage });

        var manOp = pkg.LoadPackageManifestAsync("3.0.0");
        PumpUntil(() => manOp.IsDone);

        var handle = pkg.LoadAssetSync<OfflineAsset>("sync");
        Assert.True(handle.IsValid, $"sync load error: {handle.Error}");
        Assert.NotNull(handle.GetAssetObject<OfflineAsset>());
        handle.Release();
    }

    private static void PumpUntil(Func<bool> done, int maxFrames = 2000)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            XAssets.Tick();
            if (done()) return;
        }
        throw new TimeoutException("Did not complete in " + maxFrames + " frames.");
    }

    private static void PumpUntil(AssetHandleBase handle, int maxFrames = 2000) => PumpUntil(() => handle.IsDone, maxFrames);

    private static void PumpTimes(int frames)
    {
        for (int i = 0; i < frames; i++) XAssets.Tick();
    }
}
