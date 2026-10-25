using System.Net;
using System.Threading;
using XEngine.Editor.Core;
using XEngine.Runtime.Profiling;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorProfilerCaptureTests
{
    [Fact]
    public void Capture_ReceivesNewestValidatedPlayerFrame()
    {
        using var capture = new EditorProfilerCapture();
        Assert.True(capture.TryStart(0));
        using var sender = new ProfilerTelemetrySender(new IPEndPoint(IPAddress.Loopback, capture.Port));
        var expected = new ProfilerTelemetryFrame(
            TimestampUnixMilliseconds: 1,
            FrameIndex: 2,
            ProcessCpuPercent: 20f,
            GpuFrameTimeMs: 0f,
            WorkingSetBytes: 3,
            ManagedHeapBytes: 4,
            RhiMemoryBytes: 0,
            AdapterVramBudgetBytes: 0,
            Backend: GraphicsBackend.Vulkan,
            Target: ProfilerTelemetryTarget.AndroidPlayer,
            Capabilities: ProfilerTelemetryCapabilities.None,
            ProcessCpuSource: ProfilerMetricSource.OperatingSystem,
            WorkingSetSource: ProfilerMetricSource.OperatingSystem,
            ManagedHeapSource: ProfilerMetricSource.Runtime,
            RhiMemorySource: ProfilerMetricSource.Unavailable,
            AdapterVramBudgetSource: ProfilerMetricSource.Unavailable);

        Assert.True(sender.TrySend(expected));
        for (int i = 0; i < 100; i++)
        {
            if (capture.Poll())
            {
                Assert.True(capture.HasFrame);
                Assert.Equal(expected, capture.LastFrame);
                return;
            }
            Thread.Sleep(1);
        }

        Assert.Fail("Timed out waiting for player profiler frame.");
    }
}
