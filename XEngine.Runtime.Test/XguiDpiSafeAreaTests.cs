// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiDpiSafeAreaTests : RuntimeTestBase
{
    [Fact]
    public void ConstantPhysicalSize_ScalesByDpiOverDefaultSpriteDpi()
    {
        Assert.Equal(2f, CanvasPhysicalScale.ComputeScaleFactor(192f, 96f), 4);
        Assert.Equal(1.5f, CanvasPhysicalScale.ComputeScaleFactor(144f, 96f), 4);
        Assert.Equal(1f, CanvasPhysicalScale.ComputeScaleFactor(96f, 96f), 4);
    }

    [Fact]
    public void ResolveDpi_PrefersOverrideThenContentScaleThenFallback()
    {
        float? previous = CanvasPhysicalScale.DpiOverride;
        try
        {
            CanvasPhysicalScale.DpiOverride = 192f;
            Assert.Equal(192f, CanvasPhysicalScale.ResolveDpi(96f, 1.25f), 4);

            CanvasPhysicalScale.DpiOverride = null;
            Assert.Equal(120f, CanvasPhysicalScale.ResolveDpi(96f, 1.25f), 4);
            Assert.Equal(96f, CanvasPhysicalScale.ResolveDpi(96f, 0f), 4);
        }
        finally
        {
            CanvasPhysicalScale.DpiOverride = previous;
        }
    }

    [Fact]
    public void SafeAreaLayout_InsetsStretchedRectInPlusYUpSpace()
    {
        GameObject panel = CreateGameObject("SafePanel");
        RectTransform rt = panel.EnsureRectTransform();
        rt.Pivot = new Float2(0.5f, 0.5f);

        SafeAreaLayout.Apply(rt, new WindowSafeAreaInsets(40, 80, 40, 20), scaleFactor: 1f, conformX: true, conformY: true);

        Rect computed = rt.ComputeRect(new Rect(0f, 0f, 800f, 450f));
        Assert.Equal(Float2.Zero, rt.AnchorMin);
        Assert.Equal(Float2.One, rt.AnchorMax);
        Assert.Equal(new Float2(-80f, -100f), rt.SizeDelta);
        Assert.Equal(40f, computed.Min.X, 3);
        Assert.Equal(20f, computed.Min.Y, 3);
        Assert.Equal(760f, computed.Max.X, 3);
        Assert.Equal(370f, computed.Max.Y, 3);
        Assert.Equal(new Float2(720f, 350f), computed.Size);
    }

    [Fact]
    public void SafeAreaLayout_DividesFramebufferInsetsByCanvasScale()
    {
        GameObject panel = CreateGameObject("ScaledSafePanel");
        RectTransform rt = panel.EnsureRectTransform();
        rt.Pivot = new Float2(0.5f, 0.5f);

        SafeAreaLayout.Apply(rt, new WindowSafeAreaInsets(40, 80, 40, 20), scaleFactor: 2f, conformX: true, conformY: true);
        Rect computed = rt.ComputeRect(new Rect(0f, 0f, 400f, 225f));
        Assert.Equal(new Float2(-40f, -50f), rt.SizeDelta);
        Assert.Equal(new Float2(360f, 175f), computed.Size);
    }

    [Fact]
    public void DpiAndSafeArea_AcceptanceScreenshot()
    {
        GameObject panel = CreateGameObject("ShotPanel");
        RectTransform rt = panel.EnsureRectTransform();
        rt.Pivot = new Float2(0.5f, 0.5f);
        SafeAreaLayout.Apply(rt, new WindowSafeAreaInsets(40, 80, 40, 20), 1f, true, true);
        Rect safe = rt.ComputeRect(new Rect(0f, 0f, 800f, 450f));
        float physicalScale = CanvasPhysicalScale.ComputeScaleFactor(192f, 96f);

        var board = new XGuiAcceptanceScreenshot.Canvas(860, 400, 18, 19, 26);
        board.Fill(24, 24, 812, 352, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 352, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI DPI AND SAFE AREA", 230, 232, 240, 2);
        board.Text(40, 68, "CONSTANT PHYSICAL SCALE PLUS FITTER", 140, 146, 160, 1);

        board.Stroke(48, 110, 360, 140, 90, 94, 110, 2);
        board.Fill(50, 112, 356, 136, 80, 170, 220, 255);
        board.Text(64, 128, "PHYSICAL SCALE", 18, 19, 26, 2);
        board.Text(64, 164, "DPI 192 / SPRITE 96", 18, 19, 26, 1);
        board.Text(64, 192, $"SCALE {physicalScale:0}X", 18, 19, 26, 1);

        board.Stroke(452, 110, 360, 230, 90, 94, 110, 2);
        board.Fill(454, 112, 356, 226, 40, 44, 56, 255);
        board.Fill(454 + 18, 112 + 36, 356 - 36, 226 - 54, 90, 200, 150, 255);
        board.Text(468, 128, "SAFE AREA", 210, 214, 220, 2);
        board.Text(468, 300, $"{(int)safe.Size.X}X{(int)safe.Size.Y} INSETS 40 80 40 20", 180, 186, 196, 1);

        board.Text(48, 340, "WINDOW SAFE AREA DRIVES HUD INSETS", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-dpi-safe-area", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
        Assert.Equal(2f, physicalScale, 4);
        Assert.Equal(new Float2(720f, 350f), safe.Size);
    }
}
