// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.PaperUI;
using XEngine.Vector;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// Input sink for canvas pointer / keyboard / gamepad events. JS reports CSS pixels; this type
/// maps them to framebuffer pixels before updating <see cref="IInputHandler"/> state.
/// </summary>
public sealed class BrowserInputHandler : IInputHandler, ITouchInputHandler
{
    public const int PointerDown = 0;
    public const int PointerMove = 1;
    public const int PointerUp = 2;
    public const int PointerCancel = 3;

    private const int MaxKeyCode = 348;
    private const int MaxTouches = 16;
    private const int MaxMouseButtons = 5;
    private const int MaxGamepads = 4;
    private const int GamepadButtonCount = (int)GamepadButton.DPadLeft + 1;
    private const float GamepadDeadZone = 0.125f;

    private readonly bool[] _keys = new bool[MaxKeyCode + 1];
    private readonly bool[] _keyDown = new bool[MaxKeyCode + 1];
    private readonly bool[] _keyUp = new bool[MaxKeyCode + 1];
    private readonly bool[] _mouseHeld = new bool[MaxMouseButtons];
    private readonly bool[] _mouseDown = new bool[MaxMouseButtons];
    private readonly bool[] _mouseUp = new bool[MaxMouseButtons];
    private readonly TouchPoint[] _touches = new TouchPoint[MaxTouches];
    private readonly bool[] _touchEnded = new bool[MaxTouches];
    private readonly bool[] _gamepadConnected = new bool[MaxGamepads];
    private readonly bool[,] _gamepadButtons = new bool[MaxGamepads, GamepadButtonCount];
    private readonly bool[,] _gamepadButtonDown = new bool[MaxGamepads, GamepadButtonCount];
    private readonly bool[,] _gamepadButtonUp = new bool[MaxGamepads, GamepadButtonCount];
    private readonly Float2[] _gamepadAxes = new Float2[MaxGamepads * 2];
    private readonly float[] _gamepadTriggers = new float[MaxGamepads * 2];
    private readonly Func<string>? _clipboardGetter;
    private readonly Action<string>? _clipboardSetter;
    private int _touchCount;
    private int _primaryPointerId = -1;
    private Int2 _mousePosition;
    private Int2 _previousMousePosition;
    private float _mouseWheelDelta;
    private string _inputString = string.Empty;
    private int _framebufferWidth = 1;
    private int _framebufferHeight = 1;

    public BrowserInputHandler(Func<string>? clipboardGetter = null, Action<string>? clipboardSetter = null)
    {
        _clipboardGetter = clipboardGetter;
        _clipboardSetter = clipboardSetter;
    }

    public Float2 LastMappedPointer { get; private set; }

    public string Clipboard
    {
        get => _clipboardGetter?.Invoke() ?? string.Empty;
        set => _clipboardSetter?.Invoke(value);
    }

    public bool IsAnyKeyDown
    {
        get
        {
            for (int i = 0; i < _keys.Length; i++)
                if (_keys[i])
                    return true;
            return false;
        }
    }

    public Float2 MouseDelta
    {
        get
        {
            Int2 delta = _mousePosition - _previousMousePosition;
            return new Float2(delta.X, delta.Y);
        }
    }

    public Int2 MousePosition
    {
        get => _mousePosition;
        set
        {
            _previousMousePosition = _mousePosition;
            _mousePosition = value;
        }
    }

    public float MouseWheelDelta => _mouseWheelDelta;
    public Int2 PrevMousePosition => _previousMousePosition;
    public string InputString => _inputString;
    public event Action<KeyCode, bool>? OnKeyEvent;
    public event Action<MouseButton, float, float, bool, bool>? OnMouseEvent;
    public int TouchCount => _touchCount;

