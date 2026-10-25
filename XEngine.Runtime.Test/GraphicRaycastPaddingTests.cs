// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class GraphicRaycastPaddingTests : RuntimeTestBase
{
    [Fact]
    public void ContainsLocalPoint_ExpandsAndShrinksPivotCenteredRect()
    {
        Float2 size = new(40f, 40f);
        Float2 pivot = new(0.5f, 0.5f);
        Assert.False(GraphicRaycastPadding.ContainsLocalPoint(size, pivot, default, new Float2(-21f, 0f)));
        Assert.True(GraphicRaycastPadding.ContainsLocalPoint(size, pivot, new Float4(12f, 0f, 0f, 0f), new Float2(-21f, 0f)));
        Assert.True(GraphicRaycastPadding.ContainsLocalPoint(size, pivot, default, new Float2(-10f, 0f)));
        Assert.False(GraphicRaycastPadding.ContainsLocalPoint(size, pivot, new Float4(-12f, 0f, 0f, 0f), new Float2(-10f, 0f)));
    }

    [Fact]
    public void UIImage_PositivePaddingCatchesPointerOutsideVisualRect()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = new Float2(0.5f, 0.5f);
        rt.AnchorMax = new Float2(0.5f, 0.5f);
        rt.SizeDelta = new Float2(40f, 40f);
        UIImage image = imageObject.AddComponent<UIImage>();

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
        try
        {
            Float2 outsideLeft = new(24f, 50f);
            Assert.False(UIRaycaster.TryPick(scene, outsideLeft, new Float2(100f, 100f), out _));

            image.RaycastPadding = new Float4(20f, 0f, 0f, 0f);
            Assert.True(UIRaycaster.TryPick(scene, outsideLeft, new Float2(100f, 100f), out UIRaycaster.Hit hit));
            Assert.Same(imageObject, hit.GameObject);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void RaycastPadding_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI RAYCAST PADDING", 230, 232, 240, 2);
        board.Text(40, 68, "POSITIVE PADDING EXPANDS THE HIT RECT", 140, 146, 160, 1);

        board.Stroke(80, 120, 280, 160, 90, 200, 150, 2);
        board.Fill(140, 140, 160, 120, 80, 170, 220, 255);
        board.Text(96, 128, "HIT  LEFT 20", 180, 186, 196, 1);
        board.Text(156, 180, "VISUAL", 18, 19, 26, 2);

        board.Fill(500, 140, 160, 120, 80, 170, 220, 255);
        board.Stroke(540, 160, 80, 80, 230, 120, 90, 2);
        board.Text(516, 116, "NEGATIVE INSET", 230, 120, 90, 1);
        board.Text(516, 180, "VISUAL", 18, 19, 26, 2);

        board.Text(40, 300, "GRAPHIC AND UIIMAGE SHARE THE PADDING", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-raycast-padding", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }
}
