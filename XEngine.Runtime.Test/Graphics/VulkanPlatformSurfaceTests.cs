// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Backends.Vulkan;

using Silk.NET.Vulkan;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

public sealed class VulkanPlatformSurfaceTests
{
    [Fact]
    public void AndroidInstanceExtensionsExcludeDesktopWindowSystems()
    {
        var extensions = VulkanGraphicsDevice.GetRequiredInstanceExtensions(
            isAndroid: true,
            isHarmonyOS: false,
            isWindows: false,
            isMacOS: false);

        Assert.Contains("VK_KHR_surface", extensions);
        Assert.Contains("VK_KHR_android_surface", extensions);
        Assert.DoesNotContain("VK_KHR_xcb_surface", extensions);
        Assert.DoesNotContain("VK_KHR_wayland_surface", extensions);
        Assert.DoesNotContain("VK_KHR_win32_surface", extensions);
        Assert.DoesNotContain("VK_OHOS_surface", extensions);
    }

    [Fact]
    public void HarmonyOSInstanceExtensionsRequestOhosSurface()
    {
        var extensions = VulkanGraphicsDevice.GetRequiredInstanceExtensions(
            isAndroid: false,
            isHarmonyOS: true,
            isWindows: false,
            isMacOS: false);

        Assert.Contains("VK_KHR_surface", extensions);
        Assert.Contains("VK_OHOS_surface", extensions);
        // HarmonyOS never requests Linux desktop or competing platform surface extensions.
        Assert.DoesNotContain("VK_KHR_android_surface", extensions);
        Assert.DoesNotContain("VK_KHR_xcb_surface", extensions);
        Assert.DoesNotContain("VK_KHR_wayland_surface", extensions);
        Assert.DoesNotContain("VK_KHR_win32_surface", extensions);
    }

    [Fact]
    public void SwapchainFormatPrefersSrgbNonlinearOverUnorm()
    {
        var formats = new[]
        {
            new SurfaceFormatKHR
            {
                Format = Format.B8G8R8A8Unorm,
                ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            },
            new SurfaceFormatKHR
            {
                Format = Format.R8G8B8A8Srgb,
                ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            },
        };

        SurfaceFormatKHR selected = VulkanGraphicsDevice.SelectSwapchainSurfaceFormat(formats);

        Assert.Equal(Format.R8G8B8A8Srgb, selected.Format);
        Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, selected.ColorSpace);
    }

    [Fact]
    public void SwapchainFormatUsesSpecDefaultForUndefinedFormat()
    {
        var formats = new[]
        {
            new SurfaceFormatKHR { Format = Format.Undefined },
        };

        SurfaceFormatKHR selected = VulkanGraphicsDevice.SelectSwapchainSurfaceFormat(formats);

        Assert.Equal(Format.B8G8R8A8Srgb, selected.Format);
        Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, selected.ColorSpace);
    }

    [Fact]
    public void SwapchainPreTransformPrefersIdentityWhenSurfaceSupportsIt()
    {
        var capabilities = new SurfaceCapabilitiesKHR
        {
            CurrentTransform = SurfaceTransformFlagsKHR.Rotate90BitKhr,
            SupportedTransforms = SurfaceTransformFlagsKHR.IdentityBitKhr |
                SurfaceTransformFlagsKHR.Rotate90BitKhr,
        };

        SurfaceTransformFlagsKHR selected = VulkanGraphicsDevice.SelectSwapchainPreTransform(capabilities);

        Assert.Equal(SurfaceTransformFlagsKHR.IdentityBitKhr, selected);
    }

    [Fact]
    public void SwapchainPreTransformUsesRequiredSurfaceTransformWhenIdentityIsUnsupported()
    {
        var capabilities = new SurfaceCapabilitiesKHR
        {
            CurrentTransform = SurfaceTransformFlagsKHR.Rotate270BitKhr,
            SupportedTransforms = SurfaceTransformFlagsKHR.Rotate90BitKhr |
                SurfaceTransformFlagsKHR.Rotate270BitKhr,
        };

        SurfaceTransformFlagsKHR selected = VulkanGraphicsDevice.SelectSwapchainPreTransform(capabilities);

        Assert.Equal(SurfaceTransformFlagsKHR.Rotate270BitKhr, selected);
    }

    [Fact]
    public void SurfaceExtentWaitsForIdentityTransformToMatchFramebuffer()
    {
        var capabilities = new SurfaceCapabilitiesKHR
        {
            CurrentExtent = new Extent2D(960, 540),
            CurrentTransform = SurfaceTransformFlagsKHR.Rotate270BitKhr,
            SupportedTransforms = SurfaceTransformFlagsKHR.IdentityBitKhr |
                SurfaceTransformFlagsKHR.Rotate270BitKhr,
        };

        Assert.False(VulkanGraphicsDevice.IsSurfaceExtentReady(capabilities, 540, 960));
        Assert.True(VulkanGraphicsDevice.IsSurfaceExtentReady(capabilities, 960, 540));
    }

    [Fact]
    public void SurfaceExtentDoesNotWaitForDriverControlledOrFixedTransformExtent()
    {
        var driverControlled = new SurfaceCapabilitiesKHR
        {
            CurrentExtent = new Extent2D(uint.MaxValue, uint.MaxValue),
            SupportedTransforms = SurfaceTransformFlagsKHR.IdentityBitKhr,
        };
        var fixedTransform = new SurfaceCapabilitiesKHR
        {
            CurrentExtent = new Extent2D(960, 540),
            CurrentTransform = SurfaceTransformFlagsKHR.Rotate270BitKhr,
            SupportedTransforms = SurfaceTransformFlagsKHR.Rotate270BitKhr,
        };

        Assert.True(VulkanGraphicsDevice.IsSurfaceExtentReady(driverControlled, 540, 960));
        Assert.True(VulkanGraphicsDevice.IsSurfaceExtentReady(fixedTransform, 540, 960));
        Assert.True(VulkanGraphicsDevice.IsSurfaceExtentReady(driverControlled, 0, 0));
    }
}
