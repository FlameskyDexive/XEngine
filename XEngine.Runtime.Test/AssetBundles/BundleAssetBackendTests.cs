// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.AssetBundles;
using XEngine.Runtime.AssetStreaming;

using Xunit;

namespace XEngine.Runtime.Test.AssetBundles;

/// <summary>
/// Tests for BundleAssetBackend: the AssetBackendBase that lets AssetDatabase / AssetRef / Resources.Load
/// resolve through the bundle layer. Verifies a GUID resolves to the same asset the package would
/// load, that the locator round-trips Resources paths, and that Resources.Load keeps working.
/// </summary>
public class BundleAssetBackendTests : IDisposable
{
    private readonly string _builtinDir;

    public BundleAssetBackendTests()
    {
        _builtinDir = Path.Combine(Path.GetTempPath(), "xengine-backend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_builtinDir);
        XAssets.Initialize();
    }

    public void Dispose()
    {
        XAssets.Shutdown();
        try { Directory.Delete(_builtinDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class BackendAsset : EngineObject { public string Marker = ""; }

    private static byte[] MakePayload(string marker)
    {
        var tag = EchoObject.NewCompound();
        tag["marker"] = new EchoObject(marker);
        tag["$type"] = new EchoObject(typeof(BackendAsset).FullName ?? "BackendAsset");
        using var ms = new MemoryStream();
        tag.WriteToBinary(new BinaryWriter(ms));
        return ms.ToArray();
    }

    private (AssetPackage pkg, Guid guid) BuildPackageAndLoad(string address, string marker, string? assetPath = null, bool duplicateAddress = false)
    {
        var guid = Guid.NewGuid();
        var payload = MakePayload(marker);
        var entry = new AssetBundleEntry(guid, address, typeof(BackendAsset).FullName ?? "BackendAsset", 0, payload.Length);
        string path = Path.Combine(_builtinDir, "b.bundle");
        var entries = new List<KeyValuePair<AssetBundleEntry, byte[]>> { new(entry, payload) };
        Guid otherGuid = Guid.NewGuid();
        if (duplicateAddress)
        {
            byte[] otherPayload = MakePayload("other");
            entries.Add(new(new AssetBundleEntry(otherGuid, address, typeof(BackendAsset).FullName!, 0, otherPayload.Length), otherPayload));
        }
        AssetBundleWriter.Write(path, entries,
            new AssetBundleBuildOptions { Compression = BundleCompressionMode.Lz4Block, Lz4BlockSize = 4096 });

        // Read the bundle's header hash so the manifest's BundleGuid matches what BuiltinBundleStore indexes.
        var bundleHash = ReadHeaderHash(path);
        var manifest = new PackageManifest
        {
            PackageName = "be", PackageVersion = "1.0.0",
            BuildTarget = AssetBundlePlatform.Any, EnableAddressable = true, Compression = BundleCompressionMode.Lz4Block,
        };
        manifest.BundleList.Add(new BundleInfo
        {
            BundleName = "b", FileName = "b.bundle",
            BundleGuid = bundleHash, FileHash = BundleHash.ComputeFile(path),
            FileCrc = Crc32Algorithm.ComputeFile(path), FileSize = new FileInfo(path).Length,
            DependentBundleIds = Array.Empty<int>(),
        });
        manifest.AssetList.Add(new ManifestAsset
        {
            Address = address,
            AssetPath = assetPath ?? "Assets/" + address,
            AssetGuid = guid,
            BundleId = 0,
        });
        if (duplicateAddress)
            manifest.AssetList.Add(new ManifestAsset { Address = address, AssetPath = "Assets/Other/" + address, AssetGuid = otherGuid, BundleId = 0 });
        manifest.BuildIndexes();
        PackageManifestSerializer.SerializeToFile(manifest, Path.Combine(_builtinDir, PackageFileNames.ManifestFile("be", "1.0.0")));
        File.WriteAllText(Path.Combine(_builtinDir, PackageFileNames.VersionFile("be")), "1.0.0");

        var pkg = XAssets.CreatePackage("be");
        var storage = new BuiltinStorage();
        storage.OnCreateAsync("be", _builtinDir).Wait();
        storage.InitializeStores(AssetBundlePlatform.Any).Wait();
        pkg.Initialize(AssetPlayMode.Offline, new[] { (IAssetStorage)storage });
        var manOp = pkg.LoadPackageManifestAsync("1.0.0");
        PumpUntil(() => manOp.IsDone);
        return (pkg, guid);
    }

    private static BundleHash ReadHeaderHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        br.ReadUInt32(); br.ReadUInt16(); br.ReadByte(); br.ReadByte();
        br.ReadInt32(); br.ReadUInt32(); br.ReadUInt32();
        br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64();
        return BundleHash.ReadBigEndian(br.ReadBytes(16));
    }

    [Fact]
    public void BundleAssetBackend_ResolvesGuidToPackageAsset()
    {
        var (pkg, guid) = BuildPackageAndLoad("hero", "hero-marker");

        var locator = new ResourceLocator();
        locator.RegisterPackage(pkg);
        var backend = new BundleAssetBackend(locator);

        var obj = backend.Get(guid) as BackendAsset;
        Assert.NotNull(obj);
        Assert.Equal(guid, obj!.AssetID);
        backend.Dispose();
    }

    [Fact]
    public void BundleAssetBackend_DuplicateAddressKeepsFirstAsset()
    {
        // 36a049807 changed duplicate addressable names from a hard throw to a logged error that
        // keeps the first asset — assert that contract instead of the pre-crash behavior.
        var (pkg, guid) = BuildPackageAndLoad("same-name", "first", duplicateAddress: true);

        var resolved = pkg.ActiveManifest!.ConvertLocationToAsset("same-name");
        Assert.NotNull(resolved);
        Assert.Equal(guid, resolved.AssetGuid);
    }

    [Fact]
    public void BundleAssetBackend_CachesLoadedAsset()
    {
        var (pkg, guid) = BuildPackageAndLoad("cached", "c");

        var locator = new ResourceLocator();
        locator.RegisterPackage(pkg);
        var backend = new BundleAssetBackend(locator);

        var a = backend.Get(guid);
        var b = backend.Get(guid);
        Assert.Same(a, b); // second Get hits the cache
        backend.Dispose();
    }

    [Fact]
    public void BundleAssetBackend_UnknownGuid_ReturnsNull()
    {
        var (pkg, _) = BuildPackageAndLoad("x", "x");
        var locator = new ResourceLocator();
        locator.RegisterPackage(pkg);
        var backend = new BundleAssetBackend(locator);

        Assert.Null(backend.Get(Guid.NewGuid()));
        backend.Dispose();
    }

    [Fact]
    public void ResourceLocator_BuildResourcesMap_IncludesAddresses()
    {
        var (pkg, guid) = BuildPackageAndLoad("textures/grass", "g");
        var locator = new ResourceLocator();
        locator.RegisterPackage(pkg);

        var resMap = locator.BuildResourcesMap();
        Assert.True(resMap.ContainsKey("textures/grass"));
        Assert.Equal(guid, resMap["textures/grass"]);
    }

    [Fact]
    public void ResourceLocator_BuildResourcesMap_PreservesResourcesPathWhenAddressIsGuid()
    {
        var (pkg, guid) = BuildPackageAndLoad(
            Guid.NewGuid().ToString("N"),
            "g",
            "Assets/Resources/Zonezero/HeroSkillLibrary.asset");
        var locator = new ResourceLocator();
        locator.RegisterPackage(pkg);

        var resMap = locator.BuildResourcesMap();

        Assert.Equal(guid, resMap["Zonezero/HeroSkillLibrary"]);
    }

    [Fact]
    public void BundleAssetBackend_GetEntry_ResolvesManifestSourcePath()
    {
        var (pkg, guid) = BuildPackageAndLoad("textures/grass", "g");
        var locator = new ResourceLocator();
        locator.RegisterPackage(pkg);
        var backend = new BundleAssetBackend(locator);

        RuntimeAssetEntry? entry = backend.GetEntry("Assets/textures/grass");

        Assert.NotNull(entry);
        Assert.Equal(guid, entry!.Guid);
        Assert.Equal(guid, backend.GetEntry("textures/grass")?.Guid);
        backend.Dispose();
    }

    [Fact]
    public void CreateFromProvider_LoadsAndroidPackageAndOwnsPackageLifetime()
    {
        string providerRoot = Path.Combine(_builtinDir, "provider");
        string packageRoot = Path.Combine(providerRoot, "Content", "mobile");
        Directory.CreateDirectory(packageRoot);
        Guid guid = Guid.NewGuid();
        byte[] payload = MakePayload("provider-marker");
        var entry = new AssetBundleEntry(
            guid,
            "scenes/smoke",
            typeof(BackendAsset).FullName ?? "BackendAsset",
            0,
            payload.Length);
        string bundlePath = Path.Combine(packageRoot, "mobile.bundle");
        AssetBundleWriter.Write(
            bundlePath,
            [new KeyValuePair<AssetBundleEntry, byte[]>(entry, payload)],
            new AssetBundleBuildOptions
            {
                Platform = AssetBundlePlatform.Android,
                Compression = BundleCompressionMode.Lz4Block,
                Lz4BlockSize = 4096,
            });

        var manifest = new PackageManifest
        {
            PackageName = "mobile",
            PackageVersion = "1.0.0",
            BuildTarget = AssetBundlePlatform.Android,
            EnableAddressable = true,
            Compression = BundleCompressionMode.Lz4Block,
        };
        manifest.BundleList.Add(new BundleInfo
        {
            BundleName = "mobile",
            FileName = "mobile.bundle",
            BundleGuid = ReadHeaderHash(bundlePath),
            FileHash = BundleHash.ComputeFile(bundlePath),
            FileCrc = Crc32Algorithm.ComputeFile(bundlePath),
            FileSize = new FileInfo(bundlePath).Length,
            DependentBundleIds = [],
        });
        manifest.AssetList.Add(new ManifestAsset
        {
            Address = "scenes/smoke",
            AssetPath = "Assets/AndroidSmoke.scene",
            AssetGuid = guid,
            BundleId = 0,
        });
        manifest.BuildIndexes();
        PackageManifestSerializer.SerializeToFile(
            manifest,
            Path.Combine(packageRoot, PackageFileNames.ManifestFile("mobile", "1.0.0")));
        File.WriteAllText(Path.Combine(packageRoot, PackageFileNames.VersionFile("mobile")), "1.0.0");

        using var provider = new TrackingProvider(providerRoot);
        BundleAssetBackend? backend = BundleAssetBackend.CreateFromProvider(
            provider,
            ["Content/mobile"],
            guid);

        Assert.NotNull(backend);
        Assert.Equal(guid, backend!.DefaultSceneGuid);
        Assert.Equal(guid, backend.ResourcesMap["scenes/smoke"]);
        BackendAsset? loaded = backend.Get(guid) as BackendAsset;
        Assert.NotNull(loaded);
        Assert.Equal(guid, loaded!.AssetID);
        Assert.True(XAssets.ContainsPackage("mobile"));
        Assert.True(provider.LastBundleStream!.CanRead);

        backend.Dispose();
        Assert.False(provider.LastBundleStream.CanRead);
        Assert.False(XAssets.ContainsPackage("mobile"));
    }

    private sealed class TrackingProvider(string root) : IAssetStreamProvider
    {
        private readonly DirectoryAssetStreamProvider _inner = new(root);
        public Stream? LastBundleStream { get; private set; }
        public string Description => _inner.Description;
        public Stream? OpenRead(string path)
        {
            Stream? stream = _inner.OpenRead(path);
            if (path.EndsWith(".bundle", StringComparison.Ordinal)) LastBundleStream = stream;
            return stream;
        }
        public IReadOnlyList<string> GetFiles(string directory, string extension) => _inner.GetFiles(directory, extension);
        public void Dispose() => _inner.Dispose();
    }

    private static void PumpUntil(Func<bool> done, int maxFrames = 2000)
    {
        for (int i = 0; i < maxFrames; i++) { XAssets.Tick(); if (done()) return; }
        throw new TimeoutException("Did not complete in " + maxFrames + " frames.");
    }
}
