namespace XEngine.Editor.UI;

public readonly record struct UiSize(float Width, float Height);
public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public bool Contains(float x, float y) => Width > 0 && Height > 0
        && x >= X && y >= Y && x < X + Width && y < Y + Height;
    public UiRect Intersect(UiRect other)
    {
        float x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(X + Width, other.X + other.Width) - x),
            Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - y));
    }
}

public enum UiFlow : byte { Column, Row, Overlay }
public enum UiSizing : byte { Content, Fixed, Stretch }
public enum UiPosition : byte { Flow, Absolute }
public readonly record struct UiLength(UiSizing Sizing, float Value = 0)
{
    public static UiLength Fixed(float value) => new(UiSizing.Fixed, value);
    public static UiLength Stretch(float weight = 1) => new(UiSizing.Stretch, weight);
}

/// <summary>Logical coordinates. Intrinsic text measurement is supplied by the text layer.</summary>
public readonly record struct UiStyle
{
    public UiStyle() { }
    public static UiStyle Default => new();
    public UiFlow Flow { get; init; }
    public UiPosition Position { get; init; }
    public float Left { get; init; }
    public float Top { get; init; }
    public UiLength Width { get; init; }
    public UiLength Height { get; init; }
    public float MinWidth { get; init; }
    public float MinHeight { get; init; }
    public float MaxWidth { get; init; } = float.MaxValue;
    public float MaxHeight { get; init; } = float.MaxValue;
    public float Padding { get; init; }
    public float Gap { get; init; }
    public bool Visible { get; init; } = true;
    public bool HitTestVisible { get; init; } = true;
    public bool Focusable { get; init; }
    public bool Enabled { get; init; } = true;
}

public readonly record struct UiLayoutMetrics(int Measured, int Arranged);

public sealed partial class UiNodeStore
{
    /// <summary>Accepted logical input range; large model lists use virtualization/scroll offsets.
    /// Bounded input prevents finite floats from overflowing accumulated tree geometry.</summary>
    public const float MaximumLogicalInput = 1_000_000_000f;
    public UiStyle GetStyle(UiNodeHandle node) => _slots[Validate(node)].Style;
    public UiRect GetBounds(UiNodeHandle node) => _slots[Validate(node)].Bounds;
    public UiRect GetClip(UiNodeHandle node) => _slots[Validate(node)].Clip;
    public UiSize GetDesiredSize(UiNodeHandle node) => _slots[Validate(node)].Desired;

    public void SetStyle(UiNodeHandle node, UiStyle style)
    {
        int index = Validate(node);
        ValidateStyle(style);
        if (_slots[index].Style == style) return;
        _slots[index].Style = style;
        InvalidateIndex(index, UiInvalidation.Measure);
        if (!style.Visible || !style.Enabled) ClearInputInSubtree(index);
        else if (!style.Focusable && Focused == node) SetFocus(default);
    }

    public void SetContentSize(UiNodeHandle node, UiSize size)
    {
        int index = Validate(node);
        if (!ValidNonnegative(size.Width) || !ValidNonnegative(size.Height))
            throw new ArgumentOutOfRangeException(nameof(size));
        if (_slots[index].Content == size) return;
        _slots[index].Content = size;
        InvalidateIndex(index, UiInvalidation.Measure);
    }

    public void SetRootBounds(UiNodeHandle node, UiRect bounds)
    {
        int index = Validate(node);
        if (_slots[index].Parent >= 0) throw new ArgumentException("Only roots have host bounds.", nameof(node));
        if (!ValidCoordinate(bounds.X) || !ValidCoordinate(bounds.Y) || !ValidNonnegative(bounds.Width)
            || !ValidNonnegative(bounds.Height))
            throw new ArgumentOutOfRangeException(nameof(bounds));
        if (_slots[index].RootBounds == bounds) return;
        _slots[index].RootBounds = bounds;
        InvalidateIndex(index, UiInvalidation.Arrange);
    }

