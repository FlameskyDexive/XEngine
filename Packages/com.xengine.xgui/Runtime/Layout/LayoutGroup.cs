using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>Base package layout group. The Runtime host sees it through ILayoutGroupHost.</summary>
[RequireComponent(typeof(RectTransform))]
public abstract class LayoutGroup : UIBehaviour, ILayoutElement, ILayoutGroup
{
    [SerializeField] protected int _paddingLeft;
    [SerializeField] protected int _paddingRight;
    [SerializeField] protected int _paddingTop;
    [SerializeField] protected int _paddingBottom;
    [SerializeField] protected TextAlignment _childAlignment = TextAlignment.TopLeft;
    [SerializeIgnore] private readonly List<GameObject> _layoutChildren = new(8);
    [SerializeIgnore] private readonly List<MonoBehaviour> _componentScratch = new(4);

    public int PaddingLeft { get => _paddingLeft; set => SetField(ref _paddingLeft, value, UIDirtyFlags.Layout); }
    public int PaddingRight { get => _paddingRight; set => SetField(ref _paddingRight, value, UIDirtyFlags.Layout); }
    public int PaddingTop { get => _paddingTop; set => SetField(ref _paddingTop, value, UIDirtyFlags.Layout); }
    public int PaddingBottom { get => _paddingBottom; set => SetField(ref _paddingBottom, value, UIDirtyFlags.Layout); }
    public TextAlignment ChildAlignment { get => _childAlignment; set => SetField(ref _childAlignment, value, UIDirtyFlags.Layout); }
    public bool IgnoreLayout => false;

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }
    public abstract void Arrange(Rect rect);

    public abstract float MinWidth { get; }
    public abstract float PreferredWidth { get; }
    public virtual float FlexibleWidth => -1f;
    public abstract float MinHeight { get; }
    public abstract float PreferredHeight { get; }
    public virtual float FlexibleHeight => -1f;

    protected List<GameObject> GetLayoutChildren()
    {
        _layoutChildren.Clear();
        for (int childIndex = 0; childIndex < GameObject.Children.Count; childIndex++)
        {
            GameObject child = GameObject.Children[childIndex];
            if (!child.EnabledInHierarchy || child.RectTransform is null) continue;
            bool ignored = false;
            child.GetComponents(_componentScratch);
            for (int componentIndex = 0; componentIndex < _componentScratch.Count; componentIndex++)
            {
                MonoBehaviour component = _componentScratch[componentIndex];
                if (component is ILayoutElementHost element && component.EnabledInHierarchy && element.IgnoreLayout)
                {
                    ignored = true;
                    break;
                }
            }
            if (!ignored) _layoutChildren.Add(child);
        }
        return _layoutChildren;
    }

    protected List<MonoBehaviour> ComponentScratch => _componentScratch;

    protected static void SetChildRect(GameObject child, Rect rect)
    {
        if (child.RectTransform is { } rt) rt.ComputedRect = rect;
    }

    protected float HorizontalFactor()
        => (_childAlignment & TextAlignment.Right) != 0 ? 1f : (_childAlignment & TextAlignment.Middle) != 0 ? 0.5f : 0f;
    protected float VerticalFactor()
        => (_childAlignment & TextAlignment.Top) != 0 ? 1f : (_childAlignment & TextAlignment.Center) != 0 ? 0.5f : 0f;
    protected static float AlignStart(float min, float available, float size, float factor)
        => min + Maths.Max(0f, available - size) * factor;
}

/// <summary>Shared row/column arranger preserving the engine's +Y-up convention.</summary>
public abstract class HorizontalOrVerticalLayoutGroup : LayoutGroup
{
    protected abstract bool IsVertical { get; }

    [SerializeField] protected float _spacing;
    [SerializeField] protected bool _childControlWidth = true;
    [SerializeField] protected bool _childControlHeight = true;
    [SerializeField] protected bool _childForceExpandWidth;
    [SerializeField] protected bool _childForceExpandHeight;
    [SerializeIgnore] private float[] _preferred = Array.Empty<float>();
    [SerializeIgnore] private float[] _minimum = Array.Empty<float>();
    [SerializeIgnore] private float[] _flexible = Array.Empty<float>();
    [SerializeIgnore] private float[] _sizes = Array.Empty<float>();

    public float Spacing { get => _spacing; set => SetField(ref _spacing, value, UIDirtyFlags.Layout); }
    public bool ChildControlWidth { get => _childControlWidth; set => SetField(ref _childControlWidth, value, UIDirtyFlags.Layout); }
    public bool ChildControlHeight { get => _childControlHeight; set => SetField(ref _childControlHeight, value, UIDirtyFlags.Layout); }
    public bool ChildForceExpandWidth { get => _childForceExpandWidth; set => SetField(ref _childForceExpandWidth, value, UIDirtyFlags.Layout); }
    public bool ChildForceExpandHeight { get => _childForceExpandHeight; set => SetField(ref _childForceExpandHeight, value, UIDirtyFlags.Layout); }

    private float PaddingAlong => IsVertical ? _paddingTop + _paddingBottom : _paddingLeft + _paddingRight;
    private float PaddingCross => IsVertical ? _paddingLeft + _paddingRight : _paddingTop + _paddingBottom;
    private float AlongOf(Float2 s) => IsVertical ? s.Y : s.X;
    private float CrossOf(Float2 s) => IsVertical ? s.X : s.Y;

