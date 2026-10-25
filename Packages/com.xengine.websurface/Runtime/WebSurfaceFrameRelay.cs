using System;
using System.Diagnostics;

namespace XEngine.WebSurface;

/// <summary>
/// Host-side half of the SharedBuffer frame bridge. Owns the single-in-flight produce contract
/// (a produce token is only sent when no frame is being produced), drops stale page callbacks
/// (a frame message whose number does not match the in-flight token belongs to an old page or
/// an old epoch) and turns each delivered frame into a pool commit while measuring the two costs
/// B2 must record: the produce→frame-ready sync wait and the SharedBuffer→pool copy.
/// Deliberately browser-free: tests drive it with plain delegates, the WebView2 backend supplies
/// the memory copy from the shared buffer.
/// </summary>
public sealed class WebSurfaceFrameRelay
{
    private readonly IWebSurfaceFrameSink _sink;
    private readonly WebSurfaceDiagnostics _diagnostics;
    private readonly int _width;
    private readonly int _height;
    private readonly WebSurfaceAlphaMode _alphaMode;

    private ulong _nextFrameNumber;
    private ulong _inFlight = ulong.MaxValue;
    private long _produceTimestampUs;
    private bool _disposed;

    public WebSurfaceFrameRelay(
        IWebSurfaceFrameSink sink,
        WebSurfaceDiagnostics diagnostics,
        int width,
        int height,
        WebSurfaceAlphaMode alphaMode,
        ulong firstFrameNumber = 0)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _width = width;
        _height = height;
        _alphaMode = alphaMode;
        _nextFrameNumber = firstFrameNumber;
    }

    /// <summary>Width/height the relay begins frames with (the negotiated surface size).</summary>
    public int Width => _width;
    public int Height => _height;

    /// <summary>The frame number the next produce token will carry.</summary>
    public ulong NextFrameNumber => _nextFrameNumber;

    /// <summary>True while a produce token is outstanding (no new token may be sent).</summary>
    public bool IsProducing => _inFlight != ulong.MaxValue;

    /// <summary>
    /// Attempts to hand out the next produce token. Returns null (and counts a reject) when a
    /// request is already in flight or the relay is disposed; the caller must not message the page.
    /// Otherwise returns the frame number the page must stamp its capture with.
    /// </summary>
    public ulong? BeginProduce()
    {
        if (_disposed || IsProducing)
        {
            _diagnostics.Increment(WebSurfaceCounters.ProduceRejects);
            return null;
        }

        _inFlight = _nextFrameNumber;
        _produceTimestampUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
        return _inFlight;
    }

    /// <summary>
    /// Delivers the frame the page signalled. A frame number that does not match the in-flight
    /// token is dropped (old page callback, duplicate or stale epoch). The <paramref name="copy"/>
    /// callback fills the pooled buffer from the shared memory; its duration and byte count are
    /// recorded as the copy cost.
    /// </summary>
    /// <returns>True when the frame was committed to the pool.</returns>
    public bool OnFrameReady(ulong frameNumber, double pageMs, Action<WebSurfaceFrame> copy)
    {
        if (_disposed || copy is null)
            return false;
        if (!IsProducing || frameNumber != _inFlight)
            return false; // stale page callback: not the frame we asked for

        ulong token = _inFlight;
        _inFlight = ulong.MaxValue;

        long startUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
        WebSurfaceFrame? frame = _sink.BeginFrame(
            token, _width, _height, _width * 4, _alphaMode);
        if (frame is null)
            return false; // pool refused (disposed or a frame already in flight there)

        try
        {
            copy(frame);
        }
        catch (Exception)
        {
            _sink.CancelFrame();
            frame.Dispose();
            throw;
        }

        bool committed = _sink.Commit(frame);
        if (!committed)
            frame.Dispose();

        long endUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
        _diagnostics.Increment(WebSurfaceCounters.FramesFromProducer);
        _diagnostics.Increment(WebSurfaceCounters.SharedBufferCopies);
        _diagnostics.Add(WebSurfaceCounters.CopyMicrosTotal, endUs - startUs);
        _diagnostics.Add(WebSurfaceCounters.BytesCopied, _width * 4L * _height);
        _diagnostics.Increment(WebSurfaceCounters.SyncWaits);
        _diagnostics.Add(WebSurfaceCounters.SyncWaitMicrosTotal, startUs - _produceTimestampUs);
        if (pageMs > 0)
            _diagnostics.Add(WebSurfaceCounters.PageReadbackMicrosTotal, (long)(pageMs * 1000));

        _nextFrameNumber = token + 1;
        return committed;
    }

    /// <summary>
    /// Abandons an in-flight produce (page failure, reload). The next token is issued with a
    /// fresh frame number — a late frame message from a dying document can never satisfy a new
    /// token, which is what makes reloads safe against stale page callbacks.
    /// </summary>
    public void CancelProduce()
    {
        if (IsProducing)
        {
            _inFlight = ulong.MaxValue;
            _nextFrameNumber++;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelProduce();
    }
}