    public void SetScroll(UiNodeHandle node, float x, float y)
    {
        int index = Validate(node);
        if (!ValidNonnegative(x) || !ValidNonnegative(y))
            throw new ArgumentOutOfRangeException(nameof(x));
        if (_slots[index].ScrollX == x && _slots[index].ScrollY == y) return;
        _slots[index].ScrollX = x; _slots[index].ScrollY = y;
        InvalidateIndex(index, UiInvalidation.Arrange);
    }

    /// <summary>Forced reference mode bypasses all cached traversal and recomputes every node.
    /// Both paths share sizing equations; tests also verify independent expected rectangles.</summary>
    public UiLayoutMetrics Layout(bool forceFull = false)
    {
        ThrowIfDisposed();
        int measured = 0, arranged = 0;
        for (int root = _firstRoot; root >= 0; root = _slots[root].Next)
        {
            // Iterative postorder. Clean subtrees are pruned before entering them.
            int current = root;
            while (true)
            {
                if (NeedsMeasureVisit(current, forceFull))
                {
                    int child = FindMeasureChild(_slots[current].First, forceFull);
                    if (child >= 0) { current = child; continue; }
                    FinishMeasure(current, forceFull, ref measured);
                }
                while (true)
                {
                    if (current == root) break;
                    int next = FindMeasureChild(_slots[current].Next, forceFull);
                    if (next >= 0) { current = next; break; }
                    current = _slots[current].Parent;
                    FinishMeasure(current, forceFull, ref measured);
                }
                if (current == root) break;
            }
            SetGeometry(root, _slots[root].RootBounds, _slots[root].RootBounds, forceFull);
            // Preorder with clean branches skipped; all child rectangles determined by parent.
            current = root;
            while (true)
            {
                bool dirty = forceFull || (_slots[current].Dirty & UiInvalidation.Arrange) != 0;
                bool descend = dirty || _slots[current].ArrangeBranch;
                if (dirty)
                {
                    ArrangeChildren(current, forceFull); arranged++;
                    _slots[current].Dirty &= ~(UiInvalidation.Arrange | UiInvalidation.Structure | UiInvalidation.HitTest);
                }
                _slots[current].ArrangeBranch = false;
                if (descend && _slots[current].First >= 0) { current = _slots[current].First; continue; }
                while (current != root && _slots[current].Next < 0) current = _slots[current].Parent;
                if (current == root) break;
                current = _slots[current].Next;
            }
        }
        return new(measured, arranged);
    }

    private int FindMeasureChild(int index, bool force)
    {
        while (index >= 0 && !NeedsMeasureVisit(index, force)) index = _slots[index].Next;
        return index;
    }

    private bool NeedsMeasureVisit(int index, bool force) => force || _slots[index].MeasureBranch
        || (_slots[index].Dirty & UiInvalidation.Measure) != 0;

    private void FinishMeasure(int index, bool force, ref int measured)
    {
        if (force || (_slots[index].Dirty & UiInvalidation.Measure) != 0) { Measure(index); measured++; }
        _slots[index].MeasureBranch = false;
    }

    private void Measure(int index)
    {
        ref var node = ref _slots[index];
        var style = node.Style;
        float width = 0, height = 0;
        int count = 0;
        for (int child = node.First; child >= 0; child = _slots[child].Next)
        {
            if (!_slots[child].Style.Visible || _slots[child].Style.Position == UiPosition.Absolute) continue;
            var desired = _slots[child].Desired;
            width = style.Flow == UiFlow.Row ? width + desired.Width : Math.Max(width, desired.Width);
            height = style.Flow == UiFlow.Column ? height + desired.Height : Math.Max(height, desired.Height);
            count++;
        }
        if (count > 1 && style.Flow == UiFlow.Row) width += (count - 1) * style.Gap;
        if (count > 1 && style.Flow == UiFlow.Column) height += (count - 1) * style.Gap;
        width = Math.Max(width, node.Content.Width) + 2 * style.Padding;
        height = Math.Max(height, node.Content.Height) + 2 * style.Padding;
        node.Desired = style.Visible ? new(ResolveDesired(style.Width, width, style.MinWidth, style.MaxWidth),
            ResolveDesired(style.Height, height, style.MinHeight, style.MaxHeight)) : default;
        node.Dirty &= ~UiInvalidation.Measure;
    }