    public override float PreferredWidth => IsVertical ? CrossPreferred() : AlongPreferred();
    public override float PreferredHeight => IsVertical ? AlongPreferred() : CrossPreferred();
    public override float MinWidth => IsVertical ? CrossMin() : AlongMin();
    public override float MinHeight => IsVertical ? AlongMin() : CrossMin();

    private float AlongPreferred()
    {
        List<GameObject> kids = GetLayoutChildren();
        float sum = _spacing * Maths.Max(0, kids.Count - 1) + PaddingAlong;
        for (int i = 0; i < kids.Count; i++) sum += AlongOf(XGuiLayoutUtility.GetPreferredSize(kids[i], ComponentScratch));
        return sum;
    }
    private float AlongMin()
    {
        List<GameObject> kids = GetLayoutChildren();
        float sum = _spacing * Maths.Max(0, kids.Count - 1) + PaddingAlong;
        for (int i = 0; i < kids.Count; i++) sum += AlongOf(XGuiLayoutUtility.GetMinSize(kids[i], ComponentScratch));
        return sum;
    }
    private float CrossPreferred()
    {
        List<GameObject> kids = GetLayoutChildren();
        float max = 0f;
        for (int i = 0; i < kids.Count; i++) max = Maths.Max(max, CrossOf(XGuiLayoutUtility.GetPreferredSize(kids[i], ComponentScratch)));
        return max + PaddingCross;
    }
    private float CrossMin()
    {
        List<GameObject> kids = GetLayoutChildren();
        float max = 0f;
        for (int i = 0; i < kids.Count; i++) max = Maths.Max(max, CrossOf(XGuiLayoutUtility.GetMinSize(kids[i], ComponentScratch)));
        return max + PaddingCross;
    }

    public override void Arrange(Rect rect)
    {
        List<GameObject> kids = GetLayoutChildren();
        int n = kids.Count;
        if (n == 0) return;

        Rect content = new(rect.Min.X + _paddingLeft, rect.Min.Y + _paddingBottom,
            rect.Max.X - _paddingRight, rect.Max.Y - _paddingTop);
        float contentW = Maths.Max(0f, content.Size.X);
        float contentH = Maths.Max(0f, content.Size.Y);
        float mainSize = IsVertical ? contentH : contentW;
        float crossSize = IsVertical ? contentW : contentH;
        bool forceExpandMain = IsVertical ? _childForceExpandHeight : _childForceExpandWidth;
        bool controlCross = IsVertical ? _childControlWidth : _childControlHeight;

        EnsureBufferCapacity(n);
        float[] pref = _preferred, min = _minimum, flex = _flexible, size = _sizes;
        float totalPref = _spacing * (n - 1), totalFlex = 0f, totalShrink = 0f;
        for (int i = 0; i < n; i++)
        {
            pref[i] = AlongOf(XGuiLayoutUtility.GetPreferredSize(kids[i], ComponentScratch));
            min[i] = AlongOf(XGuiLayoutUtility.GetMinSize(kids[i], ComponentScratch));
            float f = AlongOf(XGuiLayoutUtility.GetFlexible(kids[i], ComponentScratch));
            if (f <= 0f && forceExpandMain) f = 1f;
            flex[i] = f;
            totalPref += pref[i];
            totalFlex += f;
            totalShrink += Maths.Max(0f, pref[i] - min[i]);
        }

        float extra = mainSize - totalPref;
        for (int i = 0; i < n; i++)
        {
            if (extra > 0f && totalFlex > 0f)
                size[i] = pref[i] + extra * (flex[i] / totalFlex);
            else if (extra < 0f && totalShrink > 0f)
                size[i] = Maths.Max(min[i], pref[i] + extra * (Maths.Max(0f, pref[i] - min[i]) / totalShrink));
            else size[i] = pref[i];
        }

        float pos = IsVertical ? content.Max.Y : content.Min.X;
        for (int i = 0; i < n; i++)
        {
            float cross = controlCross ? crossSize : Maths.Min(CrossOf(XGuiLayoutUtility.GetPreferredSize(kids[i], ComponentScratch)), crossSize);
            if (IsVertical)
            {
                float x = AlignStart(content.Min.X, contentW, cross, HorizontalFactor());
                SetChildRect(kids[i], new Rect(x, pos - size[i], x + cross, pos));
                pos -= size[i] + _spacing;
            }
            else
            {
                float y = AlignStart(content.Min.Y, contentH, cross, VerticalFactor());
                SetChildRect(kids[i], new Rect(pos, y, pos + size[i], y + cross));
                pos += size[i] + _spacing;
            }
        }
    }

    private void EnsureBufferCapacity(int count)
    {
        if (_preferred.Length >= count) return;
        int capacity = Math.Max(count, _preferred.Length == 0 ? 4 : _preferred.Length * 2);
        Array.Resize(ref _preferred, capacity);
        Array.Resize(ref _minimum, capacity);
        Array.Resize(ref _flexible, capacity);
        Array.Resize(ref _sizes, capacity);
    }
}

[AddComponentMenu("UI/Layout/Horizontal Layout Group")]
public sealed class HorizontalLayoutGroup : HorizontalOrVerticalLayoutGroup
{
    protected override bool IsVertical => false;
}

[AddComponentMenu("UI/Layout/Vertical Layout Group")]
public sealed class VerticalLayoutGroup : HorizontalOrVerticalLayoutGroup
{
    protected override bool IsVertical => true;
}
