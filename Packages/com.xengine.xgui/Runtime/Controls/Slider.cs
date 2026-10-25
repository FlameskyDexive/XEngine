using System;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

[AddComponentMenu("UI/Slider")]
public sealed class Slider : Selectable, IDragHandler, IBeginDragHandler
{
    public enum SliderDirection { LeftToRight, RightToLeft, BottomToTop, TopToBottom }

    [SerializeField] private RectTransform? _fillRect;
    public RectTransform? FillRect { get => _fillRect; set { _fillRect = value; UpdateVisuals(); } }

    [SerializeField] private RectTransform? _handleRect;
    public RectTransform? HandleRect { get => _handleRect; set { _handleRect = value; UpdateVisuals(); } }

    [SerializeField] private SliderDirection _direction = SliderDirection.LeftToRight;
    public SliderDirection Direction { get => _direction; set { _direction = value; UpdateVisuals(); } }

    [SerializeField] private float _minValue;
    public float MinValue { get => _minValue; set { _minValue = value; SetValue(_value, false); } }

    [SerializeField] private float _maxValue = 1f;
    public float MaxValue { get => _maxValue; set { _maxValue = value; SetValue(_value, false); } }

    [SerializeField] private bool _wholeNumbers;
    public bool WholeNumbers { get => _wholeNumbers; set { _wholeNumbers = value; SetValue(_value, false); } }

    [SerializeField] private float _value;
    public float Value { get => _value; set => SetValue(value, true); }

    public float NormalizedValue
    {
        get
        {
            float span = _maxValue - _minValue;
            return Maths.Abs(span) < 1e-6f ? 0f : Maths.Clamp((_value - _minValue) / span, 0f, 1f);
        }
        set => SetValue(_minValue + (_maxValue - _minValue) * Maths.Clamp(value, 0f, 1f), true);
    }

    public event Action<float>? OnValueChanged;
    [SerializeField] private XEngineAction _onValueChanged = new();
    public XEngineAction ValueChangedAction => _onValueChanged;

    private void SetValue(float input, bool notify)
    {
        float lo = Maths.Min(_minValue, _maxValue);
        float hi = Maths.Max(_minValue, _maxValue);
        float value = Maths.Clamp(input, lo, hi);
        if (_wholeNumbers) value = Maths.Round(value);
        bool changed = value != _value;
        _value = value;
        UpdateVisuals();
        if (!changed || !notify) return;
        try { OnValueChanged?.Invoke(_value); }
        catch (Exception ex) { Debug.LogError($"[Slider] OnValueChanged on '{Name}' threw: {ex.Message}\n{ex.StackTrace}"); }
        _onValueChanged.Invoke();
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
        UpdateValueFromPointer(e);
        e.Use();
    }

    public void OnBeginDrag(PointerEventData e) { }

    public void OnDrag(PointerEventData e)
    {
        if (e.Button != MouseButton.Left || !IsInteractable()) return;
        UpdateValueFromPointer(e);
        e.Use();
    }

    /// <summary>
    /// Lets keyboard and gamepad navigation adjust the value along the slider's visual axis. A move
    /// perpendicular to that axis remains regular Selectable navigation so forms stay traversable.
    /// </summary>
    public override void OnMove(MoveDirection direction)
    {
        if (!IsInteractable()) return;

        int directionSign = _direction switch
        {
            SliderDirection.LeftToRight when direction == MoveDirection.Left => -1,
            SliderDirection.LeftToRight when direction == MoveDirection.Right => 1,
            SliderDirection.RightToLeft when direction == MoveDirection.Left => 1,
            SliderDirection.RightToLeft when direction == MoveDirection.Right => -1,
            SliderDirection.BottomToTop when direction == MoveDirection.Down => -1,
            SliderDirection.BottomToTop when direction == MoveDirection.Up => 1,
            SliderDirection.TopToBottom when direction == MoveDirection.Down => 1,
            SliderDirection.TopToBottom when direction == MoveDirection.Up => -1,
            _ => 0,
        };
        if (directionSign == 0)
        {
            base.OnMove(direction);
            return;
        }

        float span = Maths.Abs(_maxValue - _minValue);
        if (span <= 1e-6f) return;
        float step = _wholeNumbers ? 1f / span : 0.1f;
        NormalizedValue += directionSign * step;
    }

    private void UpdateValueFromPointer(PointerEventData e)
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null) return;
        Rect rect = rt.ComputedRect;
        if (rect.Size.X <= 0f || rect.Size.Y <= 0f) return;
        Float2 point = e.DesignPosition;
        float normalized = _direction switch
        {
            SliderDirection.LeftToRight => (point.X - rect.Min.X) / rect.Size.X,
            SliderDirection.RightToLeft => 1f - (point.X - rect.Min.X) / rect.Size.X,
            SliderDirection.BottomToTop => (point.Y - rect.Min.Y) / rect.Size.Y,
            SliderDirection.TopToBottom => 1f - (point.Y - rect.Min.Y) / rect.Size.Y,
            _ => 0f,
        };
        NormalizedValue = normalized;
    }

    private void UpdateVisuals()
    {
        float normalized = NormalizedValue;
        bool horizontal = _direction is SliderDirection.LeftToRight or SliderDirection.RightToLeft;
        bool reversed = _direction is SliderDirection.RightToLeft or SliderDirection.TopToBottom;
        if (_fillRect != null)
        {
            Float2 min = Float2.Zero;
            Float2 max = Float2.One;
            if (horizontal)
            {
                if (reversed) min = new Float2(1f - normalized, 0f);
                else max = new Float2(normalized, 1f);
            }
            else
            {
                if (reversed) min = new Float2(0f, 1f - normalized);
                else max = new Float2(1f, normalized);
            }
            _fillRect.AnchorMin = min;
            _fillRect.AnchorMax = max;
            _fillRect.SizeDelta = Float2.Zero;
            _fillRect.AnchoredPosition = Float2.Zero;
        }
        if (_handleRect == null) return;
        float handle = reversed ? 1f - normalized : normalized;
        if (horizontal)
        {
            _handleRect.AnchorMin = new Float2(handle, 0f);
            _handleRect.AnchorMax = new Float2(handle, 1f);
        }
        else
        {
            _handleRect.AnchorMin = new Float2(0f, handle);
            _handleRect.AnchorMax = new Float2(1f, handle);
        }
        _handleRect.AnchoredPosition = Float2.Zero;
    }
}
