// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Platforms.HarmonyOS;

/// <summary>
/// Vulkan WSI adapter over an acquired <c>OHNativeWindow*</c>. The generated native host supplies
/// surface creation because the OpenHarmony NDK owns the platform-specific Vulkan extension call.
/// </summary>
public sealed class HarmonyOSWindowSurface : IWindowSurface
{
    /// <summary>
    /// The OpenHarmony-specific Vulkan instance extension required to create a
    /// <c>VkSurfaceKHR</c> from an <c>OHNativeWindow</c>. Declared here rather than derived from
    /// host-OS guessing so the surface owns its WSI contract and the device can negotiate it up
    /// front (plan §4.2).
    /// </summary>
    public const string VulkanInstanceExtension = "VK_OHOS_surface";

    private static readonly string[] s_requiredExtensions = { VulkanInstanceExtension };

    private readonly Func<nint, nint, nint>? _createVulkanSurface;
    private int _width;
    private int _height;

    public HarmonyOSWindowSurface(
        nint nativeWindow,
        int width,
        int height,
        Func<nint, nint, nint>? createVulkanSurface = null)
    {
        if (nativeWindow == nint.Zero)
            throw new ArgumentException("OHNativeWindow handle cannot be zero.", nameof(nativeWindow));
        NativeHandle = nativeWindow;
        _createVulkanSurface = createVulkanSurface;
        UpdateSize(width, height);
    }

    public nint NativeHandle { get; }
    public (int Width, int Height) FramebufferSize => (_width, _height);
    public bool VSync { get; set; } = true;
    public int SwapInterval { get; set; } = 1;

    /// <summary>
    /// OpenHarmony's XComponent may expose a non-identity current transform when an ability is
    /// rotated away from its natural orientation. Encoding that transform in the swapchain
    /// requires the final present blit to consume it (rotate/mirror the fullscreen copy);
    /// until the renderer is transform-aware, requesting it here froze the main thread's
    /// vSync callback past the THREAD_BLOCK_6S watchdog on Mali-G610 (device-validated), so
    /// the engine keeps presenting an identity chain and the compositor applies the rotation.
    /// </summary>
    public bool PreferCurrentTransform => false;

    public void UpdateSize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
    }

    public nint CreateVulkanSurface(nint instance)
    {
        if (instance == nint.Zero)
            throw new ArgumentException("Vulkan instance cannot be zero.", nameof(instance));
        if (_createVulkanSurface == null)
        {
            throw new PlatformNotSupportedException(
                "HarmonyOS Vulkan surface creation must be provided by the generated native host.");
        }

        nint surface = _createVulkanSurface(instance, NativeHandle);
        if (surface == nint.Zero)
            throw new InvalidOperationException("HarmonyOS native host returned a zero Vulkan surface.");
        return surface;
    }

    /// <summary>
    /// The platform WSI extension the OpenHarmony Vulkan loader needs before
    /// <see cref="CreateVulkanSurface"/> can build a <c>VkSurfaceKHR</c>.
    /// </summary>
    public IEnumerable<string> RequiredVulkanInstanceExtensions => s_requiredExtensions;

    public void MakeCurrent() { }
    public void ClearCurrent() { }
    public void SwapBuffers() { }
}
