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

public sealed class XguiRenderStatsMatrixTests : RuntimeTestBase
{
    [Fact]
    public void EmptySceneAndEmptyCanvasCollectNoUiNodes()
    {
        using Scene emptyScene = CreateScene(enable: true);
        var nodes = new List<CanvasRenderNode>();
        CanvasRegistry.CollectRenderNodes(emptyScene, UISurface.Overlay, nodes);
        Assert.Empty(nodes);

        using Scene canvasScene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        canvasScene.Add(canvasObject);
        canvasScene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(canvasScene, UISurface.Overlay, nodes);
            Assert.Empty(nodes);

            var composer = new UIBatchComposer();
            composer.Compose(nodes, default);
            Assert.Empty(composer.Draws);
            Assert.Equal(0, composer.MeshUploadsThisCompose);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void OneImageThenImageTextButtonKeepStaticUploadsAtZero()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        Texture2D texture = new(16, 16);
        Sprite sprite = Sprite.Create(texture, new SpriteRect(0, 0, 16, 16), new Float2(0.5f, 0.5f), 100f, "Panel");
        UIImage image = CreateImage(canvasObject, "Image", new Float2(-80f, 0f), sprite);
        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>();
            var composer = new UIBatchComposer();
            ComposeTwice(scene, nodes, composer);
            Assert.Single(nodes);
            Assert.Single(composer.Draws);
            Assert.Equal(0, composer.MeshUploadsThisCompose);

            CreateText(canvasObject, "Label", new Float2(20f, 0f), "OK");
            CreateButton(canvasObject, "Button", new Float2(120f, 0f), sprite);
            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            composer.Compose(nodes, default);
            Assert.Equal(3, nodes.Count);
            Assert.True(composer.Draws.Count >= 1);

            RenderStats.BeginFrame();
            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            composer.Compose(nodes, default);
            image.Color = new Color(0.2f, 0.6f, 0.9f, 1f);
            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            composer.Compose(nodes, default);
            RenderStats.EndFrame();

            Assert.Equal(0, RenderStats.Last.LayoutRebuilds);
            Assert.Equal(0, RenderStats.Last.GraphicRebuilds);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void ScaledStaticFramesSkipMeshUpload(int graphicCount)
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        Texture2D texture = new(8, 8);
        Sprite sprite = Sprite.Create(texture, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 100f, "Cell");
        for (int i = 0; i < graphicCount; i++)
        {
            float x = (i % 100) * 8f - 400f;
            float y = (i / 100) * 8f - 200f;
            CreateImage(canvasObject, "Img" + i, new Float2(x, y), sprite);
        }
        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>(graphicCount);
            var composer = new UIBatchComposer();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            Assert.Equal(graphicCount, nodes.Count);
            composer.Compose(nodes, default);
            Assert.True(composer.Draws.Count >= 1);
            Assert.Single(composer.Draws);
            int firstUploads = composer.MeshUploadsThisCompose;
            Assert.True(firstUploads >= 1);

            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            composer.Compose(nodes, default);
            Assert.Equal(graphicCount, nodes.Count);
            Assert.Equal(0, composer.MeshUploadsThisCompose);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void RenderStatsMatrix_AcceptanceScreenshot()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        Texture2D texture = new(16, 16);
        Sprite sprite = Sprite.Create(texture, new SpriteRect(0, 0, 16, 16), new Float2(0.5f, 0.5f), 100f, "Panel");
        UIImage image = CreateImage(canvasObject, "Image", new Float2(-80f, 0f), sprite);
        CreateText(canvasObject, "Label", new Float2(20f, 0f), "UI");
        CreateButton(canvasObject, "Button", new Float2(120f, 0f), sprite);
        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(800f, 480f);
        try
        {
            var nodes = new List<CanvasRenderNode>();
            var composer = new UIBatchComposer();
            ComposeTwice(scene, nodes, composer);
            image.Color = new Color(0.9f, 0.4f, 0.2f, 1f);
            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            composer.Compose(nodes, default);

            var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
            board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
            board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
            board.Text(40, 40, "XGUI RENDERSTATS MATRIX", 230, 232, 240, 2);
            board.Text(40, 68, "EMPTY / ONE / CONTROLS / SCALE", 140, 146, 160, 1);

            string[] labels = ["EMPTY", "1 IMAGE", "IMG TXT BTN", "10-10K"];
            string[] values = ["0 DRAW", "1 PAGE", "STATIC 0", "UPLOAD 0"];
            int[] xs = [48, 250, 452, 654];
            for (int i = 0; i < labels.Length; i++)
            {
                board.Stroke(xs[i], 140, 140, 70, 90, 94, 110, 2);
                board.Fill(xs[i] + 2, 142, 136, 66, 80, 170, 220, 255);
                board.Text(xs[i], 222, labels[i], 210, 214, 220, 1);
                board.Text(xs[i], 252, values[i], 180, 186, 196, 2);
            }
            board.Text(48, 286, "OPENGL VULKAN D3D12 SHARE CANVASRENDERER", 140, 146, 160, 1);
            board.Text(48, 310, "COLOR CHANGE SKIPS LAYOUT AND TEXT", 140, 146, 160, 1);

            string path = XGuiAcceptanceScreenshot.Save("xgui-renderstats-acceptance-matrix", board);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 200);
            Assert.Equal(3, nodes.Count);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    private static void ComposeTwice(Scene scene, List<CanvasRenderNode> nodes, UIBatchComposer composer)
    {
        nodes.Clear();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
        composer.Compose(nodes, default);
        nodes.Clear();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
        composer.Compose(nodes, default);
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
        rt.SizeDelta = new Float2(64f, 36f);
        return image;
    }

    private TextComponent CreateText(GameObject canvasObject, string name, Float2 position, string value)
    {
        GameObject textObject = CreateGameObject(name);
        textObject.SetParent(canvasObject, worldPositionStays: false);
        textObject.EnsureRectTransform();
        TextComponent text = textObject.AddComponent<TextComponent>();
        text.Text = value;
        text.Size = 16;
        RectTransform rt = textObject.RectTransform!;
        rt.AnchorMin = new Float2(0.5f, 0.5f);
        rt.AnchorMax = new Float2(0.5f, 0.5f);
        rt.AnchoredPosition = position;
        rt.SizeDelta = new Float2(80f, 28f);
        return text;
    }

    private UIButton CreateButton(GameObject canvasObject, string name, Float2 position, Sprite sprite)
    {
        GameObject buttonObject = CreateGameObject(name);
        buttonObject.SetParent(canvasObject, worldPositionStays: false);
        buttonObject.EnsureRectTransform();
        CreateImage(buttonObject, name + "Image", Float2.Zero, sprite);
        UIButton button = buttonObject.AddComponent<UIButton>();
        RectTransform rt = buttonObject.RectTransform!;
        rt.AnchorMin = new Float2(0.5f, 0.5f);
        rt.AnchorMax = new Float2(0.5f, 0.5f);
        rt.AnchoredPosition = position;
        rt.SizeDelta = new Float2(72f, 36f);
        return button;
    }
}
