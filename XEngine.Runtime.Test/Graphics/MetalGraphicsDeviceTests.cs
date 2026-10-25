// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.Metal;
using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// M1: Metal backend name resolution, factory wiring, and Apple device create.
/// Non-macOS hosts skip the headless lifecycle test; factory availability is tested separately.
/// </summary>
public class MetalGraphicsDeviceTests
{
    [Fact]
    public void BindingPath_IsFrozenObjcMsgSend()
    {
        Assert.Equal("I0 path (2) objc_msgSend / Metal.framework", MetalNative.HeaderRevision);
        Assert.Equal(6, (int)GraphicsBackend.Metal);
        Assert.Equal("newCommandQueue", MetalSelectorNames.NewCommandQueue);
        Assert.Equal("commandBuffer", MetalSelectorNames.CommandBuffer);
        Assert.Equal("presentDrawable:", MetalSelectorNames.PresentDrawable);
        Assert.Equal("nextDrawable", MetalSelectorNames.NextDrawable);
        Assert.Equal("waitUntilCompleted", MetalSelectorNames.WaitUntilCompleted);
        Assert.Equal("newTextureWithDescriptor:", MetalSelectorNames.NewTextureWithDescriptor);
        Assert.Equal("newBufferWithLength:options:", MetalSelectorNames.NewBufferWithLengthOptions);
        Assert.Equal("generateMipmapsForTexture:", MetalSelectorNames.GenerateMipmapsForTexture);
        Assert.Equal("replaceRegion:mipmapLevel:withBytes:bytesPerRow:", MetalSelectorNames.ReplaceRegionMipmapLevelWithBytesBytesPerRow);
        Assert.Equal("newLibraryWithSource:options:error:", MetalSelectorNames.NewLibraryWithSourceOptionsError);
        Assert.Equal("newRenderPipelineStateWithDescriptor:error:", MetalSelectorNames.NewRenderPipelineStateWithDescriptorError);
        Assert.DoesNotContain("Microsoft.iOS", MetalNative.HeaderRevision, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpMetal", MetalNative.HeaderRevision, StringComparison.Ordinal);
        Assert.DoesNotContain("Silk.NET", MetalNative.HeaderRevision, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseName_RecognizesMetalAliases()
    {
        Assert.True(GraphicsBackendSelection.TryParseName("metal", out GraphicsBackend backend));
        Assert.Equal(GraphicsBackend.Metal, backend);
        Assert.True(GraphicsBackendSelection.TryParseName("mtl", out backend));
        Assert.Equal(GraphicsBackend.Metal, backend);
    }

    [Fact]
    public void AutoCandidates_PreferMetalOnMacOnly()
    {
        GraphicsBackend[] candidates = GraphicsDeviceFactory.GetAutoCandidatesForTests();
        if (OperatingSystem.IsMacOS())
            Assert.Equal(GraphicsBackend.Metal, candidates[0]);
        else
            Assert.DoesNotContain(GraphicsBackend.Metal, candidates);
        Assert.DoesNotContain(GraphicsBackend.WebGPU, candidates);
        Assert.DoesNotContain(GraphicsBackend.WebGL2, candidates);
    }

    [Fact]
    public void ResolveMetalLayer_PrefersMetalLayer_NeverFallsBackToNativeHandle()
    {
        // The old NativeHandle fallback passed GLFW/Win32 handles to objc_msgSend as if they
        // were CAMetalLayer pointers, which crashes. A zero layer now means headless.
        Assert.Equal(0, MetalGraphicsDevice.ResolveMetalLayer(null));
        Assert.Equal(0, MetalGraphicsDevice.ResolveMetalLayer(new StubWindowSurface(11, metalLayer: 0)));
        Assert.Equal(99, MetalGraphicsDevice.ResolveMetalLayer(new StubWindowSurface(11, metalLayer: 99)));
    }

    [Fact]
    public void Factory_CreatesMetalDeviceOrReportsAvailability()
    {
        Exception? ex = Record.Exception(() =>
        {
            using IGraphicsDevice device = GraphicsDeviceFactory.Create(new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.Metal,
            });
            Assert.Equal(GraphicsBackend.Metal, device.Backend);
            Assert.StartsWith("Metal", device.Capabilities.BackendName, StringComparison.Ordinal);
        });

        if (ex == null)
            return;

        Assert.DoesNotContain("Unknown graphics backend", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented yet", ex.ToString(), StringComparison.Ordinal);
        Assert.True(
            IsExpectedMetalUnavailable(ex),
            $"Unexpected Metal factory failure: {ex.GetType().FullName}: {ex.Message}");
    }

    [MacOSMetalFact]
    public void HeadlessDevice_InitializeBeginEndWaitIdle_AdvancesFence()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            Assert.True(device.IsInitialized);
            Assert.NotEqual(nint.Zero, device.NativeDevice);
            Assert.NotEqual(nint.Zero, device.NativeQueue);
            Assert.NotEqual(nint.Zero, device.ScratchTexture);
            Assert.False(device.HasMetalLayer);
            Assert.Contains("Metal", device.Capabilities.BackendName, StringComparison.Ordinal);

            device.BeginFrame();
            device.EndFrame();
            Assert.Equal(1UL, device.GetFenceValue());

            device.WaitIdle();
            Assert.Equal(1UL, device.GetFenceValue());
            device.WaitFence(1);

            CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m1");
            device.Execute(cmd, wait: true);
            Assert.Equal(2UL, device.GetFenceValue());
        }
    }

    private static bool IsExpectedMetalUnavailable(Exception ex)
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

            if (current is InvalidOperationException
                && (current.Message.Contains("MTLCreateSystemDefaultDevice", StringComparison.Ordinal)
                    || current.Message.Contains("Metal is unavailable", StringComparison.Ordinal)
                    || current.Message.Contains("scratch texture", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class StubWindowSurface : IWindowSurface
    {
        public StubWindowSurface(nint nativeHandle, nint metalLayer)
        {
            NativeHandle = nativeHandle;
            MetalLayer = metalLayer;
        }

        public nint NativeHandle { get; }
        public nint MetalLayer { get; }
        public (int Width, int Height) FramebufferSize => (64, 64);
        public bool VSync { get; set; }
        public int SwapInterval { get; set; }
        public void MakeCurrent() { }
        public void ClearCurrent() { }
        public void SwapBuffers() { }
    }
}
