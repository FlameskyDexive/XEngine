// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// The scene view shows screen-space canvases in world space: it enables
/// <see cref="GameCanvas.EditorWorldSpaceOverride"/> around its render and collects the Overlay
/// surface into the world UI pass (UniversalRenderPipeline.RenderUIWorldCore when IsSceneView).
/// These tests pin the runtime contract that pass relies on - under the override, an overlay
/// canvas's collected render nodes and world AABBs map through the GameObject transform at
/// <see cref="GameCanvas.EditorOverlayPixelsPerUnit"/> design pixels per unit, exactly like the
/// gizmos/handles/picking that use <see cref="GameCanvas.CanvasToWorld"/>.
/// </summary>
public sealed class CanvasEditorWorldSpaceTests : RuntimeTestBase
{
    private const float PixelsPerUnit = 100f; // GameCanvas.EditorOverlayPixelsPerUnit

    [Fact]
    public void EditorWorldSpaceOverride_MapsOverlayCanvasThroughWorldTransform()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        canvas.RenderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.Transform.Position = new Float3(500f, 40f, -3f);

        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.Zero;
        rt.Pivot = new Float2(0.5f, 0.5f);
        rt.SizeDelta = new Float2(40f, 40f);
        rt.AnchoredPosition = new Float2(20f, 20f);
        UIImage image = imageObject.AddComponent<UIImage>();
        image.Type = ImageType.Simple;
        using Texture2D texture = new(8, 8);
        image.Sprite = Sprite.Create(texture, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 100f, "Chip");

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(1280f, 720f);
        GameCanvas.EditorWorldSpaceOverride = false;
        try
        {
            // Game view: an overlay canvas maps design pixels by ScaleFactor only, ignoring
            // the GameObject transform (it is pasted onto the screen, not placed in the world).
            canvas.RebuildIfDirty();
            Float3 flatOrigin = Float4x4.TransformPoint(Float3.Zero, canvas.CanvasToWorld);
            Assert.Equal(0f, flatOrigin.X, 3);
            Assert.Equal(0f, flatOrigin.Y, 3);
            Assert.Equal(0f, flatOrigin.Z, 3);

            // Scene view: same canvas, same surface, but the world mapping goes through the
            // transform at EditorOverlayPixelsPerUnit design pixels per world unit.
            GameCanvas.EditorWorldSpaceOverride = true;
            canvas.RebuildIfDirty();
            Float3 worldOrigin = Float4x4.TransformPoint(Float3.Zero, canvas.CanvasToWorld);
            Assert.Equal(500f, worldOrigin.X, 3);
            Assert.Equal(40f, worldOrigin.Y, 3);
            Assert.Equal(-3f, worldOrigin.Z, 3);
            Float3 hundredUp = Float4x4.TransformPoint(new Float3(0f, PixelsPerUnit, 0f), canvas.CanvasToWorld);
            Assert.Equal(41f, hundredUp.Y, 3);

            // The Overlay surface still collects (the scene view's world UI pass asks for it),
            // and the node model carries the world mapping the gizmos/handles use.
            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            CanvasRenderNode node = Assert.Single(nodes);
            node.GetRenderingData(default, out _, out _, out Float4x4 model, out _);
            Float3 elementPivotWorld = Float4x4.TransformPoint(Float3.Zero, model);
            Assert.Equal(500f + 20f / PixelsPerUnit, elementPivotWorld.X, 3);
            Assert.Equal(40f + 20f / PixelsPerUnit, elementPivotWorld.Y, 3);
            Assert.Equal(-3f, elementPivotWorld.Z, 3);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
            GameCanvas.EditorWorldSpaceOverride = false;
        }
    }

    [Fact]
    public void TryGetWorldAabb_CoversRectTransformOnlyGroupingNodes()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        canvas.RenderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.Transform.Position = new Float3(10f, 0f, 0f);

        // A grouping node: RectTransform only, no UIBehaviour. Scene-view framing (F / hierarchy
        // double-click) must fit its layout rect through the canvas mapping instead of falling
        // back to a half-unit point at the transform origin.
        GameObject groupObject = CreateGameObject("Group");
        groupObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = groupObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.Zero;
        rt.Pivot = new Float2(0.5f, 0.5f);
        rt.SizeDelta = new Float2(200f, 100f);
        rt.AnchoredPosition = new Float2(100f, 50f);

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(1280f, 720f);
        GameCanvas.EditorWorldSpaceOverride = true;
        try
        {
            Assert.True(canvas.TryGetWorldAabb(rt, out Float3 min, out Float3 max));
            // Rect spans design pixels (0..200, 0..100) -> world (10..12, 0..1) at 100 px/unit.
            Assert.Equal(10f, min.X, 3);
            Assert.Equal(12f, max.X, 3);
            Assert.Equal(0f, min.Y, 3);
            Assert.Equal(1f, max.Y, 3);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
            GameCanvas.EditorWorldSpaceOverride = false;
        }
    }
}
