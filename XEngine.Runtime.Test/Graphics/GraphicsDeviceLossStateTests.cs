using System;

using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

public sealed class GraphicsDeviceLossStateTests
{
    [Fact]
    public void FirstLossTransitionsToTerminalState()
    {
        var state = new GraphicsDeviceLossState();

        Assert.True(state.RecordLoss("vkQueueSubmit", "ErrorDeviceLost"));
        Assert.True(state.IsLost);
        Assert.Equal(1, state.LossCount);
        Assert.Equal("vkQueueSubmit", state.LastOperation);
        Assert.Throws<InvalidOperationException>(state.ThrowIfLost);
    }

    [Fact]
    public void RepeatedLossesUpdateDiagnosticWithoutIncreasingEpisodes()
    {
        var state = new GraphicsDeviceLossState();

        state.RecordLoss("vkQueueSubmit", "ErrorDeviceLost");
        state.RecordLoss("vkQueuePresentKHR", "ErrorDeviceLost");

        Assert.Equal(1, state.LossCount);
        Assert.Equal("vkQueuePresentKHR", state.LastOperation);
        Assert.Equal("ErrorDeviceLost", state.LastResult);
    }

    [Fact]
    public void InvalidDiagnosticsAreRejected()
    {
        var state = new GraphicsDeviceLossState();

        Assert.Throws<ArgumentException>(() => state.RecordLoss("", "ErrorDeviceLost"));
        Assert.Throws<ArgumentException>(() => state.RecordLoss("vkQueueSubmit", ""));
    }
}
