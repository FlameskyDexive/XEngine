// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using ImageMagick;

using Xunit;
using XEngine.Runtime.Video;

using Xunit.Abstractions;

namespace XEngine.Runtime.Test.Video;

public sealed class FFmpegVideoDecoderTests : IDisposable
{
    // Generated with the dev-only ffmpeg CLI (testsrc2 + sine): 2s, 320x240@30 clips.
    private static readonly string MediaRoot = Path.Combine(
        RepoRoot.Find(), "XEngine.Runtime.Test", "Media");

    // N0 acceptance evidence lands here (first-frame PNGs + decode baseline lines).
    private static string EvidenceDir => VideoTestEvidence.Root;

    private readonly ITestOutputHelper _output;

    public FFmpegVideoDecoderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(EvidenceDir);
    }

    public void Dispose()
    {
    }

    [FFmpegFact]
    public void Open_Mp4_ReportsFullMetadata()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var decoder = new FFmpegVideoDecoder();
        decoder.Open(Path.Combine(MediaRoot, "n0-h264-aac.mp4"), CancellationToken.None);

        VideoMediaInfo info = decoder.Metadata;
        Assert.Equal("h264", info.Video.Codec);
        Assert.Equal(320, info.Video.Width);
        Assert.Equal(240, info.Video.Height);
        Assert.Equal(320, info.Video.CodedWidth);
        Assert.Equal(240, info.Video.CodedHeight);
        Assert.InRange(info.Video.Fps, 29.0, 31.0);
        Assert.InRange(info.DurationSeconds, 1.8, 2.2);
        Assert.NotNull(info.Audio);
        Assert.Equal("aac", info.Audio!.Codec);
        Assert.Equal(44100, info.Audio.SampleRate);
        Assert.Equal(2, info.Audio.Channels);
        Assert.True(info.CanSeek);
        Assert.Equal(0, info.Video.RotationDegrees);
        Assert.Equal(1, info.Video.SampleAspectNum);
        Assert.Equal(1, info.Video.SampleAspectDen);
    }

    [FFmpegFact]
    public void DecodeFirstFrame_Mp4_ReturnsRgbaAndSavesEvidence()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var decoder = new FFmpegVideoDecoder();
        string path = Path.Combine(MediaRoot, "n0-h264-aac.mp4");

        var stopwatch = Stopwatch.StartNew();
        decoder.Open(path, CancellationToken.None);
        VideoFrame frame = decoder.DecodeFirstFrame(CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(320, frame.Width);
        Assert.Equal(240, frame.Height);
        Assert.Equal(320 * 240 * 4, frame.Data.Length);
        Assert.True(frame.PtsSeconds >= 0);

        // testsrc2 is colorful; a broken conversion would produce a flat buffer.
        int distinct = frame.Data.Where((b, i) => i % 61 == 0).Distinct().Count();
        Assert.True(distinct > 8, $"Frame looks flat: only {distinct} distinct byte values.");

        double ms = stopwatch.Elapsed.TotalMilliseconds;
        _output.WriteLine($"mp4 open+first-frame: {ms:F1} ms");
        AppendBaseline($"mp4(h264/aac 320x240) open+first-frame={ms:F1}ms");
        SaveEvidencePng(frame, "2026-09-12-n0-firstframe-h264.png");
    }

    [FFmpegFact]
    public void DecodeFirstFrame_WebM_ReturnsRgbaAndSavesEvidence()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var decoder = new FFmpegVideoDecoder();
        string path = Path.Combine(MediaRoot, "n0-vp9-opus.webm");

        var stopwatch = Stopwatch.StartNew();
        decoder.Open(path, CancellationToken.None);
        VideoFrame frame = decoder.DecodeFirstFrame(CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(320, frame.Width);
        Assert.Equal(240, frame.Height);
        Assert.Equal(320 * 240 * 4, frame.Data.Length);
        Assert.Equal("vp9", decoder.Metadata.Video.Codec);
        Assert.NotNull(decoder.Metadata.Audio);
        Assert.Equal("opus", decoder.Metadata.Audio!.Codec);

        double ms = stopwatch.Elapsed.TotalMilliseconds;
        _output.WriteLine($"webm open+first-frame: {ms:F1} ms");
        AppendBaseline($"webm(vp9/opus 320x240) open+first-frame={ms:F1}ms");
        SaveEvidencePng(frame, "2026-09-12-n0-firstframe-vp9.png");
    }

    [FFmpegFact]
    public void DecodeAllFrames_DrainsAtEof_AndRestartsAfterSeek()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var decoder = new FFmpegVideoDecoder();
        decoder.Open(Path.Combine(MediaRoot, "n0-h264-aac.mp4"), CancellationToken.None);

        for (int pass = 0; pass < 2; pass++)
        {
            if (pass != 0) decoder.Seek(0, CancellationToken.None);
            int count = 0;
            double lastPts = -1;
            while (true)
            {
                using var frame = decoder.DecodeNextFrame(CancellationToken.None, ArrayPool<byte>.Shared);
                if (frame is null) break;
                Assert.True(count < 60, "Decoder returned more than the fixture's 60 frames.");
                Assert.True(frame.PtsSeconds >= lastPts);
                lastPts = frame.PtsSeconds;
                count++;
            }

            Assert.Equal(60, count);
            Assert.Null(decoder.DecodeNextFrame(CancellationToken.None, ArrayPool<byte>.Shared));
        }
    }

    [FFmpegFact]
    public void Open_RotatedMp4_ReportsRotationMetadata()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var decoder = new FFmpegVideoDecoder();
        decoder.Open(Path.Combine(MediaRoot, "n0-rotated.mp4"), CancellationToken.None);

        Assert.Equal(90, decoder.Metadata.Video.RotationDegrees);
        Assert.Equal((240, 320), decoder.Metadata.Video.DisplaySize);
        // Coded dimensions stay landscape; only the display size swaps.
        Assert.Equal(320, decoder.Metadata.Video.CodedWidth);
        Assert.Equal(240, decoder.Metadata.Video.CodedHeight);
    }

    [FFmpegFact]
    public void Open_WithPreCanceledToken_ThrowsCanceled()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var decoder = new FFmpegVideoDecoder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        VideoException exception = Assert.Throws<VideoException>(
            () => decoder.Open(Path.Combine(MediaRoot, "n0-h264-aac.mp4"), cts.Token));
        Assert.Equal(VideoErrorCode.Canceled, exception.ErrorCode);
    }

    [FFmpegFact]
    public void Open_CorruptFile_ThrowsVideoException()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var decoder = new FFmpegVideoDecoder();

        VideoException exception = Assert.Throws<VideoException>(
            () => decoder.Open(Path.Combine(MediaRoot, "n0-corrupt.mp4"), CancellationToken.None));
        Assert.NotEqual(VideoErrorCode.None, exception.ErrorCode);
    }

    [FFmpegFact]
    public void Open_MissingFile_ThrowsOpenFailed()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var decoder = new FFmpegVideoDecoder();

        VideoException exception = Assert.Throws<VideoException>(
            () => decoder.Open(Path.Combine(MediaRoot, "does-not-exist.mp4"), CancellationToken.None));
        Assert.Equal(VideoErrorCode.OpenFailed, exception.ErrorCode);
    }

    [FFmpegFact]
    public void Dispose_CanBeCalledTwice_AndDecoderReopens()
    {
        FFmpegNativeLoader.EnsureLoaded();
        string path = Path.Combine(MediaRoot, "n0-h264-aac.mp4");
        VideoFrame first;

        using (var decoder = new FFmpegVideoDecoder())
        {
            decoder.Open(path, CancellationToken.None);
            first = decoder.DecodeFirstFrame(CancellationToken.None);
            decoder.Dispose();
            decoder.Dispose();
        }

        Assert.Equal(320, first.Width);

        // A fresh instance must work after the previous one was fully torn down.
        using var second = new FFmpegVideoDecoder();
        second.Open(path, CancellationToken.None);
        VideoFrame again = second.DecodeFirstFrame(CancellationToken.None);
        Assert.Equal(320, again.Width);
    }

    [Fact]
    public void Metadata_BeforeOpen_Throws()
    {
        using var decoder = new FFmpegVideoDecoder();
        Assert.Throws<VideoException>(() => _ = decoder.Metadata);
    }

    [FFmpegFact]
    public void Open_Wav_AudioOnly_ReportsMetadataAndDummyFrame()
    {
        FFmpegNativeLoader.EnsureLoaded();
        string path = Path.Combine(Path.GetTempPath(), "xengine-n3-audio-only.wav");
        WritePcmWav(path, sampleRate: 44100, channels: 2, seconds: 0.35, frequency: 440);

        var ring = new VideoPcmRing(48000, 2, 1.0);
        using var decoder = new FFmpegVideoDecoder();
        decoder.ConfigureAudio(ring, 48000, 2);
        decoder.Open(path, CancellationToken.None);

        Assert.True(decoder.HasAudio);
        Assert.NotNull(decoder.Metadata.Audio);
        Assert.Equal("none", decoder.Metadata.Video.Codec);
        Assert.Equal(2, decoder.Metadata.Video.Width);
        Assert.InRange(decoder.Metadata.DurationSeconds, 0.2, 0.6);

        VideoFrame first = decoder.DecodeFirstFrame(CancellationToken.None);
        Assert.Equal(2, first.Width);
        Assert.Equal(2, first.Height);
        Assert.Equal(16, first.Data.Length);

        for (int i = 0; i < 24; i++)
        {
            decoder.DecodeNextFrame(CancellationToken.None, ArrayPool<byte>.Shared)?.Dispose();
        }

        Assert.True(ring.BufferedFrames > 200, $"audio-only wav produced {ring.BufferedFrames} PCM frames");
    }

    private static void WritePcmWav(string path, int sampleRate, int channels, double seconds, double frequency)
    {
        int frames = (int)(sampleRate * seconds);
        int dataBytes = frames * channels * 2;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 16000);
            for (int c = 0; c < channels; c++)
            {
                writer.Write(sample);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void SaveEvidencePng(VideoFrame frame, string fileName)
    {
        string fullPath = Path.Combine(EvidenceDir, fileName);
        var settings = new MagickReadSettings
        {
            Format = MagickFormat.Rgba,
            Width = (uint)frame.Width,
            Height = (uint)frame.Height,
            Depth = 8,
        };
        using var image = new MagickImage(frame.Data, settings);
        image.Format = MagickFormat.Png;
        image.Write(fullPath);
    }

    private void AppendBaseline(string line)
    {
        string fullPath = Path.Combine(EvidenceDir, "2026-09-12-n0-decode-baseline.txt");
        File.AppendAllText(fullPath, $"{DateTime.Now:O} {line}{Environment.NewLine}", Encoding.UTF8);
        _output.WriteLine($"baseline: {line}");
    }
}

/// <summary>Walks up from the test output directory to the repository root.</summary>
internal static class RepoRoot
{
    public static string Find()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }
}
