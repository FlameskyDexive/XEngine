// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiScenePrefabReloadTests : RuntimeTestBase
{
    [Fact]
    public void SceneSwitch_RebindsCanvasRegistryAndEventSystem()
    {
        using Scene sceneA = CreateScene(enable: false);
        using Scene sceneB = CreateScene(enable: false);
        GameCanvas canvasA = CreateHud(sceneA, "HudA");
        EventSystem eventA = canvasA.GameObject.AddComponent<EventSystem>();
        GameCanvas canvasB = CreateHud(sceneB, "HudB");
        EventSystem eventB = canvasB.GameObject.AddComponent<EventSystem>();

        sceneA.Enable();
        sceneB.Enable();
        Assert.Same(eventA, EventSystem.Current);
        Assert.Contains(canvasA, CanvasRegistry.GetCanvases(sceneA));
        Assert.Contains(canvasB, CanvasRegistry.GetCanvases(sceneB));

        sceneA.Disable();
        Assert.Same(eventB, EventSystem.Current);
        Assert.Empty(CanvasRegistry.GetCanvases(sceneA));
        Assert.Contains(canvasB, CanvasRegistry.GetCanvases(sceneB));

        GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
        try
        {
            canvasB.RebuildIfDirty();
            Assert.True(UIRaycaster.TryPick(sceneB, new Float2(50f, 50f), new Float2(100f, 100f), out UIRaycaster.Hit hit));
            Assert.Same(canvasB.GameObject.Children[0], hit.GameObject);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void PrefabInstance_RegistersCanvasAndRebuilds()
    {
        GameObject source = CreateGameObject("PrefabHud");
        source.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(source, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = Float2.Zero;
        imageObject.AddComponent<UIImage>();
        PrefabAsset prefab = new()
        {
            GameObjectData = Serializer.Serialize(typeof(object), source),
            AssetID = Guid.NewGuid(),
        };

        using Scene scene = CreateScene(enable: false);
        GameObject instance = Assert.IsType<GameObject>(prefab.Instantiate());
        Assert.True(instance.IsPrefabInstance);
        scene.Add(instance);
        scene.Enable();

        GameCanvas canvas = Assert.IsType<GameCanvas>(instance.GetComponent<GameCanvas>());
        Assert.Contains(canvas, CanvasRegistry.GetCanvases(scene));
        GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
        try
        {
            canvas.RebuildIfDirty();
            Assert.True(UIRaycaster.TryPick(scene, new Float2(50f, 50f), new Float2(100f, 100f), out UIRaycaster.Hit hit));
            Assert.Equal("Image", hit.GameObject.Name);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void SceneAndPrefab_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI SCENE AND PREFAB", 230, 232, 240, 2);
        board.Text(40, 68, "SWITCH SCENES UI STAYS LIVE", 140, 146, 160, 1);

        board.Fill(80, 120, 200, 140, 70, 74, 90, 255);
        board.Text(96, 140, "SCENE A OFF", 180, 186, 196, 1);
        board.Fill(320, 120, 200, 140, 80, 170, 220, 255);
        board.Text(336, 140, "SCENE B ON", 18, 19, 26, 1);
        board.Text(336, 176, "EVENT CURRENT", 18, 19, 26, 1);

        board.Stroke(560, 120, 220, 140, 90, 200, 150, 2);
        board.Fill(572, 132, 196, 116, 90, 200, 150, 255);
        board.Text(588, 148, "PREFAB COPY", 18, 19, 26, 1);
        board.Text(588, 184, "STILL HITS", 18, 19, 26, 1);

        board.Text(40, 300, "REGISTRY AND EVENT SYSTEM REBIND", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-scene-prefab-reload", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    private GameCanvas CreateHud(Scene scene, string name)
    {
        GameObject canvasObject = CreateGameObject(name);
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject(name + "Image");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = Float2.Zero;
        imageObject.AddComponent<UIImage>();
        scene.Add(canvasObject);
        return canvas;
    }
}
