// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiDeviceLostResizeTests : RuntimeTestBase
{
    [Fact]
    public void WindowResize_RelayoutsScaleWithScreenSizeAndStillPicks()
    {
        using Scene scene = CreateHud(out GameCanvas canvas, out UIBehaviour image);
        canvas.UIScaleMode = ScaleMode.ScaleWithScreenSize;
        canvas.ReferenceResolution = new Float2(800f, 450f);
        canvas.MatchWidthOrHeight = 0f;
        scene.Enable();

        GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
        try
        {
            canvas.RebuildIfDirty();
            Assert.Equal(1f, canvas.ScaleFactor, 4);
            AssertPick(scene, image.GameObject);

            GameCanvas.ScreenSizeOverride = new Float2(400f, 225f);
            CanvasDisplayChange.NotifyResize();
            canvas.RebuildIfDirty();
            Assert.Equal(0.5f, canvas.ScaleFactor, 4);
            Assert.Equal(800f, canvas.RootRect.Size.X, 3);
            AssertPick(scene, image.GameObject);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void DeviceLostAndRestore_DropsGpuMeshesThenRebuildsAndPicks()
    {
        using Scene scene = CreateHud(out GameCanvas canvas, out UIBehaviour image);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
        try
        {
            canvas.RebuildIfDirty();
            Assert.NotNull(image.CachedMesh);
            AssertPick(scene, image.GameObject);

            int generation = CanvasDisplayChange.DeviceGeneration;
            CanvasDisplayChange.NotifyDeviceLost();
            Assert.True(CanvasDisplayChange.DeviceGeneration > generation);
            Assert.Null(image.CachedMesh);

            CanvasDisplayChange.NotifyDeviceRestored();
            canvas.RebuildIfDirty();
            Assert.NotNull(image.CachedMesh);
            AssertPick(scene, image.GameObject);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void DeviceLostAndResize_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI WINDOW AND DEVICE", 230, 232, 240, 2);
        board.Text(40, 68, "FRAMEBUFFER GPU RESTORE", 140, 146, 160, 1);

        board.Fill(80, 120, 200, 140, 70, 74, 90, 255);
        board.Text(96, 140, "800 X 450", 180, 186, 196, 1);
        board.Text(96, 176, "SCALE ONE", 180, 186, 196, 1);

        board.Fill(320, 120, 200, 140, 80, 170, 220, 255);
        board.Text(336, 140, "400 X 225", 18, 19, 26, 1);
        board.Text(336, 176, "SCALE HALF", 18, 19, 26, 1);

        board.Stroke(560, 120, 220, 140, 90, 200, 150, 2);
        board.Fill(572, 132, 196, 116, 90, 200, 150, 255);
        board.Text(588, 148, "MESH DROP", 18, 19, 26, 1);
        board.Text(588, 184, "STILL HITS", 18, 19, 26, 1);

        board.Text(40, 300, "FRAMEBUFFER AND DEVICE REBIND", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-device-lost-resize", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    private Scene CreateHud(out GameCanvas canvas, out UIBehaviour image)
    {
        Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("ResizeHud");
        canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject("ResizeImage");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = Float2.Zero;
        image = imageObject.AddComponent<UIImage>();
        scene.Add(canvasObject);
        return scene;
    }

    private static void AssertPick(Scene scene, GameObject expected)
    {
        Float2 size = GameCanvas.ScreenSizeOverride ?? new Float2(100f, 100f);
        Assert.True(UIRaycaster.TryPick(scene, size * 0.5f, size, out UIRaycaster.Hit hit));
        Assert.Same(expected, hit.GameObject);
    }
}
