// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using XEngine.Runtime;
using XEngine.Runtime.Profiling;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.GUI;

namespace XEngine.Editor.Core;

/// <summary>
/// Low-frequency editor profiler sampler. It intentionally reports GPU timestamps only when a
/// backend has a real query implementation; <see cref="Graphics.LastFrameWaitMs"/> is exposed
/// separately because it measures CPU time blocked waiting for the render thread/present.
/// </summary>
public static class EditorProfiler
{
    public const double MinimumSampleIntervalSeconds = 0.25;
    public const int MaxCpuScopesPerThread = 128;

    private static readonly Process s_process = Process.GetCurrentProcess();
    [ThreadStatic] private static CpuScopeBuffer? t_cpuScopes;
    private static long s_cpuFrameIndex;
    private static long s_lastTimestamp;
    private static TimeSpan s_lastCpuTime;
    private static ProfilerSample s_last;
    private static ProfilerTelemetryFrame s_lastTelemetry;
    private static ulong s_nextTelemetryFrameIndex;

    public static ProfilerSample Last => s_last;

    /// <summary>Advance the fixed-capacity CPU scope buffer to a new editor frame.</summary>
    [HotPath]
    public static void BeginCpuFrame() => Interlocked.Increment(ref s_cpuFrameIndex);

    /// <summary>Start an allocation-free CPU timing scope on the current thread.</summary>
    [HotPath]
    public static CpuScope BeginCpuScope(string name)
    {
        CpuScopeBuffer? buffer = t_cpuScopes;
        if (buffer is null)
            return BeginCpuScopeCold(name);
        return new CpuScope(buffer, name, Volatile.Read(ref s_cpuFrameIndex), Stopwatch.GetTimestamp());
    }

    /// <summary>Returns the current thread's bounded scope frame summary.</summary>
    public static CpuScopeFrame GetCurrentThreadCpuFrame()
    {
        CpuScopeBuffer? buffer = t_cpuScopes;
        return buffer is null
            ? new CpuScopeFrame(Volatile.Read(ref s_cpuFrameIndex), 0, 0, MaxCpuScopesPerThread)
            : buffer.GetFrame(Volatile.Read(ref s_cpuFrameIndex));
    }

    /// <summary>Reads a scope by insertion order from the current thread's current frame.</summary>
    public static bool TryGetCurrentThreadCpuScope(int index, out CpuScopeSample sample)
    {
        CpuScopeBuffer? buffer = t_cpuScopes;
        if (buffer is not null)
            return buffer.TryGet(Volatile.Read(ref s_cpuFrameIndex), index, out sample);

        sample = default;
        return false;
    }

    /// <summary>Capture a sample when the minimum interval has elapsed. Returns the previous
    /// sample when called more frequently, so a panel can call this from every repaint.</summary>
    public static ProfilerSample SampleNow()
    {
        long now = Stopwatch.GetTimestamp();
        if (s_lastTimestamp != 0 &&
            (now - s_lastTimestamp) / (double)Stopwatch.Frequency < MinimumSampleIntervalSeconds)
            return s_last;

        TimeSpan cpu = s_process.TotalProcessorTime;
        double elapsedMs = s_lastTimestamp == 0
            ? 0
            : (now - s_lastTimestamp) * 1000.0 / Stopwatch.Frequency;
        double cpuMs = s_lastTimestamp == 0 ? 0 : (cpu - s_lastCpuTime).TotalMilliseconds;
        float cpuPercent = elapsedMs <= 0 || Environment.ProcessorCount <= 0
            ? 0
            : (float)Math.Clamp(cpuMs / elapsedMs / Environment.ProcessorCount * 100.0, 0.0, 100.0);

        var device = Graphics.Device;
        string backend = device?.Capabilities.BackendName ?? device?.Backend.ToString() ?? "No device";
        float? gpuFrameTimeMs = Graphics.LastGpuTimestampFrame?.DurationMs;
        bool gpuTimestampSupported = gpuFrameTimeMs.HasValue;
        string gpuStatus = gpuTimestampSupported
            ? $"GPU frame {gpuFrameTimeMs.Value:F2} ms (asynchronous timestamp query)"
            : device == null
            ? "GPU timestamps unavailable (no graphics device)"
            : device.Capabilities.HardwareSupportsTimestampQueries
                    ? "GPU hardware timestamps available; engine resolve pending"
                    : "GPU timestamps unavailable on this backend";

        s_last = new ProfilerSample(
            DateTimeOffset.UtcNow,
            cpuPercent,
            s_process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            RenderStats.Last,
            Graphics.LastFrameWaitMs,
            backend,
            gpuTimestampSupported,
            gpuStatus,
            gpuFrameTimeMs,
            SampleSurface());
        s_lastTimestamp = now;
        s_lastCpuTime = cpu;
        return s_last;
    }

