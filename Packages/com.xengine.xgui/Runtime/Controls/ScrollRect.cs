using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Package-owned scroll view. Content is moved in canvas design pixels and remains compatible with the
/// legacy UIScrollRect field names. Clamped nested views let a parent consume a drag only after a child
/// reaches its edge; Elastic views apply a bounded overscroll and settle back after release.
/// </summary>
[AddComponentMenu("UI/Scroll View")]
[ComponentIcon("")]
[RequireComponent(typeof(RectTransform))]
public class ScrollRect : UIBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
{
    [SerializeField] private RectTransform? _content;
    public RectTransform? Content { get => _content; set => _content = value; }

    [SerializeField] private RectTransform? _viewport;
    public RectTransform? Viewport { get => _viewport; set => _viewport = value; }

    [SerializeField] private Scrollbar? _horizontalScrollbar;
    public Scrollbar? HorizontalScrollbar { get => _horizontalScrollbar; set => _horizontalScrollbar = value; }

    [SerializeField] private Scrollbar? _verticalScrollbar;
    public Scrollbar? VerticalScrollbar { get => _verticalScrollbar; set => _verticalScrollbar = value; }

    public enum ScrollbarVisibilityMode
    {
        Permanent,
        AutoHide,
        AutoHideAndExpandViewport,
    }

    [SerializeField] private ScrollbarVisibilityMode _horizontalScrollbarVisibility = ScrollbarVisibilityMode.AutoHideAndExpandViewport;
    public ScrollbarVisibilityMode HorizontalScrollbarVisibility { get => _horizontalScrollbarVisibility; set => _horizontalScrollbarVisibility = value; }

    [SerializeField] private ScrollbarVisibilityMode _verticalScrollbarVisibility = ScrollbarVisibilityMode.AutoHideAndExpandViewport;
    public ScrollbarVisibilityMode VerticalScrollbarVisibility { get => _verticalScrollbarVisibility; set => _verticalScrollbarVisibility = value; }

    [SerializeField] private float _horizontalScrollbarSpacing;
    public float HorizontalScrollbarSpacing { get => _horizontalScrollbarSpacing; set => _horizontalScrollbarSpacing = value; }

    [SerializeField] private float _verticalScrollbarSpacing;
    public float VerticalScrollbarSpacing { get => _verticalScrollbarSpacing; set => _verticalScrollbarSpacing = value; }

    [SerializeField] private bool _horizontal = true;
    public bool Horizontal { get => _horizontal; set => _horizontal = value; }

    [SerializeField] private bool _vertical = true;
    public bool Vertical { get => _vertical; set => _vertical = value; }

    [SerializeField] private float _scrollSensitivity = 30f;
    public float ScrollSensitivity { get => _scrollSensitivity; set => _scrollSensitivity = value; }

    [SerializeField] private bool _inertia = true;
    public bool Inertia { get => _inertia; set => _inertia = value; }

    [SerializeField] private float _decelerationRate = 0.135f;
    public float DecelerationRate { get => _decelerationRate; set => _decelerationRate = Maths.Clamp(value, 0f, 1f); }

    public enum MovementType
    {
        Unrestricted,
        Elastic,
        Clamped,
    }

    [SerializeField] private MovementType _movementType = MovementType.Clamped;
    public MovementType Movement { get => _movementType; set => _movementType = value; }

    [SerializeField] private float _elasticity = 0.1f;
    public float Elasticity { get => _elasticity; set => _elasticity = Maths.Max(0f, value); }

    public event Action? OnValueChanged;

    [SerializeIgnore] private bool _dragging;
    [SerializeIgnore] private Float2 _dragStartContent;
    [SerializeIgnore] private Float2 _dragStartPointer;
    [SerializeIgnore] private Float2 _velocity;
    [SerializeIgnore] private Float2 _prevContentPos;

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void OnEnable()
    {
        base.OnEnable();
        _dragging = false;
        _velocity = Float2.Zero;
    }

    public override void OnDisable()
    {
        _dragging = false;
        _velocity = Float2.Zero;
        base.OnDisable();
    }

    private Rect ViewportRect() => (_viewport ?? GameObject.RectTransform)!.ComputedRect;

