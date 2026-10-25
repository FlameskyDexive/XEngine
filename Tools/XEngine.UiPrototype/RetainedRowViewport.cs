namespace XEngine.UiPrototype;

public readonly record struct RowVisual(int SourceIndex, string Text, float Width, bool Selected,
    int Identity = 0, int Depth = 0, bool HasChildren = false, bool Expanded = false);
public readonly record struct RowPrepareResult(int InspectedRows, int RebuiltRows, bool ViewChanged);

/// <summary>N1 retained-row prototype: row-local visuals survive scrolling, with a bounded ring
/// and an O(1) unchanged-frame exit. Drawing, text shaping and GPU uploads are measured separately.</summary>
public sealed class RetainedRowViewport : IDisposable
{
    private struct Slot
    {
        public bool Valid;
        public long Version;
        public RowVisual Visual;
    }

    private IRowSource? _document;
    private readonly Slot[] _slots;
    private long _observedVersion = -1;
    private bool _prepared;
    private float _width, _height, _rowHeight;

    public RetainedRowViewport(IRowSource document, int maxVisibleRows)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (maxVisibleRows <= 0) throw new ArgumentOutOfRangeException(nameof(maxVisibleRows));
        _document = document;
        _slots = new Slot[maxVisibleRows];
    }

    public int FirstRow { get; private set; }
    public int VisibleCount { get; private set; }
    public double ScrollOffset { get; private set; }
    public int Capacity => _slots.Length;

    public RowPrepareResult Prepare(double scrollOffset, float width, float height, float rowHeight)
    {
        var document = _document ?? throw new ObjectDisposedException(nameof(RetainedRowViewport));
        if (!double.IsFinite(scrollOffset)) throw new ArgumentOutOfRangeException(nameof(scrollOffset));
        if (!float.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(height) || height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!float.IsFinite(rowHeight) || rowHeight <= 0) throw new ArgumentOutOfRangeException(nameof(rowHeight));

        double offset = Math.Clamp(scrollOffset, 0, Math.Max(0, (double)document.Count * rowHeight - height));
        int first = document.Count == 0 ? 0 : (int)Math.Min(document.Count - 1, Math.Floor(offset / rowHeight));
        double fraction = offset - (double)first * rowHeight;
        int count = width == 0 || height == 0 ? 0 : (int)Math.Min(document.Count - first, Math.Ceiling((height + fraction) / rowHeight));
        if (count > _slots.Length) throw new InvalidOperationException("Visible rows exceed the explicit cache budget.");

        bool viewChanged = !_prepared || offset != ScrollOffset || width != _width || height != _height || rowHeight != _rowHeight;
        if (!viewChanged && _observedVersion == document.Version) return default;

        int rebuilt = 0;
        for (int i = first; i < first + count; i++)
        {
            ref var slot = ref _slots[i % _slots.Length];
            PrototypeRow row = document.GetRow(i);
            bool selected = row.Selected;
            long version = row.Version;
            if (slot.Valid && slot.Visual.SourceIndex == i && slot.Version == version
                && slot.Visual.Width == width && slot.Visual.Selected == selected
                && slot.Visual.Identity == row.Identity && slot.Visual.Depth == row.Depth
                && slot.Visual.HasChildren == row.HasChildren && slot.Visual.Expanded == row.Expanded) continue;
            slot.Valid = true;
            slot.Version = version;
            slot.Visual = new RowVisual(i, row.Text, width, selected, row.Identity, row.Depth, row.HasChildren, row.Expanded);
            rebuilt++;
        }
        FirstRow = first;
        VisibleCount = count;
        ScrollOffset = offset;
        _width = width; _height = height; _rowHeight = rowHeight;
        _observedVersion = document.Version;
        _prepared = true;
        return new RowPrepareResult(count, rebuilt, viewChanged);
    }

    public RowVisual GetVisibleRow(int visibleIndex)
    {
        ObjectDisposedException.ThrowIf(_document == null, this);
        if ((uint)visibleIndex >= (uint)VisibleCount) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        return _slots[(FirstRow + visibleIndex) % _slots.Length].Visual;
    }

    public double GetRowTop(int visibleIndex)
    {
        if ((uint)visibleIndex >= (uint)VisibleCount) throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        return (double)(FirstRow + visibleIndex) * _rowHeight - ScrollOffset;
    }

    public bool TryHitTest(double localY, out int row)
    {
        var document = _document ?? throw new ObjectDisposedException(nameof(RetainedRowViewport));
        row = -1;
        if (!_prepared || _width == 0 || !double.IsFinite(localY) || localY < 0 || localY >= _height) return false;
        double index = Math.Floor((ScrollOffset + localY) / _rowHeight);
        if (index < 0 || index >= document.Count) return false;
        row = (int)index;
        return true;
    }

    public void Dispose()
    {
        Array.Clear(_slots);
        _document = null;
        VisibleCount = 0;
        _prepared = false;
    }
}
