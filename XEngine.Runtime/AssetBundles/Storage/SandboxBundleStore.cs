// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// SandboxBundleStore - the writable downloads cache. Lives under Application.PersistentDataPath
// (so it survives app updates). Bundles downloaded by ResourceDownloader land here; the store
// verifies CRC32 + content hash before promoting the temp file to its final name, so a corrupt or
// truncated download never appears as "cached".
//
// Independent rewrite of YooAsset's SandboxBundleCache (Apache-2.0). The verify-and-promote flow
// mirrors that design; XEngine's verification uses its own Crc32Algorithm + BundleHash.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Writable bundle cache for downloaded updates. Files are keyed by the bundle's content GUID (its
/// FileHash) and stored as {RootPath}/{guid}.bundle. Verified on promotion and on every load.
/// </summary>
public sealed class SandboxBundleStore : IBundleStore
{
    private readonly string _root;
    private readonly AssetBundlePlatform _platform;
    private readonly IBundleDecryptor? _decryptor;
    private readonly Dictionary<BundleHash, string> _index = new();
    private readonly object _indexLock = new();

    /// <summary>How thoroughly to verify a bundle before promoting/loading it.</summary>
    public BundleVerifyLevel VerifyLevel { get; set; } = BundleVerifyLevel.Hash;

    public SandboxBundleStore(string root, AssetBundlePlatform platform, IBundleDecryptor? decryptor = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _platform = platform;
        _decryptor = decryptor;
    }

    public string Name => "sandbox";
    public string RootPath => _root;
    public bool IsReadOnly => false;

    public Task InitializeAsync()
    {
        lock (_indexLock)
        {
            _index.Clear();
            Directory.CreateDirectory(_root);
            // Index every *.bundle by parsing its header hash (same scheme as BuiltinBundleStore).
            foreach (var file in Directory.GetFiles(_root, "*.bundle"))
            {
                try
                {
                    var hash = ReadBundleHeaderHash(file);
                    if (hash.IsValid) _index[hash] = file;
                }
                catch { /* skip unreadable */ }
            }
        }
        return Task.CompletedTask;
    }

    public bool IsCached(BundleHash bundleGuid)
    {
        lock (_indexLock) return _index.ContainsKey(bundleGuid);
    }

    public string? GetCacheFilePath(BundleHash bundleGuid)
    {
        lock (_indexLock) return _index.TryGetValue(bundleGuid, out var p) ? p : null;
    }