    public void OnBeginDrag(PointerEventData e)
    {
        if (_content == null || e.Button != MouseButton.Left) return;
        _dragging = true;
        _velocity = Float2.Zero;
        _dragStartContent = _content.AnchoredPosition;
        _dragStartPointer = e.DesignPosition;
        _prevContentPos = _dragStartContent;
    }

    public void OnDrag(PointerEventData e)
    {
        if (_content == null || !_dragging || e.Button != MouseButton.Left) return;

        Float2 delta = e.DesignPosition - _dragStartPointer;
        Float2 candidate = _dragStartContent;
        if (_horizontal) candidate.X += delta.X;
        if (_vertical) candidate.Y += delta.Y;

        Float2 target = ApplyMovement(candidate, forDrag: true);
        if (target == _content.AnchoredPosition) return;

        SetContentPosition(target);
        e.Use();
    }

    public void OnEndDrag(PointerEventData e)
    {
        _dragging = false;
    }

    public void OnScroll(PointerEventData e)
    {
        if (_content == null) return;

        Float2 candidate = _content.AnchoredPosition;
        if (_vertical) candidate.Y += e.ScrollDelta * _scrollSensitivity;
        else if (_horizontal) candidate.X += e.ScrollDelta * _scrollSensitivity;

        Float2 target = ApplyMovement(candidate, forDrag: false);
        if (target == _content.AnchoredPosition) return;

        _velocity = Float2.Zero;
        SetContentPosition(target);
        e.Use();
    }

    public override void Update()
    {
        if (!Application.IsPlaying || _content == null) return;

        UpdateScrollbarVisibility();
        SyncScrollbar(_horizontalScrollbar, horizontal: true);
        SyncScrollbar(_verticalScrollbar, horizontal: false);

        if (_dragging)
        {
            float dt = Maths.Max(Time.DeltaTime, 1e-4f);
            Float2 cur = _content.AnchoredPosition;
            _velocity = (cur - _prevContentPos) / dt;
            _prevContentPos = cur;
            return;
        }

        if (_movementType == MovementType.Elastic)
        {
            Float2 settled = ClampContent(_content.AnchoredPosition);
            if (settled != _content.AnchoredPosition)
            {
                float t = Maths.Clamp(Time.DeltaTime / Maths.Max(_elasticity, 0.0001f), 0f, 1f);
                SetContentPosition(Maths.Lerp(_content.AnchoredPosition, settled, t));
            }
        }

        if (!_inertia || Maths.Abs(_velocity.X) + Maths.Abs(_velocity.Y) < 1f)
        {
            _velocity = Float2.Zero;
            return;
        }

        float decay = MathF.Pow(_decelerationRate, Time.DeltaTime);
        _velocity *= decay;
        Float2 candidate = _content.AnchoredPosition + _velocity * Time.DeltaTime;
        Float2 target = _movementType == MovementType.Unrestricted ? ApplyAxisLocks(candidate) : ClampContent(candidate);
        if (target.X != candidate.X) _velocity.X = 0f;
        if (target.Y != candidate.Y) _velocity.Y = 0f;
        SetContentPosition(target);
    }

    private Float2 ApplyMovement(Float2 candidate, bool forDrag)
    {
        candidate = ApplyAxisLocks(candidate);
        if (_movementType == MovementType.Unrestricted) return candidate;

        Float2 clamped = ClampContent(candidate);
        if (_movementType != MovementType.Elastic || !forDrag) return clamped;

        Rect viewport = ViewportRect();
        float x = clamped.X + RubberDelta(candidate.X - clamped.X, viewport.Size.X);
        float y = clamped.Y + RubberDelta(candidate.Y - clamped.Y, viewport.Size.Y);
        return new Float2(x, y);
    }

    private Float2 ApplyAxisLocks(Float2 candidate)
    {
        Float2 current = _content?.AnchoredPosition ?? Float2.Zero;
        if (!_horizontal) candidate.X = current.X;
        if (!_vertical) candidate.Y = current.Y;
        return candidate;
    }

    private void SetContentPosition(Float2 pos)
    {
        if (_content == null || _content.AnchoredPosition == pos) return;
        _content.AnchoredPosition = pos;
        try { OnValueChanged?.Invoke(); }
        catch (Exception ex) { Debug.LogError($"[ScrollRect] OnValueChanged on '{Name}' threw: {ex.Message}\n{ex.StackTrace}"); }
    }

