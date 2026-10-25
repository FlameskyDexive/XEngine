// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;

using XEngine.Runtime;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// P7 stage A contract for <see cref="RenderThreadGraphicsDevice"/>. The fake-backend tests are
/// deterministic and GPU-free: they pin submit ordering, blocking semantics, error propagation
/// and command-buffer ownership. The <c>Optional_*</c> tests run the same wrapper over real
/// Vulkan / D3D12 devices and skip when no GPU is present.
/// </summary>
public class RenderThreadGraphicsDeviceTests
{
    [Fact]
    public void PrimaryResizeAfterSecondaryPresentRoutesToPrimarySurface()
    {
        var inner = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(inner);
        device.Initialize(null);
        var secondary = new PresentationSurfaceId(2);
        Assert.True(device.BeginFrame(secondary));
        Assert.True(device.EndFrame(secondary));
        Graphics.ResizePrimarySurface(device, 800, 600);
        Assert.Contains("ResizeSurface(1,800x600)", inner.Calls);
        Assert.DoesNotContain("Resize(800x600)", inner.Calls);
        Assert.DoesNotContain("ResizeSurface(2,800x600)", inner.Calls);
    }

    // ─────────────────────── Fake backend ───────────────────────

    private sealed class FakeDevice : IGraphicsDevice, IPresentationSurfaceLifecycle
    {
        public readonly List<string> Calls = new();
        public readonly object CallGate = new();
        public Thread? LastExecuteThread;
        public Exception? ThrowOnNextExecute;
        public Exception? ThrowOnNextEndFrame;
        public ManualResetEventSlim? BlockExecute;
        public ManualResetEventSlim? BlockEndFrame;
        public Action<FakeDevice>? OnExecute;
        public bool Initialized;
        public int FrameDepth;
        public ulong NextSurfaceId = 1;
        public GpuTimestampFrame Timestamp;
        public bool HasTimestamp;

        public GraphicsBackend Backend => GraphicsBackend.Null;
        public GraphicsDeviceCapabilities Capabilities => new() { BackendName = "Fake" };
        public bool IsInitialized => Initialized;

        private void Record(string call)
        {
            lock (CallGate)
                Calls.Add(call);
        }

        public string[] Snapshot()
        {
            lock (CallGate)
                return Calls.ToArray();
        }

        public void Initialize(IWindowSurface? surface) { Initialized = true; Record("Initialize"); }
        public void Shutdown() { Initialized = false; Record("Shutdown"); }
        public void BeginFrame() { FrameDepth++; Record("BeginFrame"); }
        public void EndFrame()
        {
            BlockEndFrame?.Wait();
            FrameDepth--;
            Record("EndFrame");
            if (ThrowOnNextEndFrame is { } ex)
            {
                ThrowOnNextEndFrame = null;
                throw ex;
            }
        }
        public void Resize(int width, int height) => Record($"Resize({width}x{height})");

        public void Execute(CommandBuffer commandBuffer, bool wait)
        {
            LastExecuteThread = Thread.CurrentThread;
            BlockExecute?.Wait();
            OnExecute?.Invoke(this);
            Record($"Execute({commandBuffer.Name},{wait})");
            CommandBufferPool.Return(commandBuffer);
            if (ThrowOnNextExecute is { } ex)
            {
                ThrowOnNextExecute = null;
                throw ex;
            }
        }

        public void WaitIdle() => Record("WaitIdle");
        public ulong GetFenceValue() => 0;
        public void WaitFence(ulong fenceValue) => Record($"WaitFence({fenceValue})");
        public void Dispose() => Record("Dispose");

        public bool TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame frame)
        {
            frame = Timestamp;
            return HasTimestamp;
        }

        public bool TryGetPrimaryPresentationSurface(out PresentationSurfaceId surface)
        {
            surface = new PresentationSurfaceId(1);
            return true;
        }

        public PresentationSurfaceId CreatePresentationSurface(IWindowSurface surface)
        {
            Record("CreateSurface");
            return new PresentationSurfaceId(NextSurfaceId++);
        }

        public bool DestroyPresentationSurface(PresentationSurfaceId surface)
        {
            Record($"DestroySurface({surface.Value})");
            return true;
        }

        public bool ResizePresentationSurface(PresentationSurfaceId surface, int width, int height)
        {
            Record($"ResizeSurface({surface.Value},{width}x{height})");
            return true;
        }

        public bool TryGetPresentationSurface(PresentationSurfaceId surface, out PresentationSurfaceInfo info)
        {
            info = new PresentationSurfaceInfo(surface, 4, 4, true);
            return true;
        }

