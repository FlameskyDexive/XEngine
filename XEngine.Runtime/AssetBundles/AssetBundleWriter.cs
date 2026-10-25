// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Writes an <see cref="AssetBundle"/> container file. Each entry is a raw byte payload (typically
/// an Echo binary blob from Library/cache/{guid}.asset). The writer assembles the header, index,
/// (optionally LZ4-compressed) data region, and manifest tail, and computes the header CRC and the
/// FNV content hash.
/// </summary>
public static class AssetBundleWriter
{
    /// <summary>
    /// Build an AssetBundle file. <paramref name="entries"/> maps each asset to its payload bytes.
    /// The payloads are concatenated into the data region in iteration order; offsets are recorded.
    /// </summary>
    public static void Write(string outputPath, IReadOnlyCollection<KeyValuePair<AssetBundleEntry, byte[]>> entries, AssetBundleBuildOptions options)
    {
        if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException(nameof(outputPath));
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Compression == BundleCompressionMode.Lz4Block && options.Lz4BlockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Lz4BlockSize must be positive");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Stage the data region (concatenated payloads) and the block table.
        using var dataMs = new MemoryStream();
        // Keep the raw payloads alongside offsets so the content hash is computed from the
        // uncompressed bytes (stable across compression-mode changes), not from the on-disk region.
        var writtenEntries = new List<(AssetBundleEntry entry, long offset, long length, byte[] payload)>();
        var blocks = new List<Lz4Block>();

        if (options.Compression == BundleCompressionMode.Lz4Block)
        {
            // Build one logical decompressed stream of all payloads, then compress in fixed blocks.
            using var uncompressed = new MemoryStream();
            foreach (var kv in entries)
            {
                var entry = kv.Key;
                var payload = kv.Value;
                long off = uncompressed.Position;
                uncompressed.Write(payload, 0, payload.Length);
                writtenEntries.Add((entry, off, payload.Length, payload));
            }
            uncompressed.Position = 0;
            blocks = BundleCompression.CompressStream(uncompressed, dataMs, options.Lz4BlockSize);
        }
        else
        {
            foreach (var kv in entries)
            {
                var entry = kv.Key;
                var payload = kv.Value;
                long off = dataMs.Position;
                dataMs.Write(payload, 0, payload.Length);
                writtenEntries.Add((entry, off, payload.Length, payload));
            }
        }

        byte[] dataBytes = dataMs.ToArray();
        uint contentCrc = Crc32Algorithm.Compute(dataBytes);

        // Build the index table bytes.
        byte[] indexBytes = BuildIndexBytes(blocks, writtenEntries);
        byte[] manifestBytes = BuildManifestBytes(options, contentCrc);

        // Layout: header(84) + index + data + manifest.
        long indexOffset = AssetBundle.HeaderLength;
        long indexLength = indexBytes.Length;
        long dataOffset = indexOffset + indexLength;
        long dataLength = dataBytes.Length;
        long manifestOffset = dataOffset + dataLength;
        long manifestLength = manifestBytes.Length;

        // Header CRC covers the first 16 bytes (magic..flags). Those are fixed before hashing; the
        // CRC field itself lives at offset 16 so it does not cover itself.
        var headerSoFar = new byte[16];
        BinaryPrimitivesWriteUInt32LE(headerSoFar, 0, AssetBundle.Magic);
        BinaryPrimitivesWriteUInt16LE(headerSoFar, 4, AssetBundle.CurrentVersion);
        headerSoFar[6] = (byte)options.Platform;
        headerSoFar[7] = (byte)options.Compression;
        BinaryPrimitivesWriteUInt32LE(headerSoFar, 8, checked((uint)entries.Count));
        BinaryPrimitivesWriteUInt32LE(headerSoFar, 12, options.MainAssetGuid != Guid.Empty ? 1u : 0u);
        uint headerCrc = Crc32Algorithm.Compute(headerSoFar);

        // Compute the bundle content hash over the concatenated payloads (the logical content, not
        // the compressed bytes - so it is stable across compression-mode changes).
        BundleHash bundleHash = ComputeEntriesHash(writtenEntries, dataBytes, options);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        // Fixed header (84 bytes).
        bw.Write(AssetBundle.Magic);
        bw.Write(AssetBundle.CurrentVersion);
        bw.Write((byte)options.Platform);
        bw.Write((byte)options.Compression);
        bw.Write(checked((uint)entries.Count));
        bw.Write(options.MainAssetGuid != Guid.Empty ? 1u : 0u);
        bw.Write(headerCrc);
        bw.Write(indexOffset);
        bw.Write(indexLength);
        bw.Write(dataOffset);
        bw.Write(dataLength);
        bw.Write(manifestOffset);
        bw.Write(manifestLength);
        var hashBytes = new byte[16];
        bundleHash.WriteBigEndian(hashBytes);
        bw.Write(hashBytes);

        // Regions.
        bw.Write(indexBytes);
        bw.Write(dataBytes);
        bw.Write(manifestBytes);
    }

