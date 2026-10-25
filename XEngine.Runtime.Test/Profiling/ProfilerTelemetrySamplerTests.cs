using System;
using System.Net;
using XEngine.Runtime.Profiling;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ProfilerTelemetrySamplerTests
{
    [Fact]
    public void Capture_UsesExplicitSourcesForPortableMetrics()
    {
        ProfilerTelemetrySampler.ResetForTests();

        ProfilerTelemetryFrame frame = ProfilerTelemetrySampler.Capture(7, ProfilerTelemetryTarget.AndroidPlayer);

        Assert.Equal((ulong)7, frame.FrameIndex);
        Assert.Equal(ProfilerTelemetryTarget.AndroidPlayer, frame.Target);
        Assert.Equal(ProfilerMetricSource.Runtime, frame.ManagedHeapSource);
        Assert.Equal(ProfilerMetricSource.Unavailable, frame.RhiMemorySource);
        Assert.Equal(ProfilerMetricSource.Unavailable, frame.AdapterVramBudgetSource);
        Assert.Equal(0, frame.RhiMemoryBytes);
        Assert.Equal(0, frame.AdapterVramBudgetBytes);
        Assert.True(frame.TimestampUnixMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData("127.0.0.1:48765", "127.0.0.1", 48765)]
    [InlineData("[::1]:48765", "::1", 48765)]
    public void CommandLine_ParsesLiteralDirectIpEndpoints(string text, string expectedAddress, int expectedPort)
    {
        Assert.True(ProfilerTelemetryCommandLine.TryParseEndpoint(text, out IPEndPoint endpoint));
        Assert.Equal(expectedAddress, endpoint.Address.ToString());
        Assert.Equal(expectedPort, endpoint.Port);
    }

    [Theory]
    [InlineData("localhost:48765")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("::1:48765")]
    public void CommandLine_RejectsNonDirectOrInvalidEndpoints(string text)
    {
        Assert.False(ProfilerTelemetryCommandLine.TryParseEndpoint(text, out _));
    }
}
