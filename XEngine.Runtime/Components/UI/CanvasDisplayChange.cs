// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;
using Silk.NET.Maths;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Relayouts live Canvas hosts after a window resize, safe-area inset change, or GPU device
/// lost/restore. The render pipeline already rebuilds when the surface size changes; these
/// hooks cover editor viewports, headless tests, and mesh GPU buffers that die with the device.
/// </summary>
public static class CanvasDisplayChange
{
    private static bool s_subscribed;

    /// <summary>Increments on each device-lost or device-restored notification.</summary>
    public static int DeviceGeneration { get; private set; }

    [ModuleInitializer]
    [OnAssemblyLoad(Order = 50)]
    internal static void Subscribe()
    {
        if (s_subscribed) return;
        Window.FramebufferResize += OnFramebufferResize;
        Window.SafeAreaChanged += OnSafeAreaChanged;
        s_subscribed = true;
    }

    [OnAssemblyUnload]
    internal static void Unsubscribe()
    {
        Window.FramebufferResize -= OnFramebufferResize;
        Window.SafeAreaChanged -= OnSafeAreaChanged;
        s_subscribed = false;
    }

    /// <summary>Marks every registered Canvas dirty so Scale With Screen Size and safe-area HUD relayout.</summary>
    public static void NotifyResize()
        => CanvasRegistry.NotifyAllDirty(UIDirtyFlags.All);

    /// <summary>Drops baked UI GPU meshes. Call when the graphics device is lost.</summary>
    public static void NotifyDeviceLost()
    {
        DeviceGeneration++;
        CanvasRegistry.DiscardAllGpuMeshes();
    }

    /// <summary>Drops stale GPU meshes and dirties canvases so the restored device gets a fresh upload.</summary>
    public static void NotifyDeviceRestored()
    {
        DeviceGeneration++;
        CanvasRegistry.DiscardAllGpuMeshes();
    }

    private static void OnFramebufferResize(Vector2D<int> _)
        => NotifyResize();

    private static void OnSafeAreaChanged(WindowSafeArea _)
        => CanvasRegistry.NotifyAllDirty(UIDirtyFlags.Layout);
}