    private Float2 ClampContent(Float2 candidate)
    {
        if (_content == null) return candidate;

        Rect viewport = ViewportRect();
        Rect content = _content.ComputedRect;
        Float2 current = _content.AnchoredPosition;
        if (_horizontal)
            candidate.X = ClampAxis(candidate.X, current.X, content.Min.X, content.Max.X, content.Size.X,
                viewport.Min.X, viewport.Max.X, viewport.Size.X, pinToMax: false);
        if (_vertical)
            candidate.Y = ClampAxis(candidate.Y, current.Y, content.Min.Y, content.Max.Y, content.Size.Y,
                viewport.Min.Y, viewport.Max.Y, viewport.Size.Y, pinToMax: true);
        return candidate;
    }

    private static float RubberDelta(float overStretch, float viewSize)
    {
        if (Maths.Abs(overStretch) < 1e-5f || viewSize <= 1e-5f) return 0f;
        float stretch = Maths.Abs(overStretch);
        float value = viewSize * (1f - 1f / (stretch * 0.55f / viewSize + 1f));
        return overStretch < 0f ? -value : value;
    }

    private void UpdateScrollbarVisibility()
    {
        if (_content == null) return;

        Rect rootRect = GameObject.RectTransform!.ComputedRect;
        Rect content = _content.ComputedRect;
        float fullW = rootRect.Size.X, fullH = rootRect.Size.Y;
        float contentW = content.Size.X, contentH = content.Size.Y;

        RectTransform? verticalRt = _verticalScrollbar?.GameObject.RectTransform;
        RectTransform? horizontalRt = _horizontalScrollbar?.GameObject.RectTransform;
        float verticalWidth = verticalRt != null ? MathF.Abs(verticalRt.SizeDelta.X) : 0f;
        float horizontalHeight = horizontalRt != null ? MathF.Abs(horizontalRt.SizeDelta.Y) : 0f;

        bool verticalExpands = _verticalScrollbarVisibility == ScrollbarVisibilityMode.AutoHideAndExpandViewport;
        bool horizontalExpands = _horizontalScrollbarVisibility == ScrollbarVisibilityMode.AutoHideAndExpandViewport;
        bool horizontalNeeded = false, verticalNeeded = false;
        for (int i = 0; i < 2; i++)
        {
            float reservedW = verticalExpands && verticalNeeded ? verticalWidth + _verticalScrollbarSpacing : 0f;
            float reservedH = horizontalExpands && horizontalNeeded ? horizontalHeight + _horizontalScrollbarSpacing : 0f;
            horizontalNeeded = _horizontal && _horizontalScrollbar != null && contentW > fullW - reservedW + 0.01f;
            verticalNeeded = _vertical && _verticalScrollbar != null && contentH > fullH - reservedH + 0.01f;
        }

        bool verticalVisible = _verticalScrollbar != null
            && (_verticalScrollbarVisibility == ScrollbarVisibilityMode.Permanent || verticalNeeded);
        bool horizontalVisible = _horizontalScrollbar != null
            && (_horizontalScrollbarVisibility == ScrollbarVisibilityMode.Permanent || horizontalNeeded);
        bool verticalReserve = _verticalScrollbar != null && _vertical && (!verticalExpands || verticalNeeded);
        bool horizontalReserve = _horizontalScrollbar != null && _horizontal && (!horizontalExpands || horizontalNeeded);
        float reserveW = verticalReserve ? verticalWidth + _verticalScrollbarSpacing : 0f;
        float reserveH = horizontalReserve ? horizontalHeight + _horizontalScrollbarSpacing : 0f;

        if (_verticalScrollbar != null && _verticalScrollbar.GameObject.Enabled != verticalVisible)
            _verticalScrollbar.GameObject.Enabled = verticalVisible;
        if (_horizontalScrollbar != null && _horizontalScrollbar.GameObject.Enabled != horizontalVisible)
            _horizontalScrollbar.GameObject.Enabled = horizontalVisible;

        if (_viewport != null)
        {
            _viewport.AnchorMin = Float2.Zero;
            _viewport.AnchorMax = Float2.One;
            _viewport.Pivot = new Float2(0.5f, 0.5f);
            _viewport.SizeDelta = new Float2(-reserveW, -reserveH);
            _viewport.AnchoredPosition = new Float2(-reserveW * 0.5f, reserveH * 0.5f);
        }
        if (verticalRt != null)
        {
            verticalRt.SizeDelta = new Float2(verticalWidth, -reserveH);
            verticalRt.AnchoredPosition = new Float2(0f, reserveH * 0.5f);
        }
        if (horizontalRt != null)
        {
            horizontalRt.SizeDelta = new Float2(-reserveW, horizontalHeight);
            horizontalRt.AnchoredPosition = new Float2(-reserveW * 0.5f, 0f);
        }
    }