    private static float ResolveDesired(UiLength length, float intrinsic, float min, float max)
        => Math.Clamp(length.Sizing == UiSizing.Fixed ? length.Value : intrinsic, min, max);

    private void ArrangeChildren(int index, bool force)
    {
        var node = _slots[index];
        var style = node.Style;
        bool row = style.Flow == UiFlow.Row, overlay = style.Flow == UiFlow.Overlay;
        float width = Math.Max(0, node.Bounds.Width - 2 * style.Padding);
        float height = Math.Max(0, node.Bounds.Height - 2 * style.Padding);
        double available = row ? width : height, occupied = 0;
        int count = 0;
        for (int child = node.First; child >= 0; child = _slots[child].Next)
        {
            var item = _slots[child];
            if (!item.Style.Visible || item.Style.Position == UiPosition.Absolute) continue;
            var length = row ? item.Style.Width : item.Style.Height;
            if (length.Sizing != UiSizing.Stretch) occupied += row ? item.Desired.Width : item.Desired.Height;
            count++;
        }
        double remaining = Math.Max(0, available - occupied - (double)Math.Max(0, count - 1) * style.Gap);
        if (!overlay) DistributeStretch(node.First, row, remaining);
        float cursor = 0;
        for (int child = node.First; child >= 0; child = _slots[child].Next)
        {
            var item = _slots[child];
            if (!item.Style.Visible)
            {
                SetGeometry(child, default, default, force);
                continue;
            }
            float w = item.Desired.Width, h = item.Desired.Height;
            bool absolute = item.Style.Position == UiPosition.Absolute;
            if (item.Style.Width.Sizing == UiSizing.Stretch)
                w = Math.Clamp(row && !overlay && !absolute ? (float)item.StretchSize : Math.Max(0, width - (absolute ? item.Style.Left : 0)), item.Style.MinWidth, item.Style.MaxWidth);
            if (item.Style.Height.Sizing == UiSizing.Stretch)
                h = Math.Clamp(!row && !overlay && !absolute ? (float)item.StretchSize : Math.Max(0, height - (absolute ? item.Style.Top : 0)), item.Style.MinHeight, item.Style.MaxHeight);
            var bounds = new UiRect(node.Bounds.X + style.Padding - node.ScrollX + (absolute ? item.Style.Left : row && !overlay ? cursor : 0),
                node.Bounds.Y + style.Padding - node.ScrollY + (absolute ? item.Style.Top : !row && !overlay ? cursor : 0), w, h);
            var contentClip = new UiRect(node.Bounds.X + style.Padding, node.Bounds.Y + style.Padding, width, height).Intersect(node.Clip);
            SetGeometry(child, bounds, bounds.Intersect(contentClip), force);
            if (!absolute) cursor += (row ? w : h) + style.Gap;
        }
    }

    // Constrained weighted distribution. Freeze only bounds that contribute to the total
    // violation; freezing both min and max violations together gives incorrect mixed cases.
    // Scratch lives in slots, so no per-layout allocations. At least one item freezes each
    // nonterminal pass; worst case is quadratic in stretch siblings (measure in N2 benchmarks).
    private void DistributeStretch(int first, bool row, double remaining)
    {
        double weights = 0;
        for (int child = first; child >= 0; child = _slots[child].Next)
        {
            ref var item = ref _slots[child];
            var length = row ? item.Style.Width : item.Style.Height;
            item.StretchFrozen = !item.Style.Visible || item.Style.Position == UiPosition.Absolute || length.Sizing != UiSizing.Stretch;
            if (!item.StretchFrozen) weights += length.Value;
        }
        while (weights > 0)
        {
            double sum = 0;
            for (int child = first; child >= 0; child = _slots[child].Next)
            {
                ref var item = ref _slots[child];
                if (item.StretchFrozen) continue;
                var style = item.Style;
                double share = remaining * (row ? style.Width.Value : style.Height.Value) / weights;
                item.StretchSize = Math.Clamp(share, row ? style.MinWidth : style.MinHeight, row ? style.MaxWidth : style.MaxHeight);
                sum += item.StretchSize;
            }
            double violation = sum - remaining;
            if (Math.Abs(violation) <= Math.Max(1, Math.Abs(remaining)) * 1e-12) break;
            double frozenSize = 0, frozenWeights = 0;
            for (int child = first; child >= 0; child = _slots[child].Next)
            {
                ref var item = ref _slots[child];
                if (item.StretchFrozen) continue;
                double weight = row ? item.Style.Width.Value : item.Style.Height.Value;
                double share = remaining * weight / weights;
                if (violation > 0 ? item.StretchSize > share : item.StretchSize < share)
                {
                    item.StretchFrozen = true;
                    frozenSize += item.StretchSize; frozenWeights += weight;
                }
            }
            if (frozenWeights == 0) break;
            remaining -= frozenSize;
            // Re-sum active weights: subtracting a dominant frozen weight can erase a
            // much smaller active weight through cancellation and leave stale sizes.
            weights = 0;
            for (int child = first; child >= 0; child = _slots[child].Next)
                if (!_slots[child].StretchFrozen) weights += row ? _slots[child].Style.Width.Value : _slots[child].Style.Height.Value;
        }
    }

