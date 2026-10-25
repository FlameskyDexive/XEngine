using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.RHI;

namespace XEngine.Editor.UI.Rendering;

/// <summary>
/// Lazily sized paint region, normally a visible row or independently invalidated panel subtree.
/// Controls inside the region share one Canvas/cache. Maximum capacities and the shared budget
/// are mandatory; no GPU allocation occurs until a nonempty capture. Growth reserves both old
/// and replacement caches until capture succeeds. This is not a native retirement budget.
/// </summary>
public sealed class UiQuillPaintIsland : IDisposable
{
    private readonly UiMemoryBudget _budget;
    private readonly int _maximumVertices, _maximumIndices, _maximumBrushPages, _maximumDraws;
    private UiQuillPaintCache? _cache;
    private object? _device;
    private bool _disposed;
    private int _vertices, _indices, _brushPages, _draws;

    public UiQuillPaintIsland(UiMemoryBudget budget, int maximumVertices, int maximumIndices,
        int maximumBrushPages, int maximumDraws)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumVertices, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumIndices, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBrushPages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDraws, 1);
        _budget = budget; _maximumVertices = maximumVertices; _maximumIndices = maximumIndices;
        _maximumBrushPages = maximumBrushPages; _maximumDraws = maximumDraws;
    }

    public bool NeedsRecapture => _cache?.NeedsRecapture ?? false;
    public int CacheCreations { get; private set; }
    public long CapacityCharge => _cache == null ? 0 : UiQuillPaintCache.EstimateCapacityCharge(_vertices, _indices, _brushPages, _draws);

    public void Capture(Canvas canvas)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(canvas);
        if (_cache?.HasPendingEncoding == true)
            throw new InvalidOperationException("Complete or cancel the island's encoding before capture or growth.");
        if (_cache != null && (!ReferenceEquals(_device, Graphics.Device) || Graphics.IsDeviceLost))
            throw new InvalidOperationException("Graphics device changed or was lost; evict and repaint the region.");
        int vertices = canvas.Vertices.Count, indices = canvas.Indices.Count, draws = canvas.DrawCalls.Count;
        int pages = checked((canvas.BrushRecordCount + Canvas.BrushRecordsPerPage - 1) / Canvas.BrushRecordsPerPage);
        if (vertices > _maximumVertices || indices > _maximumIndices || pages > _maximumBrushPages || draws > _maximumDraws)
            throw new InvalidOperationException("Paint region exceeds its configured maximum; split the region.");
        if (_cache == null && draws == 0) return;
        if (_cache != null && vertices <= _vertices && indices <= _indices && pages <= _brushPages && draws <= _draws)
        { _cache.Capture(canvas); return; }

        int newVertices = Size(Math.Max(vertices, _vertices), 64, _maximumVertices);
        int newIndices = Size(Math.Max(indices, _indices), 96, _maximumIndices);
        int newPages = Size(Math.Max(pages, _brushPages), 1, _maximumBrushPages);
        int newDraws = Size(Math.Max(draws, _draws), 4, _maximumDraws);
        var replacement = new UiQuillPaintCache(newVertices, newIndices, newPages, newDraws, _budget);
        try { replacement.Capture(canvas); }
        catch { replacement.Dispose(); throw; }
        var previous = _cache;
        _cache = replacement; _vertices = newVertices; _indices = newIndices; _brushPages = newPages; _draws = newDraws;
        _device = Graphics.Device;
        CacheCreations++;
        previous?.Dispose();
    }

    private static int Size(int required, int minimum, int maximum)
    {
        int capacity = Math.Min(minimum, maximum);
        while (capacity < required) capacity = (int)Math.Min((long)capacity * 2, maximum);
        return capacity;
    }

    public UiPaintSubmission Render(GraphicsFrameBuffer? target, int width, int height, bool visible = true,
        bool captureGpuTiming = false, PresentationSurfaceId presentationSurface = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _cache?.Render(target, width, height, visible, captureGpuTiming, presentationSurface) ?? default;
    }

    public void Clear()
    { ObjectDisposedException.ThrowIf(_disposed, this); _cache?.Clear(); }

    public UiPaintEncoding Encode(CommandBuffer command, int width, int height, bool visible = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        return _cache?.Encode(command, width, height, visible) ?? default;
    }

    public void CompleteEncoding(UiPaintEncoding encoding, bool submitted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cache == null) throw new InvalidOperationException("The island has no encoded paint cache.");
        _cache.CompleteEncoding(encoding, submitted);
    }

    /// <summary>Explicit eviction for a closed/inactive region. The owner must invalidate Paint
    /// before showing it again. Shared Canvas/font ownership remains with the panel.</summary>
    public void Evict()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cache?.Dispose(); _cache = null;
        _device = null;
        _vertices = _indices = _brushPages = _draws = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Evict(); _disposed = true;
    }
}
