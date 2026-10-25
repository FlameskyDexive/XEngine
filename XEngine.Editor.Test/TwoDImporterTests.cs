// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Buffers.Binary;
using System.Text;

using ImageMagick;
using ImageMagick.Formats;

using XEngine.Echo;
using XEngine.Editor;
using XEngine.Editor.Importers;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>PSD/Aseprite importer registration and CPU header parse (no Texture2D / GPU).</summary>
public sealed class TwoDImporterTests
{
    [Fact]
    public void GetImporter_Psd_UsesPsdImporterNotTextureImporter()
    {
        EditorRegistries.Initialize();
        AssetImporter? importer = EditorRegistries.GetImporter(".psd");
        Assert.IsType<PsdImporter>(importer);
        Assert.IsNotType<TextureImporter>(importer);
    }

    [Fact]
    public void GetImporter_Aseprite_UsesAsepriteImporter()
    {
        EditorRegistries.Initialize();
        Assert.IsType<AsepriteImporter>(EditorRegistries.GetImporter(".aseprite"));
        Assert.IsType<AsepriteImporter>(EditorRegistries.GetImporter(".ase"));
    }

    [Fact]
    public void AsepriteDocument_Read_ParsesHeader()
    {
        AsepriteDocument doc = AsepriteDocument.Read(WriteAseHeader(width: 16, height: 32, frames: 3));

        Assert.Equal(16, doc.Width);
        Assert.Equal(32, doc.Height);
        Assert.Equal(3, doc.FrameCount);
    }

