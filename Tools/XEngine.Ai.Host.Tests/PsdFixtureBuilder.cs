using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// Writes minimal-but-valid PSD files for the parser tests.
/// </summary>
/// <remarks>
/// <para>
/// The vendored plugin only reads, so fixtures cannot come from a save round-trip — and checking in
/// binary .psd blobs would pin the tests to files nobody can inspect or minimally vary. Building the
/// bytes in code keeps the format contract visible in the test itself: every offset written here is
/// one the reader is documented (and observed) to consume.
/// </para>
/// <para>
/// Scope matches what AI design tools actually emit: PSD version 1, RGB, 8-bit, RAW or RLE
/// compression, layer groups (lsct), unicode names (luni), layer ids (lyid). Nothing else is written,
/// so a test that needs CMYK or 16-bit must extend the builder first — deliberately, not by accident.
/// </para>
/// </remarks>
internal sealed class PsdFixtureBuilder
{
    /// <summary>One layer to write, top-most first (Photoshop stores bottom-to-top; the builder flips).</summary>
    internal sealed class LayerSpec
    {
        public string Name = "Layer";
        public int X;
        public int Y;
        public int Width;
        public int Height;

        /// <summary>RGBA, row-major, top row first. Null builds a solid fill from <see cref="Fill"/>.</summary>
        public byte[] Pixels;

        public (byte R, byte G, byte B, byte A) Fill = (255, 255, 255, 255);

        public bool Visible = true;
        public byte Opacity = 255;
        public int LayerId = -1;
        public bool Rle;

        /// <summary>Starts a group. Layers that follow (until <see cref="GroupEnd"/>) become its children.</summary>
        public bool GroupStart;

        /// <summary>Closes the innermost open group (the hidden divider layer Photoshop writes).</summary>
        public bool GroupEnd;

        public byte[] BuildPixels()
        {
            if (Pixels != null) return Pixels;
            var rgba = new byte[Width * Height * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = Fill.R;
                rgba[i + 1] = Fill.G;
                rgba[i + 2] = Fill.B;
                rgba[i + 3] = Fill.A;
            }
            return rgba;
        }
    }

    private readonly int _width;
    private readonly int _height;
    private readonly List<LayerSpec> _layers = new();

