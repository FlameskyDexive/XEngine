// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XEngine.Editor.Importers;

public readonly struct AsepriteTag
{
    public AsepriteTag(string name, int fromFrame, int toFrame)
    {
        Name = name;
        FromFrame = fromFrame;
        ToFrame = toFrame;
    }

    public string Name { get; }
    public int FromFrame { get; }
    public int ToFrame { get; }
}

/// <summary>One flattened Aseprite frame in top-left RGBA8 (length <c>Width * Height * 4</c>).</summary>
public sealed class AsepriteFramePixels
{
    public int Index;
    public int Width;
    public int Height;
    public int DurationMs;
    public byte[] Rgba = Array.Empty<byte>();
}

public readonly struct AsepriteSlice
{
    public AsepriteSlice(string name, int x, int y, int width, int height)
    {
        Name = name;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public string Name { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>CPU parse of an Aseprite file: header plus tag and slice chunks.</summary>
public sealed class AsepriteDocument
{
    public const ushort Magic = 0xA5E0;
    private const ushort FrameMagic = 0xF1FA;
    private const ushort TagsChunk = 0x2018;
    private const ushort SliceChunk = 0x2022;
    private const ushort CelChunk = 0x2005;

    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }
    public IReadOnlyList<AsepriteTag> Tags { get; }
    public IReadOnlyList<AsepriteSlice> Slices { get; }
    /// <summary>Per-frame flattened RGBA (top-left). Empty when the file has no drawable cels.</summary>
    public IReadOnlyList<AsepriteFramePixels> FramePixels { get; }

    public AsepriteDocument(int width, int height, int frameCount)
        : this(width, height, frameCount, Array.Empty<AsepriteTag>(), Array.Empty<AsepriteSlice>())
    {
    }

    public AsepriteDocument(
        int width,
        int height,
        int frameCount,
        IReadOnlyList<AsepriteTag> tags,
        IReadOnlyList<AsepriteSlice> slices)
        : this(width, height, frameCount, tags, slices, Array.Empty<AsepriteFramePixels>())
    {
    }

    public AsepriteDocument(
        int width,
        int height,
        int frameCount,
        IReadOnlyList<AsepriteTag> tags,
        IReadOnlyList<AsepriteSlice> slices,
        IReadOnlyList<AsepriteFramePixels> framePixels)
    {
        Width = width;
        Height = height;
        FrameCount = frameCount;
        Tags = tags;
        Slices = slices;
        FramePixels = framePixels;
    }

    public static AsepriteDocument Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 128)
            throw new InvalidDataException("Aseprite header is 128 bytes.");

        ReadOnlySpan<byte> span = bytes;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
        if (magic != Magic)
            throw new InvalidDataException($"Not an Aseprite file (magic 0x{magic:X4}).");

        int frames = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6));
        int width = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10));
        int colorDepth = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12));

        if (AsepriteDotNetReader.TryRead(bytes, out AsepriteDocument? fromLibrary) && fromLibrary != null
            && AnyOpaque(fromLibrary.FramePixels))
            return fromLibrary;

        var tags = new List<AsepriteTag>();
        var slices = new List<AsepriteSlice>();
        var pixels = new List<AsepriteFramePixels>();
        ReadChunks(span, frames, width, height, colorDepth, tags, slices, pixels);

        if (fromLibrary != null)
        {
            return new AsepriteDocument(fromLibrary.Width, fromLibrary.Height, fromLibrary.FrameCount,
                fromLibrary.Tags.Count > 0 ? fromLibrary.Tags : tags,
                fromLibrary.Slices.Count > 0 ? fromLibrary.Slices : slices,
                AnyOpaque(pixels) ? pixels : fromLibrary.FramePixels);
        }

        return new AsepriteDocument(width, height, frames, tags, slices, pixels);
    }

    internal static bool AnyOpaque(IReadOnlyList<AsepriteFramePixels> frames)
    {
        for (int f = 0; f < frames.Count; f++)
        {
            byte[] rgba = frames[f].Rgba;
            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] > 0)
                    return true;
            }
        }

        return false;
    }

    private static void ReadChunks(
        ReadOnlySpan<byte> span,
        int frameCount,
        int canvasWidth,
        int canvasHeight,
        int colorDepth,
        List<AsepriteTag> tags,
        List<AsepriteSlice> slices,
        List<AsepriteFramePixels> pixels)
    {
        int offset = 128;
        for (int frame = 0; frame < frameCount && offset + 16 <= span.Length; frame++)
        {
            uint frameSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset));
            if (frameSize < 16 || offset + (int)frameSize > span.Length)
                break;

            ushort frameMagic = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 4));
            if (frameMagic != FrameMagic)
                break;

            int oldChunks = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 6));
            int durationMs = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 8));
            int newChunks = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 14));
            int chunkCount = newChunks != 0 ? newChunks : oldChunks;
            int chunkOffset = offset + 16;
            int frameEnd = offset + (int)frameSize;
            byte[]? canvas = null;

            for (int c = 0; c < chunkCount && chunkOffset + 6 <= frameEnd; c++)
            {
                uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(chunkOffset));
                if (chunkSize < 6 || chunkOffset + (int)chunkSize > frameEnd)
                    break;

                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(chunkOffset + 4));
                ReadOnlySpan<byte> data = span.Slice(chunkOffset + 6, (int)chunkSize - 6);
                if (type == TagsChunk)
                    ReadTags(data, tags);
                else if (type == SliceChunk)
                    ReadSlice(data, slices);
                else if (type == CelChunk && colorDepth == 32)
                {
                    canvas ??= new byte[canvasWidth * canvasHeight * 4];
                    ReadCel(data, canvasWidth, canvasHeight, canvas);
                }

                chunkOffset += (int)chunkSize;
            }

            if (canvas != null)
            {
                pixels.Add(new AsepriteFramePixels
                {
                    Index = frame,
                    Width = canvasWidth,
                    Height = canvasHeight,
                    DurationMs = durationMs,
                    Rgba = canvas,
                });
            }

            offset = frameEnd;
        }
    }

    private static void ReadCel(ReadOnlySpan<byte> data, int canvasW, int canvasH, byte[] canvas)
    {
        if (data.Length < 16)
            return;

        short x = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2));
        short y = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(4));
        byte opacity = data[6];
        ushort celType = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(7));
        if (celType != 0 && celType != 2)
            return;
        if (data.Length < 20)
            return;

        int celW = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(16));
        int celH = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(18));
        if (celW <= 0 || celH <= 0)
            return;

        ReadOnlySpan<byte> payload = data.Slice(20);
        byte[] pixels;
        if (celType == 0)
        {
            int need = celW * celH * 4;
            if (payload.Length < need)
                return;
            pixels = payload.Slice(0, need).ToArray();
        }
        else
        {
            try
            {
                using var input = new MemoryStream(payload.ToArray(), writable: false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                pixels = output.ToArray();
            }
            catch
            {
                return;
            }
        }

        BlitSrcOver(canvas, canvasW, canvasH, pixels, celW, celH, x, y, opacity);
    }

    private static void BlitSrcOver(
        byte[] canvas, int canvasW, int canvasH,
        byte[] src, int srcW, int srcH,
        int destX, int destY, byte opacity)
    {
        for (int sy = 0; sy < srcH; sy++)
        {
            int cy = destY + sy;
            if ((uint)cy >= (uint)canvasH)
                continue;
            for (int sx = 0; sx < srcW; sx++)
            {
                int cx = destX + sx;
                if ((uint)cx >= (uint)canvasW)
                    continue;
                int si = (sy * srcW + sx) * 4;
                if (si + 3 >= src.Length)
                    return;
                int srcA = src[si + 3] * opacity / 255;
                if (srcA <= 0)
                    continue;
                int di = (cy * canvasW + cx) * 4;
                if (srcA >= 255)
                {
                    canvas[di] = src[si];
                    canvas[di + 1] = src[si + 1];
                    canvas[di + 2] = src[si + 2];
                    canvas[di + 3] = 255;
                    continue;
                }

                int dstA = canvas[di + 3];
                int outA = srcA + dstA * (255 - srcA) / 255;
                if (outA <= 0)
                    continue;
                canvas[di] = (byte)((src[si] * srcA + canvas[di] * dstA * (255 - srcA) / 255) / outA);
                canvas[di + 1] = (byte)((src[si + 1] * srcA + canvas[di + 1] * dstA * (255 - srcA) / 255) / outA);
                canvas[di + 2] = (byte)((src[si + 2] * srcA + canvas[di + 2] * dstA * (255 - srcA) / 255) / outA);
                canvas[di + 3] = (byte)outA;
            }
        }
    }

    private static void ReadTags(ReadOnlySpan<byte> data, List<AsepriteTag> tags)
    {
        if (data.Length < 10)
            return;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int offset = 10;
        for (int i = 0; i < count && offset + 17 <= data.Length; i++)
        {
            int from = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
            int to = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2));
            offset += 17;
            if (!TryReadString(data, ref offset, out string name))
                break;
            tags.Add(new AsepriteTag(name, from, to));
        }
    }

    private static void ReadSlice(ReadOnlySpan<byte> data, List<AsepriteSlice> slices)
    {
        if (data.Length < 12)
            return;

        uint keyCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
        int offset = 12;
        if (!TryReadString(data, ref offset, out string name))
            return;

        int x = 0, y = 0, width = 0, height = 0;
        if (keyCount > 0 && offset + 20 <= data.Length)
        {
            offset += 4;
            x = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
            y = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4));
            width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 8));
            height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 12));
        }

        _ = flags;
        slices.Add(new AsepriteSlice(name, x, y, width, height));
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = "";
        if (offset + 2 > data.Length)
            return false;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
        offset += 2;
        if (offset + length > data.Length)
            return false;
        value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return true;
    }
}
