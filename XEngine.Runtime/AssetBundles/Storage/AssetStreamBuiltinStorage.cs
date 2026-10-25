// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using XEngine.Runtime.AssetStreaming;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Read-only package storage over an <see cref="IAssetStreamProvider"/>. This is the platform-package
/// counterpart to <see cref="BuiltinStorage"/> and is used when assets do not have stable filesystem
/// paths, such as Android APK assets.
/// </summary>
public sealed class AssetStreamBuiltinStorage : IAssetStorage
{
    private readonly IAssetStreamProvider _provider;
    private readonly string _packageRoot;
    private readonly AssetStreamBundleStore _store;
    private string _packageName;

    public AssetStreamBuiltinStorage(
        IAssetStreamProvider provider,
        string packageName,
        string packageRoot,
        AssetBundlePlatform platform)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        _packageName = packageName;
        _packageRoot = AssetStreamPath.Normalize(packageRoot);
        _store = new AssetStreamBundleStore(provider, _packageRoot, platform);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public string Name => "asset-stream-builtin";
    public string PackageName => _packageName;

    public Task OnCreateAsync(string packageName, string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        string normalizedRoot = AssetStreamPath.Normalize(packageRoot);
        if (!string.Equals(packageName, _packageName, StringComparison.Ordinal) ||
            !string.Equals(normalizedRoot, _packageRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Asset stream storage identity cannot change after construction.");
        }
        return Task.CompletedTask;
    }

    public bool CanAcceptBundle(BundleInfo bundle) => _store.IsCached(bundle.BundleGuid);
    public bool IsDownloadRequired(BundleInfo bundle) => false;

    public AsyncOperationBase<string?> RequestPackageVersionAsync()
    {
        var operation = new VersionOperation(this);
        AsyncOperationSystem.StartOperation(operation);
        return operation;
    }

    public AsyncOperationBase<PackageManifest?> LoadPackageManifestAsync(
        string version,
        IManifestDecryptor? manifestDecryptor)
    {
        var operation = new ManifestOperation(this, version, manifestDecryptor);
        AsyncOperationSystem.StartOperation(operation);
        return operation;
    }

    public AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor) =>
        _store.LoadBundleAsync(bundle, decryptor);

    public Task DownloadBundleAsync(BundleInfo bundle) =>
        throw new InvalidOperationException("Asset stream builtin storage does not download bundles.");

    public Task ClearCacheAsync() => Task.CompletedTask;

    public void OnDestroy()
    {
        // The generated platform host owns the provider and disposes it after packages are removed.
    }

    /// <summary>Reads and validates the package's active manifest synchronously during player startup.</summary>
    public PackageManifest LoadActiveManifest()
    {
        string version = ReadVersion();
        PackageManifest? manifest = ReadManifest(version, decryptor: null);
        if (manifest == null)
            throw new InvalidDataException($"AssetBundle package '{_packageName}' has an unsupported manifest.");
        if (!string.Equals(manifest.PackageName, _packageName, StringComparison.Ordinal) ||
            !string.Equals(manifest.PackageVersion, version, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"AssetBundle package '{_packageName}' manifest identity mismatch.");
        }
        return manifest;
    }

    private string ReadVersion()
    {
        string path = Combine(PackageFileNames.VersionFile(_packageName));
        using Stream? stream = _provider.OpenRead(path);
        if (stream == null)
            throw new FileNotFoundException($"AssetBundle package version is missing: {path}", path);
        using var reader = new StreamReader(stream);
        string version = reader.ReadToEnd().Trim();
        if (version.Length == 0 || version.Contains('/') || version.Contains('\\'))
            throw new InvalidDataException($"AssetBundle package '{_packageName}' has an invalid version.");
        return version;
    }

    private PackageManifest? ReadManifest(string version, IManifestDecryptor? decryptor)
    {
        string path = Combine(PackageFileNames.ManifestFile(_packageName, version));
        using Stream? stream = _provider.OpenRead(path);
        if (stream == null)
            throw new FileNotFoundException($"AssetBundle package manifest is missing: {path}", path);
        if (decryptor == null)
            return PackageManifestSerializer.Deserialize(stream);

        byte[] bytes = ReadAllBytes(stream);
        return PackageManifestSerializer.Deserialize(decryptor.Decrypt(bytes));
    }