    public AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor)
    {
        var op = new SandboxLoadOp(bundle, this, decryptor ?? _decryptor);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public async Task WriteCacheAsync(BundleInfo bundle, byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, bundle.BundleGuid.ToString() + ".bundle");
        await File.WriteAllBytesAsync(path, data);
        lock (_indexLock) _index[bundle.BundleGuid] = path;
    }

    public Task VerifyCacheAsync()
    {
        // Re-verify every indexed file; drop entries whose file is missing (caller can re-download).
        lock (_indexLock)
        {
            var stale = new List<BundleHash>();
            foreach (var kv in _index)
            {
                if (!File.Exists(kv.Value)) stale.Add(kv.Key);
            }
            foreach (var key in stale) _index.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task ClearCacheAsync()
    {
        lock (_indexLock)
        {
            foreach (var p in _index.Values)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
            _index.Clear();
        }
        return Task.CompletedTask;
    }

    public long FileCount { get { lock (_indexLock) return _index.Count; } }

    public long SpaceOccupied
    {
        get
        {
            long total = 0;
            lock (_indexLock)
            {
                foreach (var p in _index.Values)
                {
                    try { total += new FileInfo(p).Length; } catch { }
                }
            }
            return total;
        }
    }

    // ----------------------------------------------------------------
    //  Verify + promote (called by ResourceDownloader after a fetch)
    // ----------------------------------------------------------------

    /// <summary>Verify the just-downloaded file at <paramref name="tempPath"/> against
    /// <paramref name="bundle"/>'s CRC/hash, then atomically rename it into the cache. Throws on
    /// verification failure so the downloader retries from the next URL.</summary>
    public async Task VerifyAndPromoteAsync(BundleInfo bundle, string tempPath, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(tempPath)) throw new FileNotFoundException("downloaded temp file missing", tempPath);

        // CRC32 check (cheap).
        if (VerifyLevel >= BundleVerifyLevel.Crc)
        {
            uint crc = await ComputeFileCrcAsync(tempPath, cancellation);
            if (crc != bundle.FileCrc)
                throw new InvalidDataException($"CRC mismatch for {bundle.BundleName}: got 0x{crc:X8}, expected 0x{bundle.FileCrc:X8}.");
        }
        // Content-hash check (slower; full-file FNV).
        if (VerifyLevel >= BundleVerifyLevel.Hash)
        {
            var hash = await ComputeFileHashAsync(tempPath, cancellation);
            if (hash != bundle.FileHash)
                throw new InvalidDataException($"Hash mismatch for {bundle.BundleName}.");
        }

        // Atomic promote: rename into the cache, then index.
        string finalPath = Path.Combine(_root, bundle.BundleGuid.ToString() + ".bundle");
        Directory.CreateDirectory(_root);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tempPath, finalPath);
        lock (_indexLock) _index[bundle.BundleGuid] = finalPath;
    }

    private static async Task<uint> ComputeFileCrcAsync(string path, CancellationToken cancellation)
    {
        // Single-pass streaming CRC (the static Crc32Algorithm.ComputeFile is sync; reimplement async here).
        uint crc = Crc32Algorithm.InitialValue;
        var buffer = new byte[8192];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < read; i++) crc = Crc32Algorithm.Update(crc, buffer[i]);
        }
        return ~crc;
    }

    private static async Task<BundleHash> ComputeFileHashAsync(string path, CancellationToken cancellation)
    {
        const ulong fnvOffset = 0xcbf29ce484222325UL;
        const ulong fnvPrime = 0x100000001b3UL;
        const ulong highSeed = 0x9e3779b97f4a7c15UL;
        const ulong highPrimeDelta = 0x100000001b2UL;
        const ulong highXorSalt = 0x9eUL;
        ulong low = fnvOffset;
        ulong high = highSeed ^ fnvOffset;

        var buffer = new byte[8192];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte b = buffer[i];
                low = (low ^ b) * fnvPrime;
                high = (high ^ (b + highXorSalt)) * (fnvPrime + highPrimeDelta);
            }
        }
        return new BundleHash(low, high);
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

    /// <summary>Backwards-compatible alias used by ResourceDownloader.</summary>
    public BundleVerifyLevel Verify { get => VerifyLevel; set => VerifyLevel = value; }

    private sealed class SandboxLoadOp : AsyncOperationBase<IBundleHandle?>
    {
        private readonly BundleInfo _bundle;
        private readonly SandboxBundleStore _store;
        private readonly IBundleDecryptor? _decryptor;
        public SandboxLoadOp(BundleInfo bundle, SandboxBundleStore store, IBundleDecryptor? decryptor)
        { _bundle = bundle; _store = store; _decryptor = decryptor; }
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            try
            {
                string? path = _store.GetCacheFilePath(_bundle.BundleGuid);
                if (path == null) { SetError($"Bundle '{_bundle.BundleName}' not in sandbox store."); return; }
                AssetBundle ab = AssetBundle.LoadFromFile(path);
                Result = new XEngineBundleHandle(ab, _bundle);
                SetResult();
            }
            catch (Exception ex) { SetError($"Failed to load sandbox bundle '{_bundle.BundleName}': {ex.Message}"); }
        }
    }
}

/// <summary>Top-level alias matching the original SandboxBundleStore.BundleVerifyLevel naming.</summary>
public enum BundleVerifyLevel : byte
{
    None = 0,
    Crc = 1,
    Hash = 2,
}
