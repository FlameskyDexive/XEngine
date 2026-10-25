using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Editor.UI.Rendering;

/// <summary>
/// Owns one Canvas's atlas textures while borrowing its renderer. Call RetireObsolete after
/// a stable paint/capture boundary. RHI texture disposal supplies native deferred retirement.
/// Admission charges remain until owner disposal, including retired generations; this is a
/// conservative cumulative payload cap per Canvas, not a measurement of resident VRAM.
/// </summary>
public sealed class UiCanvasAtlasOwner : ICanvasRenderer
{
    private readonly ICanvasRenderer _renderer;
    private readonly object _device = Graphics.Device ?? throw new InvalidOperationException("Initialize graphics before creating atlas owners.");
    private readonly UiMemoryBudget _budget;
    private readonly int _maximumGenerations;
    private readonly List<Entry> _entries = new();
    private bool _disposed;
    private sealed class Entry(object texture, UiMemoryLease lease)
    {
        public object? Texture = texture;
        public readonly UiMemoryLease Lease = lease;
    }

    public UiCanvasAtlasOwner(ICanvasRenderer renderer, UiMemoryBudget budget, int maximumGenerations = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGenerations, 1);
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _maximumGenerations = maximumGenerations;
    }
    public int LiveTextures { get; private set; }
    public int RetiredTextures { get; private set; }
    public long AdmittedPayloadBytes { get; private set; }
    public long LivePayloadBytes { get; private set; }
    public bool SupportsBackdropBlur => _renderer.SupportsBackdropBlur;
    public bool SupportsRenderLayers => _renderer.SupportsRenderLayers;

    public object CreateTexture(uint width, uint height)
    {
        CheckAlive();
        if (_entries.Count == _maximumGenerations) throw new InvalidOperationException("Canvas atlas generation limit exhausted.");
        ArgumentOutOfRangeException.ThrowIfZero(width); ArgumentOutOfRangeException.ThrowIfZero(height);
        long bytes = checked((long)width * height * 4);
        var lease = _budget.Reserve(bytes);
        object? texture = null;
        try
        {
            texture = _renderer.CreateTexture(width, height);
            if (texture is not IDisposable) throw new InvalidOperationException("Atlas textures must support disposal.");
            _entries.Add(new(texture, lease));
            LiveTextures++; AdmittedPayloadBytes += bytes; LivePayloadBytes += bytes;
            return texture;
        }
        catch { (texture as IDisposable)?.Dispose(); lease.Dispose(); throw; }
    }

    /// <summary>Only a stable, current Canvas snapshot may release older atlas generations.
    /// Retained caches must validate Canvas.FontAtlasRevision before submitting old geometry.</summary>
    public void RetireObsolete(Canvas canvas)
    {
        CheckAlive(); ArgumentNullException.ThrowIfNull(canvas);
        if (!canvas.IsFontAtlasStableForFrame) throw new InvalidOperationException("Rebuild an unstable atlas paint before retirement.");
        object current = canvas.FontAtlasTexture;
        if (_entries.Count == 0 || !ReferenceEquals(_entries[^1].Texture, current))
            throw new ArgumentException("Canvas does not use this owner's latest atlas.", nameof(canvas));
        for (int i = 0; i < _entries.Count - 1; i++)
        {
            var entry = _entries[i];
            if (entry.Texture is not IDisposable texture) continue;
            texture.Dispose(); entry.Texture = null;
            LiveTextures--; RetiredTextures++; LivePayloadBytes -= entry.Lease.Bytes;
        }
    }

    public Int2 GetTextureSize(object texture) { CheckAlive(); return _renderer.GetTextureSize(texture); }
    public void SetTextureData(object texture, IntRect bounds, byte[] data)
    { CheckAlive(); _renderer.SetTextureData(texture, bounds, data); }
    public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls)
    {
        CheckAlive();
        if (!canvas.IsFontAtlasStableForFrame) throw new InvalidOperationException("Cannot render an unstable atlas paint.");
        _renderer.RenderCalls(canvas, drawCalls);
    }
    private void CheckAlive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_device, Graphics.Device) || Graphics.IsDeviceLost)
            throw new InvalidOperationException("Graphics device changed or was lost; recreate the atlas owner.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries) { (entry.Texture as IDisposable)?.Dispose(); entry.Texture = null; entry.Lease.Dispose(); }
        _entries.Clear(); LiveTextures = 0; LivePayloadBytes = AdmittedPayloadBytes = 0;
        // The shared renderer is borrowed and may serve other canvases.
    }
}