    private void SyncScrollbar(Scrollbar? bar, bool horizontal)
    {
        if (bar == null || _content == null) return;
        if (bar.IsPressed)
        {
            if (horizontal) SetHorizontalNormalized(bar.Value);
            else SetVerticalNormalized(bar.Value);
        }
        else
        {
            bar.SetValueWithoutNotify(horizontal ? HorizontalNormalized() : VerticalNormalized());
        }
        bar.Size = horizontal ? HorizontalVisibleFraction() : VerticalVisibleFraction();
    }

    public float HorizontalNormalized()
    {
        if (_content == null) return 0f;
        Rect viewport = ViewportRect(); Rect content = _content.ComputedRect;
        float overflow = content.Size.X - viewport.Size.X;
        return overflow > 1e-4f ? Maths.Clamp((viewport.Min.X - content.Min.X) / overflow, 0f, 1f) : 0f;
    }

    public float VerticalNormalized()
    {
        if (_content == null) return 0f;
        Rect viewport = ViewportRect(); Rect content = _content.ComputedRect;
        float overflow = content.Size.Y - viewport.Size.Y;
        return overflow > 1e-4f ? Maths.Clamp((content.Max.Y - viewport.Max.Y) / overflow, 0f, 1f) : 0f;
    }

    private float HorizontalVisibleFraction()
    {
        if (_content == null) return 1f;
        Rect viewport = ViewportRect(); Rect content = _content.ComputedRect;
        return content.Size.X > 1e-4f ? Maths.Clamp(viewport.Size.X / content.Size.X, 0.05f, 1f) : 1f;
    }

    private float VerticalVisibleFraction()
    {
        if (_content == null) return 1f;
        Rect viewport = ViewportRect(); Rect content = _content.ComputedRect;
        return content.Size.Y > 1e-4f ? Maths.Clamp(viewport.Size.Y / content.Size.Y, 0.05f, 1f) : 1f;
    }

    public void SetHorizontalNormalized(float value)
    {
        if (_content == null) return;
        Rect viewport = ViewportRect(); Rect content = _content.ComputedRect;
        float overflow = content.Size.X - viewport.Size.X;
        if (overflow <= 1e-4f) return;
        float dx = viewport.Min.X - Maths.Clamp(value, 0f, 1f) * overflow - content.Min.X;
        if (Maths.Abs(dx) < 1e-4f) return;
        _velocity = Float2.Zero;
        SetContentPosition(_content.AnchoredPosition + new Float2(dx, 0f));
    }

    public void SetVerticalNormalized(float value)
    {
        if (_content == null) return;
        Rect viewport = ViewportRect(); Rect content = _content.ComputedRect;
        float overflow = content.Size.Y - viewport.Size.Y;
        if (overflow <= 1e-4f) return;
        float dy = viewport.Max.Y + Maths.Clamp(value, 0f, 1f) * overflow - content.Max.Y;
        if (Maths.Abs(dy) < 1e-4f) return;
        _velocity = Float2.Zero;
        SetContentPosition(_content.AnchoredPosition + new Float2(0f, dy));
    }

    private static float ClampAxis(float candidate, float current, float contentMin, float contentMax,
        float contentSize, float viewportMin, float viewportMax, float viewportSize, bool pinToMax)
    {
        if (contentSize <= viewportSize)
            return pinToMax ? current + viewportMax - contentMax : current + viewportMin - contentMin;

        float high = current + viewportMax - contentMax;
        float low = current + viewportMin - contentMin;
        return Maths.Clamp(candidate, Maths.Min(high, low), Maths.Max(high, low));
    }
}
