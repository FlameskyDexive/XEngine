using System;
using System.Collections.Generic;
using System.Threading;

namespace XEngine.WebSurface;

/// <summary>
/// Triple-buffer frame pipeline: one buffer in flight with the producer ("writing"), the newest
/// complete frame ("ready") and one frame a consumer is currently presenting ("consuming").
/// Only the newest complete frame is kept — a newer commit replaces an unconsumed ready frame
/// (the dropped frame is counted, matching the "drop, never queue" contract for live web
/// content). Every buffer change validates the generation so frames from an old resize epoch or
/// an old page/device callback are discarded instead of presented.
/// </summary>
public sealed class WebSurfaceFramePool
{
    private readonly object _lock = new();
    private readonly WebSurfaceDiagnostics _diagnostics;
    private readonly int _maxPoolSize;

    private FrameSlot? _writing;
    private FrameSlot? _ready;
    private FrameSlot? _consuming;
    private readonly List<FrameSlot> _spare = [];
    private int _generation = 1;
    private bool _disposed;

    public WebSurfaceFramePool(WebSurfaceDiagnostics diagnostics, int maxPoolSize = 4)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _maxPoolSize = Math.Max(2, maxPoolSize);
    }

    /// <summary>Current resize generation; bumped by <see cref="Resize"/>.</summary>
    public int Generation { get { lock (_lock) return _generation; } }

    /// <summary>
    /// Begins a new frame on the current generation. Returns null when a frame is already in
    /// flight (one production request at a time — the session must not double-produce).
    /// </summary>
    public WebSurfaceFrame? BeginFrame(ulong frameNumber, int width, int height, int stride, WebSurfaceAlphaMode alphaMode)
    {
        lock (_lock)
        {
            if (_disposed || _writing is not null)
                return null;

            byte[] pixels = RentBuffer(stride * height);
            var slot = new FrameSlot(frameNumber, _generation, width, height, stride, alphaMode, pixels);
            _writing = slot;
            return slot.ToFrame(new Lease(() => Return(slot)));
        }
    }

    /// <summary>
    /// Commits the in-flight frame, making it the newest ready frame. A commit for a stale
    /// generation (resize happened mid-production) is rejected and its buffer returned.
    /// </summary>
    public bool Commit(WebSurfaceFrame frame)
    {
        if (frame.Lease.IsReturned)
            return false;

        lock (_lock)
        {
            if (_disposed || _writing is null || !ReferenceEquals(_writing.Pixels, frame.Pixels))
                return false;

            if (frame.Generation != _generation)
            {
                // Resize mid-production: this frame belongs to a dead epoch.
                _writing = null;
                frame.Dispose();
                _diagnostics.Increment(WebSurfaceCounters.StaleGenerationDrops);
                return false;
            }

            // The previous ready frame is superseded unseen — count it as dropped.
            if (_ready is not null)
            {
                Return(_ready);
                _diagnostics.Increment(WebSurfaceCounters.SupersededDrops);
            }

            _ready = _writing;
            _writing = null;
            _diagnostics.Increment(WebSurfaceCounters.FramesCommitted);
            return true;
        }
    }

    /// <summary>
    /// Abandons the in-flight frame without presenting it (producer error, cancel, dispose).
    /// </summary>
    public void CancelWriting()
    {
        lock (_lock)
        {
            if (_writing is null)
                return;
            Return(_writing);
            _writing = null;
            _diagnostics.Increment(WebSurfaceCounters.WritesCancelled);
        }
    }

    /// <summary>
    /// Takes the newest ready frame for presentation, or null when nothing new is available.
    /// The previously consuming frame is returned to the pool here.
    /// </summary>
    public WebSurfaceFrame? TryAcquireLatest()
    {
        lock (_lock)
        {
            if (_disposed || _ready is null)
                return null;

            if (_consuming is not null)
                Return(_consuming);

            // Capture the slot locally: the lease outlives the _ready field, which is null
            // immediately after this method returns.
            FrameSlot slot = _ready;
            var frame = slot.ToFrame(new Lease(() => Return(slot)));
            _consuming = slot;
            _ready = null;
            _diagnostics.Increment(WebSurfaceCounters.FramesPresented);
            return frame;
        }
    }

    /// <summary>
    /// Bumps the resize generation: in-flight and ready frames of the old epoch are discarded
    /// (the consuming frame stays alive until its presenter releases it).
    /// </summary>
    public void Resize()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _generation++;
            if (_writing is not null)
            {
                Return(_writing);
                _writing = null;
                _diagnostics.Increment(WebSurfaceCounters.StaleGenerationDrops);
            }
            if (_ready is not null)
            {
                Return(_ready);
                _ready = null;
                _diagnostics.Increment(WebSurfaceCounters.StaleGenerationDrops);
            }
            _diagnostics.Increment(WebSurfaceCounters.Resizes);
        }
    }

    /// <summary>Returns all buffers; frames still held by consumers become no-op leases.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_writing is not null) { Return(_writing); _writing = null; }
            if (_ready is not null) { Return(_ready); _ready = null; }
            if (_consuming is not null) { Return(_consuming); _consuming = null; }
            foreach (FrameSlot spare in _spare)
            {
                if (!spare.Released)
                {
                    spare.Released = true;
                    _diagnostics.NoteBufferReleased(spare.Pixels.Length);
                }
            }
            _spare.Clear();
            _diagnostics.Increment(WebSurfaceCounters.PoolDisposes);
        }
    }

    private void Return(FrameSlot slot)
    {
        if (slot is null)
            return;
        lock (_lock)
        {
            // A consuming slot whose lease the presenter already disposed reaches Return twice —
            // once through that disposal, once when the next acquire replaces the (stale)
            // consuming pointer. Slot states must stay exclusive: clear the consuming pointer on
            // the real return, ignore re-returns, and release each slot's bytes exactly once.
            if (ReferenceEquals(_consuming, slot))
                _consuming = null;
            if (slot.InSpare || slot.Released)
                return;
            if (_disposed || _spare.Count >= _maxPoolSize)
            {
                slot.Released = true;
                _diagnostics.NoteBufferReleased(slot.Pixels.Length);
                return;
            }
            slot.InSpare = true;
            _spare.Add(slot);
        }
    }

    private byte[] RentBuffer(int sizeInBytes)
    {
        lock (_lock)
        {
            for (int i = 0; i < _spare.Count; i++)
            {
                if (_spare[i].Pixels.Length == sizeInBytes)
                {
                    FrameSlot slot = _spare[i];
                    _spare.RemoveAt(i);
                    slot.InSpare = false;
                    return slot.Pixels;
                }
            }
            _diagnostics.NoteBufferAllocated(sizeInBytes);
            return new byte[sizeInBytes];
        }
    }

    internal sealed class FrameSlot(
        ulong frameNumber,
        int generation,
        int width,
        int height,
        int stride,
        WebSurfaceAlphaMode alphaMode,
        byte[] pixels)
    {
        public ulong FrameNumber { get; } = frameNumber;
        public int Generation { get; } = generation;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Stride { get; } = stride;
        public WebSurfaceAlphaMode AlphaMode { get; } = alphaMode;
        public byte[] Pixels { get; } = pixels;

        /// <summary>True while the buffer sits in the spare list (double-return guard; see Return).</summary>
        public bool InSpare { get; set; }

        /// <summary>True once the buffer's bytes were counted as released (release-once guard).</summary>
        public bool Released { get; set; }

        public WebSurfaceFrame ToFrame(Lease lease) =>
            new(FrameNumber, Generation, Width, Height, Stride,
                WebSurfacePixelFormat.Rgba8, AlphaMode, WebSurfaceOrigin.TopLeft, Pixels, lease);
    }
}

