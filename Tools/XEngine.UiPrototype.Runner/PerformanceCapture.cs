using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text.Json;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using XEngine.Runtime;
using XEngine.Runtime.RHI;

namespace XEngine.UiPrototype;

internal sealed class PerformanceCapture(string destination, string adapterName, bool dynamicInput = false, ViewportReplay? viewport = null, bool effects = false)
{
    private int _viewportUpdates, _viewportSkipped;
    private double _viewportMs;
    private long _viewportAllocated;
    private readonly TimedInputReplay? _replay = dynamicInput ? new() : null;
    private int _cyclesChecked, _cyclesFailed, _advancedSamples, _firstReplayTick = -1, _lastReplayTick = -1;
    public const int WarmupSeconds = 30, SampleSeconds = 60, Capacity = 500000, DrainFrames = 30;
    private readonly Sample[] _samples = new Sample[Capacity];
    private readonly List<GpuCommandTiming> _gpu = new(Capacity);
    private long _allAllocated, _dropped, _start, _end;
    private int _gen0, _gen1, _gen2;
    private long _previousFrame;
    private int _observerDropped;
    private long _warmStart;
    private bool _started, _ended;
    private int _sampleCount, _cpuDropped, _endTick, _warmFrames;
    public bool Finished(int tick) => _ended && tick >= _endTick + DrainFrames;
    private readonly record struct Sample(double PrepareMs, double SubmitMs, long PrepareBytes, long SubmitBytes,
        double FrameIntervalMs, int Draws, long UploadBytes, int ReplayTick, bool InputAdvanced,
        double ViewportUploadMs, long ViewportAllocatedBytes, int ViewportSourceTick, bool ViewportUploaded, int ViewportSkipped);

    public void Draw(int tick, IPrototypeAdapter adapter, in PrototypeInput input, float width, float height, float scale, object texture, PrototypeWorkload model)
    {
        long frameStart = Stopwatch.GetTimestamp();
        if (_warmStart == 0) _warmStart = frameStart;
        PrototypeInput actualInput = _replay?.Advance(Ms(frameStart - _warmStart) / 1000, width, height) ?? input;
        if (!_started && Ms(frameStart - _warmStart) >= WarmupSeconds * 1000 && (_replay == null || _replay.CurrentTick >= WarmupSeconds * 60))
        {
            _allAllocated = GC.GetTotalAllocatedBytes(true); _dropped = Graphics.Device!.DroppedGpuCommandTimings;
            _gen0 = GC.CollectionCount(0); _gen1 = GC.CollectionCount(1); _gen2 = GC.CollectionCount(2);
            _start = frameStart; _started = true; _warmFrames = tick; adapter.SetGpuTiming(true);
        }
        if (_started && !_ended && Ms(frameStart - _start) >= SampleSeconds * 1000 && (_replay == null || _replay.CurrentTick >= _firstReplayTick + SampleSeconds * 60))
        {
            _end = frameStart; _ended = true; _endTick = tick; adapter.SetGpuTiming(false);
            _allAllocated = GC.GetTotalAllocatedBytes(true) - _allAllocated;
            _gen0 = GC.CollectionCount(0) - _gen0; _gen1 = GC.CollectionCount(1) - _gen1; _gen2 = GC.CollectionCount(2) - _gen2;
        }
        bool sample = _started && !_ended;
        int previousSkipped = viewport?.SkippedSourceFrames ?? 0;
        bool uploaded = viewport?.Advance(Ms(frameStart - _warmStart) / 1000, (XEngine.Runtime.Resources.Texture2D)texture) ?? false;
        int skipped = (viewport?.SkippedSourceFrames ?? 0) - previousSkipped;
        if (sample && viewport != null)
        {
            if (uploaded) _viewportUpdates++;
            _viewportSkipped += skipped; _viewportMs += viewport.LastUploadMs;
            _viewportAllocated += viewport.LastUploadAllocatedBytes;
        }
        if (sample && _replay != null)
        {
            if (_firstReplayTick < 0) _firstReplayTick = _replay.CurrentTick;
            _lastReplayTick = _replay.CurrentTick;
            if (_replay.Advanced) _advancedSamples++;
        }
        long bytes0 = sample ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long time0 = sample ? Stopwatch.GetTimestamp() : 0;
        adapter.SetInput(actualInput); adapter.Prepare(width, height, scale, texture);
        long time1 = sample ? Stopwatch.GetTimestamp() : 0;
        long bytes1 = sample ? GC.GetAllocatedBytesForCurrentThread() : 0;
        adapter.Render();
        long time2 = sample ? Stopwatch.GetTimestamp() : 0;
        long bytes2 = sample ? GC.GetAllocatedBytesForCurrentThread() : 0;
        if (sample)
        {
            if (_sampleCount < Capacity) _samples[_sampleCount] = new Sample(Ms(time1 - time0), Ms(time2 - time1), bytes1 - bytes0, bytes2 - bytes1,
                _previousFrame == 0 ? 0 : Ms(frameStart - _previousFrame), adapter.Stats.UiDrawCalls, adapter.Stats.BufferUploadBytes,
                _replay?.CurrentTick ?? -1, _replay?.Advanced ?? false,
                viewport?.LastUploadMs ?? 0, viewport?.LastUploadAllocatedBytes ?? 0,
                viewport?.SourceTick ?? -1, uploaded, skipped);
            else _cpuDropped++;
            _sampleCount++;
        }
        if (_replay != null && _replay.Advanced && _replay.CurrentTick % CyclicInputReplay.Period == 179)
        {
            _cyclesChecked++;
            if (model.Tree.Count != 10000 || model.Tree.SelectedIdentity != 1
                || model.Properties.GetText(0) != "Property 0000: 0" || model.InputText != "N1 rendering smoke"
                || model.TreeScroll != 0 || model.PropertyScroll != 0 || Math.Abs(model.SplitRatio - .5) >= .00001
                || model.EditingProperty != -1) _cyclesFailed++;
        }
        _previousFrame = frameStart;
        while (Graphics.Device!.TryDequeueGpuCommandTiming(out var timing))
        {
            if (_gpu.Count < Capacity) _gpu.Add(timing); else _observerDropped++;
        }
    }

