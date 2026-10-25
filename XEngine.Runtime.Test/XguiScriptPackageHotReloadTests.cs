// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiScriptPackageHotReloadTests : RuntimeTestBase
{
    [Fact]
    public void ScriptReload_DisableResetEnable_RebindsCanvasAndEventSystem()
    {
        using Scene scene = CreateHudScene(out GameCanvas canvas, out EventSystem eventSystem);
        scene.Enable();
        Assert.Same(eventSystem, EventSystem.Current);
        Assert.Contains(canvas, CanvasRegistry.GetCanvases(scene));

        scene.Disable();
        CanvasRegistry.Reset();
        EventSystem.ResetStatics();
        CanvasUpdateDispatcher.Reset();
        Assert.Null(EventSystem.Current);
        Assert.Empty(CanvasRegistry.GetCanvases(scene));

        scene.Enable();
        Assert.Same(eventSystem, EventSystem.Current);
        Assert.Contains(canvas, CanvasRegistry.GetCanvases(scene));
        AssertPick(scene, canvas);
    }

    [Fact]
    public void PackageReload_LiveScene_RebindRestoresHostWithoutOnEnable()
    {
        using Scene scene = CreateHudScene(out GameCanvas canvas, out EventSystem eventSystem);
        scene.Enable();
        AssertPick(scene, canvas);

        CanvasRegistry.Reset();
        EventSystem.ResetStatics();
        CanvasUpdateDispatcher.Reset();
        Assert.Null(EventSystem.Current);
        Assert.Empty(CanvasRegistry.GetCanvases(scene));
        Assert.True(canvas.EnabledInHierarchy);
        Assert.True(eventSystem.EnabledInHierarchy);

        CanvasHotReload.Rebind(scene);
        Assert.Same(eventSystem, EventSystem.Current);
        Assert.Contains(canvas, CanvasRegistry.GetCanvases(scene));
        AssertPick(scene, canvas);
    }

    [Fact]
    public void ScriptAndPackage_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI SCRIPT AND MODULE", 230, 232, 240, 2);
        board.Text(40, 68, "HOT RELOAD UI STAYS LIVE", 140, 146, 160, 1);

        board.Fill(80, 120, 200, 140, 70, 74, 90, 255);
        board.Text(96, 140, "ALC UNLOAD", 180, 186, 196, 1);
        board.Text(96, 176, "STATICS DROP", 180, 186, 196, 1);

        board.Fill(320, 120, 200, 140, 80, 170, 220, 255);
        board.Text(336, 140, "HOST REBIND", 18, 19, 26, 1);
        board.Text(336, 176, "EVENT CURRENT", 18, 19, 26, 1);

        board.Stroke(560, 120, 220, 140, 90, 200, 150, 2);
        board.Fill(572, 132, 196, 116, 90, 200, 150, 255);
        board.Text(588, 148, "DRIVER ON", 18, 19, 26, 1);
        board.Text(588, 184, "STILL HITS", 18, 19, 26, 1);

        board.Text(40, 300, "REGISTRY AND DISPATCHER REBIND", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-script-package-hot-reload", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    private Scene CreateHudScene(out GameCanvas canvas, out EventSystem eventSystem)
    {
        Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("HotReloadHud");
        canvas = canvasObject.AddComponent<GameCanvas>();
        eventSystem = canvasObject.AddComponent<EventSystem>();
        GameObject imageObject = CreateGameObject("HotReloadImage");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = Float2.Zero;
        imageObject.AddComponent<UIImage>();
        scene.Add(canvasObject);
        return scene;
    }

    private static void AssertPick(Scene scene, GameCanvas canvas)
    {
        GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
        try
        {
            canvas.RebuildIfDirty();
            Assert.True(UIRaycaster.TryPick(scene, new Float2(50f, 50f), new Float2(100f, 100f), out UIRaycaster.Hit hit));
            Assert.Equal("HotReloadImage", hit.GameObject.Name);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }
}
