// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.XUI;

/// <summary>
/// Size queries for a layout child: preferred, minimum, and flexible weight.
/// </summary>
/// <remarks>
/// Everything here goes through <see cref="ILayoutElementHost"/>, the Runtime-neutral contract the
/// canvas itself uses, so XUI measures XGUI controls, its own containers, and any third-party
/// component identically.
/// </remarks>
internal static class XuiLayoutMeasure
{
    public static Float2 Preferred(GameObject go, List<MonoBehaviour> scratch)
    {
        float preferredWidth = -1f, preferredHeight = -1f, minWidth = -1f, minHeight = -1f;
        go.GetComponents(scratch);
        for (int i = 0; i < scratch.Count; i++)
        {
            if (scratch[i] is not ILayoutElementHost element || !scratch[i].EnabledInHierarchy) continue;
            preferredWidth = Maths.Max(preferredWidth, element.PreferredWidth);
            preferredHeight = Maths.Max(preferredHeight, element.PreferredHeight);
            minWidth = Maths.Max(minWidth, element.MinWidth);
            minHeight = Maths.Max(minHeight, element.MinHeight);
        }

        Float2 intrinsic = Intrinsic(go);
        float width = preferredWidth >= 0f ? preferredWidth : (minWidth >= 0f ? minWidth : intrinsic.X);
        float height = preferredHeight >= 0f ? preferredHeight : (minHeight >= 0f ? minHeight : intrinsic.Y);
        if (minWidth >= 0f) width = Maths.Max(width, minWidth);
        if (minHeight >= 0f) height = Maths.Max(height, minHeight);
        return new Float2(width, height);
    }

    public static Float2 Minimum(GameObject go, List<MonoBehaviour> scratch)
    {
        float minWidth = -1f, minHeight = -1f;
        go.GetComponents(scratch);
        for (int i = 0; i < scratch.Count; i++)
        {
            if (scratch[i] is not ILayoutElementHost element || !scratch[i].EnabledInHierarchy) continue;
            minWidth = Maths.Max(minWidth, element.MinWidth);
            minHeight = Maths.Max(minHeight, element.MinHeight);
        }

        Float2 intrinsic = Intrinsic(go);
        return new Float2(minWidth >= 0f ? minWidth : intrinsic.X, minHeight >= 0f ? minHeight : intrinsic.Y);
    }

    public static Float2 Flexible(GameObject go, List<MonoBehaviour> scratch)
    {
        float flexibleWidth = 0f, flexibleHeight = 0f;
        go.GetComponents(scratch);
        for (int i = 0; i < scratch.Count; i++)
        {
            if (scratch[i] is not ILayoutElementHost element || !scratch[i].EnabledInHierarchy) continue;
            if (element.FlexibleWidth > flexibleWidth) flexibleWidth = element.FlexibleWidth;
            if (element.FlexibleHeight > flexibleHeight) flexibleHeight = element.FlexibleHeight;
        }
        return new Float2(flexibleWidth, flexibleHeight);
    }

    private static Float2 Intrinsic(GameObject go)
        => go.RectTransform is { } rect ? rect.SizeDelta : new Float2(100f, 100f);
}

/// <summary>
/// Base for the containers XUI's <c>VStack</c> / <c>HStack</c> / <c>Grid</c> tags assemble.
/// </summary>
/// <remarks>
/// XUI ships its own containers rather than reusing XGUI's <c>LayoutGroup</c> family because those
/// seven type names are declared identically in both <c>XEngine.Runtime</c> and
/// <c>XEngine.XGUI.Runtime</c>; an assembly referencing both — which every UI package does — cannot
/// name them without CS0433. Owning them here also pins the layout semantics a <c>.xui</c> document
/// describes, so the same document lays out the same way across XGUI revisions.
/// </remarks>
[RequireComponent(typeof(RectTransform))]
public abstract class XuiLayoutContainer : UIBehaviour, ILayoutGroupHost
{
    [SerializeField] private int _paddingLeft;
    [SerializeField] private int _paddingRight;
    [SerializeField] private int _paddingTop;
    [SerializeField] private int _paddingBottom;
    [SerializeField] private TextAlignment _childAlignment = TextAlignment.TopLeft;

    [SerializeIgnore] private readonly List<GameObject> _children = new(8);
    [SerializeIgnore] private readonly List<MonoBehaviour> _scratch = new(4);