    private static byte[] BuildIndexBytes(List<Lz4Block> blocks, List<(AssetBundleEntry entry, long offset, long length, byte[] payload)> writtenEntries)
    {
        using var ms = new MemoryStream();
        var writer = new BundleBufferWriter(new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true));

        writer.WriteVarUInt64((ulong)blocks.Count);
        foreach (var b in blocks)
        {
            writer.WriteVarUInt64((ulong)b.UncompressedOffset);
            writer.WriteVarUInt64((ulong)b.UncompressedSize);
            writer.WriteVarUInt64((ulong)b.CompressedOffset);
            writer.WriteVarUInt64((ulong)b.CompressedSize);
        }

        writer.WriteVarUInt64((ulong)writtenEntries.Count);
        foreach (var (entry, offset, length, _) in writtenEntries)
        {
            writer.WriteGuid(entry.AssetGuid);
            writer.WriteString(entry.Name);
            writer.WriteString(entry.TypeName);
            writer.WriteVarUInt64((ulong)offset);
            writer.WriteVarUInt64((ulong)length);
        }

        return ms.ToArray();
    }

    private static byte[] BuildManifestBytes(AssetBundleBuildOptions options, uint contentCrc)
    {
        using var ms = new MemoryStream();
        var writer = new BundleBufferWriter(new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true));

        bool hasMain = options.MainAssetGuid != Guid.Empty;
        writer.WriteBool(hasMain);
        if (hasMain) writer.WriteGuid(options.MainAssetGuid);

        writer.WriteVarUInt64((ulong)options.DirectDependencyBundleIds.Count);
        foreach (var dep in options.DirectDependencyBundleIds) writer.WriteGuid(dep);

        // CRC32 of the uncompressed data region. The loader exposes this as AssetBundle.ContentCrc;
        // download/verify flows compare it against a freshly computed value over the bytes on disk.
        writer.WriteUInt32(contentCrc);

        return ms.ToArray();
    }

    private static BundleHash ComputeEntriesHash(List<(AssetBundleEntry entry, long offset, long length, byte[] payload)> entries, byte[] dataBytes, AssetBundleBuildOptions options)
    {
        // Build a stable byte stream over the LOGICAL (uncompressed) content: for each entry,
        // (guid16)(length u64)(payload). This is independent of the compression mode so the hash is
        // stable whether the bundle is stored None or LZ4, and it changes iff the content changes.
        using var ms = new MemoryStream();
        foreach (var (entry, _, length, payload) in entries)
        {
            Span<byte> guid16 = stackalloc byte[16];
            entry.AssetGuid.TryWriteBytes(guid16);
            ms.Write(guid16);
            byte[] len = new byte[8];
            BinaryPrimitivesWriteUInt64LE(len, 0, (ulong)length);
            ms.Write(len, 0, 8);
            ms.Write(payload, 0, payload.Length);
        }
        return BundleHash.Compute(ms.ToArray());
    }

    private static void BinaryPrimitivesWriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static void BinaryPrimitivesWriteUInt16LE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static void BinaryPrimitivesWriteUInt64LE(byte[] buf, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++) buf[offset + i] = (byte)(value >> (i * 8));
    }
}
