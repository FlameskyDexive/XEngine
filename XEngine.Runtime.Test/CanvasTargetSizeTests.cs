// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CanvasTargetSizeTests : RuntimeTestBase
{
    [Fact]
    public void Resolve_PrefersOverrideThenRenderTargetThenFramebuffer()
    {
        Assert.Equal(new Float2(256f, 128f),
            CanvasTargetSize.Resolve(new Float2(256f, 128f), new Float2(64f, 64f), new Float2(1920f, 1080f), 0));
        Assert.Equal(new Float2(64f, 48f),
            CanvasTargetSize.Resolve(null, new Float2(64f, 48f), new Float2(1920f, 1080f), 0));
        Assert.Equal(new Float2(1920f, 1080f),
            CanvasTargetSize.Resolve(null, null, new Float2(1920f, 1080f), 0));
        Assert.Equal(new Float2(CanvasTargetSize.FallbackWidth, CanvasTargetSize.FallbackHeight),
            CanvasTargetSize.Resolve(null, null, Float2.Zero, 0));
    }

    [Fact]
    public void ApplyRotation_SwapsAxesOn90And270()
    {
        Float2 landscape = new(800f, 450f);
        Assert.Equal(new Float2(450f, 800f), CanvasTargetSize.ApplyRotation(landscape, 90));
        Assert.Equal(new Float2(450f, 800f), CanvasTargetSize.ApplyRotation(landscape, 270));
        Assert.Equal(landscape, CanvasTargetSize.ApplyRotation(landscape, 0));
        Assert.Equal(landscape, CanvasTargetSize.ApplyRotation(landscape, 180));
        Assert.Equal(90, CanvasTargetSize.NormalizeDegrees(-270));
    }

    [Fact]
    public void OverlayCanvas_RelayoutsWhenRenderTargetRotates()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        canvas.UIScaleMode = ScaleMode.ScaleWithScreenSize;
        canvas.ReferenceResolution = new Float2(1280f, 720f);
        canvas.MatchWidthOrHeight = 0f;
        scene.Add(canvasObject);
        scene.Enable();

        GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
        try
        {
            canvas.RebuildIfDirty();
            float landscapeScale = canvas.ScaleFactor;
            Float2 landscapeRoot = canvas.RootRect.Size;
            Assert.Equal(800f / 1280f, landscapeScale, 4);
            Assert.Equal(1280f, landscapeRoot.X, 3);

            GameCanvas.ScreenSizeOverride = new Float2(450f, 800f);
            canvas.RebuildIfDirty();
            float portraitScale = canvas.ScaleFactor;
            Float2 portraitRoot = canvas.RootRect.Size;
            Assert.Equal(450f / 1280f, portraitScale, 4);
            Assert.NotEqual(landscapeScale, portraitScale);
            Assert.True(portraitRoot.Y > landscapeRoot.Y);

            int? previousRotation = CanvasTargetSize.RotationOverride;
            try
            {
                GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
                CanvasTargetSize.RotationOverride = 90;
                canvas.RebuildIfDirty();
                Assert.Equal(450f / 1280f, canvas.ScaleFactor, 4);
                Assert.Equal(portraitRoot.Y, canvas.RootRect.Size.Y, 3);
            }
            finally
            {
                CanvasTargetSize.RotationOverride = previousRotation;
            }
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
            CanvasTargetSize.RotationOverride = null;
        }
    }

    [Fact]
    public void RenderTargetAndRotation_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 400, 18, 19, 26);
        board.Fill(24, 24, 812, 352, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 352, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI RENDER TARGET", 230, 232, 240, 2);
        board.Text(40, 68, "RT SIZE AND WINDOW ROTATION", 140, 146, 160, 1);

        board.Stroke(48, 110, 360, 200, 90, 200, 150, 2);
        board.Fill(50, 112, 356, 196, 80, 170, 220, 255);
        board.Text(64, 128, "LANDSCAPE", 18, 19, 26, 2);
        board.Text(64, 176, "800 X 450", 18, 19, 26, 2);
        board.Text(64, 220, "CAMERA RT OR OVERRIDE", 18, 19, 26, 1);

        board.Stroke(452, 110, 220, 280, 230, 120, 90, 2);
        board.Fill(454, 112, 216, 276, 90, 200, 150, 255);
        board.Text(468, 128, "PORTRAIT", 18, 19, 26, 2);
        board.Text(468, 176, "450 X 800", 18, 19, 26, 2);
        board.Text(468, 300, "90 DEG SWAP", 18, 19, 26, 1);

        board.Text(48, 340, "SCALE WITH SCREEN FOLLOWS THE SURFACE", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-render-texture-rotation", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
        Assert.Equal(new Float2(450f, 800f), CanvasTargetSize.ApplyRotation(new Float2(800f, 450f), 90));
    }
}
