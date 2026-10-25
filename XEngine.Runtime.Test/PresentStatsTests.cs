using System;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Contract tests for the per-present cadence telemetry: the sustained EMA must only aggregate
/// back-to-back rendered frames (stream breaks restart it), the ring must expose newest-first
/// deltas, and PresentFps counts presents inside the trailing second.
/// </summary>
public class PresentStatsTests
{
    private static long T(double ms) => (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    [Fact]
    public void NextEma_StreamBreak_RestartsInsteadOfPolluting()
    {
        // A 5000ms gap is an idle stream break: the EMA restarts at the fresh delta
        // rather than being dragged up by the idle time.
        Assert.Equal(5000.0, Window.PresentStats.PresentStatsNextEmaForTests(16.0, 5000.0));
    }

    [Fact]
    public void NextEma_ConsecutiveFrames_Converge()
    {
        double ema = 0;
        foreach (double d in new[] { 16.0, 16.0, 16.0, 16.0, 16.0, 16.0, 16.0, 16.0, 16.0, 16.0 })
            ema = Window.PresentStats.PresentStatsNextEmaForTests(ema, d);
        Assert.Equal(16.0, ema, 5);
    }

    [Fact]
    public void Ring_NewestFirst_AndCapacityBounded()
    {
        Window.PresentStats.PresentStatsResetForTests();
        long t = 0;
        for (int i = 1; i <= 300; i++) // 300 > RingSize(240)
        {
            t += T(10);
            Window.PresentStats.PresentStatsRecordForTests(t, System.Diagnostics.Stopwatch.Frequency, cpuMs: 1.0);
        }
        Assert.Equal(240, Window.PresentStats.RingCountForTests());
        Assert.Equal(10.0, Window.PresentStats.DeltaForTests(0), 5);   // newest
        Assert.Equal(10.0, Window.PresentStats.DeltaForTests(239), 5); // oldest retained
        Assert.Equal(0.0, Window.PresentStats.DeltaForTests(240), 5);  // out of range
    }

    [Fact]
    public void PresentFps_CountsTrailingSecond()
    {
        Window.PresentStats.PresentStatsResetForTests();
        long t = 0;
        // 60 presents at 16.7ms = ~1s of continuous rendering.
        for (int i = 0; i < 60; i++)
        {
            t += T(16.7);
            Window.PresentStats.PresentStatsRecordForTests(t, System.Diagnostics.Stopwatch.Frequency, cpuMs: 1.0);
        }
        Assert.InRange(Window.PresentStats.PresentFpsForTests(), 55, 61);
    }

    [Fact]
    public void PresentFps_IdleGap_DropsRate()
    {
        Window.PresentStats.PresentStatsResetForTests();
        long t = 0;
        for (int i = 0; i < 30; i++) { t += T(16.7); Window.PresentStats.PresentStatsRecordForTests(t, System.Diagnostics.Stopwatch.Frequency, 0); }
        t += T(2000); // two seconds idle — the trailing-second window now holds nothing
        Window.PresentStats.PresentStatsRecordForTests(t, System.Diagnostics.Stopwatch.Frequency, 0);
        Assert.Equal(1, Window.PresentStats.PresentFpsForTests()); // only the post-gap present
    }

    [Fact]
    public void CpuFrameMs_LastWriteWins()
    {
        Window.PresentStats.PresentStatsResetForTests();
        Window.PresentStats.PresentStatsRecordForTests(T(0) + 1, System.Diagnostics.Stopwatch.Frequency, cpuMs: 3.5);
        Window.PresentStats.PresentStatsRecordForTests(T(0) + 1 + T(16), System.Diagnostics.Stopwatch.Frequency, cpuMs: 9.25);
        Assert.Equal(9.25, Window.PresentStats.CpuMsForTests(), 5);
    }
}
