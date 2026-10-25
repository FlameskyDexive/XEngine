// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// Icey G3 crashed the editor with <c>vkBeginCommandBuffer failed: ErrorOutOfHostMemory</c>
/// while creating textures off-frame. Host/device memory pressure is recoverable; device loss is not.
/// </summary>
public sealed class VulkanHostMemoryRecoveryTests
{
    [Theory]
    [InlineData(Result.ErrorOutOfHostMemory)]
    [InlineData(Result.ErrorOutOfDeviceMemory)]
    public void MemoryPressure_IsRecoverable(Result result)
    {
        Assert.True(VulkanGraphicsDevice.IsRecoverableMemoryPressure(result));
    }

    [Theory]
    [InlineData(Result.Success)]
    [InlineData(Result.ErrorDeviceLost)]
    [InlineData(Result.Timeout)]
    [InlineData(Result.ErrorOutOfDateKhr)]
    public void DeviceLossAndSuccess_AreNotRecoverableMemoryPressure(Result result)
    {
        Assert.False(VulkanGraphicsDevice.IsRecoverableMemoryPressure(result));
    }

    [Fact]
    public void Check_StillReportsMemoryPressureAfterFailedRetry()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VulkanGraphicsDevice.Check(Result.ErrorOutOfHostMemory, "vkBeginCommandBuffer"));
        Assert.Contains("ErrorOutOfHostMemory", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("device loss", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Optional_Vulkan_OffFrameTextureBurst_DoesNotThrow_Or_Skip()
    {
        try
        {
            using var device = new VulkanGraphicsDevice(new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.Vulkan,
                Debug = false,
            });
            device.Initialize(null);

            const int count = 128;
            var textures = new GraphicsTexture[count];
            var pixels = new byte[32 * 32 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 32;
                pixels[i + 1] = 64;
                pixels[i + 2] = 96;
                pixels[i + 3] = 255;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    textures[i] = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
                    using CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("vk-host-mem-burst");
                    create.EncodeCreateTexture(textures[i]);
                    create.EncodeAllocateTexture2D(textures[i], 0, 32, 32, 0, pixels);
                    device.Execute(create, wait: false);
                }

                device.WaitIdle();
                Assert.False(device.IsDeviceLost);
            }
            finally
            {
                for (int i = 0; i < textures.Length; i++)
                    textures[i]?.Dispose();
                device.WaitIdle();
            }
        }
        catch (Exception ex)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected Vulkan off-frame texture burst failure: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                return true;
            string message = current.Message;
            if (message.Contains("Vulkan is unavailable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("vkCreateInstance failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No Vulkan physical devices", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("vkCreateDevice failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No graphics queue family", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
