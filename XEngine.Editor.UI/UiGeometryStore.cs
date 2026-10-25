namespace XEngine.Editor.UI;

/// <summary>Borrowed draw snapshot; reacquire after geometry mutation/removal. Not an ownership handle.</summary>
public readonly record struct UiGeometryRange(int Offset, int Count);

/// <summary>
/// Bounded, node-owned CPU geometry with stable page ranges. Mutations may scan the page
/// map; unchanged lookups are O(1). This is not a GPU allocator: recycled CPU offsets may
/// only be uploaded in an ordered stream after earlier draws using the old contents.
/// </summary>
public sealed class UiGeometryStore<T> : IDisposable, IUiUploadSource<T> where T : unmanaged
{
    private struct Entry
    {
        public UiNodeHandle Node;
        public int Page, Pages, Count;
    }

    private readonly UiNodeStore _nodes;
    private readonly int _pageElements;
    private Entry[] _entries;
    private bool[] _pages;
    private bool _disposed;
    private readonly UiUploadBuffer<T> _data;

    public UiGeometryStore(UiNodeStore nodes, int nodeCapacity, int pageCount, int pageElements = 64)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageElements, 1);
        int elements = checked(pageCount * pageElements);
        _nodes = nodes; _pageElements = pageElements;
        _entries = new Entry[nodeCapacity]; _pages = new bool[pageCount];
        _data = new UiUploadBuffer<T>(elements, pageElements);
    }

    public int UsedPages { get; private set; }
    public int Count { get; private set; }
    public int DirtyPageCount => _data.DirtyPageCount;
    public int Capacity => _data.Capacity;
    public int PageCount => _data.PageCount;

    public bool TryGet(UiNodeHandle node, out UiGeometryRange range)
    {
        CheckNode(node);
        ref var entry = ref _entries[node.Index];
        if (entry.Node != node) { range = default; return false; }
        range = new(entry.Page * _pageElements, entry.Count);
        return true;
    }

    /// <summary>
    /// Replaces one node's geometry without moving any other node. Returns false on arena
    /// exhaustion, preserving the previous entry. Empty geometry is a retained valid result.
    /// Does not acknowledge Paint: the owner must finish all vertex/index/brush streams first.
    /// </summary>
    public bool TrySet(UiNodeHandle node, ReadOnlySpan<T> geometry, out UiGeometryRange range)
    {
        CheckNode(node);
        ref var entry = ref _entries[node.Index];
        if (!entry.Node.IsNull && entry.Node != node) Release(ref entry);
        int required = geometry.Length == 0 ? 0 : (geometry.Length - 1) / _pageElements + 1;
        int page = entry.Page;
        if (required > entry.Pages)
        {
            page = FindPages(required);
            if (page < 0) { range = default; return false; }
        }
        // Write before changing ownership. Only the valid prefix is drawn; a shortened tail
        // remains unspecified storage and must never become part of a later draw implicitly.
        _data.Write(page * _pageElements, geometry);
        if (entry.Node.IsNull) Count++;
        if (page != entry.Page || required == 0)
        {
            Mark(entry.Page, entry.Pages, false);
            entry.Pages = 0;
        }
        if (required > entry.Pages)
        {
            Mark(page, required, true);
            entry.Pages = required;
        }
        entry.Node = node; entry.Page = page; entry.Count = geometry.Length;
        range = new(page * _pageElements, geometry.Length);
        return true;
    }

    /// <summary>Explicit panel/model lifecycle removal; accepts a removed node's old handle.</summary>
    public bool Remove(UiNodeHandle node)
    {
        CheckAlive();
        if ((uint)node.Index >= (uint)_entries.Length || node.IsNull || _entries[node.Index].Node != node)
            return false;
        Release(ref _entries[node.Index]);
        return true;
    }

    /// <summary>Maintenance operation, not a per-frame traversal. Reclaims removed subtrees.</summary>
    public int PruneRemovedNodes()
    {
        CheckAlive(); int removed = 0;
        for (int i = 0; i < _entries.Length; i++)
            if (!_entries[i].Node.IsNull && !_nodes.IsAlive(_entries[i].Node))
            { Release(ref _entries[i]); removed++; }
        return removed;
    }

    public ReadOnlySpan<T> Read(UiGeometryRange range) => _data.Read(range.Offset, range.Count);
    public ReadOnlySpan<T> Read(int offset, int count) => _data.Read(offset, count);
    public bool TryGetPending(out UiUploadRange range, int maxPages = int.MaxValue, int startOffset = 0)
        => _data.TryGetPending(out range, maxPages, startOffset);
    public ReadOnlySpan<T> ReadPending(UiUploadRange range) => _data.ReadPending(range);
    public bool Acknowledge(UiUploadRange range) => _data.Acknowledge(range);
    public void InvalidateAll() => _data.InvalidateAll();

    private int FindPages(int count)
    {
        int run = 0;
        for (int i = 0; i < _pages.Length; i++)
        {
            run = _pages[i] ? 0 : run + 1;
            if (run == count) return i - count + 1;
        }
        return -1;
    }
    private void Mark(int start, int count, bool used)
    {
        for (int i = start; i < start + count; i++)
            if (_pages[i] != used) { _pages[i] = used; UsedPages += used ? 1 : -1; }
    }
    private void Release(ref Entry entry)
    { Mark(entry.Page, entry.Pages, false); entry = default; Count--; }
    private void CheckNode(UiNodeHandle node)
    {
        CheckAlive();
        if (!_nodes.IsAlive(node)) throw new ArgumentException("Stale or foreign node.", nameof(node));
        if ((uint)node.Index >= (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(node), "Node exceeds the configured geometry capacity.");
    }
    private void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _data.Dispose(); _entries = []; _pages = []; Count = UsedPages = 0;
    }
}