    private string Combine(string fileName) => AssetStreamPath.Combine(_packageRoot, fileName);

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private sealed class VersionOperation(AssetStreamBuiltinStorage owner) : AsyncOperationBase<string?>
    {
        protected override void InternalStart() { }

        protected override void InternalUpdate()
        {
            try
            {
                Result = owner.ReadVersion();
                SetResult();
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
            }
        }
    }

    private sealed class ManifestOperation(
        AssetStreamBuiltinStorage owner,
        string version,
        IManifestDecryptor? decryptor) : AsyncOperationBase<PackageManifest?>
    {
        protected override void InternalStart() { }

        protected override void InternalUpdate()
        {
            try
            {
                Result = owner.ReadManifest(version, decryptor);
                if (Result == null)
                    SetError($"AssetBundle package '{owner.PackageName}' manifest format is unsupported.");
                else
                    SetResult();
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
            }
        }
    }
}

/// <summary>Read-only bundle store backed by logical provider paths.</summary>
public sealed class AssetStreamBundleStore : IBundleStore
{
    private readonly IAssetStreamProvider _provider;
    private readonly string _root;
    private readonly AssetBundlePlatform _platform;
    private readonly Dictionary<BundleHash, string> _paths = new();
    private readonly Dictionary<BundleHash, long> _sizes = new();

