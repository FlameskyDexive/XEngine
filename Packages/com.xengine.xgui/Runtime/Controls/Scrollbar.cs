using System;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

[AddComponentMenu("UI/Scrollbar")]
public sealed class Scrollbar : Selectable, IDragHandler, IBeginDragHandler
{
    public enum ScrollbarDirection { LeftToRight, RightToLeft, BottomToTop, TopToBottom }

    [SerializeField] private RectTransform? _handleRect;
    public RectTransform? HandleRect { get => _handleRect; set { _handleRect = value; UpdateVisuals(); } }

    [SerializeField] private ScrollbarDirection _direction = ScrollbarDirection.LeftToRight;
    public ScrollbarDirection Direction { get => _direction; set { _direction = value; UpdateVisuals(); } }

    [SerializeField] private float _value;
    public float Value { get => _value; set => SetValue(value, true); }

    [SerializeField] private float _size = 0.2f;
    public float Size { get => _size; set { _size = Maths.Clamp(value, 0.02f, 1f); UpdateVisuals(); } }

    public event Action<float>? OnValueChanged;
    [SerializeField] private XEngineAction _onValueChanged = new();
    public XEngineAction ValueChangedAction => _onValueChanged;

    private void SetValue(float input, bool notify)
    {
        float value = Maths.Clamp(input, 0f, 1f);
        bool changed = value != _value;
        _value = value;
        UpdateVisuals();
        if (!changed || !notify) return;
        try { OnValueChanged?.Invoke(_value); }
        catch (Exception ex) { Debug.LogError($"[Scrollbar] OnValueChanged on '{Name}' threw: {ex.Message}\n{ex.StackTrace}"); }
        _onValueChanged.Invoke();
    }

    public void SetValueWithoutNotify(float value)
    {
        _value = Maths.Clamp(value, 0f, 1f);
        UpdateVisuals();
    }

    public override void OnEnable()
    {
        base.OnEnable();
        UpdateVisuals();
    }

    public override void OnPointerDown(PointerEventData e)
    {
        base.OnPointerDown(e);
        if (e.Button != MouseButton.Left || !IsInteractable()) return;
        SetValue(ValueFromPointer(e), true);
        e.Use();
    }

    public void OnBeginDrag(PointerEventData e) { }

    public void OnDrag(PointerEventData e)
    {
        if (e.Button != MouseButton.Left || !IsInteractable()) return;
        SetValue(ValueFromPointer(e), true);
        e.Use();
    }

    /// <summary>
    /// Adjusts the handle from keyboard or gamepad moves on its own axis. Perpendicular moves are
    /// delegated to Selectable navigation so focus can leave the scrollbar in that direction.
    /// </summary>
    public override void OnMove(MoveDirection direction)
    {
        if (!IsInteractable()) return;

        int directionSign = _direction switch
        {
            ScrollbarDirection.LeftToRight when direction == MoveDirection.Left => -1,
            ScrollbarDirection.LeftToRight when direction == MoveDirection.Right => 1,
            ScrollbarDirection.RightToLeft when direction == MoveDirection.Left => 1,
            ScrollbarDirection.RightToLeft when direction == MoveDirection.Right => -1,
            ScrollbarDirection.BottomToTop when direction == MoveDirection.Down => -1,
            ScrollbarDirection.BottomToTop when direction == MoveDirection.Up => 1,
            ScrollbarDirection.TopToBottom when direction == MoveDirection.Down => 1,
            ScrollbarDirection.TopToBottom when direction == MoveDirection.Up => -1,
            _ => 0,
        };
        if (directionSign == 0)
        {
            base.OnMove(direction);
            return;
        }

        Value += directionSign * 0.1f;
    }

    private float ValueFromPointer(PointerEventData e)
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null) return 0f;
        Rect rect = rt.ComputedRect;
        bool horizontal = _direction is ScrollbarDirection.LeftToRight or ScrollbarDirection.RightToLeft;
        Float2 point = e.DesignPosition;
        float raw = horizontal
            ? (rect.Size.X > 0f ? (point.X - rect.Min.X) / rect.Size.X : 0f)
            : (rect.Size.Y > 0f ? (point.Y - rect.Min.Y) / rect.Size.Y : 0f);
        raw = Maths.Clamp(raw, 0f, 1f);
        float directionValue = _direction switch
        {
            ScrollbarDirection.RightToLeft => 1f - raw,
            ScrollbarDirection.TopToBottom => 1f - raw,
            _ => raw,
        };
        float size = Maths.Clamp(_size, 0.02f, 1f);
        float span = 1f - size;
        return span > 1e-4f ? Maths.Clamp((directionValue - size * 0.5f) / span, 0f, 1f) : 0f;
    }

    private void UpdateVisuals()
    {
        if (_handleRect == null) return;
        float size = Maths.Clamp(_size, 0.02f, 1f);
        float span = 1f - size;
        float value = Maths.Clamp(_value, 0f, 1f);
        bool horizontal = _direction is ScrollbarDirection.LeftToRight or ScrollbarDirection.RightToLeft;
        float min;
        float max;
        switch (_direction)
        {
            case ScrollbarDirection.RightToLeft:
                min = span * (1f - value); max = min + size; break;
            case ScrollbarDirection.TopToBottom:
                max = 1f - value * span; min = max - size; break;
            default:
                min = value * span; max = min + size; break;
        }
        if (horizontal)
        {
            _handleRect.AnchorMin = new Float2(min, 0f);
            _handleRect.AnchorMax = new Float2(max, 1f);
        }
        else
        {
            _handleRect.AnchorMin = new Float2(0f, min);
            _handleRect.AnchorMax = new Float2(1f, max);
        }
        _handleRect.SizeDelta = Float2.Zero;
        _handleRect.AnchoredPosition = Float2.Zero;
    }
}
