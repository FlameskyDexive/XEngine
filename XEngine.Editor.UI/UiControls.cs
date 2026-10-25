namespace XEngine.Editor.UI;

public enum UiControlKind : byte { Container, Label, Button, Toggle, Slider }
public enum UiKey : byte { Tab, Enter, Space, Left, Right, Escape }
public enum UiActionKind : byte { None, Activated, ValueChanged }
public readonly record struct UiAction(UiActionKind Kind, UiNodeHandle Node, float Value = 0);

public sealed partial class UiNodeStore
{
    private UiNodeHandle _pressed;

    public UiNodeHandle CreateControl(UiControlKind kind, UiNodeHandle parent = default)
    {
        // Closed contiguous byte enum: avoid the measured post-GC Enum.IsDefined allocation.
        if ((uint)kind > (uint)UiControlKind.Slider) throw new ArgumentOutOfRangeException(nameof(kind));
        var node = Create(parent);
        _slots[node.Index].Control = kind;
        _slots[node.Index].Style = UiStyle.Default with
        {
            Focusable = kind is UiControlKind.Button or UiControlKind.Toggle or UiControlKind.Slider,
            HitTestVisible = kind != UiControlKind.Label
        };
        return node;
    }

    public UiControlKind GetControlKind(UiNodeHandle node) => _slots[Validate(node)].Control;
    public float GetValue(UiNodeHandle node) => _slots[Validate(node)].Value;
    public void SetValue(UiNodeHandle node, float value)
    {
        int index = Validate(node);
        if (!float.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
        if (_slots[index].Control is not (UiControlKind.Toggle or UiControlKind.Slider))
            throw new ArgumentException("Only toggles and sliders have a scalar value.", nameof(node));
        if (_slots[index].Control == UiControlKind.Toggle) value = value >= 0.5f ? 1 : 0;
        if (_slots[index].Value == value) return;
        _slots[index].Value = value;
        InvalidateIndex(index, UiInvalidation.Paint);
    }

    // Explicit event APIs: adapters preserve native event order and dispatch on the UI thread.
    // These are logical events, not an OS event queue, IME service, or cross-window arbiter.
    public UiAction PointerDown(float x, float y)
    {
        ThrowIfDisposed();
        if (!Captured.IsNull) return default;
        var hit = HitTest(x, y);
        if (hit.IsNull || !CanInteract(hit.Index)) { SetFocus(default); return default; }
        int index = hit.Index;
        if (!_slots[index].Style.Focusable) { SetFocus(default); return default; }
        SetFocus(hit);
        SetCapture(hit);
        _pressed = hit;
        Invalidate(hit, UiInvalidation.Paint);
        return _slots[index].Control == UiControlKind.Slider ? DragSlider(hit, x) : default;
    }

    public UiAction PointerMove(float x, float y)
    {
        ThrowIfDisposed();
        return !_pressed.IsNull && IsAlive(_pressed) && Captured == _pressed
            && _slots[_pressed.Index].Control == UiControlKind.Slider ? DragSlider(_pressed, x) : default;
    }

    public UiAction PointerUp(float x, float y)
    {
        ThrowIfDisposed();
        var pressed = _pressed;
        _pressed = default;
        if (!IsAlive(pressed) || Captured != pressed) return default;
        Captured = default;
        Invalidate(pressed, UiInvalidation.Paint);
        if (_slots[pressed.Index].Control == UiControlKind.Slider) return DragSlider(pressed, x);
        return CanInteract(pressed.Index) && HitTest(x, y) == pressed ? Activate(pressed) : default;
    }

    public UiAction KeyDown(UiKey key, bool shift = false, bool repeat = false)
    {
        ThrowIfDisposed();
        if (key == UiKey.Escape) { CancelPointer(); return default; }
        if (key == UiKey.Tab) { MoveFocus(shift); return default; }
        if (Focused.IsNull || !CanInteract(Focused.Index)) return default;
        var focused = Focused;
        if (key is UiKey.Enter or UiKey.Space) return repeat ? default : Activate(focused);
        if (_slots[focused.Index].Control == UiControlKind.Slider && key is UiKey.Left or UiKey.Right)
        {
            float before = GetValue(focused);
            SetValue(focused, Math.Clamp(before + (key == UiKey.Left ? -0.01f : 0.01f), 0, 1));
            return before == GetValue(focused) ? default : new(UiActionKind.ValueChanged, focused, GetValue(focused));
        }
        return default;
    }

    public void CancelPointer()
    {
        ThrowIfDisposed();
        if (IsAlive(_pressed)) Invalidate(_pressed, UiInvalidation.Paint);
        _pressed = Captured = default;
    }

    public bool IsPressed(UiNodeHandle node) => IsAlive(node) && _pressed == node && Captured == node;

    private UiAction Activate(UiNodeHandle node)
    {
        switch (_slots[node.Index].Control)
        {
            case UiControlKind.Button: return new(UiActionKind.Activated, node);
            case UiControlKind.Toggle:
                SetValue(node, 1 - GetValue(node));
                return new(UiActionKind.ValueChanged, node, GetValue(node));
            default: return default;
        }
    }

    private UiAction DragSlider(UiNodeHandle node, float x)
    {
        if (!float.IsFinite(x)) return default;
        var bounds = GetBounds(node);
        if (bounds.Width <= 0) return default;
        float previous = GetValue(node);
        SetValue(node, Math.Clamp((x - bounds.X) / bounds.Width, 0, 1));
        return previous == GetValue(node) ? default : new(UiActionKind.ValueChanged, node, GetValue(node));
    }

    private bool CanInteract(int index)
    {
        for (int ancestor = index; ancestor >= 0; ancestor = _slots[ancestor].Parent)
            if (!_slots[ancestor].Style.Visible || !_slots[ancestor].Style.Enabled) return false;
        return true;
    }

    private void MoveFocus(bool reverse)
    {
        UiNodeHandle first = default, last = default, previous = default, next = default;
        bool found = Focused.IsNull;
        // Keyboard traversal occurs only on a Tab event, never in Layout or PointerMove.
        for (int index = _firstRoot; index >= 0;)
        {
            var node = _slots[index];
            bool interactive = CanInteract(index);
            if (interactive && node.Style.Focusable)
            {
                var handle = Handle(index);
                if (first.IsNull) first = handle;
                if (handle == Focused) { previous = last; found = true; }
                else if (found && next.IsNull) next = handle;
                last = handle;
            }
            if (interactive && node.First >= 0) { index = node.First; continue; }
            while (index >= 0 && _slots[index].Next < 0) index = _slots[index].Parent;
            if (index >= 0) index = _slots[index].Next;
        }
        SetFocus(reverse ? (previous.IsNull ? last : previous) : (next.IsNull ? first : next));
    }
}