    /// <summary>Captures the local Editor through the player-compatible telemetry schema.</summary>
    public static ProfilerTelemetryFrame SampleTelemetryNow()
    {
        ProfilerSample sample = SampleNow();
        long timestamp = sample.TimestampUtc.ToUnixTimeMilliseconds();
        if (s_lastTelemetry.TimestampUnixMilliseconds == timestamp)
            return s_lastTelemetry;

        var device = Graphics.Device;
        ProfilerTelemetryCapabilities capabilities = ProfilerTelemetryCapabilities.None;
        if (device?.Capabilities.HardwareSupportsTimestampQueries == true)
            capabilities |= ProfilerTelemetryCapabilities.GpuTimestampQueries;
        if (sample.GpuFrameTimeMs.HasValue)
            capabilities |= ProfilerTelemetryCapabilities.GpuFrameTimestampSample;

        s_lastTelemetry = new ProfilerTelemetryFrame(
            timestamp,
            ++s_nextTelemetryFrameIndex,
            sample.ProcessCpuPercent,
            sample.GpuFrameTimeMs ?? 0f,
            sample.WorkingSetBytes,
            sample.ManagedHeapBytes,
            RhiMemoryBytes: 0,
            AdapterVramBudgetBytes: 0,
            device?.Backend ?? GraphicsBackend.Null,
            ProfilerTelemetryTarget.LocalEditor,
            capabilities,
            s_lastTelemetry.FrameIndex == 0 ? ProfilerMetricSource.Unavailable : ProfilerMetricSource.OperatingSystem,
            ProfilerMetricSource.OperatingSystem,
            ProfilerMetricSource.Runtime,
            ProfilerMetricSource.Unavailable,
            ProfilerMetricSource.Unavailable);
        return s_lastTelemetry;
    }