    public void Save(float width, float height, float scale)
    {
        Directory.CreateDirectory(destination);
        using (var writer = new StreamWriter(new GZipStream(File.Create(Path.Combine(destination, "frames.csv.gz")), CompressionLevel.Fastest)))
        {
            writer.WriteLine("sample,prepare_ms,submit_ms,prepare_bytes,submit_bytes,frame_interval_ms,draws,upload_bytes,replay_tick,input_advanced,viewport_upload_ms,viewport_allocated_bytes,viewport_source_tick,viewport_uploaded,viewport_skipped");
            for (int i = 0; i < Math.Min(_sampleCount, Capacity); i++)
            {
                var s = _samples[i];
                writer.WriteLine(FormattableString.Invariant($"{i},{s.PrepareMs:R},{s.SubmitMs:R},{s.PrepareBytes},{s.SubmitBytes},{s.FrameIntervalMs:R},{s.Draws},{s.UploadBytes},{s.ReplayTick},{(s.InputAdvanced ? 1 : 0)},{s.ViewportUploadMs:R},{s.ViewportAllocatedBytes},{s.ViewportSourceTick},{(s.ViewportUploaded ? 1 : 0)},{s.ViewportSkipped}"));
            }
        }
        // Deliberate post-measurement diagnostic collection. Never during sampling.
        long managedLive = GC.GetTotalMemory(true);
        var gcInfo = GC.GetGCMemoryInfo(); using var process = Process.GetCurrentProcess(); process.Refresh();
        var options = new JsonSerializerOptions { WriteIndented = true };
        using (var gpuFile = new GZipStream(File.Create(Path.Combine(destination, "gpu.json.gz")), CompressionLevel.Fastest)) JsonSerializer.Serialize(gpuFile, _gpu);
        File.WriteAllText(Path.Combine(destination, "summary.json"), JsonSerializer.Serialize(new {
            Adapter = adapterName, Backend = Graphics.Device!.Backend.ToString(), Runtime = Environment.Version.ToString(),
            Device = Graphics.Device.Capabilities.Baseline,
            DynamicInput = dynamicInput, CyclesChecked = _cyclesChecked, CyclesFailed = _cyclesFailed,
            VisualEffects = effects,
            DynamicViewport = viewport != null, ViewportUploadMode = viewport?.UploadMode,
            ViewportUpdates = _viewportUpdates, ViewportSkippedSourceFrames = _viewportSkipped,
            ViewportUploadCpuMs = _viewportMs, ViewportUploadAllocatedBytes = _viewportAllocated,
            ViewportUploadPayloadBytes = (long)_viewportUpdates * ViewportReplay.BytesPerUpdate,
            AdvancedSamples = _advancedSamples, FirstReplayTick = _firstReplayTick, LastReplayTick = _lastReplayTick,
            MaximumBacklogTicks = _replay?.MaximumBacklogTicks ?? 0,
            ServerGc = GCSettings.IsServerGC, Width = width, Height = height, Scale = scale, VSync = Window.VSync,
            WarmupSeconds, SampleSeconds, WarmupFrames = _warmFrames, SampleFrames = _sampleCount, DrainFrames,
            ElapsedMs = Ms(_end - _start), PrepareMs = Summarize(0), SubmitMs = Summarize(1), FrameIntervalMs = Summarize(2),
            PrepareBytesPerFrame = MeanBytes(true), SubmitBytesPerFrame = MeanBytes(false),
            AllThreadAllocatedBytes = _allAllocated, Gen0 = _gen0, Gen1 = _gen1, Gen2 = _gen2,
            GpuSupported = Graphics.Device.SupportsGpuCommandTimings, GpuCount = _gpu.Count,
            GpuDropped = Graphics.Device.DroppedGpuCommandTimings - _dropped, ObserverDropped = _observerDropped,
            CpuDropped = _cpuDropped, CaptureComplete = _gpu.Count == _sampleCount && _observerDropped == 0 && _cpuDropped == 0 && Graphics.Device.DroppedGpuCommandTimings == _dropped
                && (!dynamicInput || (_cyclesChecked >= 30 && _cyclesFailed == 0 && Math.Abs(_advancedSamples - 3600) <= 1)),
            ObserverCapacity = Capacity, ObserverArrayPayloadBytes = (long)Capacity * (Unsafe.SizeOf<Sample>() + Unsafe.SizeOf<GpuCommandTiming>()),
            PostMeasurementManagedLive = managedLive, gcInfo.TotalCommittedBytes, gcInfo.FragmentedBytes,
            process.PrivateMemorySize64, process.WorkingSet64,
            Scope = "DynamicInput selects 60Hz logical events, DynamicViewport selects 30Hz latest-source image updates. Upload CPU/payload is recorded separately from UI Prepare/Submit. Frame interval and all-thread allocation include uploads, shared host and replay observer. UI GPU intervals may include upload dependencies, but are not isolated transfer timers. Dynamic sample boundaries wait for the full input sequence as well as elapsed time. Memory is whole-process diagnostic."
        }, options));
    }
    private object Summarize(int field)
    {
        int count = Math.Min(_sampleCount, Capacity);
        var values = new double[count]; double sum = 0;
        for (int i = 0; i < count; i++) { var s = _samples[i]; values[i] = field == 0 ? s.PrepareMs : field == 1 ? s.SubmitMs : s.FrameIntervalMs; sum += values[i]; }
        Array.Sort(values);
        return new { Mean = sum / count, P50 = values[(int)Math.Ceiling(count * .50) - 1], P95 = values[(int)Math.Ceiling(count * .95) - 1],
            P99 = values[(int)Math.Ceiling(count * .99) - 1], Min = values[0], Max = values[^1] };
    }
    private double MeanBytes(bool prepare) { long sum = 0; int count = Math.Min(_sampleCount, Capacity); for (int i = 0; i < count; i++) sum += prepare ? _samples[i].PrepareBytes : _samples[i].SubmitBytes; return sum / (double)count; }
    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
}