    public AssetStreamBundleStore(
        IAssetStreamProvider provider,
        string root,
        AssetBundlePlatform platform)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _root = AssetStreamPath.Normalize(root);
        _platform = platform;
    }

    public string Name => "asset-stream-builtin";
    public string RootPath => _provider.Description + "/" + _root;
    public bool IsReadOnly => true;

    public Task InitializeAsync()
    {
        _paths.Clear();
        _sizes.Clear();
        IReadOnlyList<string> files = _provider.GetFiles(_root, ".bundle");
        for (int index = 0; index < files.Count; index++)
        {
            using Stream? stream = _provider.OpenRead(files[index]);
            if (stream == null)
                continue;
            byte[] header = new byte[AssetBundle.HeaderLength];
            try
            {
                stream.ReadExactly(header);
            }
            catch (EndOfStreamException)
            {
                continue;
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != AssetBundle.Magic)
                continue;
            AssetBundlePlatform bundlePlatform = (AssetBundlePlatform)header[6];
            if (bundlePlatform != AssetBundlePlatform.Any && bundlePlatform != _platform)
                continue;
            BundleHash bundleGuid = BundleHash.ReadBigEndian(header.AsSpan(68, 16));
            if (!bundleGuid.IsValid || !_paths.TryAdd(bundleGuid, files[index]))
                throw new InvalidDataException($"Duplicate or invalid AssetBundle identity in provider: {files[index]}");
            _sizes[bundleGuid] = stream.CanSeek ? stream.Length : 0;
        }
        return Task.CompletedTask;
    }

    public bool IsCached(BundleHash bundleGuid) => _paths.ContainsKey(bundleGuid);

    public string? GetCacheFilePath(BundleHash bundleGuid) =>
        _paths.TryGetValue(bundleGuid, out string? path) ? path : null;

    public AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor)
    {
        var operation = new LoadOperation(this, bundle, decryptor);
        AsyncOperationSystem.StartOperation(operation);
        return operation;
    }

    public Task WriteCacheAsync(BundleInfo bundle, byte[] data) =>
        throw new InvalidOperationException("Asset stream bundle storage is read-only.");

    public Task VerifyCacheAsync() => Task.CompletedTask;
    public Task ClearCacheAsync() => Task.CompletedTask;
    public long FileCount => _paths.Count;

    public long SpaceOccupied
    {
        get
        {
            long total = 0;
            foreach (long size in _sizes.Values)
                total += size;
            return total;
        }
    }

    private byte[] ReadBundle(BundleInfo bundle)
    {
        string? path = GetCacheFilePath(bundle.BundleGuid);
        if (path == null)
            throw new FileNotFoundException($"Bundle '{bundle.BundleName}' is not present in provider storage.");
        using Stream? stream = _provider.OpenRead(path);
        if (stream == null)
            throw new FileNotFoundException($"Bundle provider stream is missing: {path}", path);
        using var output = bundle.FileSize is > 0 and <= int.MaxValue
            ? new MemoryStream((int)bundle.FileSize)
            : new MemoryStream();
        stream.CopyTo(output);
        byte[] bytes = output.ToArray();
        if (bytes.LongLength != bundle.FileSize)
            throw new InvalidDataException($"Bundle '{bundle.BundleName}' byte count mismatch.");
        if (Crc32Algorithm.Compute(bytes) != bundle.FileCrc)
            throw new InvalidDataException($"Bundle '{bundle.BundleName}' CRC mismatch.");
        if (BundleHash.Compute(bytes) != bundle.FileHash)
            throw new InvalidDataException($"Bundle '{bundle.BundleName}' file hash mismatch.");
        return bytes;
    }

    private AssetBundle CreateBundle(BundleInfo bundle, IBundleDecryptor? decryptor)
    {
        if (decryptor == null)
        {
            string path = GetCacheFilePath(bundle.BundleGuid)
                ?? throw new FileNotFoundException($"Bundle '{bundle.BundleName}' is not present in provider storage.");
            Stream stream = _provider.OpenRead(path)
                ?? throw new FileNotFoundException("Bundle provider stream is missing.", path);
            try
            {
                if (stream.CanSeek)
                {
                    if (stream.Length != bundle.FileSize)
                        throw new InvalidDataException($"Bundle '{bundle.BundleName}' byte count mismatch.");
                    if (Crc32Algorithm.Compute(stream) != bundle.FileCrc)
                        throw new InvalidDataException($"Bundle '{bundle.BundleName}' CRC mismatch.");
                    stream.Position = 0;
                    if (BundleHash.Compute(stream) != bundle.FileHash)
                        throw new InvalidDataException($"Bundle '{bundle.BundleName}' file hash mismatch.");
                    stream.Position = 0;
                    AssetBundle streamed = AssetBundle.LoadFromStream(stream, ownsStream: true);
                    if (streamed.BundleHash != bundle.BundleGuid ||
                        (streamed.Platform != AssetBundlePlatform.Any && streamed.Platform != _platform))
                    {
                        streamed.Dispose();
                        throw new InvalidDataException($"Bundle '{bundle.BundleName}' header identity mismatch.");
                    }
                    return streamed;
                }
            }
            catch { stream.Dispose(); throw; }
            stream.Dispose();
        }
        byte[] bytes = ReadBundle(bundle);
        string logicalPath = GetCacheFilePath(bundle.BundleGuid) ?? string.Empty;
        AssetBundle loaded;
        if (decryptor is IBundleMemoryDecryptor memoryDecryptor)
        {
            byte[] decrypted = memoryDecryptor.GetDecryptedData(new BundleDecryptArgs(bundle, logicalPath, bytes));
            loaded = AssetBundle.LoadFromMemory(decrypted);
        }
        else if (decryptor is IBundleOffsetDecryptor offsetDecryptor)
        {
            long offset = offsetDecryptor.GetFileOffset(new BundleDecryptArgs(bundle, logicalPath, null));
            if (offset < 0 || offset >= bytes.LongLength)
                throw new InvalidDataException($"Bundle '{bundle.BundleName}' decryption offset is invalid.");
            loaded = AssetBundle.LoadFromMemory(bytes.AsSpan(checked((int)offset)).ToArray());
        }
        else if (decryptor is IBundleStreamDecryptor)
        {
            throw new NotSupportedException("Provider-backed bundles require memory or offset decryption.");
        }
        else
        {
            loaded = AssetBundle.LoadFromMemory(bytes);
        }

        if (loaded.BundleHash != bundle.BundleGuid ||
            (loaded.Platform != AssetBundlePlatform.Any && loaded.Platform != _platform))
        {
            loaded.Dispose();
            throw new InvalidDataException($"Bundle '{bundle.BundleName}' header identity mismatch.");
        }
        return loaded;
    }

    private sealed class LoadOperation(
        AssetStreamBundleStore owner,
        BundleInfo bundle,
        IBundleDecryptor? decryptor) : AsyncOperationBase<IBundleHandle?>
    {
        protected override void InternalStart() { }

        protected override void InternalUpdate()
        {
            try
            {
                Result = new XEngineBundleHandle(owner.CreateBundle(bundle, decryptor), bundle);
                SetResult();
            }
            catch (Exception exception)
            {
                SetError($"Failed to load provider bundle '{bundle.BundleName}': {exception.Message}");
            }
        }
    }
}