    private void SetGeometry(int index, UiRect bounds, UiRect clip, bool force)
    {
        ref var node = ref _slots[index];
        if (!node.Style.Visible) bounds = clip = default;
        if (!force && node.Bounds == bounds && node.Clip == clip) return;
        AdvanceDrawRevision();
        node.Bounds = bounds; node.Clip = clip;
        node.Dirty |= UiInvalidation.Arrange | UiInvalidation.Paint | UiInvalidation.HitTest;
        MarkPaint(index);
    }

    /// <summary>Last sibling paints on top. Cached clipping defines half-open hit rectangles.</summary>
    public UiNodeHandle HitTest(float x, float y)
    {
        ThrowIfDisposed();
        UiNodeHandle result = default;
        for (int root = _firstRoot; root >= 0; root = _slots[root].Next)
        {
            int current = root;
            while (true)
            {
                var node = _slots[current];
                if (node.Style.Visible && node.Clip.Contains(x, y))
                {
                    if (node.Style.HitTestVisible) result = Handle(current);
                    if (node.First >= 0) { current = node.First; continue; }
                }
                while (current != root && _slots[current].Next < 0) current = _slots[current].Parent;
                if (current == root) break;
                current = _slots[current].Next;
            }
        }
        return result;
    }

    private void ClearInputInSubtree(int index)
    {
        if (InSubtree(Focused, index)) SetFocus(default);
        if (InSubtree(Captured, index)) Captured = default;
        if (InSubtree(_pressed, index)) _pressed = default;
    }

    private bool InSubtree(UiNodeHandle handle, int root)
    {
        if (handle.IsNull) return false;
        for (int index = handle.Index; index >= 0; index = _slots[index].Parent)
            if (index == root) return true;
        return false;
    }

    private static void ValidateStyle(UiStyle style)
    {
        if ((uint)style.Flow > (uint)UiFlow.Overlay || (uint)style.Position > (uint)UiPosition.Absolute || !ValidCoordinate(style.Left) || !ValidCoordinate(style.Top)
            || !ValidLength(style.Width) || !ValidLength(style.Height)
            || !ValidRange(style.MinWidth, style.MaxWidth) || !ValidRange(style.MinHeight, style.MaxHeight)
            || !ValidNonnegative(style.Padding) || !ValidNonnegative(style.Gap))
            throw new ArgumentOutOfRangeException(nameof(style));
    }
    private static bool ValidLength(UiLength length) => (uint)length.Sizing <= (uint)UiSizing.Stretch
        && (length.Sizing == UiSizing.Stretch ? float.IsFinite(length.Value) && length.Value >= 1e-6f && length.Value <= 1e6f : ValidNonnegative(length.Value));
    private static bool ValidRange(float min, float max) => ValidNonnegative(min) && float.IsFinite(max) && max >= min;
    private static bool ValidNonnegative(float value) => float.IsFinite(value) && value >= 0 && value <= MaximumLogicalInput;
    private static bool ValidCoordinate(float value) => float.IsFinite(value) && Math.Abs(value) <= MaximumLogicalInput;
}
