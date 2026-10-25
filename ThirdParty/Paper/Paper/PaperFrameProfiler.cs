using System;
using System.Diagnostics;

namespace XEngine.PaperUI;

/// <summary>Optional bounded CPU capture, independent of DevTools and its UI. Main-thread only.
/// Times include synchronous renderer work but do not measure GPU execution or presentation.
/// Copy samples only after a measurement interval: exporting allocates intentionally.</summary>
public sealed class PaperFrameProfiler
{
    private readonly PaperFrameSample[] _samples;
    private PaperFrameSample _current;
    private long _start, _checkpoint, _allocationStart;
    private int _next, _count;

    public PaperFrameProfiler(int capacity = 32768)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new PaperFrameSample[capacity];
    }

    public int Count => _count;
    public long TotalFrames { get; private set; }
    public long DroppedFrames => TotalFrames - _count;

    internal void Begin()
    {
        _current = default;
        _allocationStart = GC.GetAllocatedBytesForCurrentThread();
        _start = _checkpoint = Stopwatch.GetTimestamp();
    }

    internal void Mark(PaperFramePhase phase)
    {
        long now = Stopwatch.GetTimestamp();
        double milliseconds = (now - _checkpoint) * (1000.0 / Stopwatch.Frequency);
        _checkpoint = now;
        switch (phase)
        {
            case PaperFramePhase.Build: _current.BuildMs = milliseconds; break;
            case PaperFramePhase.Styles: _current.StylesMs = milliseconds; break;
            case PaperFramePhase.Layout: _current.LayoutMs = milliseconds; break;
            case PaperFramePhase.PostLayout: _current.PostLayoutMs = milliseconds; break;
            case PaperFramePhase.Culling: _current.CullingMs = milliseconds; break;
            case PaperFramePhase.Layered: _current.LayeredMs = milliseconds; break;
            case PaperFramePhase.Interaction: _current.InteractionMs = milliseconds; break;
            case PaperFramePhase.Render: _current.GeometryMs = milliseconds; break;
            case PaperFramePhase.Upload: _current.RendererMs = milliseconds; break;
            case PaperFramePhase.Cleanup: _current.CleanupMs = milliseconds; break;
        }
    }

    internal void End(int elements, int vertices, int indices, int drawCalls)
    {
        _current.TotalMs = (Stopwatch.GetTimestamp() - _start) * (1000.0 / Stopwatch.Frequency);
        _current.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _allocationStart;
        _current.Elements = elements;
        _current.Vertices = vertices;
        _current.Indices = indices;
        _current.DrawCalls = drawCalls;
        _current.Sequence = ++TotalFrames;
        _samples[_next] = _current;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    /// <summary>Returns retained samples in chronological order. Does not reset the capture.</summary>
    public PaperFrameSample[] CopySamples()
    {
        var result = new PaperFrameSample[_count];
        int first = (_next - _count + _samples.Length) % _samples.Length;
        for (int i = 0; i < _count; i++) result[i] = _samples[(first + i) % _samples.Length];
        return result;
    }
}

internal enum PaperFramePhase { Build, Styles, Layout, PostLayout, Culling, Layered, Interaction, Render, Upload, Cleanup }

public struct PaperFrameSample
{
    public long Sequence { get; internal set; }
    public double TotalMs { get; internal set; }
    public double BuildMs { get; internal set; }
    public double StylesMs { get; internal set; }
    public double LayoutMs { get; internal set; }
    public double PostLayoutMs { get; internal set; }
    public double CullingMs { get; internal set; }
    public double LayeredMs { get; internal set; }
    public double InteractionMs { get; internal set; }
    public double GeometryMs { get; internal set; }
    public double RendererMs { get; internal set; }
    public double CleanupMs { get; internal set; }
    public long AllocatedBytes { get; internal set; }
    public int Elements { get; internal set; }
    public int Vertices { get; internal set; }
    public int Indices { get; internal set; }
    public int DrawCalls { get; internal set; }
}