    public int PaddingLeft { get => _paddingLeft; set => SetField(ref _paddingLeft, value, UIDirtyFlags.Layout); }
    public int PaddingRight { get => _paddingRight; set => SetField(ref _paddingRight, value, UIDirtyFlags.Layout); }
    public int PaddingTop { get => _paddingTop; set => SetField(ref _paddingTop, value, UIDirtyFlags.Layout); }
    public int PaddingBottom { get => _paddingBottom; set => SetField(ref _paddingBottom, value, UIDirtyFlags.Layout); }

    public TextAlignment ChildAlignment
    {
        get => _childAlignment;
        set => SetField(ref _childAlignment, value, UIDirtyFlags.Layout);
    }

    public bool IgnoreLayout => false;

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public abstract void Arrange(Rect rect);

    public abstract float MinWidth { get; }
    public abstract float PreferredWidth { get; }
    public abstract float MinHeight { get; }
    public abstract float PreferredHeight { get; }
    public virtual float FlexibleWidth => -1f;
    public virtual float FlexibleHeight => -1f;

    protected float PaddingHorizontal => _paddingLeft + _paddingRight;
    protected float PaddingVertical => _paddingTop + _paddingBottom;
    protected List<MonoBehaviour> Scratch => _scratch;

    /// <summary>Enabled children with a rect, skipping anything that opted out of layout.</summary>
    protected List<GameObject> LayoutChildren()
    {
        _children.Clear();
        IReadOnlyList<GameObject> children = GameObject.Children;
        for (int childIndex = 0; childIndex < children.Count; childIndex++)
        {
            GameObject child = children[childIndex];
            if (!child.EnabledInHierarchy || child.RectTransform is null) continue;

            bool ignored = false;
            child.GetComponents(_scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                if (_scratch[i] is ILayoutElementHost element && _scratch[i].EnabledInHierarchy && element.IgnoreLayout)
                {
                    ignored = true;
                    break;
                }
            }
            if (!ignored) _children.Add(child);
        }
        return _children;
    }

    protected Rect ContentRect(in Rect rect) => new(
        rect.Min.X + _paddingLeft,
        rect.Min.Y + _paddingBottom,
        rect.Max.X - _paddingRight,
        rect.Max.Y - _paddingTop);

    protected static void SetChildRect(GameObject child, Rect rect)
    {
        if (child.RectTransform is { } rectTransform) rectTransform.ComputedRect = rect;
    }

    protected float HorizontalFactor()
        => (_childAlignment & TextAlignment.Right) != 0 ? 1f
        : (_childAlignment & TextAlignment.Middle) != 0 ? 0.5f
        : 0f;

    protected float VerticalFactor()
        => (_childAlignment & TextAlignment.Top) != 0 ? 1f
        : (_childAlignment & TextAlignment.Center) != 0 ? 0.5f
        : 0f;

    protected static float AlignStart(float min, float available, float size, float factor)
        => min + Maths.Max(0f, available - size) * factor;
}

/// <summary>
/// A row or column. Children take their preferred size along the axis; leftover space is shared by
/// flexible weight, and a shortfall is taken back down to each child's minimum in proportion to how
/// much it can give up.
/// </summary>
[AddComponentMenu("UI/XUI/Stack")]
public sealed class XuiStack : XuiLayoutContainer
{
    [SerializeField] private bool _vertical = true;
    [SerializeField] private float _spacing;
    [SerializeField] private bool _stretchCross = true;
    [SerializeField] private bool _expandAlong;

    [SerializeIgnore] private float[] _preferred = Array.Empty<float>();
    [SerializeIgnore] private float[] _minimum = Array.Empty<float>();
    [SerializeIgnore] private float[] _flexible = Array.Empty<float>();
    [SerializeIgnore] private float[] _sizes = Array.Empty<float>();

    /// <summary>True for <c>VStack</c> (top to bottom), false for <c>HStack</c> (left to right).</summary>
    public bool Vertical { get => _vertical; set => SetField(ref _vertical, value, UIDirtyFlags.Layout); }

    public float Spacing { get => _spacing; set => SetField(ref _spacing, value, UIDirtyFlags.Layout); }

    /// <summary>Children fill the container across the axis. Off, each keeps its preferred width.</summary>
    public bool StretchCross { get => _stretchCross; set => SetField(ref _stretchCross, value, UIDirtyFlags.Layout); }

    /// <summary>Children with no flexible weight still share leftover space along the axis.</summary>
    public bool ExpandAlong { get => _expandAlong; set => SetField(ref _expandAlong, value, UIDirtyFlags.Layout); }

