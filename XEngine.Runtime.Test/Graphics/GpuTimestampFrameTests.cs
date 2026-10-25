// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class GpuTimestampFrameTests
{
    [Fact]
    public void DefaultDeviceTimestampContract_ReportsNoResolvedSample()
    {
        IGraphicsDevice device = new TimestampContractDevice();

        Assert.False(device.TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame frame));
        Assert.Equal(default, frame);
        Assert.False(device.SupportsGpuCommandTimings);
        Assert.False(device.TryDequeueGpuCommandTiming(out _));
        Assert.Equal(0, device.DroppedGpuCommandTimings);
    }

    [Fact]
    public void TimestampFrame_PreservesFenceRetiredInterval()
    {
        var frame = new GpuTimestampFrame(42, 3.5f);

        Assert.Equal(42UL, frame.FrameId);
        Assert.Equal(3.5f, frame.DurationMs);
    }

    private sealed class TimestampContractDevice : IGraphicsDevice
    {
        public GraphicsBackend Backend => GraphicsBackend.Null;
        public GraphicsDeviceCapabilities Capabilities { get; } = new();
        public bool IsInitialized => true;
        public void Initialize(IWindowSurface? surface) { }
        public void Shutdown() { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Execute(CommandBuffer commandBuffer, bool wait) { }
        public void WaitIdle() { }
        public ulong GetFenceValue() => 0;
        public void WaitFence(ulong fenceValue) { }
        public void Dispose() { }
    }
}
