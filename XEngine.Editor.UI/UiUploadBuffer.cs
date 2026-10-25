using System.Runtime.InteropServices;

namespace XEngine.Editor.UI;

/// <summary>Opaque pending upload snapshot. Offsets/counts are elements, not bytes.</summary>
public readonly record struct UiUploadRange
{
    internal UiUploadRange(object owner, ulong revision, int offset, int count)
    { Owner = owner; Revision = revision; Offset = offset; Count = count; }
    internal object? Owner { get; }
    internal ulong Revision { get; }
    public int Offset { get; }
    public int Count { get; }
}

/// <summary>
/// Fixed-capacity CPU mirror with bounded page dirty tracking. Single owning UI thread.
/// Upload acknowledgment means successful command submission, not GPU completion or safe
/// resource retirement. A renderer must supply those separate lifetime guarantees.
/// </summary>
public sealed class UiUploadBuffer<T> : IDisposable, IUiUploadSource<T> where T : unmanaged
{
    private readonly object _identity = new();
    private T[] _data;
    private bool[] _dirty;
    private readonly int _pageElements;
    private ulong _revision = 1;
    private int _firstDirty;
    private bool _disposed;

    public UiUploadBuffer(int capacity, int pageElements = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageElements, 1);
        _data = new T[capacity];
        _pageElements = pageElements;
        _dirty = new bool[(capacity - 1) / pageElements + 1];
        // New GPU storage may be undefined; the initial mirror must be uploaded even if zero.
        Array.Fill(_dirty, true);
        DirtyPageCount = _dirty.Length;
    }

    public int Capacity => _data.Length;
    public int PageCount => _dirty.Length;
    public int DirtyPageCount { get; private set; }

    public bool Write(int offset, ReadOnlySpan<T> values)
    {
        ValidateBounds(offset, values.Length);
        var destination = _data.AsSpan(offset, values.Length);
        if (MemoryMarshal.AsBytes(values).SequenceEqual(MemoryMarshal.AsBytes(destination))) return false;
        AdvanceRevision();
        int last = (offset + values.Length - 1) / _pageElements;
        for (int page = offset / _pageElements; page <= last; page++)
        {
            int begin = Math.Max(offset, page * _pageElements);
            int end = (int)Math.Min((long)offset + values.Length, ((long)page + 1) * _pageElements);
            var sourcePage = values.Slice(begin - offset, end - begin);
            var targetPage = _data.AsSpan(begin, end - begin);
            if (MemoryMarshal.AsBytes(sourcePage).SequenceEqual(MemoryMarshal.AsBytes(targetPage))) continue;
            if (!_dirty[page]) { _dirty[page] = true; DirtyPageCount++; }
            _firstDirty = Math.Min(_firstDirty, page);
        }
        // Defer the copy until every page is compared: values may overlap this mirror.
        values.CopyTo(destination);
        return true;
    }

    public ReadOnlySpan<T> Read(int offset, int count)
    {
        ValidateBounds(offset, count);
        return _data.AsSpan(offset, count);
    }

    /// <summary>Coalesces adjacent dirty pages, capped by maxPages. Unchanged neighboring
    /// elements in a dirty page are deliberately uploaded too. No retained range queue.</summary>
    /// <param name="startOffset">Page-aligned cursor, or Capacity to end enumeration.
    /// Advance to range.Offset + range.Count to encode several ranges in one submission,
    /// then acknowledge their snapshots only after submission succeeds.</param>
    public bool TryGetPending(out UiUploadRange range, int maxPages = int.MaxValue, int startOffset = 0)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        ValidateBounds(startOffset, 0);
        if (startOffset != Capacity && startOffset % _pageElements != 0)
            throw new ArgumentException("Upload cursor must start on a page boundary.", nameof(startOffset));
        range = default;
        if (DirtyPageCount == 0 || startOffset == Capacity) return false;
        int first = Math.Max(_firstDirty, startOffset / _pageElements);
        while (first < _dirty.Length && !_dirty[first]) first++;
        if (first == _dirty.Length) return false;
        int last = first;
        while (last + 1 < _dirty.Length && last - first + 1 < maxPages && _dirty[last + 1]) last++;
        int offset = first * _pageElements;
        int end = (int)Math.Min((long)_data.Length, ((long)last + 1) * _pageElements);
        range = new(_identity, _revision, offset, end - offset);
        return true;
    }

    /// <summary>Read the payload only while this snapshot is current. The RHI command
    /// encoder copies it; do not retain this span for deferred native reads.</summary>
    public ReadOnlySpan<T> ReadPending(UiUploadRange range)
    {
        ValidateRange(range);
        if (range.Revision != _revision) throw new InvalidOperationException("Upload snapshot was superseded by a CPU write.");
        return _data.AsSpan(range.Offset, range.Count);
    }

    /// <summary>Call only after successful submission of the copied payload. Any intervening
    /// write makes this conservative acknowledgment a no-op, preserving new dirty data.</summary>
    public bool Acknowledge(UiUploadRange range)
    {
        ValidateRange(range);
        if (range.Revision != _revision) return false;
        int last = (range.Offset + range.Count - 1) / _pageElements;
        for (int page = range.Offset / _pageElements; page <= last; page++)
            if (_dirty[page]) { _dirty[page] = false; DirtyPageCount--; }
        if (DirtyPageCount == 0) _firstDirty = _dirty.Length;
        else while (!_dirty[_firstDirty]) _firstDirty++;
        return true;
    }

    /// <summary>Recreated/lost GPU storage requires replaying every CPU page.</summary>
    public void InvalidateAll()
    {
        ThrowIfDisposed(); AdvanceRevision();
        Array.Fill(_dirty, true); DirtyPageCount = _dirty.Length; _firstDirty = 0;
    }

    private void AdvanceRevision()
    {
        if (_revision == ulong.MaxValue) throw new InvalidOperationException("Upload revision exhausted; recreate the mirror.");
        _revision++;
    }
    private void ValidateRange(UiUploadRange range)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(range.Owner, _identity) || range.Count <= 0)
            throw new ArgumentException("Null or foreign upload range.", nameof(range));
        ValidateBounds(range.Offset, range.Count);
    }
    private void ValidateBounds(int offset, int count)
    {
        ThrowIfDisposed();
        if (offset < 0 || count < 0 || offset > _data.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _data = []; _dirty = []; DirtyPageCount = 0; _firstDirty = 0;
    }
}
