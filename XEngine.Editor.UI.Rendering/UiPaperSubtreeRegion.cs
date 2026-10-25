using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime.GUI;

namespace XEngine.Editor.UI.Rendering;

/// <summary>One panel-owned atlas/cache for a complete Paper subtree. Caller must explicitly
/// invalidate every visual dependency (including hover and animation); layout/input remain live.
/// Paint callbacks must be side-effect-free because atlas growth may replay them.</summary>
public sealed class UiPaperSubtreeRegion : IPaperSubtreePainter, IDisposable
{
    private readonly Paper _paper;
    private readonly UiPaperPaintRegion _region;
    private ElementHandle _root;
    private int? _rootId;
    private readonly PaperSubtreeVisualState? _visualState;
    public long CaptureCount => _region.CaptureCount;
    public UiRenderStats LastStats => _region.LastStats;
    public bool IsDisposed => _region.IsDisposed;

    public UiPaperSubtreeRegion(Paper paper, UiMemoryBudget geometryBudget, UiMemoryBudget atlasBudget, int initialAtlasSize = 512, bool shareHostFonts = false,
        bool compareVisualState = false)
    {
        _paper = paper;
        if (compareVisualState) _visualState = new PaperSubtreeVisualState();
        _region = new UiPaperPaintRegion(paper, geometryBudget, atlasBudget,
            (canvas, _) => _paper.PaintSubtreeTo(_root, canvas), initialAtlasSize: initialAtlasSize, shareHostFonts: shareHostFonts);
    }

    public void Attach(ElementHandle root)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!root.IsValid || !ReferenceEquals(root.Owner, _paper))
            throw new ArgumentException("Subtree belongs to another Paper or is invalid.", nameof(root));
        if (_rootId != root.Data.ID) { _region.Invalidate(); _rootId = root.Data.ID; }
        root.Data.SubtreePainter = this;
    }

    public void Invalidate() => _region.Invalidate();

    public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
    {
        if (IsDisposed) return true;
        if (!ReferenceEquals(paper, _paper)) throw new InvalidOperationException("Subtree region changed host.");
        if (_visualState != null)
        {
            // A subtree past the snapshot's element limit cannot be compared; treat it as volatile
            // (recapture every frame) rather than letting the limit abort the host's render pass.
            bool changed;
            try { changed = _visualState.HasChanged(paper, root); }
            catch (InvalidOperationException) { changed = true; }
            if (changed) _region.Invalidate();
        }
        _root = root;
        try { _region.Draw(host, root.Data.LayoutRect); }
        finally { _root = default; }
        return true;
    }

    public void Dispose()
    {
        _region.Dispose();
        _visualState?.Dispose();
        _root = default;
    }
}
