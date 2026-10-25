using XEngine.Runtime;
using XEngine.Runtime.RHI;
using Xunit;

namespace XEngine.Runtime.Test;

public class GpuCommandTimingTests
{
    [Fact]
    public void BoundedBufferPreservesSurfaceOrderAndReportsOverflow()
    {
        var buffer = new GpuCommandTimingBuffer(2);
        buffer.Publish(new GpuCommandTiming(1, new(10), "UI", .1f));
        buffer.Publish(new GpuCommandTiming(2, new(20), "UI", .2f));
        buffer.Publish(new GpuCommandTiming(3, new(10), "UI", .3f));
        Assert.Equal(1, buffer.Dropped);
        Assert.True(buffer.TryRead(out var second));
        Assert.Equal(2ul, second.FrameId);
        Assert.Equal(20ul, second.Surface.Value);
        Assert.True(buffer.TryRead(out var third));
        Assert.Equal(3ul, third.FrameId);
        Assert.False(buffer.TryRead(out _));
    }

    [Fact]
    public void ReusedCommandDoesNotRetainDiagnosticOptIn()
    {
        var command = new CommandBuffer();
        command.OnRent("diagnostic");
        command.CaptureGpuTiming = true;
        command.OnReturn();
        command.OnRent("ordinary");
        Assert.False(command.CaptureGpuTiming);
        command.OnReturn();
    }

    [Fact]
    public void FixedBufferHandoffDoesNotAllocateAfterWarmup()
    {
        var buffer = new GpuCommandTimingBuffer(4);
        var sample = new GpuCommandTiming(1, new(1), "UI", .5f);
        for (int i = 0; i < 100; i++) { buffer.Publish(in sample); buffer.TryRead(out _); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { buffer.Publish(in sample); buffer.TryRead(out _); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
