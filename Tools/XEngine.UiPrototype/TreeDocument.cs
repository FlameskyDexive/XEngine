namespace XEngine.UiPrototype;

/// <summary>Immutable preorder topology with mutable expansion, text and selection.
/// Range-add/min-count indexing hides descendants in O(log N), including toggles under
/// a collapsed ancestor. Visible-rank lookup is O(log N), with no full flatten per frame.</summary>
public sealed class TreeDocument : IRowSource
{
    private readonly string[] _text;
    private readonly int[] _depth, _end;
    private readonly long[] _versions;
    private readonly bool[] _expanded;
    private readonly int[] _minimum, _minimumCount, _lazy;
    private int _selected = -1;

    public TreeDocument(ReadOnlySpan<string> text, ReadOnlySpan<int> parents)
    {
        if (text.Length != parents.Length) throw new ArgumentException("Text and parent counts differ.");
        _text = text.ToArray();
        _depth = new int[text.Length]; _end = new int[text.Length];
        _versions = new long[text.Length]; _expanded = new bool[text.Length];
        int size = checked(Math.Max(1, text.Length * 4));
        _minimum = new int[size]; _minimumCount = new int[size]; _lazy = new int[size];
        var stack = new int[text.Length];
        int stackCount = 0;
        for (int i = 0; i < text.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(_text[i]);
            int parent = parents[i];
            if (parent < -1 || parent >= i) throw new ArgumentException("Parents must precede children.");
            while (stackCount > 0 && stack[stackCount - 1] != parent) _end[stack[--stackCount]] = i;
            if (parent >= 0 && stackCount == 0) throw new ArgumentException("Topology must be contiguous preorder.");
            _depth[i] = stackCount;
            _expanded[i] = true;
            stack[stackCount++] = i;
        }
        while (stackCount > 0) _end[stack[--stackCount]] = text.Length;
        if (text.Length > 0) Build(1, 0, text.Length - 1);
    }

    public int NodeCount => _text.Length;
    public int Count => NodeCount == 0 || _minimum[1] != 0 ? 0 : _minimumCount[1];
    public long Version { get; private set; }
    public int SelectedIdentity => _selected;
    public int LastUpdatedSegments { get; private set; }
    public bool IsExpanded(int identity) => _expanded[identity];
    public bool HasChildren(int identity) => _end[identity] > identity + 1;

    public void SetExpanded(int identity, bool expanded)
    {
        LastUpdatedSegments = 0;
        if (_expanded[identity] == expanded || !HasChildren(identity)) return;
        Add(1, 0, NodeCount - 1, identity + 1, _end[identity] - 1, expanded ? -1 : 1);
        _expanded[identity] = expanded;
        Version++;
    }

    public void SetText(int identity, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.Equals(_text[identity], text, StringComparison.Ordinal)) return;
        _text[identity] = text;
        _versions[identity]++;
        Version++;
    }

    public void Select(int identity)
    {
        if (identity < -1 || identity >= NodeCount) throw new ArgumentOutOfRangeException(nameof(identity));
        if (_selected == identity) return;
        _selected = identity;
        Version++;
    }

    public PrototypeRow GetRow(int visibleIndex)
    {
        if ((uint)visibleIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        int node = Find(1, 0, NodeCount - 1, visibleIndex, 0);
        return new PrototypeRow(node, _text[node], _versions[node], _selected == node,
            _depth[node], HasChildren(node), _expanded[node]);
    }

    private void Build(int node, int lo, int hi)
    {
        _minimumCount[node] = hi - lo + 1;
        if (lo == hi) return;
        int mid = lo + (hi - lo) / 2;
        Build(node * 2, lo, mid); Build(node * 2 + 1, mid + 1, hi);
    }

    private void Add(int node, int lo, int hi, int from, int to, int delta)
    {
        LastUpdatedSegments++;
        if (from <= lo && hi <= to) { _minimum[node] += delta; _lazy[node] += delta; return; }
        int mid = lo + (hi - lo) / 2;
        if (from <= mid) Add(node * 2, lo, mid, from, to, delta);
        if (to > mid) Add(node * 2 + 1, mid + 1, hi, from, to, delta);
        int left = node * 2, right = left + 1;
        int min = Math.Min(_minimum[left], _minimum[right]);
        _minimum[node] = min + _lazy[node];
        _minimumCount[node] = (_minimum[left] == min ? _minimumCount[left] : 0)
            + (_minimum[right] == min ? _minimumCount[right] : 0);
    }

    private int Find(int node, int lo, int hi, int rank, int inherited)
    {
        if (lo == hi) return lo;
        inherited += _lazy[node];
        int mid = lo + (hi - lo) / 2, left = node * 2;
        int leftCount = _minimum[left] + inherited == 0 ? _minimumCount[left] : 0;
        return rank < leftCount ? Find(left, lo, mid, rank, inherited)
            : Find(left + 1, mid + 1, hi, rank - leftCount, inherited);
    }
}