    /// <summary>Reset the sampler state. Intended for deterministic tests and editor restarts.</summary>
    internal static void ResetForTests()
    {
        s_lastTimestamp = 0;
        s_lastCpuTime = default;
        s_last = default;
        s_lastTelemetry = default;
        s_nextTelemetryFrameIndex = 0;
        Interlocked.Exchange(ref s_cpuFrameIndex, 0);
        t_cpuScopes?.Reset();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CpuScope BeginCpuScopeCold(string name)
    {
        var buffer = new CpuScopeBuffer();
        t_cpuScopes = buffer;
        return new CpuScope(buffer, name, Volatile.Read(ref s_cpuFrameIndex), Stopwatch.GetTimestamp());
    }

    private static EditorSurfaceSample SampleSurface()
    {
        var app = EditorApplication.Instance;
        UiRenderStats ui = app?.PaperRenderer?.LastUiStats ?? default;
        int width = ui.FrameWidth;
        int height = ui.FrameHeight;
        if (Window.InternalWindow != null)
        {
            var framebuffer = Window.InternalWindow.FramebufferSize;
            width = framebuffer.X;
            height = framebuffer.Y;
        }

        EditorFrameScheduler? scheduler = app?.FrameScheduler;
        return new EditorSurfaceSample(
            Width: width,
            Height: height,
            DirtyReason: scheduler?.LastRenderReasons ?? EditorSurfaceDirtyReason.None,
            SchedulerState: scheduler?.State ?? EditorSurfaceSchedulerState.Unscheduled,
            UiDrawCalls: ui.UiDrawCalls,
            UiRenderTargetBytes: ui.UiRenderTargetBytes,
            PresentCount: Window.PresentCount,
            PresentWaitMs: Graphics.LastFrameWaitMs,
            PresentFps: Window.PresentStats.PresentFps,
            SustainedFps: Window.PresentStats.SustainedFps,
            CpuFrameMs: Window.PresentStats.LastCpuMs);
    }

    internal sealed class CpuScopeBuffer
    {
        private readonly CpuScopeSample[] _samples = new CpuScopeSample[MaxCpuScopesPerThread];
        private long _frameIndex = long.MinValue;
        private int _count;
        private int _dropped;

        [HotPath]
        public void Record(string name, long frameIndex, long startedAt)
        {
            if (_frameIndex != frameIndex)
            {
                _frameIndex = frameIndex;
                _count = 0;
                _dropped = 0;
            }

            if (_count == _samples.Length)
            {
                _dropped++;
                return;
            }

            long elapsed = Stopwatch.GetTimestamp() - startedAt;
            _samples[_count++] = new CpuScopeSample(name,
                (float)(elapsed * 1000.0 / Stopwatch.Frequency));
        }

        public CpuScopeFrame GetFrame(long frameIndex) => _frameIndex == frameIndex
            ? new CpuScopeFrame(frameIndex, _count, _dropped, _samples.Length)
            : new CpuScopeFrame(frameIndex, 0, 0, _samples.Length);

        public bool TryGet(long frameIndex, int index, out CpuScopeSample sample)
        {
            if (_frameIndex == frameIndex && (uint)index < (uint)_count)
            {
                sample = _samples[index];
                return true;
            }

            sample = default;
            return false;
        }

        public void Reset()
        {
            _frameIndex = long.MinValue;
            _count = 0;
            _dropped = 0;
        }
    }
}

/// <summary>Stack-only scope returned by <see cref="EditorProfiler.BeginCpuScope"/>.</summary>
public readonly struct CpuScope : IDisposable
{
    private readonly EditorProfiler.CpuScopeBuffer? _buffer;
    private readonly string _name;
    private readonly long _frameIndex;
    private readonly long _startedAt;

    internal CpuScope(EditorProfiler.CpuScopeBuffer buffer, string name, long frameIndex, long startedAt)
    {
        _buffer = buffer;
        _name = name;
        _frameIndex = frameIndex;
        _startedAt = startedAt;
    }

    [HotPath]
    public void Dispose() => _buffer?.Record(_name, _frameIndex, _startedAt);
}

/// <summary>A retained CPU scope duration. Scope storage is bounded per thread and per frame.</summary>
public readonly record struct CpuScopeSample(string Name, float DurationMs);

/// <summary>Current thread CPU scope retention state for the active editor frame.</summary>
public readonly record struct CpuScopeFrame(long FrameIndex, int ScopeCount, int DroppedScopeCount, int Capacity);

public readonly record struct ProfilerSample(
    DateTimeOffset TimestampUtc,
    float ProcessCpuPercent,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    RenderStats.Frame Render,
    float RenderThreadWaitMs,
    string BackendName,
    bool GpuTimestampSupported,
    string GpuStatus,
    float? GpuFrameTimeMs = null,
    EditorSurfaceSample Surface = default)
{
    public float FrameTimeMs => Render.FrameTimeMs;
    public float ColorPassMs => Render.ColorPassMs;
    public float ShadowPassMs => Render.ShadowPassMs;
    public float PostFxMs => Render.PostFxMs;
}

public readonly record struct EditorSurfaceSample(
    int Width,
    int Height,
    EditorSurfaceDirtyReason DirtyReason,
    EditorSurfaceSchedulerState SchedulerState,
    int UiDrawCalls,
    long UiRenderTargetBytes,
    long PresentCount,
    float PresentWaitMs,
    double PresentFps = 0,
    double SustainedFps = 0,
    double CpuFrameMs = 0);
