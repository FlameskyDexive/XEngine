using System.IO.Compression;
using System.Text;

using Spine;

using XEngine.Echo;
using XEngine.Editor;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Spine;
using XEngine.Spine.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class SpineImporterTests
{
    private const string MinimalWalkJson = """
        {
          "skeleton": { "hash": "m2", "spine": "4.2.37", "width": 64, "height": 64 },
          "bones": [
            { "name": "root" },
            { "name": "body", "parent": "root", "y": 20 }
          ],
          "slots": [
            { "name": "body", "bone": "body" }
          ],
          "skins": [
            { "name": "default" }
          ],
          "animations": {
            "walk": {
              "bones": {
                "body": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.5, "x": 10, "y": 0 }
                  ]
                }
              }
            }
          }
        }
        """;

    private const string MinimalAtlas = """

        hero.png
        size: 64,64
        format: RGBA8888
        filter: Linear,Linear
        body
        bounds: 0,0,32,32
        """;

    [Fact]
    public void GetImporter_SpineExtensions_UseSpineImporters()
    {
        EditorRegistries.Initialize();
        Assert.IsType<SpineAtlasImporter>(EditorRegistries.GetImporter(".atlas"));
        Assert.IsType<SpineAtlasImporter>(EditorRegistries.GetImporter(".txt"));
        Assert.IsType<SpineSkeletonImporter>(EditorRegistries.GetImporter(".skel"));
        Assert.IsType<SpineSkeletonImporter>(EditorRegistries.GetImporter(".json"));
        Assert.IsType<SpineSkeletonImporter>(EditorRegistries.GetImporter(".bytes"));
    }

    [Fact]
    public void GetImporterForFile_ClaimsOnlySpineShapedSources()
    {
        EditorRegistries.Initialize();
        string dir = CreateTempDir();
        try
        {
            string atlasTxt = Path.Combine(dir, "hero.atlas.txt");
            string readme = Path.Combine(dir, "readme.txt");
            string notes = Path.Combine(dir, "notes.json");
            string spineJson = Path.Combine(dir, "hero.json");
            string skelBytes = Path.Combine(dir, "hero.skel.bytes");
            string blob = Path.Combine(dir, "blob.bytes");
            File.WriteAllText(atlasTxt, MinimalAtlas);
            File.WriteAllText(readme, "hello");
            File.WriteAllText(notes, """{ "name": "notes" }""");
            File.WriteAllText(spineJson, MinimalWalkJson);
            File.WriteAllBytes(skelBytes, [1, 2, 3]);
            File.WriteAllBytes(blob, [1, 2, 3]);

            Assert.IsType<SpineAtlasImporter>(EditorRegistries.GetImporterForFile(atlasTxt));
            Assert.Null(EditorRegistries.GetImporterForFile(readme));
            Assert.Equal("DefaultImporter", EditorRegistries.GetImporterTypeNameForFile(readme));
            Assert.Null(EditorRegistries.GetImporterForFile(notes));
            Assert.Equal("DefaultImporter", EditorRegistries.GetImporterTypeNameForFile(notes));
            Assert.IsType<SpineSkeletonImporter>(EditorRegistries.GetImporterForFile(spineJson));
            Assert.IsType<SpineSkeletonImporter>(EditorRegistries.GetImporterForFile(skelBytes));
            Assert.Null(EditorRegistries.GetImporterForFile(blob));
            Assert.IsType<SpineSkeletonImporter>(EditorRegistries.GetImporterForFile(Path.Combine(dir, "hero.skel")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SpineAtlasImporter_Import_ParsesPageRegionAndTextureDependency()
    {
        string dir = CreateTempDir();
        string path = Path.Combine(dir, "hero.atlas.txt");
        File.WriteAllText(path, MinimalAtlas);
        Guid textureGuid = Guid.NewGuid();
        MetaFile.Write(Path.Combine(dir, "hero.png.meta"), new MetaFileData
        {
            Guid = textureGuid,
            ImporterType = "TextureImporter",
            ImporterVersion = 3,
        });

        try
        {
            var ctx = new ImportContext(Guid.NewGuid(), path, null);
            Assert.True(new SpineAtlasImporter().Import(ctx));
            var asset = Assert.IsType<SpineAtlasAsset>(ctx.MainAsset);
            Assert.Equal("hero", asset.Name);
            Assert.Equal(["hero.png"], asset.PageFileNames);
            Assert.Equal(["body"], asset.RegionNames);
            Assert.Equal(textureGuid, Assert.Single(asset.PageTextures).AssetID);
            Assert.Contains(textureGuid, ctx.Dependencies);
            Assert.Single(asset.CreateAtlas().Regions);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SpineSkeletonImporter_Import_JsonSetsScaleAnimationsAndAtlasLink()
    {
        string dir = CreateTempDir();
        string jsonPath = Path.Combine(dir, "hero.json");
        string atlasPath = Path.Combine(dir, "hero.atlas.txt");
        File.WriteAllText(jsonPath, MinimalWalkJson);
        File.WriteAllText(atlasPath, MinimalAtlas);
        Guid atlasGuid = Guid.NewGuid();
        MetaFile.Write(atlasPath + ".meta", new MetaFileData
        {
            Guid = atlasGuid,
            ImporterType = nameof(SpineAtlasImporter),
            ImporterVersion = 1,
        });

        try
        {
            Guid parent = Guid.NewGuid();
            EchoObject? settings = new SpineSkeletonImporter().DefaultSettings();
            var ctx = new ImportContext(parent, jsonPath, settings);
            Assert.True(new SpineSkeletonImporter().Import(ctx));

            var asset = Assert.IsType<SkeletonDataAsset>(ctx.MainAsset);
            Assert.Equal("hero", asset.Name);
            Assert.Equal(SkeletonDataAsset.DefaultScale, asset.Scale);
            Assert.False(asset.IsBinary);
            Assert.Equal(["walk"], asset.AnimationNames);
            Assert.Contains("default", asset.SkinNames);
            Assert.Equal(["hero.atlas.txt"], asset.AtlasFileNames);
            Assert.Equal(atlasGuid, Assert.Single(asset.AtlasAssets).AssetID);
            Assert.Contains(atlasGuid, ctx.Dependencies);

            AnimationReferenceAsset walk = Assert.Single(ctx.SubAssets.OfType<AnimationReferenceAsset>());
            Assert.Equal("walk", walk.AnimationName);
            Assert.Equal(AssetEntry.DeriveSubAssetGuid(parent, "anim:walk"), walk.AssetID);
            Assert.Equal(parent, walk.SkeletonDataAsset.AssetID);

            SkeletonData data = asset.GetSkeletonData();
            Assert.NotNull(data.FindAnimation("walk"));
            Assert.NotNull(data.FindBone("body"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SpineSkeletonImporter_Import_HonorsScaleSetting()
    {
        string dir = CreateTempDir();
        string path = Path.Combine(dir, "hero.json");
        File.WriteAllText(path, MinimalWalkJson);
        try
        {
            var settings = EchoObject.NewCompound();
            settings[SpineSkeletonImporter.ScaleSetting] = new EchoObject(1f);
            var ctx = new ImportContext(Guid.NewGuid(), path, settings);
            Assert.True(new SpineSkeletonImporter().Import(ctx));
            Assert.Equal(1f, Assert.IsType<SkeletonDataAsset>(ctx.MainAsset).Scale);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SkeletonDataAsset_GetSkeletonData_ReplaysWalk()
    {
        var asset = new SkeletonDataAsset
        {
            Scale = 1f,
            SkeletonBytes = Encoding.UTF8.GetBytes(MinimalWalkJson),
        };
        SkeletonData data = asset.GetSkeletonData();
        var skeleton = new global::Spine.Skeleton(data);
        var state = new global::Spine.AnimationState(new global::Spine.AnimationStateData(data));
        state.SetAnimation(0, "walk", loop: false);
        global::Spine.Bone body = skeleton.FindBone("body")!;
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        float startX = body.AppliedPose.WorldX;
        state.Update(0.5f);
        state.Apply(skeleton);
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        Assert.True(body.AppliedPose.WorldX > startX + 5f);
    }

    [Fact]
    public void ImportEvidence_WritesM2Screenshot()
    {
        EditorRegistries.Initialize();
        string repo = FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(evidenceDir);

        string dir = CreateTempDir();
        try
        {
            string atlasPath = Path.Combine(dir, "hero.atlas.txt");
            string jsonPath = Path.Combine(dir, "hero.json");
            string notesPath = Path.Combine(dir, "notes.json");
            File.WriteAllText(atlasPath, MinimalAtlas);
            File.WriteAllText(jsonPath, MinimalWalkJson);
            File.WriteAllText(notesPath, """{ "name": "notes" }""");

            var atlasCtx = new ImportContext(Guid.NewGuid(), atlasPath, null);
            Assert.True(new SpineAtlasImporter().Import(atlasCtx));
            var atlas = Assert.IsType<SpineAtlasAsset>(atlasCtx.MainAsset);

            var skelCtx = new ImportContext(Guid.NewGuid(), jsonPath, new SpineSkeletonImporter().DefaultSettings());
            Assert.True(new SpineSkeletonImporter().Import(skelCtx));
            var skeleton = Assert.IsType<SkeletonDataAsset>(skelCtx.MainAsset);

            bool notesFallback = EditorRegistries.GetImporterForFile(notesPath) == null;
            bool atlasClaimed = EditorRegistries.GetImporterForFile(atlasPath) is SpineAtlasImporter;
            bool jsonClaimed = EditorRegistries.GetImporterForFile(jsonPath) is SpineSkeletonImporter;

            const int width = 960;
            const int height = 220;
            byte[] rgba = new byte[width * height * 4];
            Fill(rgba, width, 0, 0, width, height, 28, 30, 36);
            Fill(rgba, width, 0, 0, width, 36, 38, 40, 48);
            DrawPassBar(rgba, width, 16, 52, atlasClaimed && atlas.RegionNames.Contains("body"));
            DrawPassBar(rgba, width, 16, 92, jsonClaimed && skeleton.AnimationNames.Contains("walk"));
            DrawPassBar(rgba, width, 16, 132, notesFallback && skeleton.Scale == SkeletonDataAsset.DefaultScale);
            DrawPassBar(rgba, width, 16, 172, skeleton.GetSkeletonData().FindAnimation("walk") != null);

            string pngPath = Path.Combine(evidenceDir, "m2-spine-importers.png");
            File.WriteAllBytes(pngPath, EncodePng(rgba, width, height));
            Assert.True(new FileInfo(pngPath).Length > 256);

            File.WriteAllText(Path.Combine(evidenceDir, "m2-spine-importers.md"),
                "# M2 Spine importers\n\n" +
                "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
                "`CanImport` keeps `.txt` / `.json` / `.bytes` from being stolen: only `*.atlas.txt`, " +
                "Spine JSON (`skeleton`+`bones`+`slots`), and `*.skel.bytes` are claimed.\n\n" +
                $"- Atlas import: page `{atlas.PageFileNames[0]}`, region `{atlas.RegionNames[0]}`.\n" +
                $"- Skeleton JSON import: animations `{string.Join(", ", skeleton.AnimationNames)}`, " +
                $"scale `{skeleton.Scale}`, sibling atlas `{string.Join(", ", skeleton.AtlasFileNames)}`.\n" +
                $"- Non-Spine `notes.json` importer: `{(notesFallback ? "DefaultImporter" : "claimed")}`. \n\n" +
                "Screenshot: [m2-spine-importers.png](m2-spine-importers.png).\n");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void DrawPassBar(byte[] rgba, int width, int x, int y, bool passed)
        => Fill(rgba, width, x, y, width - 32, 28,
            passed ? (byte)46 : (byte)120,
            passed ? (byte)92 : (byte)48,
            passed ? (byte)64 : (byte)48);

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "XEngineSpineImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }

    private static void Fill(byte[] rgba, int stride, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        int x1 = Math.Min(stride, x + w);
        int y1 = Math.Min(rgba.Length / (stride * 4), y + h);
        for (int py = Math.Max(0, y); py < y1; py++)
        {
            for (int px = Math.Max(0, x); px < x1; px++)
            {
                int i = (py * stride + px) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
        }
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var memory = new MemoryStream();
        memory.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(memory, "IHDR", ihdr);
        byte[] raw = new byte[(width * 4 + 1) * height];
        int srcRow = width * 4;
        for (int y = 0; y < height; y++)
        {
            raw[y * (srcRow + 1)] = 0;
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }

        using var deflate = new MemoryStream();
        deflate.WriteByte(0x78);
        deflate.WriteByte(0x9C);
        using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(raw, 0, raw.Length);
        byte[] adler = new byte[4];
        WriteInt(adler, 0, (int)Adler32(raw));
        deflate.Write(adler, 0, 4);
        WriteChunk(memory, "IDAT", deflate.ToArray());
        WriteChunk(memory, "IEND", []);
        return memory.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes) crc = Crc32Step(crc, value);
        foreach (byte value in data) crc = Crc32Step(crc, value);
        byte[] crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)(crc ^ 0xFFFFFFFF));
        stream.Write(crcBytes);
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static uint Crc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }
}
