// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

using XEngine.Editor.GUI;
using XEngine.Runtime.Video;
using XEngine.Runtime.Test.Video;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Editor preview lifecycle: the VideoPreviewSession pumps from GUI ticks, plays muted
/// by default, and teardown releases everything (idempotent, safe no-op afterwards).
/// </summary>
public sealed class VideoPreviewSessionTests : IDisposable
{
    private readonly string _media;

    public VideoPreviewSessionTests()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
        {
            current = current.Parent;
        }

        string repo = current!.FullName;
        _media = Path.Combine(repo, "XEngine.Runtime.Test", "Media", "n0-h264-aac.mp4");
    }

    public void Dispose()
    {
    }

    [FFmpegFact]
    public void Preview_PlaysMuted_ProducesTexture_StopsCleanly()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var preview = VideoPreviewSession.Create(_media, withAudio: false, loop: false, speed: 1f);
        Assert.Equal(VideoPlaybackState.Preparing, preview.State);

        preview.Play();
        var stopwatch = Stopwatch.StartNew();
        while (!preview.IsPlaying && stopwatch.ElapsedMilliseconds < 10_000)
        {
            preview.Tick();
            Thread.Yield();
        }

        Assert.True(preview.IsPlaying, $"never reached Playing (state={preview.State})");

        // Texture arrives after the first presented frame.
        stopwatch.Restart();
        while (preview.Texture is null && stopwatch.ElapsedMilliseconds < 5_000)
        {
            preview.Tick();
            Thread.Yield();
        }

        Assert.NotNull(preview.Texture);
        Assert.True(preview.Duration > 1.0);

        preview.Pause();
        Assert.False(preview.IsPlaying);

        preview.Dispose();
        preview.Dispose(); // idempotent
    }

    [FFmpegFact]
    public void Preview_Seek_MovesTheClock()
    {
        FFmpegNativeLoader.EnsureLoaded();
        using var preview = VideoPreviewSession.Create(_media, withAudio: false, loop: false, speed: 1f);
        preview.Play();
        var stopwatch = Stopwatch.StartNew();
        while (!preview.IsPlaying && stopwatch.ElapsedMilliseconds < 10_000)
        {
            preview.Tick();
            Thread.Yield();
        }

        preview.Seek(1.5);
        stopwatch.Restart();
        while (stopwatch.ElapsedMilliseconds < 5_000 && preview.Time < 1.4)
        {
            preview.Tick();
            Thread.Yield();
        }

        Assert.True(preview.Time >= 1.4, $"seek did not land (t={preview.Time:F3})");
    }
}
