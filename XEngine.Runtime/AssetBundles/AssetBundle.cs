// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// XEngine native AssetBundle container. Each bundle is a single file with:
//
//   [Magic "XEBN"]                       4 B   0
//   [Version u16 = 1]                    2 B   4
//   [Platform u8]                        1 B   6   AssetBundlePlatform
//   [Compression u8]                     1 B   7   BundleCompressionMode
//   [EntryCount u32 LE]                  4 B   8
//   [Flags u32 LE]                       4 B   12  bit0 = has MainAsset
//   [HeaderCrc32 u32 LE]                 4 B   16  CRC32 of bytes [0..16) (header so far)
//   [IndexOffset u64 LE]                 8 B   20  byte offset of the index table
//   [IndexLength u64 LE]                 8 B   28  byte length of the index table
//   [DataOffset u64 LE]                  8 B   36  byte offset of the (possibly compressed) data region
//   [DataLength u64 LE]                  8 B   44  byte length of the data region on disk
//   [ManifestOffset u64 LE]              8 B   52  byte offset of the per-bundle manifest tail
//   [ManifestLength u64 LE]              8 B   60  byte length of the per-bundle manifest tail
//   [BundleHash 16 B]                    16 B  68  FNV content hash (built by writer)
//   --- header = 84 bytes ---
//
//   Index table (at IndexOffset, varint-encoded via BundleBufferWriter):
//     [varint blockTableCount] then blockTableCount x { varint UncompressedOffset, varint
//       UncompressedSize, varint CompressedOffset, varint CompressedSize }   (LZ4 only; empty for None)
//     [varint entryCount] then entryCount x { Guid(16B raw) varstring Name varstring TypeName
//       uvarint DataOffset uvarint DataLength }
//     DataOffset/DataLength are offsets into the DECOMPRESSED data region (so the reader resolves
//     them through the block table for LZ4 bundles).
//
//   Data region (at DataOffset): for None, raw concatenated Echo blobs. For LZ4, the compressed
//     blocks concatenated in order.
//
//   Manifest tail (at ManifestOffset): varint mainAssetPresent, Guid mainAssetGuid (if present),
//     varstring[] directDependencyBundleNames (for the runtime dep resolver), u32 contentCrc32.
//
// Payloads are individual Echo binary blobs (each = one WriteToBinary(asset) call) so the existing
// Library/cache/{guid}.asset bytes can be embedded verbatim with zero serialization changes. The
// reader can locate a single asset by GUID/name and deserialize just that one blob.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// A loaded XEngine AssetBundle. Provides random access to individual asset payloads by GUID or
/// name, with Unity-4.7-style Unload(true/false) semantics. Payloads are Echo binary blobs; assets
/// are returned as <see cref="EngineObject"/> instances. Thread-safety: instance methods take a
/// per-bundle lock; nested acquisition order is bundle-lock THEN AssetBackendBase._loadLock.
/// </summary>
public sealed class AssetBundle : IDisposable
{
    /// <summary>Container magic ("XEBN" = XEngine Bundle).</summary>
    public const uint Magic = 0x4E424558u; // 'X','E','B','N' little-endian

    /// <summary>Current container format version.</summary>
    public const ushort CurrentVersion = 1;

    /// <summary>Header length in bytes (fixed).</summary>
    public const int HeaderLength = 84;

    private const uint FlagHasMainAsset = 1u;

    private readonly object _lock = new();
    private Stream? _stream;
    private readonly bool _ownsStream;
    private AssetBundlePlatform _platform;
    private BundleCompressionMode _compression;
    private BundleHash _bundleHash;
    private uint _flags;
    private uint _headerCrc;
    private long _indexOffset;
    private long _indexLength;
    private long _dataOffset;
    private long _dataLength;
    private long _manifestOffset;
    private long _manifestLength;

    private readonly List<Lz4Block> _blocks = new();
    private readonly Dictionary<Guid, AssetBundleEntry> _entriesByGuid = new();
    private readonly Dictionary<string, AssetBundleEntry> _entriesByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<Guid, EngineObject> _loadedObjects = new();
    private readonly List<Guid> _directDependencyBundleIds = new();
    private Guid _mainAssetGuid;
    private uint _contentCrc;

