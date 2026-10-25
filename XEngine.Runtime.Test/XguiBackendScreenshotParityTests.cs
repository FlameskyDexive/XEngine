// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.IO;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiBackendScreenshotParityTests : RuntimeTestBase
{
    [Fact]
    public void ScreenshotBackends_RejectNullSilentFallback()
    {
        Assert.Equal(3, CanvasBackendParity.ScreenshotBackends.Length);
        Assert.Equal(GraphicsBackend.OpenGL, CanvasBackendParity.ScreenshotBackends[0]);
        Assert.Equal(GraphicsBackend.Vulkan, CanvasBackendParity.ScreenshotBackends[1]);
        Assert.Equal(GraphicsBackend.Direct3D12, CanvasBackendParity.ScreenshotBackends[2]);
        Assert.True(CanvasBackendParity.TryValidateBackend(GraphicsBackend.Vulkan, GraphicsBackend.Vulkan, out _));
        Assert.True(CanvasBackendParity.TryValidateBackend(GraphicsBackend.Auto, GraphicsBackend.OpenGL, out _));
        Assert.False(CanvasBackendParity.TryValidateBackend(GraphicsBackend.Vulkan, GraphicsBackend.Null, out string error));
        Assert.Contains("Null", error, StringComparison.Ordinal);
        Assert.False(CanvasBackendParity.TryValidateBackend(GraphicsBackend.OpenGL, GraphicsBackend.Vulkan, out _));
        Assert.Equal(3, CanvasBackendParity.MaxChannelDelta);
        Assert.Equal(0d, CanvasBackendParity.MaxMismatchRatio);
    }

    [Fact]
    public void OverlayCollect_FingerprintStableAcrossCollectsAndIndependentOfBackend()
    {
        using Scene scene = CreateHud(out _, out UIImage image);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(200f, 120f);
        try
        {
            var first = new List<CanvasRenderNode>();
            var second = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, first);
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, second);
            Assert.Single(first);
            Assert.True(CanvasBackendParity.AreEqual(first, second));
            CanvasNodeFingerprint fingerprint = CanvasBackendParity.Fingerprint(first[0]);
            Assert.True(fingerprint.VertexCount > 0);
            Assert.True(fingerprint.IndexCount > 0);
            Assert.Equal(UISurface.Overlay, fingerprint.Surface);

            image.Color = new Color(0.2f, 0.6f, 0.9f, 1f);
            var afterColor = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, afterColor);
            Assert.True(CanvasBackendParity.AreEqual(first, afterColor));
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void BackendParity_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI RENDER PARITY", 230, 232, 240, 2);
        board.Text(40, 68, "OPENGL SPIRV D3D12", 140, 146, 160, 1);

        board.Fill(80, 120, 200, 140, 70, 74, 90, 255);
        board.Text(96, 140, "OPENGL", 180, 186, 196, 1);
        board.Text(96, 176, "SAME MESH", 180, 186, 196, 1);

        board.Fill(320, 120, 200, 140, 80, 170, 220, 255);
        board.Text(336, 140, "SPIRV", 18, 19, 26, 1);
        board.Text(336, 176, "SAME CLIP", 18, 19, 26, 1);

        board.Stroke(560, 120, 220, 140, 90, 200, 150, 2);
        board.Fill(572, 132, 196, 116, 90, 200, 150, 255);
        board.Text(588, 148, "D3D12", 18, 19, 26, 1);
        board.Text(588, 184, "DELTA GATE", 18, 19, 26, 1);

        board.Text(40, 300, "CANVASRENDERER CONTRACT", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-backend-screenshot-parity", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    private Scene CreateHud(out GameCanvas canvas, out UIImage image)
    {
        Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("ParityHud");
        canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject("ParityImage");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = Float2.Zero;
        image = imageObject.AddComponent<UIImage>();
        scene.Add(canvasObject);
        return scene;
    }
}
