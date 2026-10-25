// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// BuiltinBundleStore - read-only bundle store backed by a directory of bundle files that ship inside
// the app package (StreamingAssetsPath/Bundles/{Platform} on desktop, assets/ on Android). This is
// the "baseline" store for Offline and Host play modes: it serves whatever the build shipped with.
//
// Independent rewrite informed by YooAsset's BuiltinBundleCache (Apache-2.0).

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>Read-only store over a directory of pre-shipped bundle files. The factory for bundle
/// handles delegates to <see cref="AssetBundle.LoadFromFile"/> (with optional decryption offset/memory
/// paths). Used by <see cref="BuiltinStorage"/> for Offline and Host modes.</summary>
public sealed class BuiltinBundleStore : IBundleStore
{
    private readonly string _root;
    private readonly AssetBundlePlatform _platform;
    private readonly IBundleDecryptor? _decryptor;
    private readonly Dictionary<BundleHash, string> _index = new();

    public BuiltinBundleStore(string root, AssetBundlePlatform platform, IBundleDecryptor? decryptor = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _platform = platform;
        _decryptor = decryptor;
    }

    public string Name => "builtin";
    public string RootPath => _root;
    public bool IsReadOnly => true;

    public Task InitializeAsync()
    {
        // Index every *.bundle in the root by parsing its header's BundleHash. We read only the
        // 84-byte header (not the data), so this is cheap even for large bundles.
        _index.Clear();
        if (!Directory.Exists(_root)) return Task.CompletedTask;

        foreach (var file in Directory.GetFiles(_root, "*.bundle"))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.RandomAccess);
                using var br = new BinaryReader(fs);
                uint magic = br.ReadUInt32();
                if (magic != AssetBundle.Magic) continue;
                br.ReadUInt16();   // version
                br.ReadByte();     // platform
                br.ReadByte();     // compression
                br.ReadInt32();    // entryCount
                br.ReadUInt32();   // flags
                br.ReadUInt32();   // headerCrc
                br.ReadInt64();    // indexOffset
                br.ReadInt64();    // indexLength
                br.ReadInt64();    // dataOffset
                br.ReadInt64();    // dataLength
                br.ReadInt64();    // manifestOffset
                br.ReadInt64();    // manifestLength
                var hashBytes = br.ReadBytes(16);
                if (hashBytes.Length == 16)
                    _index[BundleHash.ReadBigEndian(hashBytes)] = file;
            }
            catch { /* skip unreadable file */ }
        }

        return Task.CompletedTask;
    }

    public bool IsCached(BundleHash bundleGuid) => _index.ContainsKey(bundleGuid);

    public string? GetCacheFilePath(BundleHash bundleGuid)
        => _index.TryGetValue(bundleGuid, out var p) ? p : null;

    public AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor)
    {
        var op = new BuiltinLoadOp(bundle, this, decryptor ?? _decryptor);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public Task WriteCacheAsync(BundleInfo bundle, byte[] data)
        => throw new InvalidOperationException("BuiltinBundleStore is read-only.");

    public Task VerifyCacheAsync() => Task.CompletedTask; // read-only: nothing to repair
    public Task ClearCacheAsync() => Task.CompletedTask;

    public long FileCount => _index.Count;
    public long SpaceOccupied
    {
        get
        {
            long total = 0;
            foreach (var p in _index.Values)
            {
                try { total += new FileInfo(p).Length; } catch { }
            }
            return total;
        }
    }

    private sealed class BuiltinLoadOp : AsyncOperationBase<IBundleHandle?>
    {
        private readonly BundleInfo _bundle;
        private readonly BuiltinBundleStore _store;
        private readonly IBundleDecryptor? _decryptor;

        public BuiltinLoadOp(BundleInfo bundle, BuiltinBundleStore store, IBundleDecryptor? decryptor)
        {
            _bundle = bundle; _store = store; _decryptor = decryptor;
        }

        protected override void InternalStart() { }

        protected override void InternalUpdate()
        {
            try
            {
                string? path = _store.GetCacheFilePath(_bundle.BundleGuid);
                if (path == null) { SetError($"Bundle '{_bundle.BundleName}' not present in builtin store."); return; }

                AssetBundle ab;
                if (_decryptor is IBundleOffsetDecryptor offsetDec)
                {
                    // Junk-header encryption: load the real bundle from the offset. We open a
                    // sub-stream starting at the offset. (A full AssetBundle.LoadFromStream path.)
                    long off = offsetDec.GetFileOffset(new BundleDecryptArgs(_bundle, path, null));
                    var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.RandomAccess);
                    fs.Seek(off, SeekOrigin.Begin);
                    ab = AssetBundle.LoadFromStream(fs, ownsStream: true);
                }
                else if (_decryptor is IBundleMemoryDecryptor memDec)
                {
                    var bytes = File.ReadAllBytes(path);
                    var decrypted = memDec.GetDecryptedData(new BundleDecryptArgs(_bundle, path, bytes));
                    ab = AssetBundle.LoadFromMemory(decrypted);
                }
                else if (_decryptor is IBundleStreamDecryptor streamDec)
                {
                    var args = new BundleDecryptArgs(_bundle, path, null);
                    var stream = streamDec.CreateDecryptionStream(args);
                    ab = AssetBundle.LoadFromStream(stream, ownsStream: true);
                }
                else
                {
                    ab = AssetBundle.LoadFromFile(path);
                }

                Result = new XEngineBundleHandle(ab, _bundle);
                SetResult();
            }
            catch (Exception ex)
            {
                SetError($"Failed to load builtin bundle '{_bundle.BundleName}': {ex.Message}");
            }
        }
    }
}
