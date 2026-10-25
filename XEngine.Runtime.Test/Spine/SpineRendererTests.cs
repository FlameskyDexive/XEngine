using System.IO.Compression;
using System.Text;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Spine;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Spine;

public sealed class SpineRendererTests : RuntimeTestBase
{
    private sealed class DeferredAssetBackend : AssetBackendBase
    {
        protected override EngineObject? LoadFresh(Guid assetId) => null;

        public void Publish(Guid assetId, EngineObject asset) => SetLoaded(assetId, asset);
    }

    [Fact]
    public void MeshGenerator_RegionAttachment_EmitsPmaQuad()
    {
        Skeleton skeleton = CreateQuadSkeleton();
        var generator = new SpineMeshGenerator();
        generator.Build(skeleton, Color.White);

        Assert.Equal(1, generator.BatchCount);
        SpineMeshBatch batch = generator.GetBatch(0);
        Assert.Equal(4, batch.Vertices.Length);
        Assert.Equal(6, batch.Indices.Length);
        Assert.Equal(BlendMode.Normal, batch.BlendMode);

        float minX = batch.Vertices[0].X, maxX = minX, minY = batch.Vertices[0].Y, maxY = minY;
        for (int i = 0; i < batch.Vertices.Length; i++)
        {
            minX = Math.Min(minX, batch.Vertices[i].X);
            maxX = Math.Max(maxX, batch.Vertices[i].X);
            minY = Math.Min(minY, batch.Vertices[i].Y);
            maxY = Math.Max(maxY, batch.Vertices[i].Y);
            Assert.Equal(255, batch.Colors[i].A);
        }

        Assert.True(maxX - minX > 30f);
        Assert.True(maxY - minY > 30f);
        Assert.Equal(0u, batch.Indices[0]);
        Assert.Equal(2u, batch.Indices[2]);
    }

