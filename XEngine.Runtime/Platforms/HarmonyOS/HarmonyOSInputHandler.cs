// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.PaperUI;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Runtime.Platforms.HarmonyOS;

/// <summary>Touch action values shared by the native XComponent host and managed player ABI.</summary>
public enum HarmonyOSTouchAction
{
    Down = 0,
    Up = 1,
    Move = 2,
    Cancel = 3,
}

/// <summary>Mouse action values shared by the native XComponent host and managed player ABI.</summary>
public enum HarmonyOSMouseAction
{
    Press = 1,
    Release = 2,
    Move = 3,
    Cancel = 4,
}

/// <summary>Fixed-capacity player-thread input sink for normalized HarmonyOS host callbacks.</summary>
public sealed class HarmonyOSInputHandler : IInputHandler, ITouchInputHandler, ITextInputService
{
    private const int MaxKeyCode = 348;
    private const int MaxTouches = 16;
    private const int MaxMouseButtons = 5;
    private readonly bool[] _keys = new bool[MaxKeyCode + 1];
    private readonly bool[] _keyDown = new bool[MaxKeyCode + 1];
    private readonly bool[] _keyUp = new bool[MaxKeyCode + 1];
    private readonly TouchPoint[] _touches = new TouchPoint[MaxTouches];
    private readonly bool[] _touchEnded = new bool[MaxTouches];
    private readonly TouchPoint[] _pendingTouches = new TouchPoint[MaxTouches];
    private readonly bool[] _hasPendingTouch = new bool[MaxTouches];
    private readonly Func<string>? _clipboardGetter;
    private readonly Action<string>? _clipboardSetter;
    private readonly Action<bool>? _softKeyboard;
    private int _touchCount;
    private int _primaryPointerId = -1;
    private Int2 _mousePosition;
    private Int2 _previousMousePosition;
    private readonly bool[] _physicalMouseHeld = new bool[MaxMouseButtons];
    private readonly bool[] _physicalMouseDown = new bool[MaxMouseButtons];
    private readonly bool[] _physicalMouseUp = new bool[MaxMouseButtons];
    private bool _touchMouseHeld;
    private bool _touchMouseDown;
    private bool _touchMouseUp;
    private float _mouseWheelDelta;
    private string _inputString = string.Empty;
    private TextInputState _textInputState = new(string.Empty, string.Empty, 0, 0, false);
    public TouchScreenKeyboardType LastKeyboardType { get; private set; }
    public bool LastSecureTextEntry { get; private set; }
    public int LastHarmonyOSInputType { get; private set; } = InputFieldContent.HarmonyOSText;
    public bool LastTextInputActive => _textInputState.IsActive;

    public HarmonyOSInputHandler(Func<string>? clipboardGetter = null, Action<string>? clipboardSetter = null, Action<bool>? softKeyboard = null)
    {
        _clipboardGetter = clipboardGetter;
        _clipboardSetter = clipboardSetter;
        _softKeyboard = softKeyboard;
    }

    public string Clipboard { get => _clipboardGetter?.Invoke() ?? string.Empty; set => _clipboardSetter?.Invoke(value); }
    public bool IsAnyKeyDown { get { for (int i = 0; i < _keys.Length; i++) if (_keys[i]) return true; return false; } }
    public Float2 MouseDelta { get { Int2 delta = _mousePosition - _previousMousePosition; return new Float2(delta.X, delta.Y); } }
    public Int2 MousePosition { get => _mousePosition; set { _previousMousePosition = _mousePosition; _mousePosition = value; } }
    public float MouseWheelDelta => _mouseWheelDelta;
    public Int2 PrevMousePosition => _previousMousePosition;
    public string InputString => _inputString;
    public TextInputState TextInputState => _textInputState;
    public bool IsTextInputSupported => _softKeyboard != null;
    public int TouchCount => _touchCount;
    public event Action<KeyCode, bool>? OnKeyEvent;
    public event Action<MouseButton, float, float, bool, bool>? OnMouseEvent;
    public event Action<TextInputState>? OnTextInput;

