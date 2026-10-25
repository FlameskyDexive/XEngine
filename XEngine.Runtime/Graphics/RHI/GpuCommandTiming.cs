using System;

namespace XEngine.Runtime.RHI;

/// <summary>A GPU elapsed interval around one opted-in command buffer, resolved only after
/// its frame fence retires. Includes dependencies/barriers within the interval, not CPU encoding
/// or presentation. Surface identifies the originating window, not the most recently presented one.</summary>
public readonly record struct GpuCommandTiming(ulong FrameId, PresentationSurfaceId Surface,
    string Name, float DurationMs);

/// <summary>Bounded render-thread to observer handoff. Overflow is reported, never silently hidden.</summary>
internal sealed class GpuCommandTimingBuffer
{
    private readonly object _gate = new();
    private readonly GpuCommandTiming[] _samples;
    private int _read, _count;
    private long _dropped;

    public GpuCommandTimingBuffer(int capacity = 256)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new GpuCommandTiming[capacity];
    }

    public long Dropped { get { lock (_gate) return _dropped; } }
    public void RecordDropped(int count) { lock (_gate) _dropped += count; }

    public void Publish(in GpuCommandTiming sample)
    {
        lock (_gate)
        {
            if (_count == _samples.Length)
            {
                _read = (_read + 1) % _samples.Length;
                _count--;
                _dropped++;
            }
            _samples[(_read + _count) % _samples.Length] = sample;
            _count++;
        }
    }

    public bool TryRead(out GpuCommandTiming sample)
    {
        lock (_gate)
        {
            if (_count == 0) { sample = default; return false; }
            sample = _samples[_read];
            _samples[_read] = default;
            _read = (_read + 1) % _samples.Length;
            _count--;
            return true;
        }
    }
}
