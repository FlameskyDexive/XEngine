// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.Backends.WebGPU.Native;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WG1: WebGPU device creation against unified webgpu.h / wgpu-native v29.
/// GPU-less machines skip with the same availability exceptions as the other backends.
/// </summary>
public class WebGPUGraphicsDeviceTests
{
    [Fact]
    public void NativeStructs_MatchUnifiedWebGpuHeaderLayoutOn64Bit()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(16, Marshal.SizeOf<WGPUStringView>());
        Assert.Equal(8, Marshal.SizeOf<WGPUFuture>());
        Assert.Equal(16, Marshal.SizeOf<WGPUFutureWaitInfo>());
        Assert.Equal(16, Marshal.SizeOf<WGPUChainedStruct>());
        Assert.Equal(32, Marshal.SizeOf<WGPUInstanceDescriptor>());
        Assert.Equal(32, Marshal.SizeOf<WGPURequestAdapterOptions>());
        Assert.Equal(24, Marshal.SizeOf<WGPUQueueDescriptor>());
        Assert.Equal(40, Marshal.SizeOf<WGPUCallbackInfo>());
        Assert.Equal(32, Marshal.SizeOf<WGPUUncapturedErrorCallbackInfo>());
        Assert.Equal(144, Marshal.SizeOf<WGPUDeviceDescriptor>());
        Assert.Equal(96, Marshal.SizeOf<WGPUAdapterInfo>());
        Assert.Equal(64, Marshal.SizeOf<WGPUSurfaceCapabilities>());
        Assert.Equal(24, Marshal.SizeOf<WGPUSurfaceTexture>());
        Assert.Equal(152, Marshal.SizeOf<WGPULimits>());
        Assert.Equal(64, Marshal.SizeOf<WGPUSurfaceConfiguration>());
        Assert.Equal(48, Marshal.SizeOf<WGPUBufferDescriptor>());
        Assert.Equal(12, Marshal.SizeOf<WGPUExtent3D>());
        Assert.Equal(12, Marshal.SizeOf<WGPUOrigin3D>());
        Assert.Equal(16, Marshal.SizeOf<WGPUTexelCopyBufferLayout>());
        Assert.Equal(24, Marshal.SizeOf<WGPUTexelCopyBufferInfo>());
        Assert.Equal(32, Marshal.SizeOf<WGPUTexelCopyTextureInfo>());
        Assert.Equal(80, Marshal.SizeOf<WGPUTextureDescriptor>());
        Assert.Equal(64, Marshal.SizeOf<WGPUSamplerDescriptor>());
        Assert.Equal(32, Marshal.SizeOf<WGPUShaderSourceWGSL>());
        Assert.Equal(12, Marshal.SizeOf<WGPUBlendComponent>());
        Assert.Equal(24, Marshal.SizeOf<WGPUBlendState>());
        Assert.Equal(16, Marshal.SizeOf<WGPUStencilFaceState>());
        Assert.Equal(32, Marshal.SizeOf<WGPUColor>());
        Assert.Equal(72, Marshal.SizeOf<WGPURenderPassColorAttachment>());
        Assert.Equal(48, Marshal.SizeOf<WGPURenderPassDepthStencilAttachment>());
        Assert.Equal(64, Marshal.SizeOf<WGPURenderPassDescriptor>());
        Assert.Equal(48, Marshal.SizeOf<WGPURenderPassDepthStencilAttachment>());
        Assert.Equal(64, Marshal.SizeOf<WGPUTextureViewDescriptor>());
        Assert.Equal(64, Marshal.SizeOf<WGPUVertexState>());
        Assert.Equal(32, Marshal.SizeOf<WGPUPrimitiveState>());
        Assert.Equal(24, Marshal.SizeOf<WGPUMultisampleState>());
        Assert.Equal(64, Marshal.SizeOf<WGPUFragmentState>());
        Assert.Equal(32, Marshal.SizeOf<WGPUColorTargetState>());
        Assert.Equal(168, Marshal.SizeOf<WGPURenderPipelineDescriptor>());
        Assert.Equal(24, Marshal.SizeOf<WGPUBufferBindingLayout>());
        Assert.Equal(16, Marshal.SizeOf<WGPUSamplerBindingLayout>());
        Assert.Equal(24, Marshal.SizeOf<WGPUTextureBindingLayout>());
        Assert.Equal(24, Marshal.SizeOf<WGPUStorageTextureBindingLayout>());
        Assert.Equal(120, Marshal.SizeOf<WGPUBindGroupLayoutEntry>());
        Assert.Equal(56, Marshal.SizeOf<WGPUBindGroupEntry>());
        Assert.Equal(48, Marshal.SizeOf<WGPUBindGroupDescriptor>());
        Assert.Equal(40, Marshal.SizeOf<WGPUBindGroupLayoutDescriptor>());
        Assert.Equal(48, Marshal.SizeOf<WGPUPipelineLayoutDescriptor>());
        Assert.Equal(72, Marshal.SizeOf<WGPUDepthStencilState>());
        Assert.Equal(32, Marshal.SizeOf<WGPUVertexAttribute>());
        Assert.Equal(40, Marshal.SizeOf<WGPUVertexBufferLayout>());
        Assert.Equal((uint)0x00000016, (uint)WGPUTextureFormat.RGBA8Unorm);
        Assert.Equal((uint)0x00000002, (uint)WGPUBufferBindingType.Uniform);
        Assert.Equal((uint)0x00000004, (uint)WGPUBufferBindingType.ReadOnlyStorage);
        Assert.Equal((uint)0x00000002, (uint)WGPUStorageTextureAccess.WriteOnly);
        Assert.Equal(WebGPUNative.HeaderRevision, "wgpu-native v29.0.1.1 / unified webgpu.h");
    }

    [Fact]
    public void Factory_CreatesWebGPUDeviceOrReportsAvailability()
    {
        Exception? ex = Record.Exception(() =>
        {
            using IGraphicsDevice device = GraphicsDeviceFactory.Create(new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.WebGPU,
            });
            Assert.Equal(GraphicsBackend.WebGPU, device.Backend);
            Assert.StartsWith("WebGPU", device.Capabilities.BackendName, StringComparison.Ordinal);
            Assert.NotEqual(0, device.Capabilities.MaxTextureSize);
        });

        if (ex == null)
            return;

        Assert.DoesNotContain("Unknown graphics backend", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented yet", ex.ToString(), StringComparison.Ordinal);
        Assert.True(
            IsExpectedGpuUnavailable(ex),
            $"Unexpected WebGPU factory failure: {ex.GetType().FullName}: {ex.Message}");
    }

    [Fact]
    public void HeadlessDevice_InitializeBeginEndWaitIdle_AdvancesFence()
    {
        WebGPUGraphicsDevice? device;
        try
        {
            device = new WebGPUGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.WebGPU });
        }
        catch (Exception ex)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected WebGPU create failure: {ex.GetType().FullName}: {ex.Message}");
            return;
        }

        using (device)
        {
            device.Initialize(null);
            Assert.True(device.IsInitialized);
            Assert.NotEqual(nint.Zero, device.NativeDevice);
            Assert.NotEqual(nint.Zero, device.NativeQueue);
            Assert.Contains("WebGPU", device.Capabilities.BackendName, StringComparison.Ordinal);
            Console.WriteLine(
                $"WG1 adapter={device.Capabilities.BackendName}; maxTexture={device.Capabilities.MaxTextureSize}; " +
                $"colorAttachments={device.Capabilities.MaxFramebufferColorAttachments}; compute={device.Capabilities.SupportsCompute}");

            device.BeginFrame();
            device.EndFrame();
            Assert.Equal(1UL, device.GetFenceValue());

            device.WaitIdle();
            Assert.Equal(1UL, device.GetFenceValue());
            device.WaitFence(1);

            CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg1");
            device.Execute(cmd, wait: true);
            Assert.Equal(2UL, device.GetFenceValue());
        }
    }

    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                return true;
            }

            if (current is InvalidOperationException && IsExplicitGpuAvailabilityMessage(current.Message))
                return true;
        }

        return false;
    }

    private static bool IsExplicitGpuAvailabilityMessage(string message) =>
        message.StartsWith("Failed to create WebGPU device. wgpu-native is unavailable", StringComparison.Ordinal)
        || message.StartsWith("wgpuCreateInstance returned null", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU adapter:", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU device:", StringComparison.Ordinal)
        || message.Equals("wgpuDeviceGetQueue returned null.", StringComparison.Ordinal);
}