    public override float PreferredWidth => _vertical ? CrossExtent(preferred: true) : AlongExtent(preferred: true);
    public override float PreferredHeight => _vertical ? AlongExtent(preferred: true) : CrossExtent(preferred: true);
    public override float MinWidth => _vertical ? CrossExtent(preferred: false) : AlongExtent(preferred: false);
    public override float MinHeight => _vertical ? AlongExtent(preferred: false) : CrossExtent(preferred: false);

    private float Along(Float2 size) => _vertical ? size.Y : size.X;
    private float Cross(Float2 size) => _vertical ? size.X : size.Y;
    private float PaddingAlong => _vertical ? PaddingVertical : PaddingHorizontal;
    private float PaddingCross => _vertical ? PaddingHorizontal : PaddingVertical;

    private float AlongExtent(bool preferred)
    {
        List<GameObject> children = LayoutChildren();
        float sum = _spacing * Maths.Max(0, children.Count - 1) + PaddingAlong;
        for (int i = 0; i < children.Count; i++)
        {
            sum += Along(preferred
                ? XuiLayoutMeasure.Preferred(children[i], Scratch)
                : XuiLayoutMeasure.Minimum(children[i], Scratch));
        }
        return sum;
    }

    private float CrossExtent(bool preferred)
    {
        List<GameObject> children = LayoutChildren();
        float max = 0f;
        for (int i = 0; i < children.Count; i++)
        {
            max = Maths.Max(max, Cross(preferred
                ? XuiLayoutMeasure.Preferred(children[i], Scratch)
                : XuiLayoutMeasure.Minimum(children[i], Scratch)));
        }
        return max + PaddingCross;
    }

    public override void Arrange(Rect rect)
    {
        List<GameObject> children = LayoutChildren();
        int count = children.Count;
        if (count == 0) return;

        Rect content = ContentRect(rect);
        float contentWidth = Maths.Max(0f, content.Size.X);
        float contentHeight = Maths.Max(0f, content.Size.Y);
        float alongSpace = _vertical ? contentHeight : contentWidth;
        float crossSpace = _vertical ? contentWidth : contentHeight;

        EnsureCapacity(count);
        float totalPreferred = _spacing * (count - 1);
        float totalFlexible = 0f;
        float totalShrinkable = 0f;

        for (int i = 0; i < count; i++)
        {
            _preferred[i] = Along(XuiLayoutMeasure.Preferred(children[i], Scratch));
            _minimum[i] = Along(XuiLayoutMeasure.Minimum(children[i], Scratch));
            float weight = Along(XuiLayoutMeasure.Flexible(children[i], Scratch));
            if (weight <= 0f && _expandAlong) weight = 1f;
            _flexible[i] = weight;
            totalPreferred += _preferred[i];
            totalFlexible += weight;
            totalShrinkable += Maths.Max(0f, _preferred[i] - _minimum[i]);
        }

        float extra = alongSpace - totalPreferred;
        for (int i = 0; i < count; i++)
        {
            if (extra > 0f && totalFlexible > 0f)
                _sizes[i] = _preferred[i] + extra * (_flexible[i] / totalFlexible);
            else if (extra < 0f && totalShrinkable > 0f)
                _sizes[i] = Maths.Max(_minimum[i], _preferred[i] + extra * (Maths.Max(0f, _preferred[i] - _minimum[i]) / totalShrinkable));
            else
                _sizes[i] = _preferred[i];
        }

        // +Y is up, so a vertical stack walks down from the content top edge.
        float cursor = _vertical ? content.Max.Y : content.Min.X;
        for (int i = 0; i < count; i++)
        {
            float crossSize = _stretchCross
                ? crossSpace
                : Maths.Min(Cross(XuiLayoutMeasure.Preferred(children[i], Scratch)), crossSpace);

            if (_vertical)
            {
                float x = AlignStart(content.Min.X, contentWidth, crossSize, HorizontalFactor());
                SetChildRect(children[i], new Rect(x, cursor - _sizes[i], x + crossSize, cursor));
                cursor -= _sizes[i] + _spacing;
            }
            else
            {
                float y = AlignStart(content.Min.Y, contentHeight, crossSize, VerticalFactor());
                SetChildRect(children[i], new Rect(cursor, y, cursor + _sizes[i], y + crossSize));
                cursor += _sizes[i] + _spacing;
            }
        }
    }

