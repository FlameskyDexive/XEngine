// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class Sprite2DTests : RuntimeTestBase
{
    [Fact]
    public void SpriteRenderer_DefaultsToSimpleDrawModeAndNativeSize()
    {
        GameObject go = CreateGameObject("Sprite");
        var renderer = go.AddComponent<SpriteRenderer>();

        Assert.Equal(SpriteDrawMode.Simple, renderer.DrawMode);
        Assert.Equal(default, renderer.Size);
        Assert.Equal(0, renderer.SortingLayer);
        Assert.Equal(0, renderer.SortingOrder);
    }

    [Fact]
    public void SpriteRenderer_EchoRoundTripsDrawModeSizeAndSorting()
    {
        GameObject go = CreateGameObject("SpriteEcho");
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.DrawMode = SpriteDrawMode.Sliced;
        renderer.Size = new Float2(2.5f, 1.25f);
        renderer.SortingLayer = 3;
        renderer.SortingOrder = 7;

        EchoObject echo = Serializer.Serialize(go);
        GameObject clone = Serializer.Deserialize<GameObject>(echo);
        var copy = clone.GetComponent<SpriteRenderer>();
        Assert.NotNull(copy);
        Assert.Equal(SpriteDrawMode.Sliced, copy.DrawMode);
        Assert.Equal(new Float2(2.5f, 1.25f), copy.Size);
        Assert.Equal(3, copy.SortingLayer);
        Assert.Equal(7, copy.SortingOrder);
    }

    [Fact]
    public void SpriteGeometry_SimpleQuadUsesNativeSizeAndPivot()
    {
        var request = new SpriteGeometryRequest
        {
            Mode = SpriteDrawMode.Simple,
            Rect = new SpriteRect(0, 0, 32, 32),
            TextureWidth = 32,
            TextureHeight = 32,
            Pivot = new Float2(0.5f, 0.5f),
            PixelsPerUnit = 32f,
        };

        SpriteGeometry.Build(request, out Float3[] verts, out Float2[] uvs, out ushort[] indices);

        Assert.Equal(4, verts.Length);
        Assert.Equal(6, indices.Length);
        Assert.Contains(verts, v => v.X == -0.5f && v.Y == -0.5f);
        Assert.Contains(verts, v => v.X == 0.5f && v.Y == 0.5f);
        Assert.Equal(new Float2(0f, 0f), uvs[0]);
        Assert.Equal(new Float2(1f, 1f), uvs[2]);
    }

    [Fact]
    public void SpriteGeometry_SlicedEmitsNineQuadsAndKeepsCornerAspect()
    {
        var request = new SpriteGeometryRequest
        {
            Mode = SpriteDrawMode.Sliced,
            Rect = new SpriteRect(0, 0, 32, 32),
            TextureWidth = 32,
            TextureHeight = 32,
            Pivot = new Float2(0.5f, 0.5f),
            PixelsPerUnit = 32f,
            Border = new Float4(8f, 8f, 8f, 8f),
            Size = new Float2(2f, 2f),
        };

        SpriteGeometry.Build(request, out Float3[] verts, out Float2[] _, out ushort[] indices);

        Assert.Equal(36, verts.Length);
        Assert.Equal(54, indices.Length);

        float corner = 8f / 32f;
        Assert.Contains(verts, v => v.X == -1f && v.Y == -1f);
        Assert.Contains(verts, v => v.X == -1f + corner && v.Y == -1f + corner);
        Assert.Contains(verts, v => v.X == 1f && v.Y == 1f);
    }

    [Fact]
    public void SpriteGeometry_TiledRepeatsNativeQuadsToFillSize()
    {
        var request = new SpriteGeometryRequest
        {
            Mode = SpriteDrawMode.Tiled,
            Rect = new SpriteRect(0, 0, 32, 32),
            TextureWidth = 32,
            TextureHeight = 32,
            Pivot = new Float2(0f, 0f),
            PixelsPerUnit = 32f,
            Size = new Float2(2f, 1f),
        };

        SpriteGeometry.Build(request, out Float3[] verts, out Float2[] _, out ushort[] indices);

        Assert.Equal(8, verts.Length);
        Assert.Equal(12, indices.Length);
        Assert.Contains(verts, v => v.X == 0f && v.Y == 0f);
        Assert.Contains(verts, v => v.X == 2f && v.Y == 1f);
    }

    [Fact]
    public void SortingGroup_OverridesChildLayerAndOrder()
    {
        GameObject parent = CreateGameObject("Group");
        var group = parent.AddComponent<SortingGroup>();
        group.SortingLayer = 4;
        group.SortingOrder = 11;

        GameObject child = CreateGameObject("Child");
        child.SetParent(parent);
        var renderer = child.AddComponent<SpriteRenderer>();
        renderer.SortingLayer = 1;
        renderer.SortingOrder = 2;

        SpriteSorting.Resolve(renderer, out int layer, out int order);
        Assert.Equal(4, layer);
        Assert.Equal(11, order);

        SpriteSorting.Resolve(parent.AddComponent<SpriteRenderer>(), out int selfLayer, out int selfOrder);
        Assert.Equal(4, selfLayer);
        Assert.Equal(11, selfOrder);
    }

    [Fact]
    public void SpriteAtlasPacker_IsDeterministicAndNeverRotates()
    {
        var sources = new SpriteAtlasPackRequest[]
        {
            new("hero/idle", 48, 32),
            new("hero/run", 16, 64),
            new("fx/spark", 8, 8),
        };
        var settings = new SpriteAtlasPackSettings
        {
            Padding = 2,
            MaxSize = 256,
        };

        SpriteAtlasPackResult first = SpriteAtlasPacker.Pack(sources, settings);
        SpriteAtlasPackResult second = SpriteAtlasPacker.Pack(sources, settings);

        Assert.True(first.Width > 0 && first.Height > 0);
        Assert.True(first.Width <= 256 && first.Height <= 256);
        Assert.Equal(3, first.Rects.Length);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);

        for (int i = 0; i < first.Rects.Length; i++)
        {
            Assert.Equal(first.Rects[i].SourceId, second.Rects[i].SourceId);
            Assert.Equal(first.Rects[i].X, second.Rects[i].X);
            Assert.Equal(first.Rects[i].Y, second.Rects[i].Y);
            Assert.Equal(first.Rects[i].Width, second.Rects[i].Width);
            Assert.Equal(first.Rects[i].Height, second.Rects[i].Height);
            Assert.False(first.Rects[i].Rotated);
        }

        Assert.Equal("hero/idle", first.Rects[0].SourceId);
        Assert.Equal(48, first.Find("hero/idle").Width);
        Assert.Equal(64, first.Find("hero/run").Height);
    }

    [Fact]
    public void SpriteAtlasPacker_RespectsPaddingAndRejectsOverflow()
    {
        var sources = new SpriteAtlasPackRequest[]
        {
            new("a", 16, 16),
            new("b", 16, 16),
        };

        SpriteAtlasPackResult packed = SpriteAtlasPacker.Pack(sources, new SpriteAtlasPackSettings { Padding = 4, MaxSize = 64 });
        SpriteAtlasPackedRect a = packed.Find("a");
        SpriteAtlasPackedRect b = packed.Find("b");
        Assert.False(RectsOverlap(a, b, padding: 4));

        Assert.Throws<InvalidOperationException>(() =>
            SpriteAtlasPacker.Pack(
                [new SpriteAtlasPackRequest("huge", 128, 128)],
                new SpriteAtlasPackSettings { MaxSize = 64 }));
    }

    [Fact]
    public void RenderStats_RecordsSpritePassCounters()
    {
        RenderStats.BeginFrame();
        RenderStats.RecordSprites(visible: 10, culled: 2, instances: 10, batches: 1, draws: 1, uploadBytes: 128, atlasSwitches: 1);
        RenderStats.EndFrame();

        Assert.Equal(10, RenderStats.Last.SpritesVisible);
        Assert.Equal(2, RenderStats.Last.SpritesCulled);
        Assert.Equal(10, RenderStats.Last.SpriteInstances);
        Assert.Equal(1, RenderStats.Last.SpriteBatches);
        Assert.Equal(1, RenderStats.Last.SpriteDraws);
        Assert.Equal(128, RenderStats.Last.SpriteUploadBytes);
        Assert.Equal(1, RenderStats.Last.SpriteAtlasSwitches);
    }

    [Fact]
    public void SpriteAtlas_BakeCopiesPixelsAndKeepsSourceGuidsAcrossRepack()
    {
        var atlas = new SpriteAtlas { Padding = 1, MaxSize = 64 };
        atlas.AssetID = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var red = Fill(2, 2, new Color32(255, 0, 0, 255));
        var blue = Fill(2, 2, new Color32(0, 0, 255, 255));
        var green = Fill(2, 2, new Color32(0, 255, 0, 255));

        atlas.Bake(
        [
            new SpriteAtlasBakeSource("hero/idle", 2, 2, red, new Float2(0.5f, 0.5f), 100f, default),
            new SpriteAtlasBakeSource("hero/run", 2, 2, blue, new Float2(0.5f, 0.5f), 100f, default, green, "_NormalMap"),
        ]);

        Sprite idle = atlas.GetSprite("hero/idle")!;
        Sprite run = atlas.GetSprite("hero/run")!;
        Assert.NotNull(idle);
        Assert.NotNull(run);
        Assert.Equal(SpriteAtlas.DeriveSpriteGuid(atlas.AssetID, "hero/idle"), idle.AssetID);
        Assert.Equal(SpriteAtlas.DeriveSpriteGuid(atlas.AssetID, "hero/run"), run.AssetID);
        Assert.Equal(new SpriteRect(atlas.FindPackedRect("hero/idle").X, atlas.FindPackedRect("hero/idle").Y, 2, 2), idle.Rect);
        Assert.Same(idle.Texture.Res, run.Texture.Res);

        Color32 idlePixel = atlas.SamplePacked(atlas.FindPackedRect("hero/idle").X, atlas.FindPackedRect("hero/idle").Y);
        Color32 runPixel = atlas.SamplePacked(atlas.FindPackedRect("hero/run").X, atlas.FindPackedRect("hero/run").Y);
        Assert.Equal(255, idlePixel.R);
        Assert.Equal(255, runPixel.B);
        Assert.Equal(255, atlas.SamplePackedSecondary(atlas.FindPackedRect("hero/run").X, atlas.FindPackedRect("hero/run").Y).G);
        Assert.True(run.SecondaryTextures.ContainsKey("_NormalMap"));

        Guid idleId = idle.AssetID;
        Guid runId = run.AssetID;
        atlas.Bake(
        [
            new SpriteAtlasBakeSource("hero/idle", 2, 2, red, new Float2(0.5f, 0.5f), 100f, default),
            new SpriteAtlasBakeSource("hero/run", 2, 2, blue, new Float2(0.5f, 0.5f), 100f, default, green, "_NormalMap"),
            new SpriteAtlasBakeSource("fx/spark", 2, 2, Fill(2, 2, new Color32(255, 255, 0, 255)), new Float2(0.5f, 0.5f), 100f, default),
        ]);

        Assert.Same(idle, atlas.GetSprite("hero/idle"));
        Assert.Same(run, atlas.GetSprite("hero/run"));
        Assert.Equal(idleId, atlas.GetSprite("hero/idle")!.AssetID);
        Assert.Equal(runId, atlas.GetSprite("hero/run")!.AssetID);
        Assert.Equal(SpriteAtlas.DeriveSpriteGuid(atlas.AssetID, "fx/spark"), atlas.GetSprite("fx/spark")!.AssetID);
    }

    [Fact]
    public void SpriteBatchCollector_SortsAndMergesAdjacentMatchingMaterialAtlas()
    {
        var collector = new SpriteBatchCollector();
        collector.Begin();
        collector.Add(new SpriteInstanceRecord(rendererId: 30, sortingLayer: 1, sortingOrder: 0, distance: 0f, materialId: 1, atlasId: 1));
        collector.Add(new SpriteInstanceRecord(rendererId: 10, sortingLayer: 0, sortingOrder: 0, distance: 4f, materialId: 1, atlasId: 1));
        collector.Add(new SpriteInstanceRecord(rendererId: 20, sortingLayer: 0, sortingOrder: 1, distance: 1f, materialId: 1, atlasId: 1));
        collector.Add(new SpriteInstanceRecord(rendererId: 40, sortingLayer: 1, sortingOrder: 1, distance: 0f, materialId: 1, atlasId: 2));
        collector.SortAndBatch();

        Assert.Equal(4, collector.InstanceCount);
        Assert.Equal(10, collector.Sorted[0].RendererId);
        Assert.Equal(20, collector.Sorted[1].RendererId);
        Assert.Equal(30, collector.Sorted[2].RendererId);
        Assert.Equal(40, collector.Sorted[3].RendererId);
        Assert.Equal(2, collector.BatchCount);
        Assert.Equal(0, collector.Batches[0].Start);
        Assert.Equal(3, collector.Batches[0].Count);
        Assert.Equal(3, collector.Batches[1].Start);
        Assert.Equal(1, collector.Batches[1].Count);

        collector.Begin();
        collector.Add(new SpriteInstanceRecord(1, 0, 0, 0f, 7, 7));
        collector.SortAndBatch();
        Assert.Equal(1, collector.InstanceCount);
        Assert.Equal(1, collector.BatchCount);
    }

    [Fact]
    public void SpriteRendererRegistry_CollectsEnabledSpritesIntoSharedBatches()
    {
        SpriteRendererRegistry.ResetForTests();
        Scene scene = CreateScene(enable: true);
        var cameraGo = CreateGameObject("Cam");
        scene.Add(cameraGo);
        Camera camera = cameraGo.AddComponent<Camera>();

        var a = CreateSpriteRenderer(scene, "A", sortingLayer: 0);
        var b = CreateSpriteRenderer(scene, "B", sortingLayer: 0);
        CreateSpriteRenderer(scene, "C", sortingLayer: 1);

        var renderables = new List<IRenderable>();
        RenderStats.BeginFrame();
        scene.CollectRenderables(camera, renderables, new List<IRenderableLight>());
        RenderStats.EndFrame();

        Assert.Equal(3, SpriteRendererRegistry.LastInstanceCount);
        Assert.Equal(1, SpriteRendererRegistry.LastBatchCount);
        Assert.Equal(3, RenderStats.Last.SpriteInstances);
        Assert.Equal(1, RenderStats.Last.SpriteBatches);
        Assert.Single(renderables);
        var batch = Assert.IsType<SpriteBatchRenderable>(renderables[0]);
        Assert.Equal(3, batch.InstanceCount);

        b.Enabled = false;
        renderables.Clear();
        RenderStats.BeginFrame();
        scene.CollectRenderables(camera, renderables, new List<IRenderableLight>());
        RenderStats.EndFrame();
        Assert.Equal(2, SpriteRendererRegistry.LastInstanceCount);
        Assert.Equal(1, SpriteRendererRegistry.LastBatchCount);
        Assert.Single(renderables);
        Assert.Equal(2, Assert.IsType<SpriteBatchRenderable>(renderables[0]).InstanceCount);
    }

    [Fact]
    public void PixelPerfect_GameViewCropsAndSceneViewKeepsFullViewport()
    {
        GameObject go = CreateGameObject("PixelCam");
        var ppc = go.AddComponent<PixelPerfectCamera>();
        ppc.RefResolutionX = 320;
        ppc.RefResolutionY = 180;
        ppc.CropFrame = true;

        PixelPerfectPreview game = ppc.GetPreview(800, 400, sceneView: false);
        Assert.True(game.Cropped);
        Assert.Equal(640, game.Width);
        Assert.Equal(360, game.Height);
        Assert.Equal(80, game.X);
        Assert.Equal(20, game.Y);

        PixelPerfectPreview scene = ppc.GetPreview(800, 400, sceneView: true);
        Assert.False(scene.Cropped);
        Assert.Equal(800, scene.Width);
        Assert.Equal(400, scene.Height);
        Assert.Equal(0, scene.X);
        Assert.Equal(0, scene.Y);
    }

    [Fact]
    public void PixelPerfect_CropFrameShrinksViewportAndStretchDisablesSnap()
    {
        GameObject go = CreateGameObject("PixelCam");
        var ppc = go.AddComponent<PixelPerfectCamera>();
        ppc.AssetsPPU = 16;
        ppc.RefResolutionX = 320;
        ppc.RefResolutionY = 180;
        ppc.CropFrame = true;

        Assert.Equal(2, ppc.ComputeZoom(800, 400));
        Assert.True(ppc.TryGetCroppedViewport(800, 400, out int width, out int height));
        Assert.Equal(640, width);
        Assert.Equal(360, height);

        ppc.CropFrame = false;
        ppc.StretchFill = true;
        Assert.False(ppc.ShouldSnapPosition());
    }

    private static bool RectsOverlap(SpriteAtlasPackedRect a, SpriteAtlasPackedRect b, int padding)
    {
        int ax1 = a.X + a.Width + padding;
        int ay1 = a.Y + a.Height + padding;
        int bx1 = b.X + b.Width + padding;
        int by1 = b.Y + b.Height + padding;
        return a.X < bx1 && ax1 > b.X && a.Y < by1 && ay1 > b.Y;
    }

    private static Color32[] Fill(int width, int height, Color32 color)
    {
        var pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = color;
        return pixels;
    }

    private SpriteRenderer CreateSpriteRenderer(Scene scene, string name, int sortingLayer)
    {
        GameObject go = CreateGameObject(name);
        scene.Add(go);
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.Sprite = new Sprite
        {
            Rect = new SpriteRect(0, 0, 16, 16),
            PixelsPerUnit = 16f,
        };
        renderer.SortingLayer = sortingLayer;
        return renderer;
    }
}
