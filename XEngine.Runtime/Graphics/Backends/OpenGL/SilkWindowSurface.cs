// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.OpenGL;

/// <summary>
/// <see cref="IWindowSurface"/> wrapping a Silk.NET <see cref="IWindow"/> and its GL context.
/// </summary>
public sealed class SilkWindowSurface : IWindowSurface
{
    private readonly IWindow _window;
    private int _swapInterval;

    public SilkWindowSurface(IWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _swapInterval = _window.VSync ? 1 : 0;
    }

    /// <summary>Underlying Silk window (used for <c>GL.GetApi</c>).</summary>
    public IWindow Window => _window;

    public nint NativeHandle =>
        _window.Native.DXHandle
        ?? _window.Native.Win32?.Item1
        ?? _window.Handle;

    private nint? _metalLayer;

    /// <summary>
    /// macOS Metal presentation: lazily attaches a CAMetalLayer to the GLFW window's content
    /// view (see <see cref="Platforms.macOS.MacMetalLayerBridge"/>). Other platforms return 0.
    /// </summary>
    public nint MetalLayer
    {
        get
        {
            if (_metalLayer.HasValue)
                return _metalLayer.Value;
            if (!OperatingSystem.IsMacOS())
                return 0;
            nint glfwWindow = _window.Handle;
            _metalLayer = Platforms.macOS.MacMetalLayerBridge.Attach(glfwWindow);
            return _metalLayer.Value;
        }
    }

    public (int Width, int Height) FramebufferSize
    {
        get
        {
            var size = _window.FramebufferSize;
            return (size.X, size.Y);
        }
    }

    public bool VSync
    {
        get => _window.VSync;
        set => _window.VSync = value;
    }

    public int SwapInterval
    {
        get => _swapInterval;
        set
        {
            _swapInterval = value;
            _window.GLContext!.SwapInterval(value);
        }
    }

    public void MakeCurrent() => _window.GLContext!.MakeCurrent();

    public void ClearCurrent() => _window.GLContext!.Clear();

    public void SwapBuffers() => _window.GLContext!.SwapBuffers();

    /// <inheritdoc />
    public unsafe nint CreateVulkanSurface(nint instance)
    {
        IVkSurface? surface = _window.VkSurface;
        if (surface == null)
            return nint.Zero;

        VkNonDispatchableHandle handle = surface.Create<AllocationCallbacks>(new VkHandle(instance), null);
        return unchecked((nint)handle.Handle);
    }
}
