using System;
using System.Collections.Generic;
using XEngine.Echo;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

[AddComponentMenu("UI/Layout/Grid Layout Group")]
public sealed class GridLayoutGroup : LayoutGroup
{
    public enum Constraint { Flexible, FixedColumnCount, FixedRowCount }
    [SerializeField] private Float2 _cellSize = new(100f, 100f);
    [SerializeField] private Float2 _spacing = Float2.Zero;
    [SerializeField] private Constraint _constraint = Constraint.Flexible;
    [SerializeField] private int _constraintCount = 2;
    public Float2 CellSize { get => _cellSize; set => SetField(ref _cellSize, value, UIDirtyFlags.Layout); }
    public Float2 Spacing { get => _spacing; set => SetField(ref _spacing, value, UIDirtyFlags.Layout); }
    public Constraint GridConstraint { get => _constraint; set => SetField(ref _constraint, value, UIDirtyFlags.Layout); }
    public int ConstraintCount { get => _constraintCount; set => SetField(ref _constraintCount, Maths.Max(1, value), UIDirtyFlags.Layout); }

    public override void Arrange(Rect rect)
    {
        List<GameObject> kids = GetLayoutChildren();
        int n = kids.Count;
        if (n == 0) return;
        Rect content = new(rect.Min.X + PaddingLeft, rect.Min.Y + PaddingBottom,
            rect.Max.X - PaddingRight, rect.Max.Y - PaddingTop);
        int cols = Maths.Max(1, ColumnsFor(n, content.Size.X));
        int rows = (n + cols - 1) / cols;
        float gridW = cols * _cellSize.X + Maths.Max(0, cols - 1) * _spacing.X;
        float gridH = rows * _cellSize.Y + Maths.Max(0, rows - 1) * _spacing.Y;
        float startX = AlignStart(content.Min.X, content.Size.X, gridW, HorizontalFactor());
        float topY = AlignStart(content.Min.Y, content.Size.Y, gridH, VerticalFactor()) + gridH;
        for (int i = 0; i < n; i++)
        {
            int c = i % cols, r = i / cols;
            float x = startX + c * (_cellSize.X + _spacing.X);
            float top = topY - r * (_cellSize.Y + _spacing.Y);
            SetChildRect(kids[i], new Rect(x, top - _cellSize.Y, x + _cellSize.X, top));
        }
    }

    private int ColumnsFor(int n, float availableWidth)
    {
        if (_constraint == Constraint.FixedColumnCount) return Maths.Max(1, _constraintCount);
        if (_constraint == Constraint.FixedRowCount) return (n + Maths.Max(1, _constraintCount) - 1) / Maths.Max(1, _constraintCount);
        float step = _cellSize.X + _spacing.X;
        return step <= 0f ? n : Maths.Clamp((int)((availableWidth + _spacing.X) / step), 1, Maths.Max(1, n));
    }

    private void Dimensions(out int cols, out int rows)
    {
        int n = GetLayoutChildren().Count;
        cols = _constraint == Constraint.FixedColumnCount ? Maths.Max(1, _constraintCount)
            : _constraint == Constraint.FixedRowCount ? Maths.Max(1, (n + Maths.Max(1, _constraintCount) - 1) / Maths.Max(1, _constraintCount))
            : Maths.Max(1, (int)MathF.Ceiling(MathF.Sqrt(Maths.Max(1, n))));
        rows = n == 0 ? 0 : (n + cols - 1) / cols;
    }

    public override float PreferredWidth { get { Dimensions(out int cols, out _); return PaddingLeft + PaddingRight + cols * _cellSize.X + Maths.Max(0, cols - 1) * _spacing.X; } }
    public override float PreferredHeight { get { Dimensions(out _, out int rows); return PaddingTop + PaddingBottom + rows * _cellSize.Y + Maths.Max(0, rows - 1) * _spacing.Y; } }
    public override float MinWidth => PreferredWidth;
    public override float MinHeight => PreferredHeight;
}
