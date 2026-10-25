namespace XEngine.Editor.UI;

/// <summary>Snapshot of one node's paint input. Only its owning store can acknowledge it.</summary>
public readonly record struct UiPaintWork
{
    internal UiPaintWork(UiNodeHandle node, ulong version, UiRect bounds, UiRect clip)
    { Node = node; Version = version; Bounds = bounds; Clip = clip; }
    public UiNodeHandle Node { get; }
    internal ulong Version { get; }
    public UiRect Bounds { get; }
    public UiRect Clip { get; }
}

public interface IUiNodePainter
{
    /// <summary>Return true only after all CPU paint streams are committed. May invalidate Paint,
    /// but must not mutate tree/layout during this callback. GPU upload occurs separately.</summary>
    bool TryPaint(in UiPaintWork work);
}

public sealed partial class UiNodeStore
{
    public bool TryGetPaintWork(UiNodeHandle node, out UiPaintWork work)
    {
        int index = Validate(node); work = default;
        for (int ancestor = index; ancestor >= 0; ancestor = _slots[ancestor].Parent)
            if (!_slots[ancestor].Style.Visible) return false;
        return TryGetVisiblePaintWork(node, out work);
    }

    internal bool TryGetVisiblePaintWork(UiNodeHandle node, out UiPaintWork work)
    {
        ref var slot = ref _slots[Validate(node)]; work = default;
        if ((slot.Dirty & (UiInvalidation.Measure | UiInvalidation.Arrange)) != 0)
            throw new InvalidOperationException("Complete layout before capturing paint.");
        if ((slot.Dirty & UiInvalidation.Paint) == 0 || slot.Clip.Width <= 0 || slot.Clip.Height <= 0) return false;
        work = new(node, slot.PaintVersion, slot.Bounds, slot.Clip);
        return true;
    }

    public bool AcknowledgePaint(in UiPaintWork work)
    {
        ThrowIfDisposed();
        if (!IsAlive(work.Node) || work.Version != _slots[work.Node.Index].PaintVersion ||
            (_slots[work.Node.Index].Dirty & UiInvalidation.Paint) == 0) return false;
        _slots[work.Node.Index].Dirty &= ~UiInvalidation.Paint;
        return true;
    }
}

public readonly record struct UiPaintMetrics(int VisitedNodes, int PaintedNodes, int PendingNodes);

/// <summary>
/// Single-owner paint pass after Layout. Idle is O(1); changed passes walk visible preorder.
/// Hidden subtrees keep dirty paint until revealed. Failed callbacks retry; an old successful
/// callback cannot erase an invalidation raised during capture. Does not rebuild draw order.
/// </summary>
public sealed class UiPaintScheduler(UiNodeStore nodes)
{
    private readonly UiNodeStore _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    private ulong _paintRevision, _drawRevision;
    private bool _retry = true, _painting;

    public UiPaintMetrics Paint<TPainter>(ref TPainter painter) where TPainter : IUiNodePainter
    {
        var root = _nodes.FirstRoot;
        if (_painting) throw new InvalidOperationException("Paint passes cannot reenter.");
        if (!_retry && _paintRevision == _nodes.PaintRevision && _drawRevision == _nodes.DrawRevision) return default;
        _paintRevision = _nodes.PaintRevision; _drawRevision = _nodes.DrawRevision;
        _retry = false; _painting = true;
        int visited = 0, painted = 0, pending = 0;
        try
        {
            for (; !root.IsNull; root = _nodes.NextSibling(root))
            {
                var node = root;
                while (true)
                {
                    visited++;
                    var clip = _nodes.GetClip(node);
                    if (_nodes.GetStyle(node).Visible && clip.Width > 0 && clip.Height > 0)
                    {
                        if (_nodes.TryGetVisiblePaintWork(node, out var work))
                        {
                            bool success = painter.TryPaint(in work);
                            if (_drawRevision != _nodes.DrawRevision)
                                throw new InvalidOperationException("Paint callback mutated tree/layout; finish mutations before painting.");
                            if (success && _nodes.AcknowledgePaint(in work)) painted++;
                            else { pending++; _retry = true; }
                        }
                        var child = _nodes.FirstChild(node);
                        if (!child.IsNull) { node = child; continue; }
                    }
                    while (node != root && _nodes.NextSibling(node).IsNull) node = _nodes.Parent(node);
                    if (node == root) break;
                    node = _nodes.NextSibling(node);
                }
            }
        }
        catch { _retry = true; throw; }
        finally { _painting = false; }
        return new(visited, painted, pending);
    }
}