    public PsdFixtureBuilder(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public PsdFixtureBuilder Layer(LayerSpec spec)
    {
        _layers.Add(spec);
        return this;
    }

    public PsdFixtureBuilder Group(string name)
        => Layer(new LayerSpec { Name = name, GroupStart = true, Width = 0, Height = 0 });

    public PsdFixtureBuilder EndGroup()
        => Layer(new LayerSpec { Name = "</Layer group>", GroupEnd = true, Width = 0, Height = 0 });

    public byte[] Build()
    {
        using var stream = new MemoryStream();
        var w = new BigEndianWriter(stream);

        // ---- header --------------------------------------------------------------
        w.Ascii("8BPS");
        w.I16(1);                       // version 1 (PSD, not PSB)
        w.Zero(6);
        w.I16(4);                       // composite channels: RGBA
        w.I32(_height);
        w.I32(_width);
        w.I16(8);                       // bit depth
        w.I16(3);                       // color mode: RGB

        w.U32(0);                       // color mode data: none
        w.U32(0);                       // image resources: none

        // ---- layer & mask info ----------------------------------------------------
        byte[] layersInfo = BuildLayersInfo();
        w.U32((uint)(4 + layersInfo.Length + 4));
        w.U32((uint)layersInfo.Length);
        w.Bytes(layersInfo);
        w.U32(0);                       // global layer mask: none

        // ---- composite image (flattened): RAW, planar RGBA -------------------------
        w.I16(0);                       // compression: raw
        byte[] composite = BuildCompositePixels();
        int planeSize = _width * _height;
        for (int channel = 0; channel < 4; channel++)
        {
            for (int i = 0; i < planeSize; i++)
                w.Byte(composite[i * 4 + channel]);
        }

        return stream.ToArray();
    }

    /// <summary>The flattened composite: the last (bottom-most in spec order) pixel layer wins per pixel.</summary>
    private byte[] BuildCompositePixels()
    {
        var rgba = new byte[_width * _height * 4];
        for (int index = _layers.Count - 1; index >= 0; index--)
        {
            LayerSpec spec = _layers[index];
            if (spec.GroupStart || spec.GroupEnd || spec.Width <= 0 || spec.Height <= 0) continue;
            byte[] pixels = spec.BuildPixels();
            for (int y = 0; y < spec.Height; y++)
            {
                int docY = spec.Y + y;
                if (docY < 0 || docY >= _height) continue;
                for (int x = 0; x < spec.Width; x++)
                {
                    int docX = spec.X + x;
                    if (docX < 0 || docX >= _width) continue;
                    int src = (y * spec.Width + x) * 4;
                    if (pixels[src + 3] == 0) continue;
                    int dst = (docY * _width + docX) * 4;
                    Array.Copy(pixels, src, rgba, dst, 4);
                }
            }
        }
        return rgba;
    }

    private byte[] BuildLayersInfo()
    {
        using var stream = new MemoryStream();
        var w = new BigEndianWriter(stream);

        // Photoshop stores layers bottom-to-top; the spec list is authored top-to-bottom, and a
        // group's members sit between its divider (below) and its folder record (above).
        var ordered = new List<LayerSpec>(_layers);
        ordered.Reverse();

        w.I16((short)ordered.Count);

        var channelData = new List<byte[][]>();     // per layer, per channel payload (incl. compression word)
        var channelIds = new List<short[]>();

        foreach (LayerSpec spec in ordered)
        {
            bool marker = spec.GroupStart || spec.GroupEnd;
            byte[] pixels = marker ? Array.Empty<byte>() : spec.BuildPixels();

            // rect
            w.I32(spec.Y);
            w.I32(spec.X);
            w.I32(spec.Y + spec.Height);
            w.I32(spec.X + spec.Width);

            // channels: RGB + alpha(-1). Marker layers carry none.
            short[] ids = marker ? Array.Empty<short>() : new short[] { 0, 1, 2, -1 };
            var payloads = new byte[ids.Length][];
            w.I16((short)ids.Length);
            for (int c = 0; c < ids.Length; c++)
            {
                int plane = c < 3 ? c : 3;
                payloads[c] = BuildChannelPayload(pixels, spec.Width, spec.Height, plane, spec.Rle);
                w.I16(ids[c]);
                w.I32(payloads[c].Length);
            }
            channelData.Add(payloads);
            channelIds.Add(ids);

            w.Ascii("8BIM");
            w.Ascii("norm");
            w.Byte(spec.Opacity);
            w.Byte(0);                                   // clipping: base
            w.Byte((byte)(spec.Visible ? 0 : 2));        // bit 1 = hidden
            w.Byte(0);                                   // filler

            byte[] extra = BuildExtraData(spec);
            w.U32((uint)extra.Length);
            w.Bytes(extra);
        }

        // Channel image data, in the same layer order, each payload already carrying its
        // compression word.
        for (int i = 0; i < channelData.Count; i++)
        {
            foreach (byte[] payload in channelData[i])
                w.Bytes(payload);
        }

        return stream.ToArray();
    }

    /// <summary>Mask info + blending ranges + pascal name + additional info blocks.</summary>
    private static byte[] BuildExtraData(LayerSpec spec)
    {
        using var stream = new MemoryStream();
        var w = new BigEndianWriter(stream);

        w.U32(0);                                        // no mask
        w.I32(0);                                        // no blending ranges

        // Pascal name, padded to a multiple of 4 counting the length byte. ASCII-lossy on
        // purpose: the real name travels in luni, exactly as Photoshop writes it.
        byte[] ascii = Encoding.ASCII.GetBytes(spec.Name);
        int nameLen = Math.Min(ascii.Length, 255);
        w.Byte((byte)nameLen);
        w.Bytes(ascii.AsSpan(0, nameLen).ToArray());
        int padded = (1 + nameLen + 3) / 4 * 4;
        w.Zero(padded - 1 - nameLen);

        // luni — authoritative unicode name.
        byte[] unicode = Encoding.BigEndianUnicode.GetBytes(spec.Name);
        w.Ascii("8BIM");
        w.Ascii("luni");
        w.I32(4 + unicode.Length);
        w.I32(spec.Name.Length);
        w.Bytes(unicode);

        // lsct — group folder / divider.
        if (spec.GroupStart || spec.GroupEnd)
        {
            w.Ascii("8BIM");
            w.Ascii("lsct");
            w.I32(4);
            w.I32(spec.GroupStart ? 1 : 3);              // 1 = open folder, 3 = divider
        }

        // lyid — stable layer id.
        if (spec.LayerId >= 0)
        {
            w.Ascii("8BIM");
            w.Ascii("lyid");
            w.I32(4);
            w.I32(spec.LayerId);
        }

        return stream.ToArray();
    }

    /// <summary>One channel's on-disk payload: compression word + data (+ RLE row table).</summary>
    private static byte[] BuildChannelPayload(byte[] rgba, int width, int height, int plane, bool rle)
    {
        var raw = new byte[width * height];
        for (int i = 0; i < raw.Length; i++)
            raw[i] = rgba[i * 4 + plane];

        using var stream = new MemoryStream();
        var w = new BigEndianWriter(stream);

        if (!rle)
        {
            w.I16(0);
            w.Bytes(raw);
            return stream.ToArray();
        }

        w.I16(1);                                        // compression: RLE
        var rows = new byte[height][];
        for (int y = 0; y < height; y++)
            rows[y] = PackBits(raw.AsSpan(y * width, width));
        foreach (byte[] row in rows) w.U16((ushort)row.Length);
        foreach (byte[] row in rows) w.Bytes(row);
        return stream.ToArray();
    }

    /// <summary>
    /// PackBits, literal-runs only. Always-literal is valid PackBits — it exercises the reader's RLE
    /// path without this builder re-implementing the encoder it is supposed to be testing against.
    /// </summary>
    private static byte[] PackBits(ReadOnlySpan<byte> row)
    {
        using var stream = new MemoryStream();
        int offset = 0;
        while (offset < row.Length)
        {
            int run = Math.Min(128, row.Length - offset);
            stream.WriteByte((byte)(run - 1));
            stream.Write(row.Slice(offset, run));
            offset += run;
        }
        return stream.ToArray();
    }

    /// <summary>PSD is big-endian throughout; BinaryWriter is not.</summary>
    private sealed class BigEndianWriter
    {
        private readonly Stream _stream;

        public BigEndianWriter(Stream stream) => _stream = stream;

        public void Byte(byte value) => _stream.WriteByte(value);

        public void Bytes(byte[] data) => _stream.Write(data, 0, data.Length);

        public void Zero(int count)
        {
            for (int i = 0; i < count; i++) _stream.WriteByte(0);
        }

        public void Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

        public void I16(short value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void U16(ushort value) => I16((short)value);

        public void I32(int value)
        {
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void U32(uint value) => I32((int)value);
    }
}
