// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

using ImageMagick;

using XEngine.Async;
using XEngine.Runtime.Video;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test.Video;

/// <summary>
/// N3 audio-clock tests: the session's media clock must follow the audio consumption
/// position (device-delivered PCM), freeze when the device starves, re-anchor on seek,
/// and fall back to the realtime clock when audio is unavailable.
/// </summary>
public sealed class VideoAudioClockTests
{
    private readonly ITestOutputHelper _output;

    public VideoAudioClockTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public async System.Threading.Tasks.Task AudioClock_FollowsConsumption_FreezesWhenStarved()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        session.EnableAudioClock(48000, 2);
        await PreparedAsync(session);

        session.Play();
        clock.Advance(10);

        // Starved (device consumed nothing): the clock must be frozen at the anchor even
        // though the realtime clock ran away.
        Assert.Equal(0, session.CurrentTimeSeconds, 3);

        // Consume ~0.5s of PCM like a device callback would; the clock must track it.
        var buffer = new float[48000 * 2];
        int totalFrames = 0;
        var stopwatch = Stopwatch.StartNew();
        while (totalFrames < 24_000 && stopwatch.ElapsedMilliseconds < 5000)
        {
            session.Pump();
            int read = session.ReadPcm(buffer, 2000);
            if (read > 0)
            {
                totalFrames += read;
                clock.Advance(read / 48000.0); // wall time passes as the device consumes
            }
            else
            {
                Thread.Yield();
            }
        }