    [Fact]
    public void AsepriteImporter_Import_SetsAsepriteAssetMain()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".aseprite");
        File.WriteAllBytes(path, WriteAseHeader(8, 8, 1));
        try
        {
            var ctx = new ImportContext(Guid.NewGuid(), path, null);
            Assert.True(new AsepriteImporter().Import(ctx));
            var asset = Assert.IsType<AsepriteAsset>(ctx.MainAsset);
            Assert.Equal(8, asset.Width);
            Assert.Equal(8, asset.Height);
            Assert.Equal(1, asset.FrameCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PsdDocument_Read_ParsesHeader()
    {
        PsdDocument doc = PsdDocument.Read(WritePsdHeader(width: 4, height: 5));

        Assert.Equal(4, doc.Width);
        Assert.Equal(5, doc.Height);
    }

    [Fact]
    public void PsdImporter_Import_SetsPsdAssetMain()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".psd");
        File.WriteAllBytes(path, WritePsdHeader(4, 5));
        try
        {
            var ctx = new ImportContext(Guid.NewGuid(), path, null);
            Assert.True(new PsdImporter().Import(ctx));
            var asset = Assert.IsType<PsdAsset>(ctx.MainAsset);
            Assert.Equal(4, asset.Width);
            Assert.Equal(5, asset.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PsdImporter_Import_AddsLayerSubAssetsWithStableIds()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".psd");
        WriteMagickPsd(path, ("Background", MagickColors.Red), ("Foreground", MagickColors.Blue));
        try
        {
            Guid guid = Guid.NewGuid();
            var ctx = new ImportContext(guid, path, null);
            Assert.True(new PsdImporter().Import(ctx));

            PsdLayerAsset[] layers = ctx.SubAssets.OfType<PsdLayerAsset>().ToArray();
            Assert.Contains(layers, l => l.LayerName == "Background");
            Assert.Contains(layers, l => l.LayerName == "Foreground");

            PsdLayerAsset background = layers.Single(l => l.LayerName == "Background");
            Assert.Equal(AssetEntry.DeriveSubAssetGuid(guid, $"layer:{background.Index}"), background.AssetID);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AsepriteDocument_Read_ParsesTagsAndSlices()
    {
        AsepriteDocument doc = AsepriteDocument.Read(WriteAseWithTagsAndSlices());

        Assert.Equal(2, doc.FrameCount);
        AsepriteTag tag = Assert.Single(doc.Tags);
        Assert.Equal("Walk", tag.Name);
        Assert.Equal(0, tag.FromFrame);
        Assert.Equal(1, tag.ToFrame);

        AsepriteSlice slice = Assert.Single(doc.Slices);
        Assert.Equal("Hitbox", slice.Name);
        Assert.Equal(1, slice.X);
        Assert.Equal(2, slice.Y);
        Assert.Equal(3, slice.Width);
        Assert.Equal(4, slice.Height);
    }

    [Fact]
    public void AsepriteImporter_Import_AddsFrameTagSliceSubAssets()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".aseprite");
        File.WriteAllBytes(path, WriteAseWithTagsAndSlices());
        try
        {
            Guid guid = Guid.NewGuid();
            var ctx = new ImportContext(guid, path, null);
            Assert.True(new AsepriteImporter().Import(ctx));

            Assert.Equal(2, ctx.SubAssets.OfType<AsepriteFrameAsset>().Count());
            AsepriteTagAsset tag = Assert.Single(ctx.SubAssets.OfType<AsepriteTagAsset>());
            Assert.Equal("Walk", tag.TagName);
            Assert.Equal(AssetEntry.DeriveSubAssetGuid(guid, "tag:Walk"), tag.AssetID);

            AsepriteSliceAsset slice = Assert.Single(ctx.SubAssets.OfType<AsepriteSliceAsset>());
            Assert.Equal("Hitbox", slice.SliceName);
            Assert.Equal(AssetEntry.DeriveSubAssetGuid(guid, "slice:Hitbox"), slice.AssetID);

            AsepriteFrameAsset frame0 = ctx.SubAssets.OfType<AsepriteFrameAsset>().Single(f => f.Index == 0);
            Assert.Equal(AssetEntry.DeriveSubAssetGuid(guid, "frame:0"), frame0.AssetID);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractGeneratedAssets_AsepriteFrame_ReimportDependsOnExternal()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".aseprite");
        File.WriteAllBytes(path, WriteAseWithTagsAndSlices());
        try
        {
            var settings = EchoObject.NewCompound();
            Guid parent = Guid.NewGuid();
            var ctx = new ImportContext(parent, path, settings);
            Assert.True(new AsepriteImporter().Import(ctx));
            AsepriteFrameAsset frame0 = ctx.SubAssets.OfType<AsepriteFrameAsset>().Single(f => f.Index == 0);

            Guid extracted = ExtractGeneratedAssets.Extract(ctx, "frame:0");
            Assert.Equal(frame0.AssetID, extracted);
            Assert.True(ExtractGeneratedAssets.TryGetExternal(settings, "frame:0", out Guid mapped));
            Assert.Equal(extracted, mapped);

            var reimport = new ImportContext(parent, path, settings);
            Assert.True(new AsepriteImporter().Import(reimport));
            Assert.DoesNotContain(reimport.SubAssets.OfType<AsepriteFrameAsset>(), f => f.Index == 0);
            Assert.Contains(extracted, reimport.Dependencies);
            Assert.Contains(reimport.SubAssets.OfType<AsepriteFrameAsset>(), f => f.Index == 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractGeneratedAssets_PsdLayer_ReimportDependsOnExternal()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".psd");
        WriteMagickPsd(path, ("Background", MagickColors.Red), ("Foreground", MagickColors.Blue));
        try
        {
            var settings = EchoObject.NewCompound();
            Guid parent = Guid.NewGuid();
            var ctx = new ImportContext(parent, path, settings);
            Assert.True(new PsdImporter().Import(ctx));
            PsdLayerAsset background = ctx.SubAssets.OfType<PsdLayerAsset>().Single(l => l.LayerName == "Background");
            string stableId = $"layer:{background.Index}";

            Guid extracted = ExtractGeneratedAssets.Extract(ctx, stableId);
            var reimport = new ImportContext(parent, path, settings);
            Assert.True(new PsdImporter().Import(reimport));
            Assert.DoesNotContain(reimport.SubAssets.OfType<PsdLayerAsset>(), l => l.LayerName == "Background");
            Assert.Contains(extracted, reimport.Dependencies);
            Assert.Contains(reimport.SubAssets.OfType<PsdLayerAsset>(), l => l.LayerName == "Foreground");
        }
        finally
        {
            File.Delete(path);
        }
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

    private static byte[] WritePsdHeader(int width, int height)
    {
        var bytes = new byte[26];
        bytes[0] = (byte)'8';
        bytes[1] = (byte)'B';
        bytes[2] = (byte)'P';
        bytes[3] = (byte)'S';
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18), (uint)width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), 3);
        return bytes;
    }

    private static void WriteMagickPsd(string path, params (string Name, MagickColor Color)[] layers)
    {
        using var images = new MagickImageCollection();
        foreach ((string name, MagickColor color) in layers)
        {
            var image = new MagickImage(color, 8, 8);
            image.Label = name;
            image.SetArtifact("psd:layer.name", name);
            images.Add(image);
        }

        IMagickImage<byte> composite = images.Flatten();
        composite.Label = "";
        images.Insert(0, composite);
        images.Write(path, new PsdWriteDefines { AdditionalInfo = PsdAdditionalInfoPart.All });
    }

    private static byte[] WriteAseWithTagsAndSlices()
    {
        byte[] tagsChunk = WriteAseTagsChunk(("Walk", 0, 1));
        byte[] sliceChunk = WriteAseSliceChunk("Hitbox", x: 1, y: 2, width: 3, height: 4);
        byte[] frame0 = WriteAseFrame(100, tagsChunk, sliceChunk);
        byte[] frame1 = WriteAseFrame(100);

        var file = new byte[128 + frame0.Length + frame1.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0), (uint)file.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 0xA5E0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), 32);
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

        byte[] data = ms.ToArray();
        var chunk = new byte[6 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0), (uint)chunk.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(4), 0x2018);
        data.CopyTo(chunk.AsSpan(6));
        return chunk;
    }

    private static byte[] WriteAseSliceChunk(string name, int x, int y, int width, int height)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(0u);
        WriteAseString(writer, name);
        writer.Write(0u);
        writer.Write(x);
        writer.Write(y);
        writer.Write((uint)width);
        writer.Write((uint)height);

        byte[] data = ms.ToArray();
        var chunk = new byte[6 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0), (uint)chunk.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(4), 0x2022);
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
