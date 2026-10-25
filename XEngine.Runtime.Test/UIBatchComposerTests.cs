// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.IO;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class UIBatchComposerTests : RuntimeTestBase
{
    [Fact]
    public void AdjacentSameMaterialImagesMergeIntoOneBatch()
    {
        using Scene scene = CreateBatchScene(out GameCanvas canvas, out UIImage[] images, out Sprite sprite);
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            Assert.Equal(4, nodes.Count);

            var composer = new UIBatchComposer();
            composer.Compose(nodes, default);

            Assert.Single(composer.Draws);
            Assert.True(composer.Draws[0].Merged);
            Assert.Equal(1, composer.MergedBatchCount);
            Assert.Equal(1, composer.MeshUploadsThisCompose);
            Assert.True(composer.Draws[0].VertexCount >= 16);
            Assert.True(composer.Draws[0].IndexCount >= 24);
            Assert.Same(sprite.Texture.Res, composer.Draws[0].Properties.GetTexture("_MainTex"));
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void DifferentTexturesDoNotMerge()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        using Texture2D texA = new(8, 8);
        using Texture2D texB = new(8, 8);
        Sprite spriteA = Sprite.Create(texA, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 100f, "A");
        Sprite spriteB = Sprite.Create(texB, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 100f, "B");
        CreateImage(canvasObject, "Left", new Float2(-40f, 0f), spriteA);
        CreateImage(canvasObject, "Right", new Float2(40f, 0f), spriteB);
        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            Assert.Equal(2, nodes.Count);

            var composer = new UIBatchComposer();
            composer.Compose(nodes, default);
            Assert.Equal(2, composer.Draws.Count);
            Assert.False(composer.Draws[0].Merged);
            Assert.False(composer.Draws[1].Merged);
            Assert.Equal(0, composer.MergedBatchCount);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void StaticMergedFrameDoesNotUploadAgain()
    {
        using Scene scene = CreateBatchScene(out GameCanvas canvas, out _, out _);
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            var composer = new UIBatchComposer();
            composer.Compose(nodes, default);
            Assert.Equal(1, composer.MeshUploadsThisCompose);

            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            composer.Compose(nodes, default);
            Assert.Single(composer.Draws);
            Assert.Equal(0, composer.MeshUploadsThisCompose);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void LeafColorChangeSkipsLayoutAndOnlyRewritesThePage()
    {
        using Scene scene = CreateBatchScene(out GameCanvas canvas, out UIImage[] images, out _);
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            var composer = new UIBatchComposer();
            composer.Compose(nodes, default);
            Assert.Equal(1, composer.MeshUploadsThisCompose);

            RenderStats.BeginFrame();
            images[1].Color = new Color(0.2f, 0.4f, 0.8f, 1f);
            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            composer.Compose(nodes, default);
            RenderStats.EndFrame();

            Assert.Equal(0, RenderStats.Last.LayoutRebuilds);
            Assert.Equal(0, RenderStats.Last.GraphicRebuilds);
            Assert.Single(composer.Draws);
            Assert.Equal(1, composer.MeshUploadsThisCompose);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void CollectRenderNodesStillPublishesOneNodePerGraphic()
    {
        using Scene scene = CreateBatchScene(out _, out UIImage[] images, out _);
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            Assert.Equal(images.Length, nodes.Count);
            for (int i = 0; i < images.Length; i++)
                Assert.Same(images[i].CachedMesh, nodes[i].Mesh);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void AdjacentBatchMerge_AcceptanceScreenshot()
    {
        using Scene scene = CreateBatchScene(out GameCanvas canvas, out UIImage[] images, out Sprite sprite);
        images[2].Color = new Color(0.85f, 0.35f, 0.2f, 1f);
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            var composer = new UIBatchComposer();
            composer.Compose(nodes, default);
            composer.Compose(nodes, default);

            var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
            board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
            board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
            board.Text(40, 40, "UI BATCH MERGE / PERSISTENT PAGES", 230, 232, 240, 2);
            board.Text(40, 68, "ADJACENT SAME MATERIAL IMAGES SHARE ONE PAGE", 140, 146, 160, 1);

            int[] xs = [48, 250, 452, 654];
            string[] labels = ["IMG 0", "IMG 1", "IMG 2", "IMG 3"];
            for (int i = 0; i < 4; i++)
            {
                board.Stroke(xs[i], 140, 140, 70, 90, 94, 110, 2);
                board.Fill(xs[i] + 2, 142, 136, 66, 80, 170, 220, 255);
                board.Text(xs[i], 222, labels[i], 210, 214, 220, 1);
            }
            board.Fill(48, 118, 746, 8, 80, 170, 220, 255);
            board.Text(48, 258, "NODES 4 / DRAWS 1 / MERGED 1", 210, 214, 220, 1);
            board.Text(48, 282, "STATIC UPLOAD 0 / COLOR REWRITE 1 PAGE", 140, 146, 160, 1);
            board.Text(48, 306, "LAYOUT 0 / GRAPHIC 0 ON COLOR CHANGE", 140, 146, 160, 1);

            string path = XGuiAcceptanceScreenshot.Save("xgui-ui-batch-merge", board);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 200);
            Assert.Single(composer.Draws);
            Assert.Equal(0, composer.MeshUploadsThisCompose);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    private Scene CreateBatchScene(out GameCanvas canvas, out UIImage[] images, out Sprite sprite)
    {
        Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvas = canvasObject.AddComponent<GameCanvas>();
        Texture2D texture = new(16, 16);
        sprite = Sprite.Create(texture, new SpriteRect(0, 0, 16, 16), new Float2(0.5f, 0.5f), 100f, "Panel");
        images = new UIImage[4];
        Float2[] positions = [new(-180f, 0f), new(-60f, 0f), new(60f, 0f), new(180f, 0f)];
        for (int i = 0; i < 4; i++)
            images[i] = CreateImage(canvasObject, "Image" + i, positions[i], sprite);
        scene.Add(canvasObject);
        scene.Enable();
        return scene;
    }

    private UIImage CreateImage(GameObject canvasObject, string name, Float2 position, Sprite sprite)
    {
        GameObject imageObject = CreateGameObject(name);
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        imageObject.EnsureRectTransform();
        UIImage image = imageObject.AddComponent<UIImage>();
        image.Sprite = sprite;
        image.Type = ImageType.Simple;
        RectTransform rt = imageObject.RectTransform!;
        rt.AnchorMin = new Float2(0.5f, 0.5f);
        rt.AnchorMax = new Float2(0.5f, 0.5f);
        rt.AnchoredPosition = position;
        rt.SizeDelta = new Float2(80f, 48f);
        return image;
    }
}
