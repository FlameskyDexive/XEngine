using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.Editor.UI.Rendering;

/// <summary>Panel-owned retained paint at an actual Paper Draw/DrawForeground boundary.
/// Painting must be side-effect-free: font atlas growth can require a second paint.
/// The caller explicitly invalidates model/theme changes and owns input/layout separately.</summary>
public sealed class UiPaperPaintRegion : IDisposable
{
    private readonly Paper _paper;
    private Action<Canvas, Rect>? _paint;
    private readonly Action<Canvas, Rect> _callback;
    private readonly UiMemoryBudget _atlasBudget;
    private readonly UiQuillPaintIsland _island;
    private UiCanvasAtlasOwner? _atlasOwner;
    private Canvas? _canvas;
    private Marker? _marker;
    private CanvasLayerContext _context;
    private Rect _rect;
    private int _width, _height;
    private ulong _revision = 1, _capturedRevision;
    private bool _disposed, _painting, _encoding;
    private readonly bool _shareHostFonts;

    public UiPaperPaintRegion(Paper paper, UiMemoryBudget geometryBudget, UiMemoryBudget atlasBudget,
        Action<Canvas, Rect> paint, int maximumVertices = 65536, int maximumIndices = 131072,
        int maximumBrushPages = 16, int maximumDraws = 256, int initialAtlasSize = 512, bool shareHostFonts = false)
    {
        _paper = paper ?? throw new ArgumentNullException(nameof(paper));
        if (shareHostFonts && !paper.StabilizeFontAtlasBeforeRender)
            throw new InvalidOperationException("Shared fonts require a replayable Paper paint transaction.");
        _shareHostFonts = shareHostFonts;
        ArgumentOutOfRangeException.ThrowIfLessThan(initialAtlasSize, 1);
        _initialAtlasSize = initialAtlasSize;
        _paint = paint ?? throw new ArgumentNullException(nameof(paint));
        _atlasBudget = atlasBudget ?? throw new ArgumentNullException(nameof(atlasBudget));
        if (!paper.Renderer.SupportsRenderLayers) throw new NotSupportedException("Paper renderer does not support retained regions.");
        _island = new(geometryBudget, maximumVertices, maximumIndices, maximumBrushPages, maximumDraws);
        _callback = Draw;
    }

    public long CaptureCount { get; private set; }
    private readonly int _initialAtlasSize;
    public long CapacityCharge => _island.CapacityCharge;
    public UiRenderStats LastStats { get; private set; }
    public bool IsDisposed => _disposed;

    public void Invalidate()
    { ObjectDisposedException.ThrowIf(_disposed, this); _revision = checked(_revision + 1); }

    /// <summary>Register once per layout frame. The resolved context is read later, at draw time.</summary>
    public void Attach(ref ElementHandle element, bool foreground = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!element.IsValid || !ReferenceEquals(element.Owner, _paper))
            throw new ArgumentException("Element belongs to another Paper host or is invalid.", nameof(element));
        if (foreground) _paper.DrawForeground(ref element, _callback);
        else _paper.Draw(ref element, _callback);
    }

    internal void Draw(Canvas host, Rect rect)
    {
        // A panel may close during Paper input dispatch after registering this callback.
        if (_disposed || rect.Size.X <= 0 || rect.Size.Y <= 0) return;
        if (!ReferenceEquals(host, _paper.Canvas)) throw new InvalidOperationException("Retained region moved to another Paper host; recreate it.");
        if (_painting || _encoding) throw new InvalidOperationException("Retained region cannot reenter painting or encoding.");
        if (_shareHostFonts && !_paper.StabilizeFontAtlasBeforeRender)
            throw new InvalidOperationException("Shared font paint transaction was disabled.");
        _canvas?.SynchronizeFontFallbacks(host);
        var context = host.GetLayerContext();
        int width = checked((int)(host.Width * host.FramebufferScale));
        int height = checked((int)(host.Height * host.FramebufferScale));
        if (_marker == null || _capturedRevision != _revision || !_context.Equals(context)
            || !_rect.Equals(rect) || _width != width || _height != height || _island.NeedsRecapture)
        {
            ulong revision = _revision;
            // Failed recapture must never leave an old marker validating against
            // partially replaced geometry if the parent later returns to its old context.
            _marker = null;
            _painting = true;
            try
            {
                if (_canvas == null)
                {
                    _atlasOwner = new UiCanvasAtlasOwner(_paper.Renderer, _atlasBudget);
                    try { _canvas = _shareHostFonts ? Canvas.CreateWithSharedFonts(_atlasOwner, host)
                        : new Canvas(_atlasOwner, new FontAtlasSettings { AtlasSize = _initialAtlasSize }); }
                    catch { _atlasOwner.Dispose(); _atlasOwner = null; throw; }
                    _canvas.SynchronizeFontFallbacks(host);
                }
                bool stable = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    _canvas.BeginFrame(host.Width, host.Height, host.FramebufferScale);
                    _canvas.ApplyLayerContext(context);
                    _paint!(_canvas, rect);
                    if (_canvas.IsFontAtlasStableForFrame) { stable = true; break; }
                }
                if (!stable) throw new InvalidOperationException("Retained region font atlas did not stabilize.");
                _island.Capture(_canvas);
                if (!_shareHostFonts) _atlasOwner!.RetireObsolete(_canvas);
                _context = context; _rect = rect; _width = width; _height = height;
                _capturedRevision = revision;
                _marker = new Marker(this, new UiQuillCanvasLayer(_island, width, height, context));
                CaptureCount++;
            }
            finally { _painting = false; }
        }
        host.AppendRenderLayer(_marker);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_painting || _encoding) throw new InvalidOperationException("Cannot close a region during painting or encoding.");
        // Existing callbacks and markers become tombstones; they no longer touch disposed GPU data.
        _disposed = true; _paint = null;
        try { _island.Dispose(); }
        finally
        {
            try { _canvas?.Dispose(); }
            finally { _canvas = null; _atlasOwner = null; _marker = null; }
        }
    }

    private sealed class Marker(UiPaperPaintRegion owner, UiQuillCanvasLayer layer) : IUiCommandLayer
    {
        private bool _encoded;
        public void Validate(in UiCommandLayerContext context)
        {
            if (owner._disposed) return;
            if (!ReferenceEquals(owner._marker, this)) throw new InvalidOperationException("Stale retained region draw list; rebuild the Paper host.");
            layer.Validate(context);
        }
        public UiRenderStats Encode(CommandBuffer command, in UiCommandLayerContext context)
        {
            Validate(context);
            if (owner._disposed) return default;
            var result = layer.Encode(command, context);
            _encoded = true; owner._encoding = true;
            return owner.LastStats = result;
        }
        public void Complete(bool submitted)
        {
            if (!_encoded) return;
            try { layer.Complete(submitted); }
            finally { _encoded = false; owner._encoding = false; }
        }
    }
}
