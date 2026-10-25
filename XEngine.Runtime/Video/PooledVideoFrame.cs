// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Threading;

namespace XEngine.Runtime.Video;

/// <summary>
/// A decoded RGBA frame whose pixel buffer is rented from <see cref="ArrayPool{Byte}"/>.
/// The playback session hands these to presenters; the receiver MUST return it via
/// <see cref="Dispose"/> (dropping late frames returns them the same way) so the pool
/// keeps the steady-state presentation path allocation-free.
/// </summary>
public sealed class PooledVideoFrame : IDisposable
{
    private ArrayPool<byte>? _pool;
    private byte[] _data;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Valid byte count in <see cref="Data"/> (rented arrays may be longer).</summary>
    public int ByteCount { get; }

    /// <summary>RGBA8 pixels, top-down, stride == Width * 4. Valid until Dispose.</summary>
    public byte[] Data => _data;

    /// <summary>Presentation timestamp in seconds on the media timeline.</summary>
    public double PtsSeconds { get; }

    public PooledVideoFrame(ArrayPool<byte>? pool, byte[] data, int byteCount, int width, int height, double ptsSeconds)
    {
        _pool = pool;
        _data = data;
        ByteCount = byteCount;
        Width = width;
        Height = height;
        PtsSeconds = ptsSeconds;
    }

    public void Dispose()
    {
        byte[]? data = _data;
        ArrayPool<byte>? pool = _pool;
        if (data is not null && pool is not null)
        {
            _data = [];
            _pool = null;
            pool.Return(data);
        }
    }
}
