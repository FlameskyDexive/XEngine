namespace XEngine.Editor.UI;

public readonly record struct UiPaintRegionWork(UiNodeHandle Root, UiRect Bounds, UiRect Clip);
public interface IUiRegionPainter
{
    /// <summary>Capture all visible content in this region. Return true only after the complete
    /// CPU paint is committed. Paint invalidation is allowed; tree/layout mutation is not.</summary>
    bool TryPaintRegion(in UiPaintRegionWork region);
}
public readonly record struct UiPaintRegionMetrics(int VisitedNodes, int PaintedRegions, int AcknowledgedNodes, int PendingNodes);

/// <summary>
/// Bounded disjoint subtree paint regions. Multiple dirty controls in one region produce one
/// capture. Versions are acknowledged only after successful capture. Changed passes walk the
/// visible tree; idle is O(1). Registration owns metadata, not renderer/cache resources.
/// Nested regions are rejected because merging their drawing could change transparent order.
/// </summary>
public sealed class UiPaintRegionScheduler : IDisposable
{
    private readonly UiNodeStore _nodes;
    private readonly HashSet<UiNodeHandle> _regions;
    private readonly int _maximumRegions;
    private UiPaintWork[] _pending;
    private ulong _paintRevision, _drawRevision;
    private bool _retry = true, _painting, _disposed;
    public int RegionCount => _regions.Count;

    public UiPaintRegionScheduler(UiNodeStore nodes, int maximumRegions, int maximumDirtyNodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRegions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDirtyNodes, 1);
        _nodes = nodes; _maximumRegions = maximumRegions;
        _regions = new(maximumRegions); _pending = new UiPaintWork[maximumDirtyNodes];
    }

    public void Register(UiNodeHandle root)
    {
        CheckMutable();
        if (!_nodes.IsAlive(root)) throw new ArgumentException("Region node is not alive.", nameof(root));
        if (_regions.Contains(root)) return;
        if (_regions.Count == _maximumRegions) throw new InvalidOperationException("Paint region capacity exhausted.");
        foreach (var other in _regions)
            if (_nodes.IsAlive(other) && (IsAncestor(root, other) || IsAncestor(other, root)))
                throw new InvalidOperationException("Paint regions must be disjoint subtrees.");
        _regions.Add(root); _nodes.Invalidate(root, UiInvalidation.Paint); _retry = true;
    }

    public bool Unregister(UiNodeHandle root)
    { CheckMutable(); bool removed = _regions.Remove(root); _retry |= removed; return removed; }
    public bool Contains(UiNodeHandle root) => !_disposed && _nodes.IsAlive(root) && _regions.Contains(root);

    public UiPaintRegionMetrics Paint<TPainter>(ref TPainter painter) where TPainter : IUiRegionPainter
    {
        CheckMutable();
        var node = _nodes.FirstRoot;
        if (!_retry && _paintRevision == _nodes.PaintRevision && _drawRevision == _nodes.DrawRevision) return default;
        // Reparenting can create overlap after registration; reject it before capturing anything.
        foreach (var region in _regions)
            if (_nodes.IsAlive(region))
                for (var parent = _nodes.Parent(region); !parent.IsNull; parent = _nodes.Parent(parent))
                    if (_regions.Contains(parent)) throw new InvalidOperationException("Reparenting nested two paint regions.");
        _paintRevision = _nodes.PaintRevision; _drawRevision = _nodes.DrawRevision; _retry = false; _painting = true;
        int visited = 0, regions = 0, acknowledged = 0, pendingCount = 0, count = 0;
        try
        {
            while (!node.IsNull)
            {
                if (!Visible(node)) { visited++; node = Next(node, false); continue; }
                if (!_regions.Contains(node)) { visited++; node = Next(node, true); continue; }
                var root = node; var after = Next(root, false);
                count = 0;
                do
                {
                    visited++;
                    bool visible = Visible(node);
                    if (visible && _nodes.TryGetVisiblePaintWork(node, out var work))
                    {
                        if (count == _pending.Length) throw new InvalidOperationException("Dirty region snapshot capacity exhausted.");
                        _pending[count++] = work;
                    }
                    node = Next(node, visible);
                } while (node != after);
                if (count == 0) continue;
                bool success = painter.TryPaintRegion(new(root, _nodes.GetBounds(root), _nodes.GetClip(root)));
                if (_drawRevision != _nodes.DrawRevision) throw new InvalidOperationException("Region painter mutated tree/layout.");
                if (success) regions++;
                for (int i = 0; i < count; i++)
                {
                    if (success && _nodes.AcknowledgePaint(_pending[i])) acknowledged++;
                    else { pendingCount++; _retry = true; }
                }
                Array.Clear(_pending, 0, count); count = 0;
            }
        }
        catch { _retry = true; throw; }
        finally { Array.Clear(_pending, 0, count); _painting = false; }
        return new(visited, regions, acknowledged, pendingCount);
    }

    private bool Visible(UiNodeHandle node)
    { var clip = _nodes.GetClip(node); return _nodes.GetStyle(node).Visible && clip.Width > 0 && clip.Height > 0; }
    private UiNodeHandle Next(UiNodeHandle node, bool descend)
    {
        if (descend) { var child = _nodes.FirstChild(node); if (!child.IsNull) return child; }
        while (!node.IsNull)
        {
            var next = _nodes.NextSibling(node); if (!next.IsNull) return next;
            node = _nodes.Parent(node);
        }
        return default;
    }
    private bool IsAncestor(UiNodeHandle parent, UiNodeHandle child)
    {
        for (var current = _nodes.Parent(child); !current.IsNull; current = _nodes.Parent(current))
            if (current == parent) return true;
        return false;
    }
    private void CheckMutable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_painting) throw new InvalidOperationException("Paint region operations cannot reenter.");
    }
    public void Dispose()
    { if (_disposed) return; CheckMutable(); _regions.Clear(); _pending = []; _disposed = true; }
}
