// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

namespace XEngine.Runtime.Test;

/// <summary>
/// Shared availability-degradation helper for GPU contract tests: hosts without a backend's
/// machinery (e.g. macOS/Linux CI without a Vulkan loader, or without the Windows-only DXC)
/// skip via <c>catch when (…)</c> instead of failing. Mirrors RhiContractTests'
/// IsExpectedGpuUnavailable contract.
/// </summary>
internal static class GraphicsTestAvailability
{
    internal static bool IsBackendUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException
                or FileNotFoundException)
            {
                return true;
            }

            if (current is InvalidOperationException && IsExplicitGpuAvailabilityMessage(current.Message))
                return true;
        }

        return false;
    }

    private static bool IsExplicitGpuAvailabilityMessage(string message) =>
        message.StartsWith("Failed to create Direct3D 12 device. D3D12 is unavailable on this machine", StringComparison.Ordinal)
        || message.StartsWith("Failed to create DXGI factory:", StringComparison.Ordinal)
        || message.Equals("No Direct3D 12-capable DXGI adapter was found.", StringComparison.Ordinal)
        || message.StartsWith("Failed to create D3D12 device:", StringComparison.Ordinal)
        || message.StartsWith("Failed to create Vulkan instance. Vulkan is unavailable on this machine", StringComparison.Ordinal)
        || message.StartsWith("vkCreateInstance failed:", StringComparison.Ordinal)
        || message.Equals("VK_KHR_surface is not available.", StringComparison.Ordinal)
        || message.Equals("No Vulkan physical devices found.", StringComparison.Ordinal)
        || message.StartsWith("vkCreateDevice failed:", StringComparison.Ordinal)
        || message.Equals("VK_KHR_swapchain is not available.", StringComparison.Ordinal)
        || message.Equals("No graphics queue family on Vulkan device.", StringComparison.Ordinal)
        || message.Equals("No Vulkan present queue family supports the window surface.", StringComparison.Ordinal)
        || message.StartsWith("Failed to create WebGPU device. wgpu-native is unavailable", StringComparison.Ordinal)
        || message.StartsWith("wgpuCreateInstance returned null", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU adapter:", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU device:", StringComparison.Ordinal);
}
