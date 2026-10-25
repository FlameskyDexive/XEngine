// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Async;
using XEngine.Echo;
using XEngine.Runtime.AssetStreaming;
using XEngine.Runtime.Platforms.Browser;

using Scene = XEngine.Runtime.Resources.Scene;

using Xunit;

namespace XEngine.Runtime.Test.AssetStreaming;

public sealed class AssetStreamProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngine-AssetStreamProviderTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DirectoryProviderAndVfsUseCanonicalMountedPaths()
    {
        string assets = Path.Combine(_root, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "Bundles"));
        File.WriteAllText(Path.Combine(assets, "readme.txt"), "mounted");
        File.WriteAllBytes(Path.Combine(assets, "Bundles", "b.bundle"), [2]);
        File.WriteAllBytes(Path.Combine(assets, "Bundles", "a.bundle"), [1]);

        using var provider = new DirectoryAssetStreamProvider(assets);
        using var vfs = new AssetVirtualFileSystem();
        vfs.MountProvider("Assets", provider);

        using Stream stream = Assert.IsAssignableFrom<Stream>(vfs.OpenRead("Assets/readme.txt"));
        using var reader = new StreamReader(stream);
        Assert.Equal("mounted", reader.ReadToEnd());
        Assert.Equal(
            ["Assets/Bundles/a.bundle", "Assets/Bundles/b.bundle"],
            vfs.GetFiles("Assets/Bundles", ".bundle"));
        Assert.Null(vfs.OpenRead("Assets/Readme.txt"));
        Assert.Empty(vfs.GetFiles("Assets/bundles", ".bundle"));
        Assert.Throws<ArgumentException>(() => vfs.OpenRead("Assets/../readme.txt"));
        Assert.Throws<ArgumentException>(() => vfs.OpenRead("Assets\\readme.txt"));
        Assert.Throws<DirectoryNotFoundException>(() => vfs.OpenRead("Missing/readme.txt"));
    }

    [Fact]
    public void VfsWritesOnlyThroughWritableSandboxMounts()
    {
        string files = Path.Combine(_root, "files");
        using var packaged = new MemoryProvider(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["readme.txt"] = "read-only"u8.ToArray(),
        });
        using var writable = new DirectoryAssetStreamProvider(files);
        using var vfs = new AssetVirtualFileSystem();
        vfs.MountProvider("Assets", packaged);
        vfs.MountProvider("Files", writable);

        Assert.False(vfs.IsWritableMount("Assets"));
        Assert.True(vfs.IsWritableMount("Files"));
        vfs.CreateDirectory("Files/Saves");
        using (Stream output = vfs.OpenWrite("Files/Saves/slot.bin"))
            output.Write([1, 2, 3]);

        using (Stream input = Assert.IsAssignableFrom<Stream>(vfs.OpenRead("Files/Saves/slot.bin")))
        {
            Assert.Equal([1, 2, 3], ReadAll(input));
        }
        Assert.Throws<IOException>(() => vfs.OpenWrite("Files/Saves/slot.bin", overwrite: false));
        Assert.Throws<NotSupportedException>(() => vfs.OpenWrite("Assets/new.bin"));
        Assert.Throws<ArgumentException>(() => vfs.OpenWrite("Files/../escape.bin"));
        Assert.True(vfs.Delete("Files/Saves/slot.bin"));
        Assert.False(vfs.Delete("Files/Saves/slot.bin"));
    }

    [Fact]
    public void ZipProviderMaterializesNonSeekablePackageEntries()
    {
        byte[] package = CreateZip(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["catalog.xshadercatalog"] = [1, 2, 3],
            ["B.xshader"] = [4, 5],
            ["A.xshader"] = [6, 7],
        });
        using var provider = new ZipAssetStreamProvider(
            new NonSeekableReadStream(new MemoryStream(package, writable: false)),
            "android raw resource");

        Assert.Equal(["A.xshader", "B.xshader"], provider.GetFiles(string.Empty, ".xshader"));
        using Stream first = Assert.IsAssignableFrom<Stream>(provider.OpenRead("A.xshader"));
        using Stream second = Assert.IsAssignableFrom<Stream>(provider.OpenRead("B.xshader"));
        Assert.Equal([6, 7], ReadAll(first));
        Assert.Equal([4, 5], ReadAll(second));
        Assert.Null(provider.OpenRead("missing.xshader"));
    }

    [Fact]
    public void PlayerBackendReadsManifestThroughStreamProvider()
    {
        Guid scene = Guid.NewGuid();
        Guid resource = Guid.NewGuid();
        var root = EchoObject.NewCompound();
        root["defaultScene"] = new EchoObject(scene.ToString());
        root["assets"] = EchoObject.NewCompound();
        var resources = EchoObject.NewCompound();
        resources["UI/Icon"] = new EchoObject(resource.ToString());
        root["resources"] = resources;
        using var manifest = new MemoryStream();
        root.WriteToBinary(new BinaryWriter(manifest));

        using var provider = new MemoryProvider(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["asset_manifest.bin"] = manifest.ToArray(),
        });
        using var vfs = new AssetVirtualFileSystem();
        vfs.MountProvider("Assets", provider);
        using var backend = new PlayerAssetBackend(AssetPackagingMode.LooseFiles, vfs, "Assets");

        Assert.Equal(scene, backend.DefaultSceneGuid);
        Assert.Equal(resource, backend.ResourcesMap["UI/Icon"]);
        Assert.False(provider.Disposed);
    }

    [Fact]
    public void PlayerBackendReadsPakThroughStreamProvider()
    {
        Guid sceneGuid = Guid.NewGuid();
        var manifestRoot = EchoObject.NewCompound();
        manifestRoot["defaultScene"] = new EchoObject(sceneGuid.ToString());
        manifestRoot["assets"] = EchoObject.NewCompound();
        manifestRoot["resources"] = EchoObject.NewCompound();
        byte[] manifest = WriteEcho(manifestRoot);

        var scene = new Scene { Name = "Provider Pak Scene" };
        byte[] sceneBytes = WriteEcho(Serializer.Serialize(scene));
        byte[] pak = CreateZip(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{sceneGuid}.asset"] = sceneBytes,
        });
        using var provider = new MemoryProvider(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["asset_manifest.bin"] = manifest,
            ["main.xenginepak"] = pak,
        });
        using var backend = new PlayerAssetBackend(AssetPackagingMode.XEnginePak, provider);

        Scene loaded = Assert.IsType<Scene>(backend.LoadScene(sceneGuid));

        Assert.Equal("Provider Pak Scene", loaded.Name);
        Assert.False(provider.Disposed);
    }

    [Fact]
    public async Task ContentAddressedCacheIsAtomicVerifiedAndReusable()
    {
        byte[] content = "android-cache-payload"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content));
        using var provider = new MemoryProvider(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Bundles/main.bundle"] = content,
        });
        string cache = Path.Combine(_root, "cache");

        AssetCacheMaterialization first = await ContentAddressedAssetCache.MaterializeAsync(
            provider,
            "Bundles/main.bundle",
            cache,
            hash);
        AssetCacheMaterialization second = await ContentAddressedAssetCache.MaterializeAsync(
            provider,
            "Bundles/main.bundle",
            cache,
            hash.ToLowerInvariant());

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.FilePath, second.FilePath);
        Assert.Equal(content, await File.ReadAllBytesAsync(first.FilePath));
        Assert.Equal(1, provider.OpenCount);

        string invalidCache = Path.Combine(_root, "invalid-cache");
        string wrongHash = new('0', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ContentAddressedAssetCache.MaterializeAsync(
                provider,
                "Bundles/main.bundle",
                invalidCache,
                wrongHash));
        Assert.Empty(Directory.GetFiles(invalidCache));
    }

    [Fact]
    public void MaterializingProviderCachesVerifiedNonSeekablePayloads()
    {
        byte[] content = "android-non-seekable-pak"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content));
        int openCount = 0;
        using var source = new CallbackAssetStreamProvider(
            "android assets",
            path =>
            {
                openCount++;
                return path == "main.xenginepak"
                    ? new NonSeekableReadStream(new MemoryStream(content, writable: false))
                    : null;
            },
            static (directory, extension) =>
                directory.Length == 0 && extension == ".xenginepak"
                    ? ["main.xenginepak"]
                    : []);
        using var provider = new MaterializingAssetStreamProvider(
            source,
            Path.Combine(_root, "materialized"),
            [new PackagedAssetDescriptor("main.xenginepak", hash, content.Length, RequiresSeek: true)],
            memoryThresholdBytes: 0);

        using Stream first = Assert.IsAssignableFrom<Stream>(provider.OpenRead("main.xenginepak"));
        Assert.True(first.CanSeek);
        Assert.Equal(content, ReadAll(first));
        using Stream second = Assert.IsAssignableFrom<Stream>(provider.OpenRead("main.xenginepak"));
        Assert.Equal(content, ReadAll(second));
        Assert.Equal(["main.xenginepak"], provider.GetFiles(string.Empty, ".xenginepak"));
        Assert.Equal(3, openCount);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "materialized"), "*.xenginepak"));
    }

    [Fact]
    public async Task OpenReadAsync_UsesMountedProviderAndDoesNotBlock()
    {
        string assets = Path.Combine(_root, "async-assets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "readme.txt"), "mounted-async");

        using var provider = new DirectoryAssetStreamProvider(assets);
        using var vfs = new AssetVirtualFileSystem();
        vfs.MountProvider("Assets", provider);

        await using Stream stream = Assert.IsAssignableFrom<Stream>(
            await vfs.OpenReadAsync("Assets/readme.txt"));
        using var reader = new StreamReader(stream);
        Assert.Equal("mounted-async", reader.ReadToEnd());
    }

    [Fact]
    public async Task MaterializeAsync_UsesOpenReadAsyncInsteadOfSyncOpen()
    {
        byte[] content = "async-only-payload"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content));
        using var provider = new AsyncOnlyMemoryProvider(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Bundles/main.bundle"] = content,
        });
        string cache = Path.Combine(_root, "async-cache");

        AssetCacheMaterialization materialized = await ContentAddressedAssetCache.MaterializeAsync(
            provider,
            "Bundles/main.bundle",
            cache,
            hash);

        Assert.False(materialized.Reused);
        Assert.Equal(content, await File.ReadAllBytesAsync(materialized.FilePath));
        Assert.Equal(0, provider.SyncOpenCount);
        Assert.Equal(1, provider.AsyncOpenCount);
    }

    [Fact]
    public async Task MaterializingProvider_OpenReadAsync_MaterializesWithoutGetResult()
    {
        byte[] content = "async-non-seekable-pak"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content));
        int openCount = 0;
        using var source = new CallbackAssetStreamProvider(
            "android assets",
            path =>
            {
                openCount++;
                return path == "main.xenginepak"
                    ? new NonSeekableReadStream(new MemoryStream(content, writable: false))
                    : null;
            },
            static (directory, extension) =>
                directory.Length == 0 && extension == ".xenginepak"
                    ? ["main.xenginepak"]
                    : []);
        using var provider = new MaterializingAssetStreamProvider(
            source,
            Path.Combine(_root, "materialized-async"),
            [new PackagedAssetDescriptor("main.xenginepak", hash, content.Length, RequiresSeek: true)],
            memoryThresholdBytes: 0);

        await using Stream first = Assert.IsAssignableFrom<Stream>(
            await provider.OpenReadAsync("main.xenginepak"));
        Assert.True(first.CanSeek);
        Assert.Equal(content, ReadAll(first));
        Assert.Equal(2, openCount);
    }

    [Fact]
    public async Task HttpProvider_FetchesBytesAndRejectsSynchronousOpen()
    {
        byte[] payload = "from-fetch"u8.ToArray();
        using var handler = new StubHttpHandler(payload);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://assets.test/") };
        using var provider = new HttpAssetStreamProvider(
            http,
            urlPrefix: "Content/",
            catalog: ["readme.txt", "Bundles/a.bundle"]);

        Assert.Throws<InvalidOperationException>(() => provider.OpenRead("readme.txt"));
        await using Stream stream = Assert.IsAssignableFrom<Stream>(
            await provider.OpenReadAsync("readme.txt"));
        Assert.Equal(payload, ReadAll(stream));
        Assert.Equal("http://assets.test/Content/readme.txt", handler.LastUri?.ToString());
        Assert.Null(await provider.OpenReadAsync("missing.txt"));
        Assert.Equal(["Bundles/a.bundle"], provider.GetFiles("Bundles", ".bundle"));
    }

    [Fact]
    public async Task BrowserAssetHost_InstallsFetchProviderAtAssetsMount()
    {
        byte[] payload = "browser-content"u8.ToArray();
        using var handler = new StubHttpHandler(payload);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8765/") };
        try
        {
            BrowserAssetHost.Install(http, contentPrefix: "Content/", ownsClient: false);
            Assert.NotNull(BrowserAssetHost.FileSystem);
            await using Stream stream = Assert.IsAssignableFrom<Stream>(
                await BrowserAssetHost.FileSystem!.OpenReadAsync("Assets/icon.bin"));
            Assert.Equal(payload, ReadAll(stream));
            Assert.Equal("http://127.0.0.1:8765/Content/icon.bin", handler.LastUri?.ToString());
        }
        finally
        {
            BrowserAssetHost.Shutdown();
        }
    }

    [Fact]
    public void MiniGamePackageProvider_OpensSyncInPackageFiles()
    {
        byte[] payload = "wx-fs"u8.ToArray();
        using var provider = new MiniGamePackageFileProvider();
        provider.Add("Content/icon.bin", payload);

        using Stream stream = Assert.IsAssignableFrom<Stream>(provider.OpenRead("Content/icon.bin"));
        Assert.Equal(payload, ReadAll(stream));
        Assert.Equal(["Content/icon.bin"], provider.GetFiles("Content", ".bin"));
        Assert.Null(provider.OpenRead("Content/missing.bin"));
        Assert.Throws<ArgumentException>(() => provider.Add("../escape.bin", payload));
    }

    [Fact]
    public async Task BrowserAssetHost_InstallPackageMountsSyncFsAndOptionalCdn()
    {
        byte[] packaged = "in-package"u8.ToArray();
        byte[] remote = "from-cdn"u8.ToArray();
        using var handler = new StubHttpHandler(remote);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://cdn.test/") };
        try
        {
            BrowserAssetHost.InstallPackage(http, cdnPrefix: "bundles/", ownsCdnClient: false);
            BrowserAssetHost.AddPackageFile("Content/icon.bin", packaged);
            Assert.NotNull(BrowserAssetHost.Package);
            Assert.NotNull(BrowserAssetHost.Content);
            using Stream local = Assert.IsAssignableFrom<Stream>(
                BrowserAssetHost.FileSystem!.OpenRead("Assets/Content/icon.bin"));
            Assert.Equal(packaged, ReadAll(local));
            await using Stream cdn = Assert.IsAssignableFrom<Stream>(
                await BrowserAssetHost.FileSystem!.OpenReadAsync("Cdn/hero.bin"));
            Assert.Equal(remote, ReadAll(cdn));
            Assert.Equal("https://cdn.test/bundles/hero.bin", handler.LastUri?.ToString());
        }
        finally
        {
            BrowserAssetHost.Shutdown();
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private static byte[] CreateZip(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            string[] names = [.. entries.Keys];
            Array.Sort(names, StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
            {
                ZipArchiveEntry entry = archive.CreateEntry(names[i], CompressionLevel.NoCompression);
                using Stream stream = entry.Open();
                stream.Write(entries[names[i]]);
            }
        }
        return output.ToArray();
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] WriteEcho(EchoObject echo)
    {
        using var output = new MemoryStream();
        echo.WriteToBinary(new BinaryWriter(output));
        return output.ToArray();
    }

    private sealed class MemoryProvider(IReadOnlyDictionary<string, byte[]> files) : IAssetStreamProvider
    {
        public string Description => "memory";
        public int OpenCount { get; private set; }
        public bool Disposed { get; private set; }

        public Stream? OpenRead(string path)
        {
            OpenCount++;
            return files.TryGetValue(path, out byte[]? value)
                ? new MemoryStream(value, writable: false)
                : null;
        }

        public IReadOnlyList<string> GetFiles(string directory, string extension)
        {
            string prefix = directory.Length == 0 ? string.Empty : directory + "/";
            var result = new List<string>();
            foreach (string path in files.Keys)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                    !path.EndsWith(extension, StringComparison.Ordinal))
                {
                    continue;
                }
                string remainder = path[prefix.Length..];
                if (!remainder.Contains('/'))
                    result.Add(path);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class AsyncOnlyMemoryProvider(IReadOnlyDictionary<string, byte[]> files) : IAssetStreamProvider
    {
        public string Description => "async-only-memory";
        public int SyncOpenCount { get; private set; }
        public int AsyncOpenCount { get; private set; }

        public Stream? OpenRead(string path)
        {
            SyncOpenCount++;
            throw new InvalidOperationException("Synchronous OpenRead is forbidden for this provider.");
        }

        public XTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        {
            AsyncOpenCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return XTask.FromResult<Stream?>(
                files.TryGetValue(path, out byte[]? value)
                    ? new MemoryStream(value, writable: false)
                    : null);
        }

        public IReadOnlyList<string> GetFiles(string directory, string extension) => [];

        public void Dispose()
        {
        }
    }

    private sealed class StubHttpHandler(byte[] payload) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/missing.txt", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload),
            });
        }
    }

    private sealed class NonSeekableReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
