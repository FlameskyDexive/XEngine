using System;
using System.Net;
using System.Threading;
using XEngine.Runtime.Profiling;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ProfilerTelemetryProtocolTests
{
    [Fact]
    public void Protocol_RoundTripsFixedVersionedFrame()
    {
        var frame = CreateFrame();
        Span<byte> packet = stackalloc byte[ProfilerTelemetryProtocol.PacketSize];

        Assert.True(ProfilerTelemetryProtocol.TryWrite(frame, packet, out int written));
        Assert.Equal(ProfilerTelemetryProtocol.PacketSize, written);
        Assert.True(ProfilerTelemetryProtocol.TryRead(packet, out ProfilerTelemetryFrame decoded));
        Assert.Equal(frame, decoded);
    }

    [Fact]
    public void Protocol_RejectsNonBoundedAndUnknownPackets()
    {
        var frame = CreateFrame();
        byte[] packet = new byte[ProfilerTelemetryProtocol.PacketSize];
        Assert.False(ProfilerTelemetryProtocol.TryWrite(frame, packet.AsSpan(0, packet.Length - 1), out _));
        Assert.True(ProfilerTelemetryProtocol.TryWrite(frame, packet, out _));
        Assert.False(ProfilerTelemetryProtocol.TryRead(packet.AsSpan(0, packet.Length - 1), out _));
        Assert.False(ProfilerTelemetryProtocol.TryRead(new byte[ProfilerTelemetryProtocol.PacketSize + 1], out _));

        packet[4] = 2;
        Assert.False(ProfilerTelemetryProtocol.TryRead(packet, out _));
    }

    [Fact]
    public void Protocol_RequiresUnavailableMetricsToRemainZero()
    {
        ProfilerTelemetryFrame frame = CreateFrame() with
        {
            RhiMemoryBytes = 1,
            RhiMemorySource = ProfilerMetricSource.Unavailable,
        };
        Span<byte> packet = stackalloc byte[ProfilerTelemetryProtocol.PacketSize];

        Assert.False(ProfilerTelemetryProtocol.TryWrite(frame, packet, out _));
    }

    [Fact]
    public void UdpTransport_ExchangesOnlyValidatedFixedFrames()
    {
        using var receiver = new ProfilerTelemetryReceiver(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new ProfilerTelemetrySender(receiver.LocalEndpoint);
        ProfilerTelemetryFrame expected = CreateFrame();

        Assert.True(sender.TrySend(expected));

        for (int i = 0; i < 100; i++)
        {
            if (receiver.TryReceive(out ProfilerTelemetryFrame actual))
            {
                Assert.Equal(expected, actual);
                return;
            }
            Thread.Sleep(1);
        }

        Assert.Fail("Timed out waiting for profiler loopback datagram.");
    }

    private static ProfilerTelemetryFrame CreateFrame() => new(
        TimestampUnixMilliseconds: 1_725_000_000_123,
        FrameIndex: 42,
        ProcessCpuPercent: 25.5f,
        GpuFrameTimeMs: 4.25f,
        WorkingSetBytes: 123_456_789,
        ManagedHeapBytes: 98_765_432,
        RhiMemoryBytes: 456_789,
        AdapterVramBudgetBytes: 8_000_000_000,
        Backend: GraphicsBackend.Vulkan,
        Target: ProfilerTelemetryTarget.AndroidPlayer,
        Capabilities: ProfilerTelemetryCapabilities.GpuTimestampQueries
            | ProfilerTelemetryCapabilities.RhiMemory
            | ProfilerTelemetryCapabilities.AdapterVramBudget
            | ProfilerTelemetryCapabilities.GpuFrameTimestampSample,
        ProcessCpuSource: ProfilerMetricSource.OperatingSystem,
        WorkingSetSource: ProfilerMetricSource.OperatingSystem,
        ManagedHeapSource: ProfilerMetricSource.Runtime,
        RhiMemorySource: ProfilerMetricSource.Rhi,
        AdapterVramBudgetSource: ProfilerMetricSource.Platform);
}