    public TouchPoint GetTouch(int index) => (uint)index < (uint)_touchCount ? _touches[index] : throw new ArgumentOutOfRangeException(nameof(index));
    public bool GetKey(KeyCode key) => TryGetKeyIndex(key, out int index) && _keys[index];
    public bool GetKeyDown(KeyCode key) => TryGetKeyIndex(key, out int index) && _keyDown[index];
    public bool GetKeyUp(KeyCode key) => TryGetKeyIndex(key, out int index) && _keyUp[index];
    public bool GetMouseButton(int button) => (uint)button < MaxMouseButtons && (_physicalMouseHeld[button] || (button == 0 && _touchMouseHeld));
    public bool GetMouseButtonDown(int button) => (uint)button < MaxMouseButtons && (_physicalMouseDown[button] || (button == 0 && _touchMouseDown));
    public bool GetMouseButtonUp(int button) => (uint)button < MaxMouseButtons && (_physicalMouseUp[button] || (button == 0 && _touchMouseUp));
    public void SetCursorVisible(bool visible, int miceIndex = 0) { }
    public void SetCursorShape(PaperCursor shape, int miceIndex = 0) { }
    public int GetGamepadCount() => 0;
    public bool IsGamepadConnected(int gamepadIndex) => false;
    public bool GetGamepadButton(int gamepadIndex, GamepadButton button) => false;
    public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button) => false;
    public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button) => false;
    public Float2 GetGamepadAxis(int gamepadIndex, int axisIndex) => Float2.Zero;
    public float GetGamepadTrigger(int gamepadIndex, int triggerIndex) => 0f;
    public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor) { }

    public void SetTextInputEnabled(bool enabled)
    {
        _textInputState = _textInputState with { IsActive = enabled };
        _softKeyboard?.Invoke(enabled);
        OnTextInput?.Invoke(_textInputState);
    }

    public void SetTextInputOptions(TouchScreenKeyboardType keyboardType, bool secure)
    {
        LastKeyboardType = keyboardType;
        LastSecureTextEntry = secure;
        LastHarmonyOSInputType = InputFieldContent.HarmonyOSInputType(keyboardType, secure);
        if (_textInputState.IsActive)
            _softKeyboard?.Invoke(true);
    }

    public void SetTextInputState(string text, int selectionStart, int selectionLength)
    {
        text ??= string.Empty;
        int start = Math.Clamp(selectionStart, 0, text.Length);
        int end = Math.Clamp(start + Math.Max(0, selectionLength), start, text.Length);
        _textInputState = new TextInputState(text, string.Empty, start, end - start, _textInputState.IsActive);
        OnTextInput?.Invoke(_textInputState);
    }

    public char? GetPressedChar()
    {
        if (_inputString.Length == 0) return null;
        char value = _inputString[0];
        _inputString = _inputString.Length == 1 ? string.Empty : _inputString[1..];
        return value;
    }

    public void CommitText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _inputString += text;
        _textInputState = _textInputState with { Text = _textInputState.Text + text, Composition = string.Empty, SelectionStart = _textInputState.Text.Length + text.Length, SelectionLength = 0 };
        OnTextInput?.Invoke(_textInputState);
    }

    [HotPath]
    public bool HandleKey(int harmonyOsKeyCode, bool pressed)
    {
        if (!TryMapKey(harmonyOsKeyCode, out KeyCode key) || !TryGetKeyIndex(key, out int index)) return false;
        if (pressed) { if (_keys[index]) return true; _keys[index] = true; _keyDown[index] = true; }
        else { if (!_keys[index]) return true; _keys[index] = false; _keyUp[index] = true; }
        OnKeyEvent?.Invoke(key, pressed);
        return true;
    }

    [HotPath]
    public bool HandleMouse(HarmonyOSMouseAction action, int harmonyOsButton, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
        switch (action)
        {
            case HarmonyOSMouseAction.Move:
                SetMousePosition(x, y);
                return true;
            case HarmonyOSMouseAction.Press:
                if (!TryMapMouseButton(harmonyOsButton, out int pressedButton)) return false;
                SetPhysicalMouse(pressedButton, x, y, held: true, down: true, up: false);
                return true;
            case HarmonyOSMouseAction.Release:
                if (!TryMapMouseButton(harmonyOsButton, out int releasedButton)) return false;
                SetPhysicalMouse(releasedButton, x, y, held: false, down: false, up: true);
                return true;
            case HarmonyOSMouseAction.Cancel:
                if (TryMapMouseButton(harmonyOsButton, out int canceledButton))
                    SetPhysicalMouse(canceledButton, x, y, held: false, down: false, up: true);
                else if (harmonyOsButton == 0)
                    ReleasePhysicalMouseButtons(x, y);
                else
                    return false;
                return true;
            default:
                return false;
        }
    }

    [HotPath]
    public bool HandleMouseWheel(float delta)
    {
        if (!float.IsFinite(delta)) return false;
        float sum = _mouseWheelDelta + delta;
        _mouseWheelDelta = float.IsFinite(sum)
            ? sum
            : delta < 0f ? -float.MaxValue : float.MaxValue;
        return true;
    }

    [HotPath]
    public void TouchBegan(int pointerId, float x, float y, float pressure)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(pressure)) return;
        int index = FindTouch(pointerId);
        if (index < 0) { if (_touchCount == MaxTouches) return; index = _touchCount++; }
        _touchEnded[index] = false;
        _hasPendingTouch[index] = false;
        _touches[index] = new TouchPoint(pointerId, new Float2(x, y), Math.Max(0f, pressure), TouchPhase.Began);
        if (_primaryPointerId < 0) { _primaryPointerId = pointerId; SetTouchMouse(x, y, true, true, false); }
    }

    [HotPath]
    public void TouchMoved(int pointerId, float x, float y, float pressure)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(pressure)) return;
        int index = FindTouch(pointerId); if (index < 0) return;
        PublishTouchSample(index, new TouchPoint(pointerId, new Float2(x, y), Math.Max(0f, pressure), TouchPhase.Moved));
        if (pointerId == _primaryPointerId) SetTouchMouse(x, y, _touchMouseHeld, false, false);
    }

    [HotPath]
    public void TouchEnded(int pointerId, float x, float y, float pressure, bool canceled = false)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(pressure)) return;
        int index = FindTouch(pointerId); if (index < 0) return;
        PublishTouchSample(index, new TouchPoint(pointerId, new Float2(x, y), Math.Max(0f, pressure), canceled ? TouchPhase.Canceled : TouchPhase.Ended));
        if (pointerId == _primaryPointerId) SetTouchMouse(x, y, false, false, true);
    }

    public void FocusLost()
    {
        Array.Clear(_hasPendingTouch);
        for (int i = 0; i < _keys.Length; i++) if (_keys[i]) { _keys[i] = false; _keyUp[i] = true; OnKeyEvent?.Invoke((KeyCode)i, false); }
        for (int i = 0; i < _touchCount; i++) { TouchPoint touch = _touches[i]; _touches[i] = touch with { Phase = TouchPhase.Canceled }; _touchEnded[i] = true; }
        _primaryPointerId = -1;
        bool wasTouchHeld = _touchMouseHeld;
        SetTouchMouse(_mousePosition.X, _mousePosition.Y, false, false, wasTouchHeld);
        ReleasePhysicalMouseButtons(_mousePosition.X, _mousePosition.Y);
    }

    [HotPath]
    public void EndFrame()
    {
        Array.Clear(_keyDown); Array.Clear(_keyUp); Array.Clear(_physicalMouseDown); Array.Clear(_physicalMouseUp); _touchMouseDown = false; _touchMouseUp = false; _mouseWheelDelta = 0f; _previousMousePosition = _mousePosition; _inputString = string.Empty;
        int write = 0;
        for (int read = 0; read < _touchCount; read++)
        {
            if (_touchEnded[read]) continue;
            TouchPoint touch = _touches[read];
            TouchPoint next = _hasPendingTouch[read] ? _pendingTouches[read]
                : touch with { Phase = touch.Phase is TouchPhase.Began or TouchPhase.Moved ? TouchPhase.Stationary : touch.Phase };
            _touches[write] = next;
            _touchEnded[write++] = next.Phase is TouchPhase.Ended or TouchPhase.Canceled;
        }
        Array.Clear(_hasPendingTouch);
        _touchCount = write;
        if (FindTouch(_primaryPointerId) < 0) _primaryPointerId = -1;
    }

    [HotPath]
    private void PublishTouchSample(int index, TouchPoint sample)
    {
        // Native callbacks are drained in batches before UI.Update. Preserve the press and
        // its origin for one frame so a coalesced move/up cannot erase the entire gesture.
        if (_touches[index].Phase == TouchPhase.Began)
        {
            _pendingTouches[index] = sample;
            _hasPendingTouch[index] = true;
            return;
        }
        _touches[index] = sample;
        _touchEnded[index] = sample.Phase is TouchPhase.Ended or TouchPhase.Canceled;
    }

    [HotPath]
    private void SetTouchMouse(float x, float y, bool held, bool down, bool up)
    {
        SetMousePosition(x, y); _touchMouseHeld = held; _touchMouseDown |= down; _touchMouseUp |= up;
        if (down || up) OnMouseEvent?.Invoke(MouseButton.Left, x, y, held, false);
    }

    [HotPath]
    private void SetPhysicalMouse(int button, float x, float y, bool held, bool down, bool up)
    {
        SetMousePosition(x, y); _physicalMouseHeld[button] = held; _physicalMouseDown[button] |= down; _physicalMouseUp[button] |= up;
        if (down || up) OnMouseEvent?.Invoke((MouseButton)button, x, y, held, false);
    }

    [HotPath]
    private void SetMousePosition(float x, float y)
    {
        _previousMousePosition = _mousePosition;
        _mousePosition = new Int2((int)x, (int)y);
    }

    private void ReleasePhysicalMouseButtons(float x, float y)
    {
        for (int button = 0; button < MaxMouseButtons; button++)
        {
            if (!_physicalMouseHeld[button]) continue;
            _physicalMouseHeld[button] = false;
            _physicalMouseUp[button] = true;
            OnMouseEvent?.Invoke((MouseButton)button, x, y, false, false);
        }
    }

    private static bool TryMapMouseButton(int harmonyOsButton, out int button)
    {
        button = harmonyOsButton switch
        {
            1 => (int)MouseButton.Left,
            2 => (int)MouseButton.Right,
            4 => (int)MouseButton.Middle,
            8 => (int)MouseButton.Button4,
            16 => (int)MouseButton.Button5,
            _ => -1,
        };
        return button >= 0;
    }
    private int FindTouch(int pointerId) { for (int i = 0; i < _touchCount; i++) if (_touches[i].PointerId == pointerId) return i; return -1; }
    private static bool TryGetKeyIndex(KeyCode key, out int index) { index = (int)key; return (uint)index <= MaxKeyCode; }

    private static bool TryMapKey(int harmonyOsKeyCode, out KeyCode key)
    {
        if ((uint)(harmonyOsKeyCode - 2017) <= 25)
        {
            key = (KeyCode)((int)KeyCode.A + harmonyOsKeyCode - 2017);
            return true;
        }
        if ((uint)(harmonyOsKeyCode - 2000) <= 9)
        {
            key = (KeyCode)((int)KeyCode.Number0 + harmonyOsKeyCode - 2000);
            return true;
        }

        key = harmonyOsKeyCode switch
        {
            2 => KeyCode.Escape,
            2012 => KeyCode.Up,
            2013 => KeyCode.Down,
            2014 => KeyCode.Left,
            2015 => KeyCode.Right,
            2016 or 2054 => KeyCode.Enter,
            2043 => KeyCode.Comma,
            2044 => KeyCode.Period,
            2049 => KeyCode.Tab,
            2050 => KeyCode.Space,
            2055 => KeyCode.Backspace,
            2056 => KeyCode.GraveAccent,
            2057 => KeyCode.Minus,
            2058 => KeyCode.Equal,
            2059 => KeyCode.LeftBracket,
            2060 => KeyCode.RightBracket,
            2061 => KeyCode.BackSlash,
            2062 => KeyCode.Semicolon,
            2063 => KeyCode.Apostrophe,
            2064 => KeyCode.Slash,
            2066 => KeyCode.Equal,
            2067 => KeyCode.Menu,
            2068 => KeyCode.PageUp,
            2069 => KeyCode.PageDown,
            2070 => KeyCode.Escape,
            2071 => KeyCode.Delete,
            2045 => KeyCode.AltLeft,
            2046 => KeyCode.AltRight,
            2047 => KeyCode.ShiftLeft,
            2048 => KeyCode.ShiftRight,
            2072 => KeyCode.ControlLeft,
            2073 => KeyCode.ControlRight,
            2074 => KeyCode.CapsLock,
            2075 => KeyCode.ScrollLock,
            2076 => KeyCode.SuperLeft,
            2077 => KeyCode.SuperRight,
            2079 => KeyCode.PrintScreen,
            2080 => KeyCode.Pause,
            2081 => KeyCode.Home,
            2082 => KeyCode.End,
            2083 => KeyCode.Insert,
            >= 2090 and <= 2101 => (KeyCode)((int)KeyCode.F1 + harmonyOsKeyCode - 2090),
            2102 => KeyCode.NumLock,
            >= 2103 and <= 2112 => (KeyCode)((int)KeyCode.Keypad0 + harmonyOsKeyCode - 2103),
            2113 => KeyCode.KeypadDivide,
            2114 => KeyCode.KeypadMultiply,
            2115 => KeyCode.KeypadSubtract,
            2116 => KeyCode.KeypadAdd,
            2117 or 2118 => KeyCode.KeypadDecimal,
            2119 => KeyCode.KeypadEnter,
            2120 => KeyCode.KeypadEqual,
            >= 2816 and <= 2827 => (KeyCode)((int)KeyCode.F13 + harmonyOsKeyCode - 2816),
            _ => KeyCode.Unknown,
        };
        return key != KeyCode.Unknown;
    }
}