    /// <summary>
    /// Region quad UVs must stay paired 1:1 with <see cref="RegionAttachment.ComputeWorldVertices"/>
    /// output: both arrays share the BL,UL,UR,BR index order. A cyclic reorder rotates each
    /// region attachment's texture content (observed as misplaced head/feet parts on spineboy).
    /// </summary>
    [Fact]
    public void MeshGenerator_RegionAttachment_UVsPairWithWorldVerticesInPlace()
    {
        Skeleton skeleton = CreateQuadSkeleton();
        Slot slot = skeleton.Slots.Items[0];
        var region = (RegionAttachment)slot.Pose.Attachment!;
        var generator = new SpineMeshGenerator();
        generator.Build(skeleton, Color.White);

        SpineMeshBatch batch = generator.GetBatch(0);
        Assert.Equal(4, batch.UVs.Length);
        // World vertices order = offset order = BL, UL, UR, BR — same slots as sequence UVs.
        var world = new float[8];
        region.ComputeWorldVertices(slot, region.Sequence.GetOffsets(0), world, 0);
        float[] regionUvs = region.Sequence.GetUVs(0);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(regionUvs[i * 2], batch.UVs[i].X, 5);
            Assert.Equal(regionUvs[i * 2 + 1], batch.UVs[i].Y, 5);
            Assert.Equal(world[i * 2], batch.Vertices[i].X, 3);
            Assert.Equal(world[i * 2 + 1], batch.Vertices[i].Y, 3);
        }
    }

    [Fact]
    public void AtlasAsset_CreateAtlas_FlipsTopLeftAtlasCoordinatesForGpuTextures()
    {
        const string atlasText = """
            body.png
            size: 100, 50
            filter: Linear, Linear
            body
            bounds: 10, 5, 20, 10
            """;
        SpineAtlasAsset atlasAsset = SpineAtlasAsset.CreateRuntime(atlasText);

        Atlas atlas = atlasAsset.CreateAtlas();
        AtlasRegion region = Assert.IsType<AtlasRegion>(atlas.FindRegion("body"));

        Assert.Equal(0.9f, region.v, 5);
        Assert.Equal(0.7f, region.v2, 5);
    }

    [Fact]
    public void Mesh_AssignStreams_KeepsCallerArrays()
    {
        var mesh = new Mesh();
        Float3[] verts = [new Float3(0, 0, 0), new Float3(1, 0, 0), new Float3(0, 1, 0)];
        Float2[] uvs = [new Float2(0, 0), new Float2(1, 0), new Float2(0, 1)];
        Color32[] colors = [new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255)];
        uint[] indices = [0, 1, 2];
        mesh.AssignStreams(verts, uvs, colors, indices);
        Assert.Same(verts, mesh.Vertices);
        Assert.Equal(uvs, mesh.UV);
        Assert.Equal(colors, mesh.Colors32);
        Assert.Equal(indices, mesh.Indices);
        mesh.Dispose();
    }

    [Fact]
    public void MeshGenerator_DeferredAtlasTexture_ResolvesAfterAssetStreamsIn()
    {
        AssetBackendBase? previousBackend = AssetDatabase.Current;
        bool previousAsync = AssetLoadingConfig.AsyncEnabled;
        Guid textureId = Guid.NewGuid();
        var texture = new Texture2D(2, 2) { AssetID = textureId };
        var backend = new DeferredAssetBackend();
        AssetDatabase.Current = backend;
        AssetLoadingConfig.AsyncEnabled = true;

        try
        {
            var atlasAsset = ScriptableObject.CreateInstance<SpineAtlasAsset>();
            atlasAsset.PageFileNames.Add("body.png");
            atlasAsset.PageTextures.Add(new AssetRef<Texture2D>(textureId));
            var page = new AtlasPage { name = "body.png", width = 2, height = 2 };
            new SpineRuntimeTextureLoader(atlasAsset).Load(page, "body.png");

            Assert.IsType<AssetRef<Texture2D>>(page.rendererObject);

            backend.Publish(textureId, texture);
            Skeleton skeleton = CreateQuadSkeleton(page);
            var generator = new SpineMeshGenerator();
            generator.Build(skeleton, Color.White);

            Assert.Equal(1, generator.BatchCount);
            SpineMeshBatch batch = generator.GetBatch(0);
            Assert.Same(texture, batch.Texture);
        }
        finally
        {
            AssetLoadingConfig.AsyncEnabled = previousAsync;
            AssetDatabase.Current = previousBackend;
            AssetDatabase.Forget(textureId);
            if (!texture.IsDisposed)
                texture.Dispose();
        }
    }

    [Fact]
    public void SkeletonRenderer_OnRenderCollect_UploadsSortedMesh()
    {
        GameObject go = CreateGameObject("Spine");
        var renderer = go.AddComponent<SkeletonRenderer>();
        renderer.SetSkeleton(CreateQuadSkeleton());
        renderer.SortingLayer = 1;
        renderer.SortingOrder = 5;

        var renderables = new List<IRenderable>();
        renderer.OnRenderCollect(null!, renderables, []);
        Assert.Single(renderables);
        Assert.Equal(1, renderer.MeshBatchCount);
        Mesh mesh = Assert.IsType<Mesh>(renderer.GetBatchMesh(0));
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(6, mesh.IndexCount);

        var meshRenderable = Assert.IsType<MeshRenderable>(renderables[0]);
        Float3 pos = meshRenderable.GetPosition();
        Assert.True(pos.Z > 0f);
    }

    [Fact]
    public void SpineShaders_ParseWithPmaBlend()
    {
        Shader skeleton = SpineShaderLibrary.LoadSkeletonShader();
        Shader lit = SpineShaderLibrary.LoadSkeletonLitShader();
        Shader additive = SpineShaderLibrary.LoadSkeletonAdditiveShader();
        Assert.Equal("Spine/Skeleton", skeleton.ArtifactName);
        Assert.Equal("Spine/SkeletonLit", lit.ArtifactName);
        Assert.Equal("Spine/SkeletonAdditive", additive.ArtifactName);
        Assert.True(skeleton.GetPass("Skeleton").HasGlsl);
        Assert.True(skeleton.GetPass("Skeleton").HasHlsl);
        Assert.True(lit.GetPass("SkeletonLit").HasHlsl);
        Assert.True(additive.GetPass("SkeletonAdditive").HasHlsl);
        Assert.Equal(RasterizerState.Blending.One, skeleton.GetPass("Skeleton").State.BlendSrc);
        Assert.Equal(RasterizerState.Blending.OneMinusSrcAlpha, skeleton.GetPass("Skeleton").State.BlendDst);
        Assert.Equal(RasterizerState.Blending.One, additive.GetPass("SkeletonAdditive").State.BlendSrc);
        Assert.Equal(RasterizerState.Blending.One, additive.GetPass("SkeletonAdditive").State.BlendDst);
    }

    [Fact]
    public void RendererEvidence_WritesM3Screenshot()
    {
        Skeleton skeleton = CreateQuadSkeleton();
        var generator = new SpineMeshGenerator();
        generator.Build(skeleton, Color.White);
        SpineMeshBatch batch = generator.GetBatch(0);

        const int width = 160;
        const int height = 160;
        byte[] rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, 24, 26, 32);
        Rasterize(rgba, width, height, batch);

        string repo = FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(dir);
        string pngPath = Path.Combine(dir, "m3-skeleton-renderer.png");
        File.WriteAllBytes(pngPath, EncodePng(rgba, width, height));
        Assert.True(new FileInfo(pngPath).Length > 256);

        File.WriteAllText(Path.Combine(dir, "m3-skeleton-renderer.md"),
            "# M3 SkeletonRenderer\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "CPU mesh from a 32×32 `RegionAttachment` (4 verts / 6 indices, PMA white). " +
            "`SkeletonRenderer.OnRenderCollect` assigns streams, `Upload()`s, and submits " +
            "`MeshRenderable` with Sprite-style sorting Z-bias.\n\n" +
            "Shaders: `Spine/Skeleton` (PMA One / OneMinusSrcAlpha), `Spine/SkeletonLit`, " +
            "`Spine/SkeletonAdditive`.\n\n" +
            "Screenshot: [m3-skeleton-renderer.png](m3-skeleton-renderer.png).\n");
    }

    private static Skeleton CreateQuadSkeleton(AtlasPage? atlasPage = null)
    {
        var bone = new BoneData(0, "root", null!);
        bone.GetSetupPose().ScaleX = 1f;
        bone.GetSetupPose().ScaleY = 1f;
        var slot = new SlotData(0, "body", bone) { AttachmentName = "body" };
        var data = new SkeletonData();
        data.Bones.Add(bone);
        data.Slots.Add(slot);
        AtlasPage page = atlasPage ?? new AtlasPage { name = "body.png", width = 32, height = 32 };
        var atlasRegion = new AtlasRegion
        {
            page = page,
            name = "body",
            width = 32,
            height = 32,
            originalWidth = 32,
            originalHeight = 32,
            u = 0f,
            v = 0f,
            u2 = 1f,
            v2 = 1f,
        };
        var sequence = new Sequence(1, false);
        sequence.Regions[0] = atlasRegion;
        var region = new RegionAttachment("body", sequence) { Width = 32, Height = 32 };
        region.UpdateSequence();
        var skin = new Skin("default");
        skin.SetAttachment(0, "body", region);
        data.Skins.Add(skin);
        data.DefaultSkin = skin;
        var skeleton = new Skeleton(data);
        skeleton.SetSkin(skin);
        skeleton.SetupPose();
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        return skeleton;
    }

    private static void Rasterize(byte[] rgba, int width, int height, SpineMeshBatch batch)
    {
        float minX = batch.Vertices[0].X, maxX = minX, minY = batch.Vertices[0].Y, maxY = minY;
        for (int i = 0; i < batch.Vertices.Length; i++)
        {
            minX = Math.Min(minX, batch.Vertices[i].X);
            maxX = Math.Max(maxX, batch.Vertices[i].X);
            minY = Math.Min(minY, batch.Vertices[i].Y);
            maxY = Math.Max(maxY, batch.Vertices[i].Y);
        }

        float pad = 8f;
        float sx = (width - pad * 2) / Math.Max(1f, maxX - minX);
        float sy = (height - pad * 2) / Math.Max(1f, maxY - minY);
        float scale = Math.Min(sx, sy);

        Float2 Map(Float3 v) => new(
            pad + (v.X - minX) * scale,
            height - (pad + (v.Y - minY) * scale));

        for (int i = 0; i + 2 < batch.Indices.Length; i += 3)
        {
            Float2 a = Map(batch.Vertices[batch.Indices[i]]);
            Float2 b = Map(batch.Vertices[batch.Indices[i + 1]]);
            Float2 c = Map(batch.Vertices[batch.Indices[i + 2]]);
            FillTriangle(rgba, width, height, a, b, c, 72, 196, 120);
        }
    }

    private static void FillTriangle(byte[] rgba, int width, int height, Float2 a, Float2 b, Float2 c, byte r, byte g, byte bl)
    {
        int minX = (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)));
        int maxX = (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X)));
        int minY = (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)));
        int maxY = (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
        minX = Math.Clamp(minX, 0, width - 1);
        maxX = Math.Clamp(maxX, 0, width - 1);
        minY = Math.Clamp(minY, 0, height - 1);
        maxY = Math.Clamp(maxY, 0, height - 1);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!PointInTriangle(new Float2(x + 0.5f, y + 0.5f), a, b, c))
                    continue;
                int i = (y * width + x) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = bl;
                rgba[i + 3] = 255;
            }
        }
    }

    private static bool PointInTriangle(Float2 p, Float2 a, Float2 b, Float2 c)
    {
        float d1 = Sign(p, a, b);
        float d2 = Sign(p, b, c);
        float d3 = Sign(p, c, a);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static float Sign(Float2 p1, Float2 p2, Float2 p3)
        => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);

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
}
