// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

using Xunit;
using Xunit.Abstractions;

using XEngine.Async;
using XEngine.Runtime.Video;

namespace XEngine.Runtime.Test.Video;

/// <summary>
/// Deterministic playback-kernel tests: fake decoder + injected clock. Time only moves
/// when a test advances it, so state transitions, PTS ordering, late-frame dropping,
/// seek semantics, loop, and queue bounds are all exact.
/// </summary>
public sealed class VideoPlaybackSessionTests
{
    private readonly ITestOutputHelper _output;

    public VideoPlaybackSessionTests(ITestOutputHelper output) => _output = output;

    /// <summary>Manual monotonic clock the test advances explicitly (nanosecond ticks).</summary>
    private sealed class ManualClock
    {
        private long _ticksNanos;
        public double Now => _ticksNanos / 1_000_000_000.0;
        public void Advance(double seconds) => Interlocked.Add(ref _ticksNanos, (long)(seconds * 1_000_000_000));
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs = 5000)
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

    private static async System.Threading.Tasks.Task PreparedAsync(VideoPlaybackSession session, string uri = "fake://media")
    {
        await session.PrepareAsync(uri).AsTask();
        Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Ready));
    }

    [Fact]
    public async System.Threading.Tasks.Task Prepare_TransitionsToReady_AndPrimesQueue()
    {
        var clock = new ManualClock();
        using var factory = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(factory, () => clock.Now);

        var states = new ConcurrentQueue<VideoPlaybackState>();
        session.StateChanged += states.Enqueue;

        await PreparedAsync(session);

        Assert.Equal(VideoPlaybackState.Ready, session.State);
        Assert.Equal(1, factory.Decoder.OpenCount);
        Assert.Equal(VideoPlaybackSession.FrameQueueCapacity, session.QueuedFrameCount);
        Assert.Equal(0, session.CurrentTimeSeconds);
        Assert.Equal(3.0, session.DurationSeconds, 3);
        Assert.Contains(VideoPlaybackState.Preparing, states);
        Assert.Contains(VideoPlaybackState.Ready, states);
    }

    private sealed class FakeFactoryHolder : IVideoDecoderFactory, IDisposable
    {
        public FakeVideoDecoder Decoder { get; } = new();

        public IVideoDecoder Create(IAudioPcmSink? audioSink, int audioSampleRate, int audioChannels)
        {
            if (audioSink is not null)
            {
                Decoder.ConfigureAudio(audioSink, audioSampleRate, audioChannels);
            }

            return Decoder;
        }

        public void Dispose() { }
    }

    [Fact]
    public async System.Threading.Tasks.Task Play_PresentsNewestDueFrame_AndRefills()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        double presentedPts = -1;
        int presentCount = 0;
        session.FramePresented += frame =>
        {
            presentedPts = frame.PtsSeconds;
            presentCount++;
        };

        session.Play();
        Assert.Equal(VideoPlaybackState.Playing, session.State);

        clock.Advance(0.5);
        // Pump 1 consumes the primed frames (pts 0..0.1) and presents their newest.
        session.Pump();
        Assert.Equal(1, presentCount);
        Assert.InRange(presentedPts, 0.0, 0.11);

        // The worker refills with the next batch; pumping again must present a strictly
        // newer frame since the clock is far ahead.
        Assert.True(SpinUntil(() => session.QueuedFrameCount == VideoPlaybackSession.FrameQueueCapacity),
            "worker did not refill the queue after presentation");
        session.Pump();
        Assert.Equal(2, presentCount);
        Assert.True(presentedPts > 0.11, $"second presentation not newer: {presentedPts}");
    }

    [Fact]
    public async System.Threading.Tasks.Task LateFrames_AreDropped_NewestPresented()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        double presented = -1;
        int presentCount = 0;
        session.FramePresented += frame => { presented = frame.PtsSeconds; presentCount++; };

        session.Play();
        clock.Advance(2.0); // queue holds frames 0..0.1s, all stale vs. 2.0s target
        session.Pump();

        Assert.Equal(1, presentCount);
        Assert.InRange(presented, 0.066, 0.135); // newest of the four primed frames
        // The queue cannot show time 2.0 until the worker refills: frames decode forward.
        Assert.True(SpinUntil(() => session.QueuedFrameCount == VideoPlaybackSession.FrameQueueCapacity));
    }

    [Fact]
    public async System.Threading.Tasks.Task Pause_FreezesClock_NoPresentation()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        int presentCount = 0;
        session.FramePresented += _ => presentCount++;

        session.Play();
        clock.Advance(0.3);
        session.Pump();
        double atPause = session.CurrentTimeSeconds;
        session.Pause();

        clock.Advance(10);
        session.Pump();
        session.Pump();

        Assert.Equal(VideoPlaybackState.Paused, session.State);
        Assert.Equal(atPause, session.CurrentTimeSeconds, 6);
        Assert.Equal(1, presentCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task Seek_WhilePaused_LandsOnTarget_AndResumesPaused()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        await session.SeekAsync(1.5).AsTask();

        Assert.Equal(VideoPlaybackState.Ready, session.State);
        Assert.Equal(1.5, session.CurrentTimeSeconds, 3);
        Assert.Equal(1.5, holder.Decoder.LastSeekSeconds!.Value, 3);

        // Present the poster at the seek target.
        session.Pump();
        Assert.Equal(VideoPlaybackState.Ready, session.State);
        Assert.Equal(1.5, session.CurrentTimeSeconds, 3);
    }

    [Fact]
    public async System.Threading.Tasks.Task ConsecutiveSeeks_OnlyLatestWins()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        XTask first = session.SeekAsync(0.5);
        XTask second = session.SeekAsync(1.0);
        await session.SeekAsync(1.9).AsTask();

        Assert.Equal(1.9, holder.Decoder.LastSeekSeconds!.Value, 3);
        Assert.Equal(1.9, session.CurrentTimeSeconds, 3);
    }

    [Fact]
    public async System.Threading.Tasks.Task EndOfStream_RaisesEnded_AndClampsTime()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        int endedCount = 0;
        session.PlaybackEnded += () => endedCount++;

        session.Play();
        clock.Advance(5.0);
        // Pump until the worker has flushed everything and EOS is observed.
        Assert.True(SpinUntil(() =>
        {
            session.Pump();
            return session.State == VideoPlaybackState.Ended;
        }));

        Assert.Equal(1, endedCount);
        Assert.Equal(3.0, session.CurrentTimeSeconds, 2);
    }

    [Fact]
    public async System.Threading.Tasks.Task Loop_SeeksBackAndKeepsPlaying()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);
        session.IsLooping = true;

        session.Play();
        clock.Advance(5.0);
        Assert.True(SpinUntil(() =>
        {
            session.Pump(); // presentation ticks drive the EOS/loop detection
            return holder.Decoder.SeekCount >= 1 && session.CurrentTimeSeconds < 1.0;
        }, timeoutMs: 5000),
            $"loop did not wrap (seeks={holder.Decoder.SeekCount}, t={session.CurrentTimeSeconds:F2})");
        Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Playing));
    }

    [Fact]
    public async System.Threading.Tasks.Task Play_AfterEnded_RestartsFromBeginning()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        session.Play();
        clock.Advance(5.0);
        Assert.True(SpinUntil(() => { session.Pump(); return session.State == VideoPlaybackState.Ended; }));

        session.Play();
        Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Playing));
        Assert.Equal(0, session.CurrentTimeSeconds, 2);
        Assert.True(SpinUntil(() => session.QueuedFrameCount > 0), "decoder did not rewind and refill");
    }

    [Fact]
    public async System.Threading.Tasks.Task PlaybackSpeed_ScalesTheMediaClock()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        session.PlaybackSpeed = 2f;
        session.Play();
        clock.Advance(1.0);

        Assert.Equal(2.0, session.CurrentTimeSeconds, 3);
    }

    [Fact]
    public async System.Threading.Tasks.Task Stop_ReturnsToIdle_AndEmptiesQueue()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);
        session.Play();

        session.Stop();

        Assert.Equal(VideoPlaybackState.Ready, session.State);
        Assert.Equal(0, session.QueuedFrameCount);
        Assert.Equal(0, session.CurrentTimeSeconds, 3);
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenFailure_TransitionsToError_AndReports()
    {
        var clock = new ManualClock();
        var holder = new FakeFactoryHolder();
        holder.Decoder.OpenException = new VideoException(VideoErrorCode.OpenFailed, "boom");
        using var session = new VideoPlaybackSession(holder, () => clock.Now);

        VideoErrorCode reported = VideoErrorCode.None;
        session.PlaybackFailed += (code, message) => reported = code;

        await session.PrepareAsync("fake://bad").AsTask();
        Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Error));

        Assert.Equal(VideoErrorCode.OpenFailed, reported);
    }

    [Fact]
    public async System.Threading.Tasks.Task Dispose_StopsWorker_AndSecondDisposeIsNoOp()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);
        session.Play();

        session.Dispose();
        session.Dispose(); // idempotent

        Assert.Equal(VideoPlaybackState.Disposed, session.State);
        Assert.Throws<ObjectDisposedException>(() => session.Play());
    }

    [Fact]
    public async System.Threading.Tasks.Task QueueNeverExceedsCapacity_UnderStress()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        holder.Decoder.FrameCount = 600; // 20s of media
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        await PreparedAsync(session);

        session.Play();
        clock.Advance(19.5);
        for (int i = 0; i < 200 && session.State == VideoPlaybackState.Playing; i++)
        {
            session.Pump();
            Assert.InRange(session.QueuedFrameCount, 0, VideoPlaybackSession.FrameQueueCapacity);
            clock.Advance(0.05);
        }
    }

    [FFmpegFact]
    public async System.Threading.Tasks.Task RealMedia_PlaysPresentsFrames_AndSavesEvidence()
    {
        FFmpegNativeLoader.EnsureLoaded();
        string media = Path.Combine(RepoRoot.Find(), "XEngine.Runtime.Test", "Media", "n0-h264-aac.mp4");
        var clock = new ManualClock();
        using var session = new VideoPlaybackSession(new FFmpegDecoderSessionFactory(), () => clock.Now);

        await session.PrepareAsync(media).AsTask();
        Assert.Equal(VideoPlaybackState.Ready, session.State);

        string evidenceDir = VideoTestEvidence.Root;
        Directory.CreateDirectory(evidenceDir);

        int presented = 0;
        double lastPts = -1;
        session.FramePresented += frame =>
        {
            presented++;
            lastPts = frame.PtsSeconds;
            if (presented == 30)
            {
                SaveEvidencePng(frame, evidenceDir, "2026-09-12-n2-realmedia-frame30.png");
            }
        };

        session.Play();
        var stopwatch = Stopwatch.StartNew();
        // Drive the manual clock at ~30fps of media time per wall iteration while the
        // worker decodes ahead in real time.
        while (stopwatch.ElapsedMilliseconds < 10000 && presented < 45)
        {
            session.Pump();
            clock.Advance(1.0 / 60.0);
            Thread.Yield();
        }

        _output.WriteLine($"presented={presented} frames in {stopwatch.ElapsedMilliseconds}ms, lastPts={lastPts:F3}");
        AppendBaseline($"real-media 320x240 h264: presented={presented} in {stopwatch.ElapsedMilliseconds}ms");
        Assert.True(presented >= 30, $"expected >=30 presented frames, got {presented}");
        Assert.True(lastPts > 0.5, $"playback did not advance: lastPts={lastPts}");
        Assert.True(File.Exists(Path.Combine(evidenceDir, "2026-09-12-n2-realmedia-frame30.png")));
    }

    private sealed class FFmpegDecoderSessionFactory : IVideoDecoderFactory
    {
        public IVideoDecoder Create(IAudioPcmSink? audioSink, int audioSampleRate, int audioChannels)
        {
            var decoder = new FFmpegVideoDecoder();
            if (audioSink is not null)
            {
                decoder.ConfigureAudio(audioSink, audioSampleRate, audioChannels);
            }

            return decoder;
        }
    }

    private static void SaveEvidencePng(PooledVideoFrame frame, string dir, string fileName)
    {
        var settings = new ImageMagick.MagickReadSettings
        {
            Format = ImageMagick.MagickFormat.Rgba,
            Width = (uint)frame.Width,
            Height = (uint)frame.Height,
            Depth = 8,
        };
        using var image = new ImageMagick.MagickImage(frame.Data.AsSpan(0, frame.ByteCount).ToArray(), settings);
        image.Format = ImageMagick.MagickFormat.Png;
        image.Write(Path.Combine(dir, fileName));
    }

    private static void AppendBaseline(string line)
    {
        string path = Path.Combine(VideoTestEvidence.Root, "2026-09-12-n2-playback-baseline.txt");
        File.AppendAllText(path, $"{DateTime.Now:O} {line}{Environment.NewLine}", Encoding.UTF8);
    }
}
