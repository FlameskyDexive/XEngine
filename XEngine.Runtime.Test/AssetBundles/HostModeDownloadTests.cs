// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Echo;
using XEngine.Runtime.AssetBundles;

using Xunit;

namespace XEngine.Runtime.Test.AssetBundles;

/// <summary>
/// Host-mode download round-trip: build a bundle + manifest to a "builtin" dir, configure a
/// SandboxStorage whose remote serves the bundle bytes, run the ResourceDownloader, then load the
/// (now-downloaded) asset through the package. Uses a fake IDownloadBackend + IRemoteService so the
/// test needs no real HTTP server.
/// </summary>
public class HostModeDownloadTests : IDisposable
{
    private readonly string _builtinDir;  // simulates StreamingAssetsPath/{Package}
    private readonly string _sandboxDir;  // simulates PersistentDataPath/{Package}
    private readonly Dictionary<string, byte[]> _cdn; // fake remote file store: fileName -> bytes

    public HostModeDownloadTests()
    {
        _builtinDir = Path.Combine(Path.GetTempPath(), "xengine-host-builtin-" + Guid.NewGuid().ToString("N"));
        _sandboxDir = Path.Combine(Path.GetTempPath(), "xengine-host-sandbox-" + Guid.NewGuid().ToString("N"));
        _cdn = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        XAssets.Initialize();
    }

    public void Dispose()
    {
        XAssets.Shutdown();
        try { Directory.Delete(_builtinDir, recursive: true); } catch { }
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class HostAsset : EngineObject { public string Marker = ""; }

    private static byte[] MakePayload(string marker)
    {
        var tag = EchoObject.NewCompound();
        tag["marker"] = new EchoObject(marker);
        tag["$type"] = new EchoObject(typeof(HostAsset).FullName ?? "HostAsset");
        using var ms = new MemoryStream();
        tag.WriteToBinary(new BinaryWriter(ms));
        return ms.ToArray();
    }

    private static BundleHash ReadBundleHeaderHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        br.ReadUInt32(); br.ReadUInt16(); br.ReadByte(); br.ReadByte();
        br.ReadInt32(); br.ReadUInt32(); br.ReadUInt32();
        br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64();
        return BundleHash.ReadBigEndian(br.ReadBytes(16));
    }

    /// <summary>Fake backend: serves bytes from the in-memory CDN dict. Runs on the thread pool
    /// (Task.Run) so it behaves like a real async HttpClient download - the download/verify callbacks
    /// complete asynchronously and report back through the downloader's concurrent result queue.</summary>
    private sealed class FakeDownloadBackend : IDownloadBackend
    {
        private readonly Dictionary<string, byte[]> _files;
        public FakeDownloadBackend(Dictionary<string, byte[]> files) { _files = files; }
        public Task DownloadAsync(string url, string localFilePath, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
            => Task.Run(() =>
            {
                string fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (!_files.TryGetValue(fileName, out var bytes))
                    throw new FileNotFoundException($"fake CDN has no {fileName}");
                Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
                File.WriteAllBytes(localFilePath, bytes);
                progress?.Report(new DownloadProgress(bytes.Length, bytes.Length));
            }, cancellation);
    }

    /// <summary>Fake remote: turns a file name into a synthetic http URL (only the file name matters
    /// to FakeDownloadBackend).</summary>
    private sealed class FakeRemote : IRemoteService
    {
        public IReadOnlyList<string> GetRemoteUrls(string fileName) => new[] { $"http://fake.test/{fileName}" };
    }