    public TouchPoint GetTouch(int index)
    {
        if ((uint)index >= (uint)_touchCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _touches[index];
    }

    public char? GetPressedChar()
    {
        if (string.IsNullOrEmpty(_inputString))
            return null;
        char value = _inputString[0];
        _inputString = _inputString.Length == 1 ? string.Empty : _inputString[1..];
        return value;
    }

    public bool GetKey(KeyCode key) => TryGetKeyIndex(key, out int index) && _keys[index];
    public bool GetKeyDown(KeyCode key) => TryGetKeyIndex(key, out int index) && _keyDown[index];
    public bool GetKeyUp(KeyCode key) => TryGetKeyIndex(key, out int index) && _keyUp[index];
    public bool GetMouseButton(int button) => IsMouseButton(button) && _mouseHeld[button];
    public bool GetMouseButtonDown(int button) => IsMouseButton(button) && _mouseDown[button];
    public bool GetMouseButtonUp(int button) => IsMouseButton(button) && _mouseUp[button];
    public void SetCursorVisible(bool visible, int miceIndex = 0) { }
    public void SetCursorShape(PaperCursor shape, int miceIndex = 0) { }

    public int GetGamepadCount()
    {
        int count = 0;
        for (int i = 0; i < MaxGamepads; i++)
            if (_gamepadConnected[i])
                count++;
        return count;
    }

    public bool IsGamepadConnected(int gamepadIndex) =>
        (uint)gamepadIndex < (uint)MaxGamepads && _gamepadConnected[gamepadIndex];

    public bool GetGamepadButton(int gamepadIndex, GamepadButton button) =>
        TryGetGamepadButtonIndex(gamepadIndex, button, out int buttonIndex) &&
        _gamepadButtons[gamepadIndex, buttonIndex];

    public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button) =>
        TryGetGamepadButtonIndex(gamepadIndex, button, out int buttonIndex) &&
        _gamepadButtonDown[gamepadIndex, buttonIndex];

