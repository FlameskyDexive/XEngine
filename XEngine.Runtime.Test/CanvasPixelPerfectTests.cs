// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CanvasPixelPerfectTests : RuntimeTestBase
{
    [Fact]
    public void Snap_RoundsDesignSpaceOntoFramebufferPixels()
    {
        Assert.Equal(10f, CanvasPixelSnap.Snap(10.4f, 1f), 4);
        Assert.Equal(11f, CanvasPixelSnap.Snap(10.6f, 1f), 4);
        Assert.Equal(16f / 1.5f, CanvasPixelSnap.Snap(10.4f, 1.5f), 4);
        Assert.False(CanvasPixelSnap.Applies(pixelPerfect: true, worldSpace: true));
        Assert.True(CanvasPixelSnap.Applies(pixelPerfect: true, worldSpace: false));
    }

    [Fact]
    public void PixelAdjustPoint_SnapsOverlayAndIgnoresWorldSpace()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        canvas.UIScaleMode = ScaleMode.ConstantPixelSize;
        canvas.ScaleFactor = 1.5f;
        canvas.PixelPerfect = true;
        scene.Add(canvasObject);
        scene.Enable();

        Assert.Equal(16f / 1.5f, canvas.PixelAdjustPoint(new Float2(10.4f, 0f)).X, 4);

        canvas.RenderMode = RenderMode.WorldSpace;
        Assert.False(canvas.PixelPerfectActive);
        Assert.Equal(10.4f, canvas.PixelAdjustPoint(new Float2(10.4f, 0f)).X, 4);
    }

    [Fact]
    public void OverlayModelAndVerts_LandOnWholePixels()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        canvas.UIScaleMode = ScaleMode.ConstantPixelSize;
        canvas.ScaleFactor = 1.5f;
        canvas.PixelPerfect = true;

        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.Zero;
        rt.Pivot = new Float2(0.5f, 0.5f);
        rt.SizeDelta = new Float2(41f, 40f);
        rt.AnchoredPosition = new Float2(10.4f, 20.3f);
        UIImage image = imageObject.AddComponent<UIImage>();
        image.Type = ImageType.Simple;
        using Texture2D texture = new(8, 8);
        image.Sprite = Sprite.Create(texture, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 100f, "Chip");

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.EditorWorldSpaceOverride = false;
        canvas.RenderMode = RenderMode.ScreenSpaceOverlay;
        canvas.PixelPerfect = true;
        canvas.ScaleFactor = 1.5f;
        GameCanvas.ScreenSizeOverride = new Float2(150f, 150f);
        try
        {
            Assert.True(canvas.PixelPerfectActive);
            Assert.Equal(1.5f, canvas.ScaleFactor, 4);
            canvas.RebuildIfDirty();
            Assert.True(canvas.PixelPerfectActive);
            Assert.Equal(1.5f, canvas.ScaleFactor, 4);
            Mesh mesh = Assert.IsType<Mesh>(image.CachedMesh);
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                Float3 vertex = mesh.Vertices[i];
                Assert.Equal(CanvasPixelSnap.Snap(vertex.X, 1.5f), vertex.X, 4);
                Assert.Equal(CanvasPixelSnap.Snap(vertex.Y, 1.5f), vertex.Y, 4);
            }

            Rect cr = rt.ComputedRect;
            float pivotX = cr.Min.X + rt.Pivot.X * cr.Size.X;
            float pivotY = cr.Min.Y + rt.Pivot.Y * cr.Size.Y;
            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            CanvasRenderNode node = Assert.Single(nodes);
            float expectedX = CanvasPixelSnap.Snap(pivotX, 1.5f) * 1.5f;
            float expectedY = CanvasPixelSnap.Snap(pivotY, 1.5f) * 1.5f;
            Assert.Equal(expectedX, node.Model.Translation.X, 3);
            Assert.Equal(expectedY, node.Model.Translation.Y, 3);
            Assert.Equal(Maths.Round(expectedX), expectedX, 3);
            Assert.Equal(Maths.Round(expectedY), expectedY, 3);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
            GameCanvas.EditorWorldSpaceOverride = false;
        }
    }

    [Fact]
    public void PixelPerfect_AcceptanceScreenshot()
    {
        float snapped = CanvasPixelSnap.Snap(10.4f, 1.5f);
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI PIXEL PERFECT", 230, 232, 240, 2);
        board.Text(40, 68, "OVERLAY UI SNAPS TO WHOLE PIXELS", 140, 146, 160, 1);

        board.Stroke(80, 120, 280, 140, 230, 120, 90, 2);
        board.Fill(92, 132, 256, 116, 80, 170, 220, 255);
        board.Text(96, 128, "OFF  15.6 SCREEN", 230, 120, 90, 1);
        board.Text(156, 176, "CRAWL", 18, 19, 26, 2);

        board.Stroke(500, 116, 280, 148, 90, 200, 150, 2);
        board.Fill(500, 132, 280, 116, 80, 170, 220, 255);
        board.Text(516, 116, "ON  LANDS ON 16", 90, 200, 150, 1);
        board.Text(580, 176, "SNAP", 18, 19, 26, 2);

        board.Text(40, 300, "SCALE 1.5  POINT 10.4 TO 10.667", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-pixel-perfect", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
        Assert.Equal(16f / 1.5f, snapped, 4);
    }
}