        _output.WriteLine($"consumed {totalFrames} frames ({totalFrames / 48000.0:F3}s), clock={session.CurrentTimeSeconds:F3}");
        Assert.True(totalFrames >= 24_000, $"only consumed {totalFrames} frames");
        Assert.InRange(session.CurrentTimeSeconds, 0.4, 0.7);
    }

    [Fact]
    public async System.Threading.Tasks.Task SeekAudio_ReanchorsRing()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        session.EnableAudioClock(48000, 2);
        await PreparedAsync(session);

        session.Play();
        // Deliver a little, then seek.
        var buffer = new float[48000 * 2];
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 2000 && session.ReadPcm(buffer, 1000) == 0)
        {
            session.Pump();
            Thread.Yield();
        }

        await session.SeekAsync(1.5).AsTask();
        Assert.Equal(1.5, session.CurrentTimeSeconds, 3);

        // The seek drops buffered PCM; fresh PCM decoded at the new position must arrive
        // and consumption continues from there.
        Assert.True(SpinUntil(() =>
        {
            session.Pump();
            return session.ReadPcm(buffer, 1000) > 0;
        }), "no PCM after seek");
        Assert.InRange(session.CurrentTimeSeconds, 1.45, 2.6); // one stale pre-seek frame may play briefly
    }

    [Fact]
    public async System.Threading.Tasks.Task SpeedTwo_DeliversDoubleMediaSeconds()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        session.EnableAudioClock(48000, 2);
        await PreparedAsync(session);

        session.PlaybackSpeed = 2f;
        session.Play();

        var buffer = new float[48000 * 2];
        int totalFrames = 0;
        var stopwatch = Stopwatch.StartNew();
        while (totalFrames < 48_000 && stopwatch.ElapsedMilliseconds < 8000)
        {
            session.Pump();
            int read = session.ReadPcm(buffer, 2000);
            if (read > 0)
            {
                totalFrames += read;
            }
            else
            {
                Thread.Yield();
            }
        }

        // 1s of output PCM at 2x = 2s of media time.
        _output.WriteLine($"delivered {totalFrames} frames ({totalFrames / 48000.0:F2}s output), clock={session.CurrentTimeSeconds:F3}");
        Assert.InRange(session.CurrentTimeSeconds, 1.5, 2.5);
    }

    [Fact]
    public async System.Threading.Tasks.Task DisableAudioClock_FallsBackToRealtime()
    {
        var clock = new ManualClock();
        using var holder = new FakeFactoryHolder();
        using var session = new VideoPlaybackSession(holder, () => clock.Now);
        session.EnableAudioClock(48000, 2);
        await PreparedAsync(session);
        session.Play();

        session.DisableAudioClock(); // e.g. the audio device failed
        clock.Advance(2.0);

        Assert.Equal(2.0, session.CurrentTimeSeconds, 3);
    }

    [FFmpegFact]
    public async System.Threading.Tasks.Task RealMedia_AudioClockTracksDelivery_AndSavesWaveformEvidence()
    {
        FFmpegNativeLoader.EnsureLoaded();
        string media = Path.Combine(RepoRoot.Find(), "XEngine.Runtime.Test", "Media", "n0-h264-aac.mp4");
        var clock = new ManualClock();
        using var session = new VideoPlaybackSession(new FFmpegDecoderSessionFactory(), () => clock.Now);
        session.EnableAudioClock(48000, 2);

        await session.PrepareAsync(media).AsTask();
        Assert.True(session.Metadata.Audio is not null);

        session.Play();

        var buffer = new float[48000 * 2];
        float[]? waveform = null;
        int waveformCount = 0;
        int totalFrames = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 10_000 && totalFrames < 48_000)
        {
            session.Pump();
            int read = session.ReadPcm(buffer, 2400);
            if (read > 0)
            {
                if (waveform is null)
                {
                    waveformCount = Math.Min(read * 2, 48000 * 2);
                    waveform = new float[waveformCount];
                    Array.Copy(buffer, waveform, waveformCount);
                }

                totalFrames += read;
            }
            else
            {
                Thread.Yield();
            }
        }

        double clockAt = session.CurrentTimeSeconds;
        _output.WriteLine($"real audio: delivered {totalFrames} frames ({totalFrames / 48000.0:F3}s), audio-clock={clockAt:F3}");
        AppendBaseline($"real-media aac 44.1k->48k stereo: delivered={totalFrames} frames, audio clock={clockAt:F3}s");

        Assert.True(totalFrames >= 48_000, $"only delivered {totalFrames} frames of PCM");
        Assert.InRange(clockAt, 0.8, 1.2);

        // Waveform evidence PNG (min/max envelope, 800x200).
        Assert.NotNull(waveform);
        SaveWaveformEvidence(waveform!, waveformCount, Path.Combine(VideoTestEvidence.Root, "2026-09-12-n3-realmedia-audio-waveform.png"));
    }

    private static void SaveWaveformEvidence(float[] samples, int count, string path)
    {
        const int width = 800, height = 200;
        var pixels = new byte[width * height * 4];
        int samplesPerPixel = Math.Max(1, count / width);
        for (int x = 0; x < width; x++)
        {
            float min = 1, max = -1;
            int start = x * samplesPerPixel;
            int end = Math.Min(count, start + samplesPerPixel);
            for (int i = start; i < end; i++)
            {
                min = Math.Min(min, samples[i]);
                max = Math.Max(max, samples[i]);
            }

            if (min > max)
            {
                continue;
            }

            int yMin = (int)((1 - (max + 1) / 2) * height);
            int yMax = (int)((1 - (min + 1) / 2) * height);
            for (int y = Math.Max(0, yMin); y <= Math.Min(height - 1, yMax); y++)
            {
                int p = (y * width + x) * 4;
                pixels[p] = 40;
                pixels[p + 1] = 160;
                pixels[p + 2] = 90;
                pixels[p + 3] = 255;
            }
        }

        var settings = new MagickReadSettings
        {
            Format = MagickFormat.Rgba,
            Width = (uint)width,
            Height = (uint)height,
            Depth = 8,
        };
        using var image = new MagickImage(pixels, settings);
        image.Format = MagickFormat.Png;
        image.Write(path);
    }

    private static void AppendBaseline(string line)
    {
        string path = Path.Combine(VideoTestEvidence.Root, "2026-09-12-n3-audio-baseline.txt");
        File.AppendAllText(path, $"{DateTime.Now:O} {line}{Environment.NewLine}");
    }

    private static async System.Threading.Tasks.Task PreparedAsync(VideoPlaybackSession session, string uri = "fake://media")
    {
        await session.PrepareAsync(uri).AsTask();
        Assert.True(SpinUntil(() => session.State == VideoPlaybackState.Ready));
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
}