        public bool BeginFrame(PresentationSurfaceId surface)
        {
            Record($"BeginFrame({surface.Value})");
            return true;
        }

        public bool EndFrame(PresentationSurfaceId surface)
        {
            Record($"EndFrame({surface.Value})");
            return true;
        }
    }

    private sealed class FakeSurface : IWindowSurface
    {
        public nint NativeHandle => 1;
        public (int Width, int Height) FramebufferSize => (4, 4);
        public bool VSync { get; set; }
        public int SwapInterval { get; set; }
        public void MakeCurrent() { }
        public void ClearCurrent() { }
        public void SwapBuffers() { }
    }

    private static CommandBuffer Rent(string name)
    {
        CommandBuffer cmd = CommandBufferPool.Rent(name);
        return cmd;
    }

    // ─────────────────────── Ordering / threading ───────────────────────

    [Fact]
    public void Submitted_Command_Buffers_Run_On_The_Render_Thread_In_Submit_Order()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);

        device.BeginFrame();
        for (int i = 0; i < 8; i++)
            device.Execute(Rent($"cb{i}"), wait: false);
        device.EndFrame();

        string[] calls = fake.Snapshot();
        Assert.Equal("BeginFrame", calls[0]);
        for (int i = 0; i < 8; i++)
            Assert.Equal($"Execute(cb{i},False)", calls[i + 1]);
        Assert.Equal("EndFrame", calls[9]);
        Assert.NotNull(fake.LastExecuteThread);
        Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, fake.LastExecuteThread!.ManagedThreadId);
        Assert.Equal(8, device.ExecutedCommandBuffers);
    }

    [Fact]
    public void Fire_And_Forget_Execute_Does_Not_Block_The_Caller()
    {
        var fake = new FakeDevice();
        using var gate = new ManualResetEventSlim(false);
        fake.BlockExecute = gate;
        using var device = new RenderThreadGraphicsDevice(fake);

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        device.Execute(Rent("slow"), wait: false);
        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // The render thread is parked inside Execute; the producer must have returned anyway.
        Assert.True(elapsedMs < 250, $"Submit blocked for {elapsedMs:F1} ms while the render thread was busy.");
        gate.Set();
        device.WaitIdle();
        Assert.Contains("Execute(slow,False)", fake.Snapshot());
    }

    [Fact]
    public void Execute_With_Wait_Blocks_Until_The_Backend_Finished()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);
        bool observedInsideExecute = false;
        fake.OnExecute = _ =>
        {
            Thread.Sleep(30);
            observedInsideExecute = true;
        };

        device.Execute(Rent("readback"), wait: true);

        Assert.True(observedInsideExecute);
        Assert.Contains("Execute(readback,True)", fake.Snapshot());
    }

    [Fact]
    public void Command_Buffer_Ownership_Transfers_Before_The_Job_Is_Queued()
    {
        var fake = new FakeDevice();
        using var gate = new ManualResetEventSlim(false);
        fake.BlockExecute = gate;
        using var device = new RenderThreadGraphicsDevice(fake);

        CommandBuffer cmd = Rent("owned");
        device.Execute(cmd, wait: false);
        // A `using` scope on the producer side closes here, long before the render thread runs
        // the job. Without ownership transfer this would return the live buffer to the pool.
        cmd.Dispose();
        Assert.False(cmd._inPool);

        gate.Set();
        device.WaitIdle();
        Assert.True(cmd._inPool);
    }

    // ─────────────────────── Error propagation ───────────────────────

    [Fact]
    public void Fire_And_Forget_Failure_Surfaces_On_The_Next_Main_Thread_Call()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);
        fake.ThrowOnNextExecute = new InvalidOperationException("ErrorDeviceLost");

        device.Execute(Rent("boom"), wait: false);
        device.WaitIdle(); // ensure the render thread has consumed the failing job

        InvalidOperationException raised = Assert.Throws<InvalidOperationException>(
            () => device.Execute(Rent("next"), wait: false));
        Assert.Contains("ErrorDeviceLost", raised.Message);

        // The pending error is reported once, not on every later call.
        device.Execute(Rent("after"), wait: false);
        device.WaitIdle();
    }

    [Fact]
    public void Waiting_Execute_Rethrows_The_Backend_Exception_On_The_Caller()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);
        fake.ThrowOnNextExecute = new NotSupportedException("shader compile failed");

        NotSupportedException raised = Assert.Throws<NotSupportedException>(
            () => device.Execute(Rent("compile"), wait: true));
        Assert.Contains("shader compile failed", raised.Message);
    }

    // ─────────────────────── Re-entrancy ───────────────────────

    [Fact]
    public void Submitting_From_The_Render_Thread_Runs_Inline_Instead_Of_Deadlocking()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);
        bool nestedRan = false;
        fake.OnExecute = self =>
        {
            if (nestedRan) return;
            nestedRan = true;
            // A backend that submits while translating (resource fixups) must not self-deadlock.
            Assert.True(device.IsOnRenderThread);
            device.Execute(Rent("nested"), wait: true);
        };

        device.Execute(Rent("outer"), wait: true);

        string[] calls = fake.Snapshot();
        Assert.Contains("Execute(nested,True)", calls);
        Assert.Contains("Execute(outer,True)", calls);
    }

    // ─────────────────────── Lifecycle ───────────────────────

    [Fact]
    public void Surface_Lifecycle_Round_Trips_Through_The_Queue()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);

        PresentationSurfaceId id = device.CreatePresentationSurface(new FakeSurface());
        Assert.True(id.IsValid);
        Assert.True(device.BeginFrame(id));
        device.Execute(Rent("panel"), wait: false);
        Assert.True(device.EndFrame(id));
        Assert.True(device.ResizePresentationSurface(id, 8, 8));
        Assert.True(device.DestroyPresentationSurface(id));

        Assert.Equal(
            new[]
            {
                "CreateSurface",
                $"BeginFrame({id.Value})",
                "Execute(panel,False)",
                $"EndFrame({id.Value})",
                $"ResizeSurface({id.Value},8x8)",
                $"DestroySurface({id.Value})",
            },
            fake.Snapshot());
    }

    [Fact]
    public void Initialize_Resize_And_WaitFence_Are_Ordered_With_Command_Buffers()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);

        device.Initialize(new FakeSurface());
        device.Execute(Rent("a"), wait: false);
        device.Resize(640, 480);
        device.Execute(Rent("b"), wait: false);
        device.WaitFence(7);
        device.WaitIdle();

        Assert.Equal(
            new[] { "Initialize", "Execute(a,False)", "Resize(640x480)", "Execute(b,False)", "WaitFence(7)", "WaitIdle" },
            fake.Snapshot());
        Assert.True(device.IsInitialized);
    }

    [Fact]
    public void Shutdown_Drains_The_Queue_And_Retires_The_Thread()
    {
        var fake = new FakeDevice();
        var device = new RenderThreadGraphicsDevice(fake);
        for (int i = 0; i < 16; i++)
            device.Execute(Rent($"pending{i}"), wait: false);

        device.Shutdown();

        string[] calls = fake.Snapshot();
        Assert.Equal("Shutdown", calls[^1]);
        for (int i = 0; i < 16; i++)
            Assert.Contains($"Execute(pending{i},False)", calls);
        // After shutdown the wrapper falls back to inline execution instead of hanging.
        device.Execute(Rent("post"), wait: true);
        Assert.Contains("Execute(post,True)", fake.Snapshot());
        device.Dispose();
    }

    [Fact]
    public void Gpu_Timestamp_Frame_Is_Published_At_Frame_End()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);
        Assert.False(device.TryGetCompletedGpuTimestampFrame(out _));

        fake.Timestamp = new GpuTimestampFrame(3, 1.25f);
        fake.HasTimestamp = true;
        device.BeginFrame();
        device.EndFrame();

        Assert.True(device.TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame frame));
        Assert.Equal(3ul, frame.FrameId);
    }

    [Fact]
    public void EndFrame_Records_The_Main_Thread_Wait()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);
        fake.OnExecute = _ => Thread.Sleep(40);

        device.BeginFrame();
        device.Execute(Rent("slow"), wait: false);
        device.EndFrame();

        // EndFrame is the sync point in stage A, so the wait must include the queued translate.
        Assert.True(device.LastFrameWaitMs >= 10f, $"LastFrameWaitMs={device.LastFrameWaitMs}");
    }

    [Fact]
    public void Wrapping_An_Already_Threaded_Device_Is_Rejected()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake);
        Assert.Throws<ArgumentException>(() => new RenderThreadGraphicsDevice(device));
    }

    // ─────────────────────── Stage B: one frame ahead ───────────────────────

    [Fact]
    public void Pipelined_EndFrame_Returns_Before_The_Frame_Has_Been_Presented()
    {
        var fake = new FakeDevice();
        using var hold = new ManualResetEventSlim(false);
        fake.BlockEndFrame = hold;
        using var device = new RenderThreadGraphicsDevice(fake) { MaxQueuedFrames = 1 };

        device.BeginFrame();
        device.Execute(Rent("frame0"), wait: false);
        device.EndFrame(); // must not block: nothing is owed yet

        Assert.True(device.HasQueuedFrame);
        Assert.Empty(Array.FindAll(fake.Snapshot(), c => c == "EndFrame"));

        hold.Set();
        device.BeginFrame();
        device.EndFrame(); // blocks on frame 0 only
        device.WaitIdle();

        string[] calls = fake.Snapshot();
        Assert.Equal(
            new[] { "BeginFrame", "Execute(frame0,False)", "EndFrame", "BeginFrame", "EndFrame", "WaitIdle" },
            calls);
    }

    [Fact]
    public void Pipelining_Off_Keeps_EndFrame_Synchronous()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake) { MaxQueuedFrames = 0 };

        device.BeginFrame();
        device.EndFrame();

        Assert.False(device.HasQueuedFrame);
        Assert.Equal(new[] { "BeginFrame", "EndFrame" }, fake.Snapshot());
    }

    [Fact]
    public void MaxQueuedFrames_Is_Clamped_To_One()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake) { MaxQueuedFrames = 9 };
        Assert.Equal(1, device.MaxQueuedFrames);
        device.MaxQueuedFrames = -5;
        Assert.Equal(0, device.MaxQueuedFrames);
    }

    [Fact]
    public void Pipelined_Frame_Failure_Surfaces_On_The_Following_EndFrame()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake) { MaxQueuedFrames = 1 };
        fake.ThrowOnNextEndFrame = new InvalidOperationException("ErrorDeviceLost on present");

        device.BeginFrame();
        device.EndFrame(); // pipelined, error not yet observable
        device.BeginFrame();

        InvalidOperationException raised = Assert.Throws<InvalidOperationException>(device.EndFrame);
        Assert.Contains("ErrorDeviceLost", raised.Message);
    }

    [Fact]
    public void Switching_Pipelining_Off_Settles_The_Outstanding_Frame()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake) { MaxQueuedFrames = 1 };

        device.BeginFrame();
        device.EndFrame();
        Assert.True(device.HasQueuedFrame);

        device.MaxQueuedFrames = 0;
        device.BeginFrame();
        device.EndFrame();

        Assert.False(device.HasQueuedFrame);
        Assert.Equal(new[] { "BeginFrame", "EndFrame", "BeginFrame", "EndFrame" }, fake.Snapshot());
    }

    [Fact]
    public void Shutdown_Settles_A_Pipelined_Frame_Before_Tearing_Down()
    {
        var fake = new FakeDevice();
        var device = new RenderThreadGraphicsDevice(fake) { MaxQueuedFrames = 1 };
        device.BeginFrame();
        device.Execute(Rent("last"), wait: false);
        device.EndFrame();

        device.Shutdown();

        string[] calls = fake.Snapshot();
        Assert.Equal(new[] { "BeginFrame", "Execute(last,False)", "EndFrame", "Shutdown" }, calls);
        Assert.False(device.HasQueuedFrame);
        device.Dispose();
    }

    [Fact]
    public void Pipelined_Waiting_Execute_Still_Observes_The_Earlier_Frame()
    {
        var fake = new FakeDevice();
        using var device = new RenderThreadGraphicsDevice(fake) { MaxQueuedFrames = 1 };

        device.BeginFrame();
        device.EndFrame();
        // A read-back submitted while a frame is still queued must not overtake it.
        device.Execute(Rent("readback"), wait: true);

        Assert.Equal(new[] { "BeginFrame", "EndFrame", "Execute(readback,True)" }, fake.Snapshot());
    }

    // ─────────────────────── Real backends ───────────────────────

    [Fact]
    public void Optional_Vulkan_RenderThread_Preserves_Per_Frame_Draw_Results_Or_Skip()
    {
        try
        {
            using var inner = new Backends.Vulkan.VulkanGraphicsDevice(
                new GraphicsDeviceOptions { Backend = GraphicsBackend.Vulkan });
            inner.Initialize(null);
            using var device = new RenderThreadGraphicsDevice(inner);

            const int frameCount = 8;
            var colors = new GraphicsTexture[frameCount];
            var framebuffers = new GraphicsFrameBuffer[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                colors[i] = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
                framebuffers[i] = GraphicsFrameBuffer.CreateDeferred(
                    [new GraphicsFrameBuffer.Attachment { Texture = colors[i] }], 1, 1);
            }

            using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("vulkan-render-thread-create"))
            {
                for (int i = 0; i < frameCount; i++)
                {
                    create.EncodeCreateTexture(colors[i]);
                    create.EncodeAllocateTexture2D(colors[i], 0, 1, 1, 0, ReadOnlySpan<byte>.Empty);
                    create.EncodeCreateFramebuffer(framebuffers[i]);
                }
                device.Execute(create, wait: true);
            }

            for (int i = 0; i < frameCount; i++)
            {
                using CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("vulkan-render-thread-clear");
                draw.SetRenderTarget(framebuffers[i]);
                draw.ClearRenderTarget(ClearFlags.Color, new Color(i / 255f, 1f - i / 255f, 0f, 1f));
                device.Execute(draw, wait: false);
            }

            device.WaitIdle();
            for (int i = 0; i < frameCount; i++)
            {
                byte[] pixels = inner.ReadTexture2D(inner.Images[colors[i].Handle], 4);
                Assert.Equal((byte)i, pixels[0]);
                Assert.Equal((byte)(255 - i), pixels[1]);
            }

            using (CommandBuffer dispose = global::XEngine.Runtime.Graphics.GetCommandBuffer("vulkan-render-thread-dispose"))
            {
                for (int i = 0; i < frameCount; i++)
                    dispose.EncodeDisposeFramebuffer(framebuffers[i]);
                device.Execute(dispose, wait: true);
            }
            for (int i = 0; i < frameCount; i++)
                colors[i].Dispose();
            device.Shutdown();
        }
        catch (Exception ex)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected Vulkan render-thread failure: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    [Fact]
    public void Optional_D3D12_RenderThread_Preserves_Per_Frame_Draw_Results_Or_Skip()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            using var inner = new Backends.D3D12.D3D12GraphicsDevice(
                new GraphicsDeviceOptions { Backend = GraphicsBackend.Direct3D12 });
            inner.Initialize(null);
            using var device = new RenderThreadGraphicsDevice(inner);

            const int frameCount = 8;
            var colors = new GraphicsTexture[frameCount];
            var framebuffers = new GraphicsFrameBuffer[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                colors[i] = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
                framebuffers[i] = GraphicsFrameBuffer.CreateDeferred(
                    [new GraphicsFrameBuffer.Attachment { Texture = colors[i] }], 1, 1);
            }

            using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("d3d12-render-thread-create"))
            {
                for (int i = 0; i < frameCount; i++)
                {
                    create.EncodeCreateTexture(colors[i]);
                    create.EncodeAllocateTexture2D(colors[i], 0, 1, 1, 0, ReadOnlySpan<byte>.Empty);
                    create.EncodeCreateFramebuffer(framebuffers[i]);
                }
                device.Execute(create, wait: true);
            }

            for (int i = 0; i < frameCount; i++)
            {
                using CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("d3d12-render-thread-clear");
                draw.SetRenderTarget(framebuffers[i]);
                draw.ClearRenderTarget(ClearFlags.Color, new Color(i / 255f, 1f - i / 255f, 0f, 1f));
                device.Execute(draw, wait: false);
            }

            device.WaitIdle();
            for (int i = 0; i < frameCount; i++)
            {
                byte[] pixels = inner.ReadTexture2D(
                    inner.Textures[colors[i].Handle].Resource!,
                    1,
                    1,
                    4,
                    inner.Textures[colors[i].Handle].State);
                Assert.Equal((byte)i, pixels[0]);
                Assert.Equal((byte)(255 - i), pixels[1]);
            }

            using (CommandBuffer dispose = global::XEngine.Runtime.Graphics.GetCommandBuffer("d3d12-render-thread-dispose"))
            {
                for (int i = 0; i < frameCount; i++)
                    dispose.EncodeDisposeFramebuffer(framebuffers[i]);
                device.Execute(dispose, wait: true);
            }
            for (int i = 0; i < frameCount; i++)
                colors[i].Dispose();
            device.Shutdown();
        }
        catch (Exception ex)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected D3D12 render-thread failure: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>Mirrors the RHI contract suite's "no GPU on this box" classifier.</summary>
    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                or PlatformNotSupportedException or TypeInitializationException)
                return true;
            string message = current.Message;
            if (message.Contains("vulkan", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("no ", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("ErrorIncompatibleDriver", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (message.Contains("D3D12CreateDevice", StringComparison.OrdinalIgnoreCase)
                || message.Contains("no compatible", StringComparison.OrdinalIgnoreCase)
                || message.Contains("adapter", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
