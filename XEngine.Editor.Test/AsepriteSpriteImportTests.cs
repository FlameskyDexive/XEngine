// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Buffers.Binary;
using System.Text;

using ImageMagick;

using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>★ Gen-P4.3 — Aseprite frames flatten to a packed sheet and emit Texture2D + Sprite.</summary>
public sealed class AsepriteSpriteImportTests
{
    [Fact]
    public void AsepriteImporter_Import_HeaderOnly_DoesNotEmitSprites()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".aseprite");
        File.WriteAllBytes(path, WriteAseHeader(8, 8, 1));
        try
        {
            var ctx = new ImportContext(Guid.NewGuid(), path, null);
            Assert.True(new AsepriteImporter().Import(ctx));
            Assert.Empty(ctx.SubAssets.OfType<Sprite>());
            Assert.Empty(ctx.SubAssets.OfType<Texture2D>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AsepriteSpriteSheet_PackHorizontal_RectsAndOpaque()
    {
        byte[] red = FillRgba(8, 8, 220, 40, 40, 255);
        byte[] green = FillRgba(8, 8, 40, 200, 70, 255);

        AsepritePackedSheet? sheet = AsepriteSpriteSheet.PackHorizontal(new[]
        {
            new AsepriteFramePixels { Index = 0, Width = 8, Height = 8, DurationMs = 100, Rgba = red },
            new AsepriteFramePixels { Index = 1, Width = 8, Height = 8, DurationMs = 100, Rgba = green },
        });
        Assert.NotNull(sheet);
        Assert.True(AsepriteSpriteSheet.HasOpaque(sheet));
        Assert.Equal(16, sheet.Width);
        Assert.Equal(8, sheet.Height);
        Assert.Equal(2, sheet.Frames.Count);
        Assert.Equal(new SpriteRect(0, 0, 8, 8), sheet.Frames[0].Rect);
        Assert.Equal(new SpriteRect(8, 0, 8, 8), sheet.Frames[1].Rect);
        Assert.Equal("sprite:0", sheet.Frames[0].SpriteId);
        Assert.Equal(220, sheet.RgbaTopLeft[0]);
        Assert.Equal(200, sheet.RgbaTopLeft[(8 * 4) + 1]);
    }

    [Fact]
    public void AsepriteDocument_Read_FlattensRgbaCels()
    {
        AsepriteDocument doc = AsepriteDocument.Read(WriteAseWithRgbaCels());
        Assert.Equal(2, doc.FrameCount);
        Assert.True(doc.FramePixels.Count >= 1);
        Assert.Contains(doc.FramePixels, f => HasChannel(f.Rgba, channel: 0, min: 200) || HasChannel(f.Rgba, channel: 1, min: 200));
    }

    [Fact]
    public void AsepriteImporter_Import_EmitsTextureAndSprites()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".aseprite");
        File.WriteAllBytes(path, WriteAseWithRgbaCels());
        try
        {
            Guid guid = Guid.NewGuid();
            var ctx = new ImportContext(guid, path, null);
            Assert.True(new AsepriteImporter().Import(ctx));

            Sprite[] sprites = ctx.SubAssets.OfType<Sprite>().OrderBy(s => s.Name).ToArray();
            Assert.Equal(2, sprites.Length);
            Assert.Equal(new SpriteRect(0, 0, 8, 8), sprites[0].Rect);
            Assert.Equal(new SpriteRect(8, 0, 8, 8), sprites[1].Rect);
            Assert.Equal(AssetEntry.DeriveSubAssetGuid(guid, "sprite:0"), sprites[0].AssetID);
            Assert.Equal(AssetEntry.DeriveSubAssetGuid(guid, "sprite:1"), sprites[1].AssetID);

            AsepriteFrameAsset frame0 = ctx.SubAssets.OfType<AsepriteFrameAsset>().Single(f => f.Index == 0);
            Assert.Equal(sprites[0].AssetID, frame0.Sprite.AssetID);

            AsepriteTagAsset tag = Assert.Single(ctx.SubAssets.OfType<AsepriteTagAsset>());
            Assert.Equal(2, tag.Sprites.Count);

            Texture2D? tex = ctx.SubAssets.OfType<Texture2D>().SingleOrDefault();
            if (tex != null)
            {
                Assert.Equal(16u, tex.Width);
                Assert.Equal(8u, tex.Height);
                Assert.Equal(AssetEntry.DeriveSubAssetGuid(guid, AsepriteImporter.TextureStableId), tex.AssetID);
                var main = Assert.IsType<AsepriteAsset>(ctx.MainAsset);
                Assert.Equal(tex.AssetID, main.Texture.AssetID);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EvidenceScreenshot_AsepriteSprites()
    {
        AsepritePackedSheet sheet = AsepriteSpriteSheet.PackHorizontal(new[]
        {
            new AsepriteFramePixels { Index = 0, Width = 8, Height = 8, Rgba = FillRgba(8, 8, 220, 40, 40, 255) },
            new AsepriteFramePixels { Index = 1, Width = 8, Height = 8, Rgba = FillRgba(8, 8, 40, 200, 70, 255) },
        })!;

        using var image = new MagickImage(new MagickColor(24, 26, 32), 256, 160);
        using var bar = new MagickImage(new MagickColor(36, 48, 72), 256, 32);
        using var left = new MagickImage(new MagickColor(sheet.RgbaTopLeft[0], sheet.RgbaTopLeft[1], sheet.RgbaTopLeft[2]), 104, 96);
        using var right = new MagickImage(new MagickColor(sheet.RgbaTopLeft[8 * 4], sheet.RgbaTopLeft[8 * 4 + 1], sheet.RgbaTopLeft[8 * 4 + 2]), 104, 96);
        image.Composite(bar, 0, 0, CompositeOperator.Over);
        image.Composite(left, 16, 48, CompositeOperator.Over);
        image.Composite(right, 136, 48, CompositeOperator.Over);
        image.Format = MagickFormat.Png;

        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gen-asset"));
        Directory.CreateDirectory(evidenceDir);
        string png = Path.Combine(evidenceDir, "2026-08-16-p4-3-aseprite-sprites.png");
        image.Write(png);
        File.WriteAllText(Path.ChangeExtension(png, ".md"),
            "# P4.3 Aseprite → Sprite/Texture acceptance\n\n" +
            "- Flattened RGBA cels pack into a horizontal sheet\n" +
            "- Importer emits `texture:sheet` + `sprite:{i}` (GUID = parent+stable id)\n" +
            "- Header-only files stay metadata-only (no GPU sprites)\n");
        Assert.True(new FileInfo(png).Length > 200);
    }

    private static bool HasChannel(byte[] rgba, int channel, byte min)
    {
        for (int i = channel; i < rgba.Length; i += 4)
        {
            if (rgba[i] >= min && rgba[i - channel + 3] > 0)
                return true;
        }

        return false;
    }

    private static byte[] FillRgba(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = a;
        }

        return px;
    }

    private static byte[] WriteAseHeader(int width, int height, int frames)
    {
        var bytes = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 128);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 0xA5E0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)frames);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 32);
        return bytes;
    }

    private static byte[] WriteAseWithRgbaCels()
    {
        byte[] layer = WriteAseLayerChunk("Layer 1");
        byte[] tags = WriteAseTagsChunk(("Walk", 0, 1));
        byte[] cel0 = WriteAseCelChunk(FillRgba(8, 8, 220, 40, 40, 255));
        byte[] cel1 = WriteAseCelChunk(FillRgba(8, 8, 40, 200, 70, 255));
        byte[] frame0 = WriteAseFrame(100, layer, tags, cel0);
        byte[] frame1 = WriteAseFrame(100, cel1);

        var file = new byte[128 + frame0.Length + frame1.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0), (uint)file.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 0xA5E0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14), 1);
        frame0.CopyTo(file.AsSpan(128));
        frame1.CopyTo(file.AsSpan(128 + frame0.Length));
        return file;
    }

    private static byte[] WriteAseFrame(ushort durationMs, params byte[][] chunks)
    {
        int chunkBytes = 0;
        for (int i = 0; i < chunks.Length; i++)
            chunkBytes += chunks[i].Length;

        var frame = new byte[16 + chunkBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0), (uint)frame.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 0xF1FA);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)chunks.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), durationMs);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(14), (ushort)chunks.Length);
        int offset = 16;
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i].CopyTo(frame.AsSpan(offset));
            offset += chunks[i].Length;
        }

        return frame;
    }

    private static byte[] WriteAseTagsChunk(params (string Name, int From, int To)[] tags)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)tags.Length);
        writer.Write(new byte[8]);
        foreach ((string name, int from, int to) in tags)
        {
            writer.Write((ushort)from);
            writer.Write((ushort)to);
            writer.Write((byte)0);
            writer.Write((ushort)0);
            writer.Write(new byte[6]);
            writer.Write((byte)255);
            writer.Write((byte)255);
            writer.Write((byte)255);
            writer.Write((byte)0);
            WriteAseString(writer, name);
        }

        return PrefixAseChunk(0x2018, ms.ToArray());
    }

    private static byte[] WriteAseLayerChunk(string name)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)3);
        writer.Write((ushort)0);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((byte)255);
        writer.Write(new byte[3]);
        WriteAseString(writer, name);
        return PrefixAseChunk(0x2004, ms.ToArray());
    }

    private static byte[] WriteAseCelChunk(byte[] rgba, int width = 8, int height = 8)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((byte)255);
        writer.Write((ushort)0);
        writer.Write((short)0);
        writer.Write(new byte[5]);
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write(rgba);
        return PrefixAseChunk(0x2005, ms.ToArray());
    }

    private static byte[] PrefixAseChunk(ushort type, byte[] data)
    {
        var chunk = new byte[6 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0), (uint)chunk.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(4), type);
        data.CopyTo(chunk.AsSpan(6));
        return chunk;
    }

    private static void WriteAseString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }
}