    [Fact]
    public void HostMode_DownloadsMissingBundle_ThenLoadsAsset()
    {
        Directory.CreateDirectory(_builtinDir);
        Directory.CreateDirectory(_sandboxDir);

        var guid = Guid.NewGuid();
        // Write the bundle to a SOURCE dir (not the builtin dir) so the builtin storage does NOT have
        // it - that's what forces the downloader to fetch it from the CDN. The bundle bytes go into
        // the fake CDN; the manifest goes into builtin so both storages can find it.
        string sourceDir = Path.Combine(Path.GetTempPath(), "xengine-host-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        try
        {
            string bundlePath = Path.Combine(sourceDir, "host_bundle.bundle");
            var bundleGuid = WriteBundleToPath(bundlePath, "host_bundle", guid, "hero");
            // FileHash is the WHOLE-FILE hash (what the sandbox verifies post-download); BundleGuid is
            // the header hash (the cache key). They differ by design.
            var fileHash = BundleHash.ComputeFile(bundlePath);

            // Build a manifest that lists the bundle + asset, and write it to the builtin dir so the
            // sandbox can find the manifest via its builtinPackageRoot fallback.
            var manifest = new PackageManifest
            {
                PackageName = "hostpkg", PackageVersion = "1.0.0",
                BuildTarget = AssetBundlePlatform.Any, EnableAddressable = true, Compression = BundleCompressionMode.Lz4Block,
            };
            manifest.BundleList.Add(new BundleInfo
            {
                BundleName = "host_bundle", FileName = "host_bundle.bundle",
                BundleGuid = bundleGuid, FileHash = fileHash,
                FileCrc = Crc32Algorithm.ComputeFile(bundlePath),
                FileSize = new FileInfo(bundlePath).Length,
                DependentBundleIds = Array.Empty<int>(),
            });
        manifest.AssetList.Add(new ManifestAsset { Address = "hero", AssetPath = "Assets/hero", AssetGuid = guid, BundleId = 0 });
            manifest.BuildIndexes();
            PackageManifestSerializer.SerializeToFile(manifest, Path.Combine(_builtinDir, PackageFileNames.ManifestFile("hostpkg", "1.0.0")));
            File.WriteAllText(Path.Combine(_builtinDir, PackageFileNames.VersionFile("hostpkg")), "1.0.0");

            // Wire up an AssetPackage in Host mode: builtin (baseline) + sandbox (downloads). The bundle
            // is NOT in builtin (we only put it in the CDN), so the downloader must fetch it.
            var pkg = XAssets.CreatePackage("hostpkg");
            var builtin = new BuiltinStorage();
            builtin.OnCreateAsync("hostpkg", _builtinDir).Wait();
            builtin.InitializeStores(AssetBundlePlatform.Any).Wait();

        var sandbox = new SandboxStorage();
        sandbox.OnCreateAsync("hostpkg", _sandboxDir).Wait();
        sandbox.Configure(bundleDecryptor: null, manifestDecryptor: null, remote: new FakeRemote(), builtinPackageRoot: _builtinDir);
        sandbox.InitializeStores(AssetBundlePlatform.Any).Wait();

            pkg.Initialize(AssetPlayMode.Host, new[] { (IAssetStorage)builtin, (IAssetStorage)sandbox });

            var manOp = pkg.LoadPackageManifestAsync("1.0.0");
            PumpUntil(() => manOp.IsDone);
            Assert.Null(manOp.Error);

            // The bundle is missing from both storages -> downloader should have 1 item.
            var backend = new FakeDownloadBackend(_cdn);
            var downloader = pkg.CreateResourceDownloader(backend, new FakeRemote());
            Assert.NotNull(downloader);
            Assert.Equal(1, downloader!.Pending.Count);

            // Run the download to completion.
            AsyncOperationSystem.StartOperation(downloader);
            PumpUntil(() => downloader.IsDone);
            Assert.True(downloader.Status == EOperationStatus.Succeeded, $"download error: {downloader.Error}");
            Assert.Equal(1, downloader.Completed.Count);
            Assert.Empty(downloader.Failed);

            // Now the bundle is in the sandbox; loading the asset should succeed.
            var handle = pkg.LoadAssetAsync<HostAsset>("hero");
            PumpUntil(handle);
            Assert.True(handle.IsValid, $"load error: {handle.Error}");
            Assert.NotNull(handle.GetAssetObject<HostAsset>());
            Assert.Equal(guid, handle.AssetGuid);
            handle.Release();
            PumpTimes(3);
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    /// <summary>Write a bundle to an explicit path (not the builtin dir) and stash its bytes in the
    /// fake CDN. Returns the bundle's header hash.</summary>
    private BundleHash WriteBundleToPath(string path, string bundleName, Guid assetGuid, string address)
    {
        var payload = MakePayload("m-" + address);
        var entry = new AssetBundleEntry(assetGuid, address, typeof(HostAsset).FullName ?? "HostAsset", 0, payload.Length);
        AssetBundleWriter.Write(path, new[] { new KeyValuePair<AssetBundleEntry, byte[]>(entry, payload) },
            new AssetBundleBuildOptions { Compression = BundleCompressionMode.Lz4Block, Lz4BlockSize = 4096 });
        _cdn[bundleName + ".bundle"] = File.ReadAllBytes(path);
        return ReadBundleHeaderHash(path);
    }

    private static void PumpUntil(Func<bool> done, int maxFrames = 5000)
    {
        // Yield a small slice each frame so thread-pool download/verify tasks can make progress - the
        // real engine pumps at ~60fps (16ms/frame); a tight loop here would starve the pool.
        for (int i = 0; i < maxFrames; i++)
        {
            XAssets.Tick();
            if (done()) return;
            System.Threading.Thread.Sleep(1);
        }
        throw new TimeoutException("Did not complete in " + maxFrames + " frames.");
    }
    private static void PumpUntil(AssetHandleBase h, int maxFrames = 5000) => PumpUntil(() => h.IsDone, maxFrames);
    private static void PumpTimes(int n) { for (int i = 0; i < n; i++) XAssets.Tick(); }
}
