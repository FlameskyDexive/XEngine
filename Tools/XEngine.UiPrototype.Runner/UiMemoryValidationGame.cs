using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.UiPrototype;

// Deliberately instrumented memory/lifetime diagnostic, not a latency benchmark.
internal sealed class UiMemoryValidationGame(string destination) : Game
{
    private const int Regions = 32, Cycles = 3, Width = 1280, Height = 720;
    private readonly UiMemoryBudget _budget = new(32 * 1024 * 1024);
    private readonly UiMemoryBudget _atlasBudget = new(96 * 1024 * 1024);
    private readonly UiQuillPaintIsland?[] _islands = new UiQuillPaintIsland[Regions];
    private readonly List<object> _results = new();
    private readonly List<WeakReference> _retiredCaches = new();
    private readonly List<WeakReference> _clearArrays = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private PaperRenderer? _renderer;
    private Canvas? _canvas;
    private RenderTexture? _target;
    private int _step;
    private long _smallCharge;
    private static readonly FieldInfo CacheField = typeof(UiQuillPaintIsland).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo FreeCommandsField = typeof(CommandBuffer).Assembly.GetType("XEngine.Runtime.CommandBufferPool")!.GetField("s_free", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo CommandStreamField = typeof(CommandBuffer).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly record struct MemorySnapshot(string Stage, long ManagedEstimate, long GcCommittedAtLastCollection,
        long GcFragmentedAtLastCollection, long PrivateBytes, long WorkingSet, long ProcessPeakWorkingSet,
        long CacheAdmission, long CacheAdmissionPeak, long AtlasAdmission, long AtlasAdmissionPeak,
        int FreeCommandPoolBuffers, long FreeCommandPoolStreamBytes);

    public override void OnGui(Scene? scene, Paper paper)
    {
        try
        {
            if (_step == 0)
            {
                Directory.CreateDirectory(destination);
                _renderer = new PaperRenderer(); _renderer.Initialize(Width, Height);
                foreach (int size in new[] { 256, 1024, 4096 }) ProbeAtlas(size);
                _canvas = new Canvas(new UiCanvasAtlasOwner(_renderer, _atlasBudget), new FontAtlasSettings { AtlasSize = 128 });
                _target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
                Wait(); Collect(); _results.Add(Snapshot("regions-baseline-after-diagnostic-gc"));
                _step++; return;
            }
            int cycle = (_step - 1) / 8, stage = (_step - 1) % 8;
            if (cycle == Cycles) { Finish(); Cleanup(); Quit(); return; }
            switch (stage)
            {
                case 0:
                    for (int i = 0; i < Regions; i++) _islands[i] = new(_budget, 8192, 16384, 4, 64);
                    CaptureAll(8); _smallCharge = _budget.ReservedBytes;
                    _results.Add(Snapshot($"cycle-{cycle}-small")); Compare(cycle, "small", 8); break;
                case 1:
                    long before = _budget.ReservedBytes;
                    TrackCaches(); CaptureAll(256);
                    if (_budget.ReservedBytes <= before || _budget.PeakReservedBytes <= _budget.ReservedBytes)
                        throw new InvalidOperationException("Growth did not record overlapping old/new capacity.");
                    _results.Add(Snapshot($"cycle-{cycle}-grown")); Compare(cycle, "grown", 256); break;
                case 2:
                    TrackCaches(); foreach (var island in _islands) island!.Evict();
                    RequireZero(); _results.Add(Snapshot($"cycle-{cycle}-evicted-before-retirement")); break;
                case 3:
                    CaptureAll(8);
                    if (_budget.ReservedBytes != _smallCharge) throw new InvalidOperationException("Reopen retained oversized capacity.");
                    _results.Add(Snapshot($"cycle-{cycle}-reopened")); Compare(cycle, "reopened", 8); break;
                case 4:
                    TrackCaches(); DisposeIslands(); RequireZero();
                    _results.Add(Snapshot($"cycle-{cycle}-closed-before-retirement")); break;
                case 7:
                    Wait(); Collect();
                    if (_retiredCaches.Any(static reference => reference.IsAlive)) throw new InvalidOperationException("Closed CPU paint caches survived diagnostic GC.");
                    _results.Add(Snapshot($"cycle-{cycle}-closed-after-frame-reuse-and-diagnostic-gc")); break;
            }
            _step++;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error); Environment.ExitCode = 2;
            File.WriteAllText(Path.Combine(destination, "partial-memory.json"), JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));
            Cleanup(); Quit();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ProbeAtlas(int size)
    {
        Wait(); Collect();
        var tracker = new AtlasProbeRenderer(_renderer!, _results, _clearArrays, Snapshot, size);
        using var owner = new UiCanvasAtlasOwner(tracker, _atlasBudget);
        _results.Add(Snapshot($"atlas-{size}-before"));
        long currentAllocated = GC.GetAllocatedBytesForCurrentThread(), allAllocated = GC.GetTotalAllocatedBytes(true);
        long start = Stopwatch.GetTimestamp();
        var canvas = new Canvas(owner, new FontAtlasSettings { AtlasSize = size });
        double instrumentedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long threadBytes = GC.GetAllocatedBytesForCurrentThread() - currentAllocated;
        long allBytes = GC.GetTotalAllocatedBytes(true) - allAllocated;
        long expected = (long)size * size * 4;
        if (tracker.FullClearBytes != expected || tracker.FullClears != 1 || owner.LivePayloadBytes != expected || threadBytes < tracker.MaxClearArrayBytes)
            throw new InvalidOperationException("Atlas clear payload/ownership accounting mismatch.");
        _results.Add(new { Stage = $"atlas-{size}-initialization", Size = size, ClearPayloadBytes = tracker.FullClearBytes,
            tracker.MaxClearArrayBytes, tracker.ClearUploadCalls, tracker.TotalUploadBytes, CurrentThreadAllocatedIncludingObserver = threadBytes,
            AllThreadAllocatedIncludingObserver = allBytes, InstrumentedWallMs = instrumentedMs,
            Snapshot = Snapshot($"atlas-{size}-created") });
        Wait(); _results.Add(Snapshot($"atlas-{size}-upload-completed"));
        canvas.Dispose(); owner.Dispose();
        if (_atlasBudget.ReservedBytes != 0) throw new InvalidOperationException("Atlas owner leaked admission on close.");
        _results.Add(Snapshot($"atlas-{size}-disposed-before-frame-reuse"));
    }

    private void CaptureAll(int rectangles)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Regions; i++)
        {
            _canvas!.BeginFrame(Width, Height);
            BuildRegion(_canvas, i, rectangles); _islands[i]!.Capture(_canvas);
        }
        _results.Add(new { Stage = "capture-all", RectanglesPerRegion = rectangles,
            ThreadAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated, Regions,
            CurrentAdmission = _budget.ReservedBytes, PeakAdmission = _budget.PeakReservedBytes });
    }

    private static void BuildRegion(Canvas canvas, int region, int rectangles)
    {
        int ox = region % 8 * 160, oy = region / 8 * 180;
        for (int i = 0; i < rectangles; i++)
            canvas.RectFilled(ox + 8 + i % 16 * 9, oy + 8 + i / 16 * 9, 7, 7,
                new Color32((byte)(40 + region * 5), (byte)(80 + region * 3), (byte)(180 - i % 2 * 30), 220));
    }

    private void Compare(int cycle, string stage, int rectangles)
    {
        _canvas!.BeginFrame(Width, Height);
        for (int i = 0; i < Regions; i++) BuildRegion(_canvas, i, rectangles);
        Clear(); _renderer!.SetRenderTarget(_target!.frameBuffer); _canvas.Render(); Wait();
        byte[] expected = Read();
        Clear();
        long uploaded = 0;
        foreach (var island in _islands)
        {
            var stats = island!.Render(_target.frameBuffer, Width, Height);
            uploaded += stats.VertexBytes + stats.IndexBytes + stats.BrushBytes;
        }
        _results.Add(Snapshot($"cycle-{cycle}-{stage}-submitted-before-wait")); Wait();
        byte[] actual = Read();
        Save(expected, $"cycle-{cycle}-{stage}-reference.png"); Save(actual, $"cycle-{cycle}-{stage}-retained.png");
        if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("Many-region paint differs from full Paper reference.");
        Clear();
        foreach (var island in _islands)
        {
            var stats = island!.Render(_target.frameBuffer, Width, Height);
            if (stats.VertexBytes + stats.IndexBytes + stats.BrushBytes != 0) throw new InvalidOperationException("Idle region uploaded geometry.");
            if (island.Render(_target.frameBuffer, Width, Height, visible: false) != default)
                throw new InvalidOperationException("Hidden region submitted work.");
        }
        Wait(); _results.Add(new { Stage = $"cycle-{cycle}-{stage}-image", PixelsEqual = true, InitialUploadBytes = uploaded, IdleUploadBytes = 0 });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TrackCaches()
    {
        foreach (var island in _islands)
        {
            object? cache = CacheField.GetValue(island!);
            if (cache != null) _retiredCaches.Add(new WeakReference(cache));
        }
    }

    private MemorySnapshot Snapshot(string stage)
    {
        _process.Refresh(); var gc = GC.GetGCMemoryInfo();
        int poolCount = 0; long poolBytes = 0;
        // Diagnostic snapshot of directly owned managed arrays, not all ArrayPool storage
        // or in-flight commands. ConcurrentBag enumeration provides a safe snapshot.
        foreach (var command in (System.Collections.IEnumerable)FreeCommandsField.GetValue(null)!)
        { poolCount++; poolBytes += ((byte[])CommandStreamField.GetValue(command)!).LongLength; }
        return new(stage, GC.GetTotalMemory(false), gc.TotalCommittedBytes, gc.FragmentedBytes,
            _process.PrivateMemorySize64, _process.WorkingSet64, _process.PeakWorkingSet64,
            _budget.ReservedBytes, _budget.PeakReservedBytes, _atlasBudget.ReservedBytes, _atlasBudget.PeakReservedBytes,
            poolCount, poolBytes);
    }

    private void Finish()
    {
        _canvas!.Dispose(); _canvas = null; Wait(); Collect(); RequireZero();
        if (_atlasBudget.ReservedBytes != 0 || _clearArrays.Any(static reference => reference.IsAlive))
            throw new InvalidOperationException("Closed atlases retained a charge or initialization array.");
        _results.Add(Snapshot("all-closed-after-diagnostic-gc"));
        File.WriteAllText(Path.Combine(destination, "validation.json"), JsonSerializer.Serialize(new {
            Backend = Graphics.Device!.Backend.ToString(), GCSettings.IsServerGC, Regions, Cycles,
            RetiredCacheReferencesChecked = _retiredCaches.Count, ClearArrayReferencesChecked = _clearArrays.Count,
            AllRetiredCachesCollected = true, AllClearArraysCollected = true,
            AdmissionAfterClose = _budget.ReservedBytes, PeakAdmission = _budget.PeakReservedBytes,
            AtlasAdmissionAfterClose = _atlasBudget.ReservedBytes, PeakAtlasAdmission = _atlasBudget.PeakReservedBytes,
            Scope = "Instrumented synchronous boundary snapshots and whole-process working-set high-water, not continuous per-operation peaks or resident VRAM. GC committed/fragmented reflect last GC. Initialization allocation/wall time includes snapshot observers. Forced GC and explicit GPU waits are diagnostics only. Geometry workload uses 32 regions sharing one Canvas/atlas, not 32 production panels. Three close/reopen cycles are bounded fixture evidence, not an editor soak test.",
            Results = _results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"N3 memory: {_clearArrays.Count} atlas array references and {_retiredCaches.Count} retired paint caches released; 32 regions / 3 cycles passed.");
    }

    private void RequireZero() { if (_budget.ReservedBytes != 0) throw new InvalidOperationException("Cache leases survived closure."); }
    private void DisposeIslands() { for (int i = 0; i < Regions; i++) { _islands[i]?.Dispose(); _islands[i] = null; } }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static void Wait() { using var cmd = Graphics.GetCommandBuffer("N3 memory diagnostic wait"); Graphics.SubmitAndWait(cmd); }
    private void Clear()
    {
        using var cmd = Graphics.GetCommandBuffer("N3 memory clear"); cmd.SetRenderTarget(_target!.frameBuffer);
        cmd.SetViewport(0, 0, Width, Height); cmd.DisableScissor();
        cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f, 0f, 0f, 1f)); Graphics.Submit(cmd);
    }
    private byte[] Read()
    {
        var bytes = new byte[_target!.MainTexture.GetSize()]; _target.MainTexture.GetData<byte>(bytes.AsMemory());
        if (Graphics.Device!.Backend == XEngine.Runtime.RHI.GraphicsBackend.OpenGL)
            for (int y = 0; y < Height / 2; y++) for (int x = 0; x < Width * 4; x++)
            { int a = y * Width * 4 + x, b = (Height - y - 1) * Width * 4 + x; (bytes[a], bytes[b]) = (bytes[b], bytes[a]); }
        return bytes;
    }
    private void Save(byte[] bytes, string name)
    {
        using var image = new ImageMagick.MagickImage();
        image.ReadPixels(bytes, new ImageMagick.PixelReadSettings(Width, Height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
        image.Write(Path.Combine(destination, name), ImageMagick.MagickFormat.Png32);
    }
    private void Cleanup() { DisposeIslands(); _canvas?.Dispose(); _target?.Dispose(); _renderer?.Dispose(); _process.Dispose(); }

    private sealed class AtlasProbeRenderer(PaperRenderer inner, List<object> results, List<WeakReference> arrays,
        Func<string, MemorySnapshot> snapshot, int size) : ICanvasRenderer
    {
        public long FullClearBytes, TotalUploadBytes;
        public int FullClears, MaxClearArrayBytes, ClearUploadCalls;
        private int _nextRow;
        public object CreateTexture(uint width, uint height)
        { var texture = inner.CreateTexture(width, height); results.Add(snapshot($"atlas-{size}-texture-created")); return texture; }
        public Int2 GetTextureSize(object texture) => inner.GetTextureSize(texture);
        public void SetTextureData(object texture, IntRect bounds, byte[] data)
        {
            bool clearing = _nextRow < size;
            bool boundary = false;
            if (clearing)
            {
                if (bounds.Min.X != 0 || bounds.Min.Y != _nextRow || bounds.Size.X != size || bounds.Size.Y <= 0 ||
                    _nextRow + bounds.Size.Y > size || data.Length != size * bounds.Size.Y * 4)
                    throw new InvalidOperationException("Atlas initialization did not cover contiguous complete rows.");
                boundary = _nextRow == 0 || _nextRow + bounds.Size.Y == size;
                FullClearBytes += data.Length; MaxClearArrayBytes = Math.Max(MaxClearArrayBytes, data.Length);
                ClearUploadCalls++; _nextRow += bounds.Size.Y;
                if (_nextRow == size) FullClears++;
                arrays.Add(new WeakReference(data));
                if (boundary) results.Add(snapshot($"atlas-{size}-clear-array-live-row-{bounds.Min.Y}"));
            }
            TotalUploadBytes += data.Length;
            inner.SetTextureData(texture, bounds, data);
            if (clearing && boundary) results.Add(snapshot($"atlas-{size}-clear-upload-queued-row-{bounds.Min.Y}"));
        }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) => inner.RenderCalls(canvas, calls);
        public void Dispose() { } // renderer is borrowed; UiCanvasAtlasOwner owns the textures.
    }
}
