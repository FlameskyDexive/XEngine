using System;

namespace XEngine.WebSurface;

/// <summary>
/// Pixel format of a produced surface frame. B1 pins the CPU baseline: tightly packed RGBA8,
/// top-left origin, straight (non-premultiplied) alpha, sRGB encoding. Additional formats are
/// opt-in per backend capability (B8 adds fast paths), never inferred.
/// </summary>
public enum WebSurfacePixelFormat : byte
{
    Rgba8 = 0,
}

/// <summary>Semantic meaning of the alpha channel in a produced frame.</summary>
public enum WebSurfaceAlphaMode : byte
{
    Opaque = 0,
    Straight = 1,
}

/// <summary>Vertical origin of the pixel data inside the buffer.</summary>
public enum WebSurfaceOrigin : byte
{
    TopLeft = 0,
}

/// <summary>
/// One produced surface frame plus the lease that returns its buffer to the pool. Instances are
/// only valid until <see cref="Lease"/> is disposed; the pool owns the memory, consumers only
/// borrow it. <see cref="FrameNumber"/> is monotonically increasing per session and
/// <see cref="Generation"/> increments on every resize — stale generations must be dropped by
/// the consumer path, never presented.
/// </summary>
public sealed class WebSurfaceFrame : IDisposable
{
    internal WebSurfaceFrame(
        ulong frameNumber,
        int generation,
        int width,
        int height,
        int stride,
        WebSurfacePixelFormat format,
        WebSurfaceAlphaMode alphaMode,
        WebSurfaceOrigin origin,
        byte[] pixels,
        Lease lease)
    {
        FrameNumber = frameNumber;
        Generation = generation;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        AlphaMode = alphaMode;
        Origin = origin;
        Pixels = pixels;
        Lease = lease;
    }

    public ulong FrameNumber { get; }
    public int Generation { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Bytes per row; may exceed Width·4 when the backend pads rows.</summary>
    public int Stride { get; }
    public WebSurfacePixelFormat Format { get; }
    public WebSurfaceAlphaMode AlphaMode { get; }
    public WebSurfaceOrigin Origin { get; }
    public byte[] Pixels { get; }
    public Lease Lease { get; }

    public void Dispose() => Lease.Dispose();
}

/// <summary>
/// Scoped ownership token for one pooled buffer. Dispose returns the buffer to the pool exactly
/// once; double disposal is a no-op (dispose/resize races make idempotent release mandatory).
/// </summary>
public sealed class Lease
{
    private Action? _returnAction;
    private readonly object _lock = new();

    internal Lease(Action returnAction) => _returnAction = returnAction;

    public bool IsReturned
    {
        get { lock (_lock) return _returnAction == null; }
    }

    public void Dispose()
    {
        Action? action;
        lock (_lock)
        {
            action = _returnAction;
            _returnAction = null;
        }
        action?.Invoke();
    }
}