    private void EnsureCapacity(int count)
    {
        if (_preferred.Length >= count) return;
        int capacity = Math.Max(count, _preferred.Length == 0 ? 4 : _preferred.Length * 2);
        Array.Resize(ref _preferred, capacity);
        Array.Resize(ref _minimum, capacity);
        Array.Resize(ref _flexible, capacity);
        Array.Resize(ref _sizes, capacity);
    }
}

/// <summary>Fixed-cell grid filling left to right, top to bottom, in a fixed number of columns.</summary>
[AddComponentMenu("UI/XUI/Grid")]
public sealed class XuiGrid : XuiLayoutContainer
{
    [SerializeField] private Float2 _cellSize = new(100f, 100f);
    [SerializeField] private Float2 _spacing;
    [SerializeField] private int _columns = 1;

    public Float2 CellSize { get => _cellSize; set => SetField(ref _cellSize, value, UIDirtyFlags.Layout); }
    public Float2 Spacing { get => _spacing; set => SetField(ref _spacing, value, UIDirtyFlags.Layout); }

    public int Columns
    {
        get => _columns;
        set => SetField(ref _columns, Maths.Max(1, value), UIDirtyFlags.Layout);
    }

    public override float PreferredWidth
        => PaddingHorizontal + _columns * _cellSize.X + Maths.Max(0, _columns - 1) * _spacing.X;

    public override float PreferredHeight
    {
        get
        {
            int rows = RowCount(LayoutChildren().Count);
            return PaddingVertical + rows * _cellSize.Y + Maths.Max(0, rows - 1) * _spacing.Y;
        }
    }

    public override float MinWidth => PreferredWidth;
    public override float MinHeight => PreferredHeight;

    private int RowCount(int count) => count == 0 ? 0 : (count + _columns - 1) / _columns;

    public override void Arrange(Rect rect)
    {
        List<GameObject> children = LayoutChildren();
        int count = children.Count;
        if (count == 0) return;

        Rect content = ContentRect(rect);
        int rows = RowCount(count);
        float gridWidth = _columns * _cellSize.X + Maths.Max(0, _columns - 1) * _spacing.X;
        float gridHeight = rows * _cellSize.Y + Maths.Max(0, rows - 1) * _spacing.Y;

        float originX = AlignStart(content.Min.X, Maths.Max(0f, content.Size.X), gridWidth, HorizontalFactor());
        float originTop = content.Max.Y - Maths.Max(0f, Maths.Max(0f, content.Size.Y) - gridHeight) * (1f - VerticalFactor());

        for (int i = 0; i < count; i++)
        {
            int column = i % _columns;
            int row = i / _columns;
            float x = originX + column * (_cellSize.X + _spacing.X);
            float top = originTop - row * (_cellSize.Y + _spacing.Y);
            SetChildRect(children[i], new Rect(x, top - _cellSize.Y, x + _cellSize.X, top));
        }
    }
}

/// <summary>
/// Sizes its own rect to the content a layout container reports. XUI's scroll content uses it so a
/// list grows with its rows instead of clipping at a fixed height.
/// </summary>
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("UI/XUI/Content Fitter")]
public sealed class XuiContentFitter : UIBehaviour
{
    [SerializeField] private bool _fitWidth;
    [SerializeField] private bool _fitHeight = true;

    [SerializeIgnore] private readonly List<MonoBehaviour> _scratch = new(4);

    public bool FitWidth { get => _fitWidth; set => SetField(ref _fitWidth, value, UIDirtyFlags.Layout); }
    public bool FitHeight { get => _fitHeight; set => SetField(ref _fitHeight, value, UIDirtyFlags.Layout); }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void ApplyLayout(in Rect parentRect)
    {
        RectTransform? rect = GameObject.RectTransform;
        if (rect is null) return;

        float width = -1f, height = -1f;
        GameObject.GetComponents(_scratch);
        for (int i = 0; i < _scratch.Count; i++)
        {
            // Skip self: this component reports no size of its own, and asking it would recurse.
            if (ReferenceEquals(_scratch[i], this)) continue;
            if (_scratch[i] is not ILayoutElementHost element || !_scratch[i].EnabledInHierarchy) continue;
            width = Maths.Max(width, element.PreferredWidth);
            height = Maths.Max(height, element.PreferredHeight);
        }

        Float2 size = rect.SizeDelta;
        rect.SizeDelta = new Float2(
            _fitWidth && width >= 0f ? width : size.X,
            _fitHeight && height >= 0f ? height : size.Y);
    }
}