/// <summary>Diagnostic counters for the frame pipeline (B1 acceptance counts every path).</summary>
public sealed class WebSurfaceDiagnostics
{
    private long[] _counters = new long[Enum.GetValues<WebSurfaceCounters>().Length];
    private long _allocatedBytes;
    private long _releasedBytes;

    public void Increment(WebSurfaceCounters counter) => Interlocked.Increment(ref _counters[(int)counter]);

    /// <summary>Accumulates a sampled cost (micros, bytes) into a counter slot.</summary>
    public void Add(WebSurfaceCounters counter, long delta) => Interlocked.Add(ref _counters[(int)counter], delta);

    public long Get(WebSurfaceCounters counter) => Interlocked.Read(ref _counters[(int)counter]);

    public void NoteBufferAllocated(int bytes) => Interlocked.Add(ref _allocatedBytes, bytes);

    public void NoteBufferReleased(int bytes) => Interlocked.Add(ref _releasedBytes, bytes);

    /// <summary>Bytes currently held by the pool allocator (peak retention check).</summary>
    public long LiveBufferBytes => Interlocked.Read(ref _allocatedBytes) - Interlocked.Read(ref _releasedBytes);

    /// <summary>Full counter snapshot for diagnostics output and tooling; key order is enum order.</summary>
    public IReadOnlyDictionary<string, long> Snapshot()
    {
        var result = new Dictionary<string, long>();
        foreach (WebSurfaceCounters counter in Enum.GetValues<WebSurfaceCounters>())
            result[counter.ToString()] = Get(counter);
        result[nameof(LiveBufferBytes)] = LiveBufferBytes;
        return result;
    }
}

public enum WebSurfaceCounters
{
    FramesCommitted,
    FramesPresented,
    SupersededDrops,
    StaleGenerationDrops,
    WritesCancelled,
    Resizes,
    PoolDisposes,

    // ---- B2 producer-path costs (accumulated via Add, not counts) ----
    /// <summary>Frames the page producer actually delivered (one per served produce token).</summary>
    FramesFromProducer,
    /// <summary>Produce tokens refused because a request was already in flight.</summary>
    ProduceRejects,
    /// <summary>Number of SharedBuffer→pool copy samples.</summary>
    SharedBufferCopies,
    /// <summary>Sum of copy sample durations, in microseconds.</summary>
    CopyMicrosTotal,
    /// <summary>Sum of bytes moved by SharedBuffer→pool copies.</summary>
    BytesCopied,
    /// <summary>Number of produce→frame-ready latency samples.</summary>
    SyncWaits,
    /// <summary>Sum of produce→frame-ready latencies, in microseconds.</summary>
    SyncWaitMicrosTotal,
    /// <summary>Sum of page-side capture/readback times reported with frames, in microseconds.</summary>
    PageReadbackMicrosTotal,
}
