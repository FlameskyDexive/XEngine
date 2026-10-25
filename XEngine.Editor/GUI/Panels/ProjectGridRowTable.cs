using System;
using System.Collections.Generic;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// Row offsets of the Project panel's wrapped thumbnail grid, measured once from a fully built
/// flow layout and reused to band the grid (build only the rows intersecting the viewport).
/// Cards are a fixed width, so every line holds the same number of cards, but their heights vary
/// with how many lines each label wraps to; a single stride is therefore not exact for mixed
/// folders. The table keeps the real Y offset of every row plus the total height, so the spacers
/// that stand in for the unbuilt rows reproduce the full layout's extent to the pixel whatever
/// the mix of label lengths. Band frames verify the rows they built against the table and drop
/// it on any mismatch; a key that keeps mismatching is left on the full layout.
/// </summary>
internal sealed class ProjectGridRowTable
{
    /// <summary>Everything the wrapped layout depends on besides the items themselves.</summary>
    internal readonly record struct Key(long EntriesStamp, float CellSize, float Width, float Scale, bool ShowExtensions);

    /// <summary>Consecutive band frames that may disagree with the table before the key is
    /// abandoned to the full layout (a persistent disagreement means the layout has a rule the
    /// band does not reproduce; oscillating between the two paths every frame would be worse).</summary>
    internal const int MaxMismatches = 3;

    private readonly List<float> _rowTops = new();
    private Key _key;
    private Key _disabledKey;
    private bool _hasDisabledKey;
    private int _mismatches;

    public int Columns { get; private set; }
    /// <summary>Vertical gap the wrap layout puts between consecutive lines (not after the last).
    /// A row's top-to-top distance is its line height plus this gap.</summary>
    public float LineGap { get; private set; }
    public int Rows => Math.Max(0, _rowTops.Count - 1);
    public float TotalHeight => _rowTops.Count > 0 ? _rowTops[^1] : 0f;
    public int Mismatches => _mismatches;
    public int Measurements { get; private set; }
    /// <summary>Description of the most recent band/table disagreement (probe diagnostics).</summary>
    public string? LastMismatch { get; private set; }

    /// <summary>The table describes <paramref name="key"/> and can band it.</summary>
    public bool IsValidFor(in Key key) => Columns > 0 && _rowTops.Count >= 2 && _key == key;

    /// <summary>This key kept disagreeing with its band frames; keep it on the full layout.</summary>
    public bool IsDisabled(in Key key) => _hasDisabledKey && _disabledKey == key;

    public float RowTop(int row) => _rowTops[row];

    /// <summary>Record a full layout: <paramref name="rowTops"/> holds one entry per row plus the
    /// total height (rows + 1 entries, first entry 0); <paramref name="lineGap"/> is the layout's
    /// gap between consecutive lines. Clears the mismatch count for a new key.</summary>
    public void Set(in Key key, int columns, ReadOnlySpan<float> rowTops, float lineGap = 0f)
    {
        if (columns <= 0 || rowTops.Length < 2) { Reset(); return; }
        if (_key != key) _mismatches = 0;
        _key = key;
        Columns = columns;
        LineGap = MathF.Max(0f, lineGap);
        _rowTops.Clear();
        for (int i = 0; i < rowTops.Length; i++) _rowTops.Add(rowTops[i]);
        Measurements++;
    }

    /// <summary>Rows to build for a viewport of <paramref name="viewportHeight"/> scrolled to
    /// <paramref name="scrollY"/>, where the grid starts <paramref name="contentOffset"/> below
    /// the scroll content origin; padded by one row on each side and clamped.</summary>
    public (int first, int last) Band(float scrollY, float viewportHeight, float contentOffset)
        => Band(_rowTops, scrollY, viewportHeight, contentOffset);

    internal static (int first, int last) Band(IReadOnlyList<float> rowTops, float scrollY, float viewportHeight, float contentOffset)
    {
        int rows = rowTops.Count - 1;
        if (rows <= 0) return (0, -1);
        float top = scrollY - contentOffset;
        int first = Math.Max(0, RowAt(rowTops, top) - 1);
        int last = Math.Min(rows - 1, RowAt(rowTops, top + MathF.Max(0f, viewportHeight)) + 1);
        return (first, last);
    }

    /// <summary>Largest row whose top is at or above <paramref name="y"/> (clamped to the grid).</summary>
    internal static int RowAt(IReadOnlyList<float> rowTops, float y)
    {
        int rows = rowTops.Count - 1;
        if (rows <= 0 || y <= rowTops[0]) return 0;
        if (y >= rowTops[rows]) return rows - 1;
        int lo = 0, hi = rows - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (rowTops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Height the wrap row has when it holds exactly rows [first, last]: the table
    /// distance minus the trailing line gap, which only exists when more rows follow.</summary>
    public float BandHeight(int first, int last)
        => last < first ? 0f : _rowTops[last + 1] - _rowTops[first] - (last + 1 < Rows ? LineGap : 0f);

    /// <summary>Spacer heights that stand in for the rows outside [first, last]; together with
    /// <see cref="BandHeight"/> they reproduce <see cref="TotalHeight"/> exactly.</summary>
    public (float top, float bottom) Spacers(int first, int last)
    {
        if (last < first) return (0f, TotalHeight);
        return (_rowTops[first], TotalHeight - _rowTops[first] - BandHeight(first, last));
    }

    /// <summary>Check the rows a band frame built: their tops relative to the band's first row and
    /// the band's own height must equal the table's. Returns false (and counts a mismatch) when
    /// the layout disagreed; after <see cref="MaxMismatches"/> in a row the key is disabled.</summary>
    public bool Verify(in Key key, int columns, int first, ReadOnlySpan<float> builtRowTops, float builtHeight, float tolerance = 0.5f)
    {
        bool ok = IsValidFor(key) && columns == Columns && first >= 0 && first + builtRowTops.Length <= Rows;
        string? why = ok ? null : $"key/columns/range: valid={IsValidFor(key)} columns={columns}/{Columns} first={first} built={builtRowTops.Length} rows={Rows}";
        if (ok)
        {
            float origin = _rowTops[first];
            for (int i = 0; i < builtRowTops.Length && ok; i++)
            {
                ok = MathF.Abs(builtRowTops[i] - (_rowTops[first + i] - origin)) <= tolerance;
                if (!ok) why = $"row {first + i}: built top {builtRowTops[i]} vs table {_rowTops[first + i] - origin}";
            }
            if (ok)
            {
                float expected = BandHeight(first, first + builtRowTops.Length - 1);
                ok = MathF.Abs(builtHeight - expected) <= tolerance;
                if (!ok) why = $"band height {builtHeight} vs table {expected} (first={first}, built={builtRowTops.Length}, gap={LineGap})";
            }
        }
        if (ok) { _mismatches = 0; return true; }
        LastMismatch = why; // diagnostic only; allocates on the failure path alone
        if (++_mismatches >= MaxMismatches)
        {
            _disabledKey = key; _hasDisabledKey = true;
        }
        Columns = 0; _rowTops.Clear();
        return false;
    }

    public void Reset()
    {
        Columns = 0; LineGap = 0f; _rowTops.Clear(); _mismatches = 0;
        _hasDisabledKey = false;
    }
}