    public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button) =>
        TryGetGamepadButtonIndex(gamepadIndex, button, out int buttonIndex) &&
        _gamepadButtonUp[gamepadIndex, buttonIndex];

    public Float2 GetGamepadAxis(int gamepadIndex, int axisIndex) =>
        IsGamepadConnected(gamepadIndex) && (uint)axisIndex < 2u
            ? _gamepadAxes[gamepadIndex * 2 + axisIndex]
            : Float2.Zero;

    public float GetGamepadTrigger(int gamepadIndex, int triggerIndex) =>
        IsGamepadConnected(gamepadIndex) && (uint)triggerIndex < 2u
            ? _gamepadTriggers[gamepadIndex * 2 + triggerIndex]
            : 0f;

    public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor) { }

    public void SetFramebufferSize(int width, int height)
    {
        _framebufferWidth = Math.Max(1, width);
        _framebufferHeight = Math.Max(1, height);
    }

    [HotPath]
    public void HandlePointer(
        int kind,
        int pointerId,
        float cssX,
        float cssY,
        float cssWidth,
        float cssHeight,
        int buttons,
        float pressure,
        bool isTouch)
    {
        BrowserInputCoordinateTransform transform = BrowserInputCoordinateTransform.Create(
            cssWidth,
            cssHeight,
            _framebufferWidth,
            _framebufferHeight);
        Float2 mapped = transform.Map(cssX, cssY);
        LastMappedPointer = mapped;
        float x = mapped.X;
        float y = mapped.Y;

        if (isTouch)
        {
            if (kind == PointerDown)
                TouchBegan(pointerId, x, y, pressure);
            else if (kind == PointerMove)
                TouchMoved(pointerId, x, y, pressure);
            else
                TouchEnded(pointerId, x, y, pressure, canceled: kind == PointerCancel);
            return;
        }

        SetMousePosition(x, y);
        ApplyMouseButtons(buttons, kind);
    }

    [HotPath]
    public void HandleWheel(float cssX, float cssY, float cssWidth, float cssHeight, float deltaY)
    {
        BrowserInputCoordinateTransform transform = BrowserInputCoordinateTransform.Create(
            cssWidth, cssHeight, _framebufferWidth, _framebufferHeight);
        Float2 mapped = transform.Map(cssX, cssY);
        LastMappedPointer = mapped;
        SetMousePosition(mapped.X, mapped.Y);
        // WheelEvent.deltaY is typically 100 per notch; expose a 1-notch = 1 unit scale.
        _mouseWheelDelta += deltaY == 0f ? 0f : -MathF.Sign(deltaY);
    }

    [HotPath]
    public bool HandleKey(string code, bool pressed, string? key = null)
    {
        if (!TryMapCode(code, out KeyCode mapped) || !TryGetKeyIndex(mapped, out int index))
            return false;

        if (pressed)
        {
            if (!_keys[index])
            {
                _keys[index] = true;
                _keyDown[index] = true;
                OnKeyEvent?.Invoke(mapped, true);
            }
            if (!string.IsNullOrEmpty(key) && key.Length == 1)
                _inputString += key;
        }
        else if (_keys[index])
        {
            _keys[index] = false;
            _keyUp[index] = true;
            OnKeyEvent?.Invoke(mapped, false);
        }

        return true;
    }

    [HotPath]
    public void HandleGamepad(
        int index,
        int buttonMask,
        float leftX,
        float leftY,
        float rightX,
        float rightY,
        float leftTrigger,
        float rightTrigger)
    {
        if ((uint)index >= (uint)MaxGamepads)
            return;
        _gamepadConnected[index] = true;
        int axisOffset = index * 2;
        _gamepadAxes[axisOffset] = new Float2(DeadZone(leftX), DeadZone(leftY));
        _gamepadAxes[axisOffset + 1] = new Float2(DeadZone(rightX), DeadZone(rightY));
        _gamepadTriggers[axisOffset] = Math.Clamp(leftTrigger, 0f, 1f);
        _gamepadTriggers[axisOffset + 1] = Math.Clamp(rightTrigger, 0f, 1f);
        SetMappedGamepadButton(index, GamepadButton.A, (buttonMask & 1) != 0);
        SetMappedGamepadButton(index, GamepadButton.B, (buttonMask & 2) != 0);
        SetMappedGamepadButton(index, GamepadButton.X, (buttonMask & 4) != 0);
        SetMappedGamepadButton(index, GamepadButton.Y, (buttonMask & 8) != 0);
        SetMappedGamepadButton(index, GamepadButton.LeftBumper, (buttonMask & 16) != 0);
        SetMappedGamepadButton(index, GamepadButton.RightBumper, (buttonMask & 32) != 0);
        SetMappedGamepadButton(index, GamepadButton.Back, (buttonMask & 256) != 0);
        SetMappedGamepadButton(index, GamepadButton.Start, (buttonMask & 512) != 0);
        SetMappedGamepadButton(index, GamepadButton.LeftStick, (buttonMask & 1024) != 0);
        SetMappedGamepadButton(index, GamepadButton.RightStick, (buttonMask & 2048) != 0);
        SetMappedGamepadButton(index, GamepadButton.DPadUp, (buttonMask & 4096) != 0);
        SetMappedGamepadButton(index, GamepadButton.DPadDown, (buttonMask & 8192) != 0);
        SetMappedGamepadButton(index, GamepadButton.DPadLeft, (buttonMask & 16384) != 0);
        SetMappedGamepadButton(index, GamepadButton.DPadRight, (buttonMask & 32768) != 0);
    }

    public void GamepadDisconnected(int index)
    {
        if ((uint)index >= (uint)MaxGamepads)
            return;
        _gamepadConnected[index] = false;
        for (int button = 0; button < GamepadButtonCount; button++)
        {
            if (_gamepadButtons[index, button])
            {
                _gamepadButtons[index, button] = false;
                _gamepadButtonUp[index, button] = true;
            }
        }
        int axisOffset = index * 2;
        _gamepadAxes[axisOffset] = Float2.Zero;
        _gamepadAxes[axisOffset + 1] = Float2.Zero;
        _gamepadTriggers[axisOffset] = 0f;
        _gamepadTriggers[axisOffset + 1] = 0f;
    }

    public void FocusLost()
    {
        for (int i = 0; i < _keys.Length; i++)
        {
            if (!_keys[i])
                continue;
            _keys[i] = false;
            _keyUp[i] = true;
            OnKeyEvent?.Invoke((KeyCode)i, false);
        }

        for (int i = 0; i < _touchCount; i++)
            _touchEnded[i] = true;
        _touchCount = 0;
        _primaryPointerId = -1;
        for (int button = 0; button < MaxMouseButtons; button++)
        {
            if (_mouseHeld[button])
                _mouseUp[button] = true;
            _mouseHeld[button] = false;
        }
    }

    [HotPath]
    public void EndFrame()
    {
        Array.Clear(_keyDown);
        Array.Clear(_keyUp);
        Array.Clear(_mouseDown);
        Array.Clear(_mouseUp);
        Array.Clear(_gamepadButtonDown);
        Array.Clear(_gamepadButtonUp);
        _mouseWheelDelta = 0f;
        _previousMousePosition = _mousePosition;
        _inputString = string.Empty;

        int write = 0;
        bool primaryStillActive = false;
        for (int read = 0; read < _touchCount; read++)
        {
            if (_touchEnded[read])
                continue;
            TouchPoint touch = _touches[read];
            primaryStillActive |= touch.PointerId == _primaryPointerId;
            _touches[write] = touch with
            {
                Phase = touch.Phase is TouchPhase.Began or TouchPhase.Moved
                    ? TouchPhase.Stationary
                    : touch.Phase,
            };
            _touchEnded[write] = false;
            write++;
        }
        _touchCount = write;
        if (!primaryStillActive)
            _primaryPointerId = -1;
    }

    [HotPath]
    private void TouchBegan(int pointerId, float x, float y, float pressure)
    {
        int index = FindTouch(pointerId);
        if (index < 0)
        {
            if (_touchCount == MaxTouches)
                return;
            index = _touchCount++;
        }
        _touchEnded[index] = false;
        _touches[index] = new TouchPoint(pointerId, new Float2(x, y), Math.Max(0f, pressure), TouchPhase.Began);
        if (_primaryPointerId < 0)
        {
            _primaryPointerId = pointerId;
            SetMousePosition(x, y);
            SetMouseButton(0, held: true, down: true, up: false);
        }
    }

    [HotPath]
    private void TouchMoved(int pointerId, float x, float y, float pressure)
    {
        int index = FindTouch(pointerId);
        if (index < 0)
            return;
        _touches[index] = new TouchPoint(pointerId, new Float2(x, y), Math.Max(0f, pressure), TouchPhase.Moved);
        if (pointerId == _primaryPointerId)
            SetMousePosition(x, y);
    }

    [HotPath]
    private void TouchEnded(int pointerId, float x, float y, float pressure, bool canceled)
    {
        int index = FindTouch(pointerId);
        if (index < 0)
            return;
        _touches[index] = new TouchPoint(
            pointerId,
            new Float2(x, y),
            Math.Max(0f, pressure),
            canceled ? TouchPhase.Canceled : TouchPhase.Ended);
        _touchEnded[index] = true;
        if (pointerId == _primaryPointerId)
        {
            SetMousePosition(x, y);
            SetMouseButton(0, held: false, down: false, up: true);
        }
    }

    private void ApplyMouseButtons(int buttons, int kind)
    {
        for (int button = 0; button < 3; button++)
        {
            bool held = (buttons & (1 << button)) != 0;
            bool wasHeld = _mouseHeld[button];
            if (kind == PointerDown && held && !wasHeld)
                SetMouseButton(button, held: true, down: true, up: false);
            else if ((kind == PointerUp || kind == PointerCancel) && !held && wasHeld)
                SetMouseButton(button, held: false, down: false, up: true);
            else if (kind == PointerMove && held != wasHeld)
                SetMouseButton(button, held, down: held, up: !held);
        }
    }

    private void SetMousePosition(float x, float y)
    {
        _previousMousePosition = _mousePosition;
        _mousePosition = new Int2((int)x, (int)y);
    }

    private void SetMouseButton(int button, bool held, bool down, bool up)
    {
        if (!IsMouseButton(button))
            return;
        _mouseHeld[button] = held;
        _mouseDown[button] |= down;
        _mouseUp[button] |= up;
        if (down || up)
            OnMouseEvent?.Invoke((MouseButton)button, _mousePosition.X, _mousePosition.Y, held, false);
    }

    private void SetMappedGamepadButton(int index, GamepadButton button, bool pressed)
    {
        int buttonIndex = (int)button;
        if ((uint)buttonIndex >= (uint)GamepadButtonCount)
            return;
        bool was = _gamepadButtons[index, buttonIndex];
        if (pressed == was)
            return;
        _gamepadButtons[index, buttonIndex] = pressed;
        if (pressed)
            _gamepadButtonDown[index, buttonIndex] = true;
        else
            _gamepadButtonUp[index, buttonIndex] = true;
    }

    private int FindTouch(int pointerId)
    {
        for (int i = 0; i < _touchCount; i++)
            if (_touches[i].PointerId == pointerId)
                return i;
        return -1;
    }

    private static bool IsMouseButton(int button) => (uint)button < (uint)MaxMouseButtons;

    private static bool TryGetKeyIndex(KeyCode key, out int index)
    {
        index = (int)key;
        return (uint)index <= MaxKeyCode;
    }

    private static bool TryGetGamepadButtonIndex(int gamepadIndex, GamepadButton button, out int buttonIndex)
    {
        buttonIndex = (int)button;
        return (uint)gamepadIndex < (uint)MaxGamepads && (uint)buttonIndex < (uint)GamepadButtonCount;
    }

    private static float DeadZone(float value) =>
        MathF.Abs(value) < GamepadDeadZone ? 0f : Math.Clamp(value, -1f, 1f);

    internal static bool TryMapCode(string code, out KeyCode key)
    {
        key = KeyCode.Unknown;
        if (string.IsNullOrEmpty(code))
            return false;
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z')
        {
            key = (KeyCode)code[3];
            return true;
        }
        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9')
        {
            key = (KeyCode)code[5];
            return true;
        }

        key = code switch
        {
            "Space" => KeyCode.Space,
            "Enter" => KeyCode.Enter,
            "Escape" => KeyCode.Escape,
            "Tab" => KeyCode.Tab,
            "Backspace" => KeyCode.Backspace,
            "Delete" => KeyCode.Delete,
            "Insert" => KeyCode.Insert,
            "ArrowLeft" => KeyCode.Left,
            "ArrowRight" => KeyCode.Right,
            "ArrowUp" => KeyCode.Up,
            "ArrowDown" => KeyCode.Down,
            "Home" => KeyCode.Home,
            "End" => KeyCode.End,
            "PageUp" => KeyCode.PageUp,
            "PageDown" => KeyCode.PageDown,
            "ShiftLeft" => KeyCode.ShiftLeft,
            "ShiftRight" => KeyCode.ShiftRight,
            "ControlLeft" => KeyCode.ControlLeft,
            "ControlRight" => KeyCode.ControlRight,
            "AltLeft" => KeyCode.AltLeft,
            "AltRight" => KeyCode.AltRight,
            "Minus" => KeyCode.Minus,
            "Equal" => KeyCode.Equal,
            "Comma" => KeyCode.Comma,
            "Period" => KeyCode.Period,
            "Slash" => KeyCode.Slash,
            "Semicolon" => KeyCode.Semicolon,
            "Quote" => KeyCode.Apostrophe,
            "Backquote" => KeyCode.GraveAccent,
            "BracketLeft" => KeyCode.LeftBracket,
            "BracketRight" => KeyCode.RightBracket,
            "F1" => KeyCode.F1,
            "F2" => KeyCode.F2,
            "F3" => KeyCode.F3,
            "F4" => KeyCode.F4,
            "F5" => KeyCode.F5,
            "F6" => KeyCode.F6,
            "F7" => KeyCode.F7,
            "F8" => KeyCode.F8,
            "F9" => KeyCode.F9,
            "F10" => KeyCode.F10,
            "F11" => KeyCode.F11,
            "F12" => KeyCode.F12,
            _ => KeyCode.Unknown,
        };
        return key != KeyCode.Unknown;
    }
}
