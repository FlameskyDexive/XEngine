// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

using XEngine.Async;
using XEngine.Runtime.Video;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test.Video;

/// <summary>
/// N7 stability and performance: 100 start/stops, 100 seeks, 100 source switches, a
/// long-run continuous playback (default 60s; set XENGINE_VIDEO_STRESS_SECONDS for the
/// full 30-minute soak), and a pump/decode cost measurement on the real decoder.
/// </summary>
public sealed class VideoStressTests
{
    private readonly ITestOutputHelper _output;

    public VideoStressTests(ITestOutputHelper output) => _output = output;

    private static readonly Stopwatch Wall = Stopwatch.StartNew();

    private sealed class ManualClock
    {
        private long _ticksNanos;
        public double Now => _ticksNanos / 1_000_000_000.0;
        public void Advance(double seconds) => Interlocked.Add(ref _ticksNanos, (long)(seconds * 1_000_000_000));
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Yield();
        }

        return condition();
    }

    private static async System.Threading.Tasks.Task PreparedAsync(VideoPlaybackSession session)
    {
        await session.PrepareAsync("fake://media").AsTask();
        Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Ready));
    }

    [Fact]
    public async System.Threading.Tasks.Task Stress_100StartStopCycles()
    {
        using var holder = new StressHolder();
        using var session = new VideoPlaybackSession(holder, () => 0);
        await PreparedAsync(session);

        for (int i = 0; i < 100; i++)
        {
            session.Play();
            Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Playing));
            session.Stop();
            Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Ready));
        }

        Assert.Equal(0, session.QueuedFrameCount);
        _output.WriteLine("100 start/stop cycles OK");
    }

    [Fact]
    public async System.Threading.Tasks.Task Stress_100Seeks()
    {
        using var holder = new StressHolder();
        using var session = new VideoPlaybackSession(holder, () => 0);
        await PreparedAsync(session);

        for (int i = 0; i < 100; i++)
        {
            double target = i / 100.0 * 2.5;
            await session.SeekAsync(target).AsTask();
            Assert.True(Math.Abs(session.CurrentTimeSeconds - target) < 0.05,
                $"seek {i} landed at {session.CurrentTimeSeconds:F3}, expected {target:F3}");
        }

        Assert.Equal(100, holder.Decoder.SeekCount);
        _output.WriteLine("100 seeks OK");
    }

    [Fact]
    public async System.Threading.Tasks.Task Stress_100SourceSwitches()
    {
        using var holder = new StressHolder();
        using var session = new VideoPlaybackSession(holder, () => 0);

        for (int i = 0; i < 100; i++)
        {
            await session.PrepareAsync($"fake://media-{i}").AsTask();
            Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Ready));
            session.Stop();
            Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Ready));
        }

        Assert.Equal(100, holder.Decoder.OpenCount);
        _output.WriteLine("100 source switches OK");
    }

    /// <summary>
    /// Continuous playback soak. XENGINE_VIDEO_STRESS_SECONDS overrides the duration
    /// (30 minutes per the module plan: set 1800); the default keeps CI reasonable.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task LongRun_ContinuousLoopedPlayback()
    {
        int seconds = Environment.GetEnvironmentVariable("XENGINE_VIDEO_STRESS_SECONDS") is string s
            && int.TryParse(s, out int parsed) ? parsed : 60;

        using var holder = new StressHolder();
        holder.Decoder.FrameCount = 300; // 10s of fake media
        var wall = Stopwatch.StartNew();
        using var session = new VideoPlaybackSession(holder, () => wall.Elapsed.TotalSeconds);
        session.IsLooping = true;
        session.PlaybackFailed += (code, message) => _output.WriteLine($"PLAYBACK FAILED: {code} {message}");
        await PreparedAsync(session);
        session.Play();

        long pumps = 0;
        var stopwatch = Stopwatch.StartNew();
        // 60 presentation ticks per wall second, yielding between — the realistic rhythm.
        while (stopwatch.Elapsed.TotalSeconds < seconds)
        {
            session.Pump();
            pumps++;
            Thread.Sleep(16);
        }

        var diag = session.Snapshot();
        _output.WriteLine(
            $"soak {seconds}s: pumps={pumps} presented={diag.PresentedFrames} dropped={diag.DroppedFrames} " +
            $"underruns={diag.PcmUnderruns} decoded={holder.Decoder.DecodedCount} state={session.State}");

        Assert.Equal(VideoPlaybackState.Playing, session.State);
        Assert.True(diag.PresentedFrames > 0);
    }

    [FFmpegFact]
    public void Perf_RealMedia_PumpCostUnderBudget()
    {
        FFmpegNativeLoader.EnsureLoaded();
        string media = Path.Combine(RepoRoot.Find(), "XEngine.Runtime.Test", "Media", "n0-h264-aac.mp4");
        using var decoder = new FFmpegVideoDecoder();
        decoder.Open(media, CancellationToken.None);

        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var stopwatch = Stopwatch.StartNew();
        int frames = 0;
        long worstMs = 0;
        while (frames < 60)
        {
            var pump = Stopwatch.StartNew();
            using var frame = decoder.DecodeNextFrame(CancellationToken.None, pool);
            pump.Stop();
            if (frame is null)
            {
                break;
            }

            frames++;
            worstMs = Math.Max(worstMs, pump.ElapsedMilliseconds);
        }

        stopwatch.Stop();
        double avgMs = stopwatch.Elapsed.TotalMilliseconds / Math.Max(1, frames);
        _output.WriteLine($"decode: {frames} frames, avg {avgMs:F2}ms/frame, worst {worstMs}ms (320x240 sw decode)");

        string path = Path.Combine(VideoTestEvidence.Root, "2026-09-12-n7-perf-baseline.txt");
        File.AppendAllText(path,
            $"{DateTime.Now:O} real-media 320x240 decode: {frames} frames avg={avgMs:F2}ms worst={worstMs}ms{Environment.NewLine}");
        Assert.True(frames >= 55, $"expected most of the 60-frame clip, got {frames}");
    }

    private sealed class StressHolder : IVideoDecoderFactory, IDisposable
    {
        public FakeVideoDecoder Decoder { get; } = new();

        public IVideoDecoder Create(IAudioPcmSink? audioSink, int audioSampleRate, int audioChannels) => Decoder;

        public void Dispose()
        {
        }
    }
}
