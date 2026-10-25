// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CameraStackTests : RuntimeTestBase
{
    [Fact]
    public void AllBase_EmptyStacks_OrdersByDepth()
    {
        var low = CreateCamera("Low", CameraRenderType.Base, depth: 0);
        var high = CreateCamera("High", CameraRenderType.Base, depth: 10);
        var ordered = Order(high, low);

        Assert.Equal(new[] { low, high }, ordered);
    }

    [Fact]
    public void BaseThenOverlay_CompositesInStackOrder()
    {
        var overlay = CreateCamera("UI", CameraRenderType.Overlay, depth: -50);
        var world = CreateCamera("World", CameraRenderType.Base, depth: 0);
        world.CameraStack.Add(overlay);

        var ordered = Order(overlay, world);

        Assert.Equal(new[] { world, overlay }, ordered);
    }

    [Fact]
    public void UnstackedOverlay_IsSkipped()
    {
        var world = CreateCamera("World", CameraRenderType.Base, depth: 0);
        var orphan = CreateCamera("OrphanUI", CameraRenderType.Overlay, depth: 1);

        Assert.Equal(new[] { world }, Order(world, orphan));
    }

    [Fact]
    public void OverlayListedTwice_RendersWithFirstBase()
    {
        var overlay = CreateCamera("UI", CameraRenderType.Overlay, depth: 0);
        var first = CreateCamera("A", CameraRenderType.Base, depth: 0);
        var second = CreateCamera("B", CameraRenderType.Base, depth: 1);
        first.CameraStack.Add(overlay);
        second.CameraStack.Add(overlay);

        var ordered = Order(first, second, overlay);

        Assert.Equal(new[] { first, overlay, second }, ordered);
    }

    [Fact]
    public void DisabledOverlay_IsSkipped()
    {
        var overlay = CreateCamera("UI", CameraRenderType.Overlay, depth: 0);
        overlay.Enabled = false;
        var world = CreateCamera("World", CameraRenderType.Base, depth: 0);
        world.CameraStack.Add(overlay);

        Assert.Equal(new[] { world }, Order(world, overlay));
    }

    [Fact]
    public void EchoRoundTrip_PreservesRenderType()
    {
        var go = CreateGameObject("OverlayCam");
        var camera = go.AddComponent<Camera>();
        camera.RenderType = CameraRenderType.Overlay;
        camera.ClearFlags = CameraClearFlags.Depth;

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneCam = clone.GetComponent<Camera>();

        Assert.NotNull(cloneCam);
        Assert.Equal(CameraRenderType.Overlay, cloneCam.RenderType);
        Assert.Equal(CameraClearFlags.Depth, cloneCam.ClearFlags);
    }

    private Camera CreateCamera(string name, CameraRenderType type, int depth)
    {
        var go = CreateGameObject(name);
        var camera = go.AddComponent<Camera>();
        camera.RenderType = type;
        camera.Depth = depth;
        return camera;
    }

    private static Camera[] Order(params Camera[] collected)
    {
        var bases = new List<Camera>();
        var ordered = new List<Camera>();
        var used = new HashSet<Camera>();
        Camera.BuildRenderOrder(collected, bases, ordered, used);
        return ordered.ToArray();
    }
}
