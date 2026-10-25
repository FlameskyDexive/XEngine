// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// LZ4 block compression. Wraps K4os.Compression.LZ4 (K4os.Compression.LZ4.LZ4Codec). The block-based
// streaming model (fixed-size compressed/uncompressed block table) is the standard way to make LZ4
// seekable; the approach mirrors YooAsset/Unity's chunk-based compression conceptually but is an
// independent implementation.

using System;
using System.Collections.Generic;
using System.IO;

using K4os.Compression.LZ4;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Describes one compressed block within an LZ4 AssetBundle's data region. The block layout stored
/// on disk is the sequence of these descriptors (each: varint UncompressedOffset, varint
/// UncompressedSize, varint CompressedOffset, varint CompressedSize) immediately before the data
/// region. A reader can locate any uncompressed byte offset by scanning the block table and
/// decompress only the single covering block.
/// </summary>
public readonly struct Lz4Block
{
    public readonly long UncompressedOffset;
    public readonly int UncompressedSize;
    public readonly long CompressedOffset;
    public readonly int CompressedSize;

    public Lz4Block(long uncompressedOffset, int uncompressedSize, long compressedOffset, int compressedSize)
    {
        UncompressedOffset = uncompressedOffset;
        UncompressedSize = uncompressedSize;
        CompressedOffset = compressedOffset;
        CompressedSize = compressedSize;
    }
}

/// <summary>Static helpers for compressing/decompressing AssetBundle payloads with LZ4.</summary>
public static class BundleCompression
{
    /// <summary>
    /// Default uncompressed block size. Smaller blocks mean finer seek granularity and less
    /// decompression wasted on partial reads; larger blocks mean better ratio. 1 MiB matches
    /// Unity's chunk-based LZ4 default and is a good balance for typical assets.
    /// </summary>
    public const int DefaultBlockSize = 1 << 20; // 1 MiB

    /// <summary>Maximum bound for the compressed form of <paramref name="uncompressedSize"/> bytes.</summary>
    public static int CompressBound(int uncompressedSize)
    {
        // LZ4Codec.MaximumOutputSize for arbitrary inputs; LZ4's bound is roughly n + n/255 + 16.
        if (uncompressedSize < 0) throw new ArgumentOutOfRangeException(nameof(uncompressedSize));
        return uncompressedSize + (uncompressedSize / 255) + 16;
    }

    /// <summary>Compress a single block. Always returns valid LZ4-encoded bytes (LZ4 can store
    /// incompressible input as literal runs, so the output is always decodable). The result may be
    /// slightly larger than the input for tiny/incompressible data; callers that want size gains
    /// should compare modes at the bundle level, not the block level.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        int bound = CompressBound(input.Length);
        var output = new byte[bound];
        int written = LZ4Codec.Encode(input, output);
        if (written <= 0)
            throw new InvalidOperationException("LZ4 encode produced no output");

        // IMPORTANT: always return LZ4-encoded bytes. Returning the raw input for incompressible
        // blocks would make the stored "compressed" bytes non-LZ4, and the reader would fail to
        // decode them. LZ4's literal-run encoding handles incompressible input and remains decodable.
        Array.Resize(ref output, written);
        return output;
    }

    /// <summary>Decompress a block into a caller-provided buffer of exactly the uncompressed size.</summary>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int n = LZ4Codec.Decode(input, output);
        if (n < 0) throw new InvalidDataException("LZ4 decode failed");
        return n;
    }

    /// <summary>
    /// Compress a full stream into <paramref name="output"/> using fixed-size uncompressed blocks,
    /// returning the block table (offsets are relative to the start of the data region). The caller
    /// is responsible for writing the block table header before this output's bytes. For mode=None,
    /// this is not used - the data is copied verbatim.
    /// </summary>
    public static List<Lz4Block> CompressStream(Stream input, Stream output, int blockSize = DefaultBlockSize)
    {
        var blocks = new List<Lz4Block>();
        var inBuf = new byte[blockSize];
        long uncompressedOffset = 0;
        long compressedOffset = 0;

        int read;
        while ((read = input.Read(inBuf, 0, blockSize)) > 0)
        {
            ReadOnlySpan<byte> inSpan = inBuf.AsSpan(0, read);
            byte[] compressed = Compress(inSpan);
            output.Write(compressed, 0, compressed.Length);

            blocks.Add(new Lz4Block(uncompressedOffset, read, compressedOffset, compressed.Length));
            uncompressedOffset += read;
            compressedOffset += compressed.Length;
        }
        return blocks;
    }
}
