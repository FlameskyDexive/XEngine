namespace XEngine.Editor.UI;

/// <summary>Bounded preorder topology, range-add visibility counts and O(log N) rank lookup.
/// Owns no scene references. Recreate on topology changes; queries allocate nothing.</summary>
public sealed class UiTreeVisibilityIndex
{
    private readonly int[] _end, _minimum, _count, _lazy;
    private readonly bool[] _expanded;
    public UiTreeVisibilityIndex(ReadOnlySpan<int> parents, int maximumNodes = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumNodes);
        if (parents.Length > maximumNodes) throw new ArgumentException("Tree exceeds the node limit.", nameof(parents));
        int n = parents.Length;
        _end = new int[n]; _expanded = new bool[n];
        int size = checked(Math.Max(1, n * 4));
        _minimum = new int[size]; _count = new int[size]; _lazy = new int[size];
        var stack = new int[n]; int top = 0;
        for (int i = 0; i < n; i++)
        {
            int parent = parents[i];
            if (parent < -1 || parent >= i) throw new ArgumentException("Parent must precede node.", nameof(parents));
            while (top > 0 && stack[top - 1] != parent) _end[stack[--top]] = i;
            if (parent >= 0 && top == 0) throw new ArgumentException("Tree must be contiguous preorder.", nameof(parents));
            _expanded[i] = true; stack[top++] = i;
        }
        while (top > 0) _end[stack[--top]] = n;
        if (n != 0) Build(1, 0, n - 1);
    }
    public int NodeCount => _end.Length;
    public int VisibleCount => NodeCount == 0 || _minimum[1] != 0 ? 0 : _count[1];
    public long Revision { get; private set; }
    /// <summary>Collapsed node whose subtree is shown for a fold transition, or -1.</summary>
    public int TransientVisibleNode => _transientNode;
    private int _transientNode = -1;
    public bool IsExpanded(int node) { Validate(node); return _expanded[node] && _end[node] > node + 1; }
    public void SetExpanded(int node, bool expanded)
    {
        Validate(node);
        if (_expanded[node] == expanded || _end[node] <= node + 1) return;
        // A real fold supersedes any transition preview of the same subtree.
        if (_transientNode == node) SetTransientVisible(node, false);
        Add(1, 0, NodeCount - 1, node + 1, _end[node] - 1, expanded ? -1 : 1);
        _expanded[node] = expanded; Revision++;
    }
    /// <summary>Shows one collapsed subtree without changing its expansion state or revision.
    /// Only one subtree may be transient at a time; callers restore it in the same frame.</summary>
    public void SetTransientVisible(int node, bool visible)
    {
        Validate(node);
        if (visible)
        {
            if (_transientNode == node) return;
            if (_transientNode >= 0) throw new InvalidOperationException("Another subtree is already transiently visible.");
            if (_expanded[node] || _end[node] <= node + 1) return;
            Add(1, 0, NodeCount - 1, node + 1, _end[node] - 1, -1);
            _transientNode = node;
        }
        else
        {
            if (_transientNode != node) return;
            Add(1, 0, NodeCount - 1, node + 1, _end[node] - 1, 1);
            _transientNode = -1;
        }
    }
    /// <summary>Rows shown beneath <paramref name="node"/> (its own collapsed descendants excluded).
    /// Zero for leaves and for collapsed nodes that are not transiently visible.</summary>
    public int GetVisibleDescendantCount(int node)
    {
        Validate(node);
        int end = _end[node];
        if (end <= node + 1 || (!_expanded[node] && _transientNode != node)) return 0;
        return CountVisible(1, 0, NodeCount - 1, node + 1, end - 1, 0);
    }
    public int GetNodeIndex(int rank)
    {
        if ((uint)rank >= (uint)VisibleCount) throw new ArgumentOutOfRangeException(nameof(rank));
        return Find(1, 0, NodeCount - 1, rank, 0);
    }
    /// <summary>Visible preorder rank, or -1 when hidden by an ancestor.</summary>
    public int GetVisibleRank(int nodeIndex)
    {
        Validate(nodeIndex);
        int node = 1, lo = 0, hi = NodeCount - 1, inherited = 0, rank = 0;
        while (lo != hi)
        {
            inherited += _lazy[node];
            int mid = lo + (hi - lo) / 2, left = node * 2;
            if (nodeIndex <= mid) { node = left; hi = mid; }
            else
            {
                if (_minimum[left] + inherited == 0) rank += _count[left];
                node = left + 1; lo = mid + 1;
            }
        }
        return _minimum[node] + inherited == 0 ? rank : -1;
    }
    private void Validate(int node)
    { if ((uint)node >= (uint)NodeCount) throw new ArgumentOutOfRangeException(nameof(node)); }
    private void Build(int node, int lo, int hi)
    {
        _count[node] = hi - lo + 1;
        if (lo == hi) return;
        int mid = lo + (hi - lo) / 2;
        Build(node * 2, lo, mid); Build(node * 2 + 1, mid + 1, hi);
    }
    private void Add(int node, int lo, int hi, int from, int to, int delta)
    {
        if (from <= lo && hi <= to) { _minimum[node] += delta; _lazy[node] += delta; return; }
        int mid = lo + (hi - lo) / 2;
        if (from <= mid) Add(node * 2, lo, mid, from, to, delta);
        if (to > mid) Add(node * 2 + 1, mid + 1, hi, from, to, delta);
        int left = node * 2, right = left + 1, min = Math.Min(_minimum[left], _minimum[right]);
        _minimum[node] = min + _lazy[node];
        _count[node] = (_minimum[left] == min ? _count[left] : 0) + (_minimum[right] == min ? _count[right] : 0);
    }
    private int CountVisible(int node, int lo, int hi, int from, int to, int inherited)
    {
        if (from <= lo && hi <= to) return _minimum[node] + inherited == 0 ? _count[node] : 0;
        inherited += _lazy[node];
        int mid = lo + (hi - lo) / 2, total = 0;
        if (from <= mid) total += CountVisible(node * 2, lo, mid, from, to, inherited);
        if (to > mid) total += CountVisible(node * 2 + 1, mid + 1, hi, from, to, inherited);
        return total;
    }
    private int Find(int node, int lo, int hi, int rank, int inherited)
    {
        if (lo == hi) return lo;
        inherited += _lazy[node];
        int mid = lo + (hi - lo) / 2, left = node * 2;
        int leftCount = _minimum[left] + inherited == 0 ? _count[left] : 0;
        return rank < leftCount ? Find(left, lo, mid, rank, inherited)
            : Find(left + 1, mid + 1, hi, rank - leftCount, inherited);
    }
}
