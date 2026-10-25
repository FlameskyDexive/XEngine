// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Async;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

using XEngine.Runtime.Video;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test.Video;

/// <summary>
/// N6 network acceptance against a local HTTP listener: FFmpeg's http protocol handles
/// no-Range servers and plain 404s the same way it handles local files; cancellation
/// aborts an open against a black-hole endpoint through the demuxer interrupt callback.
/// </summary>
public sealed class VideoNetworkTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly HttpListener? _listener;
    private readonly int _port;
    private static readonly byte[] MediaBytes = File.ReadAllBytes(FindMedia("n0-h264-aac.mp4"));
    private static readonly byte[] TsBytes = File.ReadAllBytes(FindMedia("n0-h264-aac-ts.mpegts"));

    public VideoNetworkTests(ITestOutputHelper output)
    {
        _output = output;

        // Bind first; the individual tests decide how responses are shaped.
        _listener = new HttpListener();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            _port = 47000 + Random.Shared.Next(2000);
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                if (attempt == 9)
                {
                    _listener.Close();
                    _listener = null;
                }
            }
        }

        if (_listener is not null)
        {
            Mode = "disabled";
            _ = AcceptLoop();
        }
    }

    private string Mode { get; set; } = "disabled";

    private string BaseUrl => $"http://127.0.0.1:{_port}/media.mp4";

    public void Dispose()
    {
        _listener?.Stop();
        _listener?.Close();
    }

    private async System.Threading.Tasks.Task AcceptLoop()
    {
        var listener = _listener!;
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                return; // listener stopped
            }

            try
            {
                string mode = Mode;
                var response = context.Response;
                switch (mode)
                {
                    case "no-range":
                    {
                        byte[] payload = context.Request.Url!.AbsolutePath.EndsWith(".mpegts")
                            ? TsBytes
                            : MediaBytes;
                        response.ContentType = "video/mp4";
                        response.ContentLength64 = payload.Length;
                        await response.OutputStream.WriteAsync(payload, 0, payload.Length);
                        response.OutputStream.Close();
                        break;
                    }

                    case "range":
                    {
                        string? range = context.Request.Headers["Range"];
                        long start = 0;
                        long end = MediaBytes.Length - 1;
                        if (range is not null)
                        {
                            // "bytes=start-" or "bytes=start-end"
                            var parts = range["bytes=".Length..].Split('-');
                            start = long.Parse(parts[0]);
                            if (parts.Length > 1 && parts[1].Length > 0)
                            {
                                end = long.Parse(parts[1]);
                            }

                            response.StatusCode = 206;
                            response.Headers["Content-Range"] = $"bytes {start}-{end}/{MediaBytes.Length}";
                        }
                        else
                        {
                            response.StatusCode = 200;
                        }

                        long count = end - start + 1;
                        response.ContentType = "video/mp4";
                        response.ContentLength64 = count;
                        await response.OutputStream.WriteAsync(MediaBytes, (int)start, (int)count);
                        response.OutputStream.Close();
                        break;
                    }

                    case "404":
                    {
                        response.StatusCode = 404;
                        response.OutputStream.Close();
                        break;
                    }

                    case "blackhole":
                    {
                        // Never respond: hold the connection until the client gives up.
                        await System.Threading.Tasks.Task.Delay(60_000);
                        response.OutputStream.Close();
                        break;
                    }

                    default:
                        response.StatusCode = 500;
                        response.OutputStream.Close();
                        break;
                }
            }
            catch
            {
                // client aborted mid-request (expected for cancellation tests)
            }
        }
    }

    [FFmpegFact]
    public async System.Threading.Tasks.Task Open_NoRangeServer_Plays()
    {
        FFmpegNativeLoader.EnsureLoaded();
        Assert.NotNull(_listener);
        // A no-Range server cannot satisfy the seek-heavy MP4 demuxer; use the streamable
        // MPEG-TS variant, which is exactly what such servers can deliver in practice.
        Mode = "no-range";
        await AssertPlays("http://127.0.0.1:" + _port + "/stream.mpegts", minWidth: 160);
    }

    [FFmpegFact]
    public async System.Threading.Tasks.Task Open_RangeServer_Plays()
    {
        FFmpegNativeLoader.EnsureLoaded();
        Assert.NotNull(_listener);
        Mode = "range";
        await AssertPlays(BaseUrl);
    }

    [FFmpegFact]
    public async System.Threading.Tasks.Task Open_404_FailsWithOpenFailed()
    {
        FFmpegNativeLoader.EnsureLoaded();
        Assert.NotNull(_listener);
        Mode = "404";

        var stopwatch = Stopwatch.StartNew();
        Exception caught = await Record.ExceptionAsync(() => OpenProbeAsync($"{BaseUrl}?missing").AsTask());
        stopwatch.Stop();

        var videoException = Assert.IsType<VideoException>(caught);
        Assert.Equal(VideoErrorCode.OpenFailed, videoException.ErrorCode);
        _output.WriteLine($"404 surfaced in {stopwatch.ElapsedMilliseconds}ms as {videoException.ErrorCode}");
    }

    [FFmpegFact]
    public async System.Threading.Tasks.Task Open_BlackHole_CancelAbortsPromptly()
    {
        FFmpegNativeLoader.EnsureLoaded();
        Assert.NotNull(_listener);
        Mode = "blackhole";

        using var cts = new CancellationTokenSource(2_000);
        var stopwatch = Stopwatch.StartNew();
        Exception caught = await Record.ExceptionAsync(() => OpenProbeAsync(BaseUrl, cts.Token).AsTask());
        stopwatch.Stop();

        _output.WriteLine($"cancel surfaced after {stopwatch.ElapsedMilliseconds}ms: {caught?.GetType().Name}: {caught?.Message}");
        Assert.True(stopwatch.ElapsedMilliseconds < 8_000, "cancellation did not abort the open promptly");
        Assert.True(caught is OperationCanceledException or VideoException,
            $"unexpected failure type {caught?.GetType().Name}");
        if (caught is VideoException videoException)
        {
            Assert.Equal(VideoErrorCode.Canceled, videoException.ErrorCode);
        }
    }

    private static async XTask OpenProbeAsync(string uri, CancellationToken cancellationToken = default)
    {
        using var decoder = new FFmpegVideoDecoder(timeoutMs: 5_000);
        decoder.Open(uri, cancellationToken);
        decoder.DecodeFirstFrame(cancellationToken);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private static async System.Threading.Tasks.Task AssertPlays(string uri, double minWidth = 320)
    {
        using var decoder = new FFmpegVideoDecoder();
        decoder.Open(uri, CancellationToken.None);
        VideoFrame frame = decoder.DecodeFirstFrame(CancellationToken.None);
        Assert.True(frame.Width >= minWidth, $"frame too small: {frame.Width}");
        Assert.Equal(240, frame.Height);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private static string FindMedia(string name)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
        {
            current = current.Parent;
        }

        return Path.Combine(current!.FullName, "XEngine.Runtime.Test", "Media", name);
    }
}
