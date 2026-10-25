// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Minimal window / surface abstraction so devices do not depend on a specific
/// windowing library. NativeHandle is the platform HWND / NSView / X11 window /
/// Wayland surface / OHNativeWindow pointer as required by the backend.
/// </summary>
public interface IWindowSurface
{
    /// <summary>Platform-native window or view handle.</summary>
    nint NativeHandle { get; }

    /// <summary>
    /// CSS selector for an HTML canvas (usually <c>#canvas</c>). Used by emdawnwebgpu on WASM.
    /// Desktop HWND / Metal / Vulkan surfaces leave this null.
    /// </summary>
    string? HtmlCanvasSelector => null;

    /// <summary>Current drawable framebuffer size in pixels.</summary>
    (int Width, int Height) FramebufferSize { get; }

    bool VSync { get; set; }

    /// <summary>Legacy GL swap interval (0 = immediate, 1 = vsync). Ignored by Vulkan/D3D12.</summary>
    int SwapInterval { get; set; }

    /// <summary>Make this surface's GL context current on the calling thread (OpenGL only).</summary>
    void MakeCurrent();

    /// <summary>Clear the current GL context on the calling thread (OpenGL only).</summary>
    void ClearCurrent();

    /// <summary>Present the backbuffer via the legacy GL path (OpenGL only).</summary>
    void SwapBuffers();

    /// <summary>
    /// Optional <c>CAMetalLayer*</c> for the Metal backend. Default returns
    /// <see cref="nint.Zero"/> so Vulkan/GL/D3D surfaces stay unchanged. Metal devices prefer
    /// this handle and fall back to <see cref="NativeHandle"/> when it is zero.
    /// Do not reuse <see cref="CreateVulkanSurface"/> for Metal.
    /// </summary>
    nint MetalLayer => 0;

    /// <summary>
    /// Optional hook for Vulkan WSI. Default returns <see cref="nint.Zero"/>;
    /// concrete surfaces may override when Vulkan support is wired.
    /// </summary>
    nint CreateVulkanSurface(nint instance) => nint.Zero;

    /// <summary>
    /// The platform-specific Vulkan instance extensions this surface needs to create a
    /// <c>VkSurfaceKHR</c> (e.g. <c>VK_KHR_android_surface</c>, <c>VK_OHOS_surface</c>,
    /// <c>VK_KHR_win32_surface</c>). The base implementation returns nothing; concrete
    /// Vulkan-capable surfaces override this so the device can negotiate a single instance
    /// extension set up front instead of guessing the host OS.
    /// </summary>
    IEnumerable<string> RequiredVulkanInstanceExtensions => Array.Empty<string>();
}
