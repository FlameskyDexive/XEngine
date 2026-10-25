// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#if XENGINE_ANDROID
using System;

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Platforms.Android;

/// <summary>Vulkan WSI adapter over an acquired Android <c>ANativeWindow*</c>.</summary>
public sealed unsafe class AndroidWindowSurface : IWindowSurface
{
    private int _width;
    private int _height;

    public AndroidWindowSurface(nint nativeWindow, int width, int height)
    {
        if (nativeWindow == nint.Zero)
            throw new ArgumentException("ANativeWindow handle cannot be zero.", nameof(nativeWindow));
        NativeHandle = nativeWindow;
        UpdateSize(width, height);
    }

    public nint NativeHandle { get; }
    public (int Width, int Height) FramebufferSize => (_width, _height);
    public bool VSync { get; set; } = true;
    public int SwapInterval { get; set; } = 1;

    public void UpdateSize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
    }

    public nint CreateVulkanSurface(nint instance)
    {
        if (instance == nint.Zero)
            throw new ArgumentException("Vulkan instance cannot be zero.", nameof(instance));

        using Vk vk = Vk.GetApi();
        var vkInstance = new Instance(instance);
        if (!vk.TryGetInstanceExtension(vkInstance, out KhrAndroidSurface extension))
            throw new PlatformNotSupportedException("VK_KHR_android_surface is unavailable.");
        using (extension)
        {
            var createInfo = new AndroidSurfaceCreateInfoKHR
            {
                SType = StructureType.AndroidSurfaceCreateInfoKhr,
                Window = (nint*)NativeHandle,
            };
            Result result = extension.CreateAndroidSurface(vkInstance, &createInfo, null, out SurfaceKHR surface);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateAndroidSurfaceKHR failed: {result}");
            return (nint)surface.Handle;
        }
    }

    public void MakeCurrent() { }
    public void ClearCurrent() { }
    public void SwapBuffers() { }
}
#endif