    private int _disposed;
    private bool _unloaded;

    private AssetBundle(Stream stream, bool ownsStream)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        ReadHeaderAndIndex(this, stream);
    }

    /// <summary>Bundle platform. A loader should reject bundles whose platform does not match the
    /// running host (unless <see cref="AssetBundlePlatform.Any"/>).</summary>
    public AssetBundlePlatform Platform => _platform;

    /// <summary>Compression mode of the data region.</summary>
    public BundleCompressionMode Compression => _compression;

    /// <summary>Content hash (FNV-128) written at build time.</summary>
    public BundleHash BundleHash => _bundleHash;

    /// <summary>CRC32 of the uncompressed data region (for integrity verification / download checks).</summary>
    public uint ContentCrc => _contentCrc;

    /// <summary>Direct dependency bundle GUIDs this bundle references. Resolved transitively by the manifest layer.</summary>
    public IReadOnlyList<Guid> DirectDependencyBundleIds => _directDependencyBundleIds;

    /// <summary>Number of assets in the bundle.</summary>
    public int EntryCount => _entriesByGuid.Count;

    /// <summary>Main asset GUID, or <see cref="Guid.Empty"/> if the bundle has no main asset.</summary>
    public Guid MainAssetGuid => _mainAssetGuid;

    /// <summary>True after <see cref="Unload(bool)"/> has been called. Further loads throw.</summary>
    public bool IsUnloaded => _unloaded;

    // ================================================================
    //  Load factories
    // ================================================================

    /// <summary>
    /// Load an <see cref="AssetBundle"/> from a file. The file is opened read-only and held open
    /// (random access) until <see cref="Dispose"/> or <see cref="Unload(bool)"/> with stream release.
    /// </summary>
    public static AssetBundle LoadFromFile(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.RandomAccess);
        try
        {
            return new AssetBundle(fs, ownsStream: true);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Load from an in-memory byte array. The array is fully consumed into a private copy.</summary>
    public static AssetBundle LoadFromMemory(byte[] binary)
    {
        if (binary == null) throw new ArgumentNullException(nameof(binary));
        // Copy so the caller can't mutate the bytes under us.
        var copy = new byte[binary.Length];
        Array.Copy(binary, copy, binary.Length);
        var ms = new MemoryStream(copy, writable: false);
        try
        {
            return new AssetBundle(ms, ownsStream: true);
        }
        catch
        {
            ms.Dispose();
            throw;
        }
    }

    /// <summary>Load from a seekable stream. Caller owns the stream unless it disposes the bundle.</summary>
    public static AssetBundle LoadFromStream(Stream stream, bool ownsStream = false)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("Stream must be readable and seekable", nameof(stream));
        return new AssetBundle(stream, ownsStream);
    }

    // ================================================================
    //  Queries
    // ================================================================

    public bool Contains(string name) => !string.IsNullOrEmpty(name) && _entriesByName.ContainsKey(name);
    public bool Contains(Guid assetGuid) => assetGuid != Guid.Empty && _entriesByGuid.ContainsKey(assetGuid);

    public IEnumerable<string> GetAllAssetNames()
    {
        foreach (var kv in _entriesByName) yield return kv.Key;
    }

    public IEnumerable<Guid> GetAllAssetGuids()
    {
        foreach (var kv in _entriesByGuid) yield return kv.Key;
    }

    /// <summary>Gets the serialized byte length for an asset payload without deserializing it.</summary>
    public bool TryGetAssetPayloadSize(Guid assetGuid, out long byteLength)
    {
        if (_entriesByGuid.TryGetValue(assetGuid, out AssetBundleEntry entry))
        {
            byteLength = entry.DataLength;
            return true;
        }

        byteLength = 0;
        return false;
    }

    /// <summary>
    /// Reads one asset's raw decompressed Echo payload without deserializing it. The returned bytes
    /// are a fresh copy and remain valid after the bundle is unloaded. This is used by package
    /// integrity gates that need to compare a build-time payload hash with the bytes inside a
    /// compressed bundle.
    /// </summary>
    public bool TryReadAssetPayload(Guid assetGuid, out byte[] payload)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_unloaded || !_entriesByGuid.TryGetValue(assetGuid, out AssetBundleEntry entry))
            {
                payload = Array.Empty<byte>();
                return false;
            }

            payload = ReadPayload(entry) ?? Array.Empty<byte>();
            return payload.Length == entry.DataLength;
        }
    }

    /// <summary>The main asset, deserialized, or null if none. Result is cached for the bundle's life
    /// (until <see cref="Unload(bool)"/> with unloadAllLoadedObjects).</summary>
    public EngineObject? MainAsset => _mainAssetGuid == Guid.Empty ? null : GetCachedOrLoad(_mainAssetGuid, typeof(EngineObject));

    // ================================================================
    //  Asset loading
    // ================================================================

    public T? LoadAsset<T>(string name) where T : EngineObject
        => (T?)LoadAsset(name, typeof(T));

    public T? LoadAsset<T>(Guid guid) where T : EngineObject
        => (T?)LoadAsset(guid, typeof(T));

    public EngineObject? LoadAsset(string name, Type type)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!_entriesByName.TryGetValue(name, out var entry)) return null;
        return GetCachedOrLoad(entry.AssetGuid, type);
    }

    public EngineObject? LoadAsset(Guid guid, Type type)
    {
        if (guid == Guid.Empty) return null;
        if (!_entriesByGuid.ContainsKey(guid)) return null;
        return GetCachedOrLoad(guid, type);
    }

    public T[] LoadAllAssets<T>() where T : EngineObject
    {
        var list = new List<T>();
        foreach (var guid in _entriesByGuid.Keys)
        {
            var obj = GetCachedOrLoad(guid, typeof(T));
            if (obj is T t) list.Add(t);
        }
        return list.ToArray();
    }

    /// <summary>
    /// Read and deserialize a single asset by GUID, caching the result. Thread-safe: the shared
    /// underlying stream is read under the bundle lock, but deserialization runs outside it.
    /// </summary>
    /// <remarks>
    /// <see cref="Serializer.Deserialize"/> fires <c>OnAfterDeserialize</c> / component callbacks
    /// that may themselves block-load assets (e.g. PlayerInput resolving its InputActionAsset via
    /// <see cref="AssetLoader.LoadBlocking"/>). Deserializing under <c>_lock</c> deadlocks the
    /// loader thread, which needs that same lock to serve the requested asset (AB-BA). The payload
    /// is already a private byte buffer, so parsing it needs no lock.
    /// </remarks>
    private EngineObject? GetCachedOrLoad(Guid guid, Type type)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_unloaded) throw new InvalidOperationException("AssetBundle has been unloaded; cannot load assets.");

        EchoObject? echo;
        lock (_lock)
        {
            if (_loadedObjects.TryGetValue(guid, out var cached) && cached != null && !cached.IsDisposed)
                return type.IsInstanceOfType(cached) ? cached : null;

            if (!_entriesByGuid.TryGetValue(guid, out var entry)) return null;

            byte[]? payload = ReadPayload(entry);
            if (payload == null) return null;

            using var ms = new MemoryStream(payload, writable: false);
            using var br = new BinaryReader(ms);
            echo = EchoObject.ReadFromBinary(br);
        }

        if (echo == null) return null;
        try { System.IO.File.AppendAllText("F:/Git/XEngine/Build/trace-load.log",
            System.DateTime.Now.ToString("HH:mm:ss.fff") + $" [TraceBundle] deserialize {guid} ({type.Name}) tid={Environment.CurrentManagedThreadId}" + System.Environment.NewLine); } catch { }

        // Deserialization may re-enter this bundle (asset graph) and may block-load other assets;
        // both are safe only once the bundle lock has been released.
        var obj = Serializer.Deserialize(echo, typeof(object));
        if (obj is not EngineObject engineObj) return null;

        engineObj.AssetID = guid;
        lock (_lock)
        {
            // Another thread may have deserialized the same guid while we parsed; first wins.
            if (_loadedObjects.TryGetValue(guid, out var raced) && raced != null && !raced.IsDisposed)
                return type.IsInstanceOfType(raced) ? raced : null;
            _loadedObjects[guid] = engineObj;
        }
        return type.IsInstanceOfType(engineObj) ? engineObj : null;
    }

    /// <summary>Read one entry's raw decompressed payload bytes.</summary>
    private byte[]? ReadPayload(AssetBundleEntry entry)
    {
        var stream = _stream;
        if (stream == null) return null;

        if (_compression == BundleCompressionMode.None)
        {
            stream.Seek(_dataOffset + entry.DataOffset, SeekOrigin.Begin);
            var buf = new byte[entry.DataLength];
            int read = ReadExact(stream, buf);
            return read == buf.Length ? buf : null;
        }

        // LZ4: find the block(s) covering [entry.DataOffset, entry.DataOffset + entry.DataLength).
        long wantStart = entry.DataOffset;
        long wantEnd = entry.DataOffset + entry.DataLength;

        // Single-block fast path.
        for (int i = 0; i < _blocks.Count; i++)
        {
            var b = _blocks[i];
            long blockStart = b.UncompressedOffset;
            long blockEnd = b.UncompressedOffset + b.UncompressedSize;
            if (wantStart >= blockStart && wantEnd <= blockEnd)
            {
                return ReadFromBlock(in b, (int)(wantStart - blockStart), (int)entry.DataLength);
            }
        }

        // Multi-block (entry spans blocks): concatenate the relevant slices.
        using var result = new MemoryStream(unchecked((int)entry.DataLength));
        long cursor = wantStart;
        while (cursor < wantEnd)
        {
            var b = FindBlock(cursor);
            int offsetInBlock = (int)(cursor - b.UncompressedOffset);
            int remaining = (int)Math.Min(b.UncompressedSize - offsetInBlock, wantEnd - cursor);
            var slice = ReadFromBlock(in b, offsetInBlock, remaining);
            result.Write(slice, 0, slice.Length);
            cursor += remaining;
        }
        return result.ToArray();
    }

    private Lz4Block FindBlock(long uncompressedOffset)
    {
        foreach (var b in _blocks)
        {
            if (uncompressedOffset >= b.UncompressedOffset && uncompressedOffset < b.UncompressedOffset + b.UncompressedSize)
                return b;
        }
        throw new InvalidDataException($"No block covers uncompressed offset {uncompressedOffset}");
    }

    private byte[] ReadFromBlock(in Lz4Block block, int offsetInBlock, int length)
    {
        var stream = _stream!;
        // Decompress the whole block (LZ4 blocks decode as a unit) then slice. With 1 MiB blocks this
        // is a single fast allocation; caching decompressed blocks is left to the caller (the
        // AssetManager's bundle cache) to avoid re-decompressing the same block for sibling assets.
        var compressed = new byte[block.CompressedSize];
        stream.Seek(_dataOffset + block.CompressedOffset, SeekOrigin.Begin);
        int read = ReadExact(stream, compressed);
        if (read != compressed.Length) throw new InvalidDataException("Truncated LZ4 block");

        var decompressed = new byte[block.UncompressedSize];
        int n = BundleCompression.Decompress(compressed, decompressed);
        if (n != block.UncompressedSize) throw new InvalidDataException("LZ4 block size mismatch");

        var result = new byte[length];
        Array.Copy(decompressed, offsetInBlock, result, 0, length);
        return result;
    }

    private static int ReadExact(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    // ================================================================
    //  Unload / Dispose
    // ================================================================

    /// <summary>
    /// Unload the bundle.
    /// <para><paramref name="unloadAllLoadedObjects"/> = <c>true</c>: dispose every asset loaded
    /// from this bundle (Unity Unload(true) semantics). References held elsewhere become disposed.</para>
    /// <para>= <c>false</c>: keep loaded assets alive but release the underlying file stream so no
    /// further loads are possible (Unity Unload(false) semantics).</para>
    /// </summary>
    public void Unload(bool unloadAllLoadedObjects)
    {
        lock (_lock)
        {
            _unloaded = true;

            if (unloadAllLoadedObjects)
            {
                foreach (var kv in _loadedObjects)
                {
                    try { kv.Value?.Dispose(); } catch { }
                }
                _loadedObjects.Clear();
            }

            // Either way, the stream is released: Unload(false) means "I have what I need, free the file";
            // Unload(true) means the bundle is fully gone.
            if (_ownsStream)
            {
                _stream?.Dispose();
            }
            _stream = null;
        }
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Unload(unloadAllLoadedObjects: true);
        GC.SuppressFinalize(this);
    }

    // ================================================================
    //  Header + index parsing
    // ================================================================

    private static void ReadHeaderAndIndex(AssetBundle bundle, Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Header (84 bytes).
        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Not an XEngine AssetBundle (magic 0x{magic:X8}, expected 0x{Magic:X8}).");

        ushort version = br.ReadUInt16();
        if (version != CurrentVersion)
            throw new InvalidDataException($"Unsupported AssetBundle version {version} (expected {CurrentVersion}).");

        bundle._platform = (AssetBundlePlatform)br.ReadByte();
        bundle._compression = (BundleCompressionMode)br.ReadByte();
        int entryCount = br.ReadInt32();
        bundle._flags = br.ReadUInt32();
        bundle._headerCrc = br.ReadUInt32();
        bundle._indexOffset = br.ReadInt64();
        bundle._indexLength = br.ReadInt64();
        bundle._dataOffset = br.ReadInt64();
        bundle._dataLength = br.ReadInt64();
        bundle._manifestOffset = br.ReadInt64();
        bundle._manifestLength = br.ReadInt64();
        var hashBytes = br.ReadBytes(16);
        if (hashBytes.Length < 16) throw new EndOfStreamException("Truncated header (BundleHash)");
        bundle._bundleHash = BundleHash.ReadBigEndian(hashBytes);

        // Optional: verify header CRC32 over bytes [0..16).
        stream.Seek(0, SeekOrigin.Begin);
        var headerSoFar = new byte[16];
        if (ReadExact(stream, headerSoFar) != 16) throw new EndOfStreamException("Truncated header");
        uint computed = Crc32Algorithm.Compute(headerSoFar);
        if (computed != bundle._headerCrc)
            throw new InvalidDataException($"AssetBundle header CRC mismatch (got 0x{computed:X8}, header says 0x{bundle._headerCrc:X8}).");

        // Index table.
        stream.Seek(bundle._indexOffset, SeekOrigin.Begin);
        // The index uses varint framing so it can grow without changing the fixed header layout.
        var indexReader = new BundleBufferReader(new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true));

        // Block table (LZ4 only).
        int blockCount = checked((int)indexReader.ReadVarUInt64());
        for (int i = 0; i < blockCount; i++)
        {
            long uncompOffset = checked((long)indexReader.ReadVarUInt64());
            int uncompSize = checked((int)indexReader.ReadVarUInt64());
            long compOffset = checked((long)indexReader.ReadVarUInt64());
            int compSize = checked((int)indexReader.ReadVarUInt64());
            bundle._blocks.Add(new Lz4Block(uncompOffset, uncompSize, compOffset, compSize));
        }

        // Entries.
        int actualEntries = checked((int)indexReader.ReadVarUInt64());
        if (actualEntries != entryCount)
            throw new InvalidDataException($"AssetBundle entry count mismatch (header {entryCount}, index {actualEntries}).");

        for (int i = 0; i < entryCount; i++)
        {
            Guid guid = indexReader.ReadGuid();
            string name = indexReader.ReadString();
            string typeName = indexReader.ReadString();
            long dataOffset = checked((long)indexReader.ReadVarUInt64());
            long dataLength = checked((long)indexReader.ReadVarUInt64());
            var entry = new AssetBundleEntry(guid, name, typeName, dataOffset, dataLength);
            if (!bundle._entriesByGuid.TryAdd(guid, entry))
                throw new InvalidDataException($"AssetBundle contains duplicate asset GUID {guid:N}.");
            if (!string.IsNullOrEmpty(name)) bundle._entriesByName[name] = entry;
        }

        // Manifest tail.
        stream.Seek(bundle._manifestOffset, SeekOrigin.Begin);
        var manifestReader = new BundleBufferReader(new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true));
        bool hasMain = manifestReader.ReadBool();
        bundle._mainAssetGuid = hasMain ? manifestReader.ReadGuid() : Guid.Empty;
        bundle._directDependencyBundleIds.AddRange(manifestReader.ReadArray(manifestReader.ReadGuid));
        bundle._contentCrc = manifestReader.ReadUInt32();
    }
}
