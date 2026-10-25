// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Runtime.Platforms.Android;

/// <summary>
/// Platform-neutral state sink for Android input callbacks. Android bindings feed it integer
/// key/pointer values; the fixed arrays keep steady-state frame queries allocation-free.
/// </summary>
public sealed class AndroidInputHandler : IInputHandler, ITouchHistoryInputHandler, ITextInputService
{
    private const int MaxKeyCode = 348;
    private const int MaxTouches = 16;
    private const int MaxTouchHistorySamples = 8;
    private const int MaxGamepads = 4;
    private const int GamepadButtonCount = (int)GamepadButton.DPadLeft + 1;
    private const float GamepadDeadZone = 0.125f;
    private const float DPadAxisThreshold = 0.5f;
    private readonly bool[] _keys = new bool[MaxKeyCode + 1];
    private readonly bool[] _keyDown = new bool[MaxKeyCode + 1];
    private readonly bool[] _keyUp = new bool[MaxKeyCode + 1];
    private readonly TouchPoint[] _touches = new TouchPoint[MaxTouches];
    private readonly bool[] _touchEnded = new bool[MaxTouches];
    private readonly TouchHistorySample[] _touchHistory = new TouchHistorySample[MaxTouches * MaxTouchHistorySamples];
    private readonly int[] _touchHistoryCounts = new int[MaxTouches];
    private readonly int[] _gamepadDeviceIds = new int[MaxGamepads];
    private readonly bool[,] _gamepadButtons = new bool[MaxGamepads, GamepadButtonCount];
    private readonly bool[,] _gamepadButtonDown = new bool[MaxGamepads, GamepadButtonCount];
    private readonly bool[,] _gamepadButtonUp = new bool[MaxGamepads, GamepadButtonCount];
    private readonly Float2[] _gamepadAxes = new Float2[MaxGamepads * 2];
    private readonly float[] _gamepadTriggers = new float[MaxGamepads * 2];
    private readonly Func<string>? _clipboardGetter;
    private readonly Action<string>? _clipboardSetter;
    private readonly Action<bool>? _softKeyboard;
    private int _touchCount;
    private int _gamepadCount;
    private int _primaryPointerId = -1;
    private Int2 _mousePosition;
    private Int2 _previousMousePosition;
    private bool _mouseHeld;
    private bool _mouseDown;
    private bool _mouseUp;
    private string _inputString = string.Empty;
    private TextInputState _textInputState = new(string.Empty, string.Empty, 0, 0, false);
    public TouchScreenKeyboardType LastKeyboardType { get; private set; }
    public bool LastSecureTextEntry { get; private set; }

    public AndroidInputHandler(
        Func<string>? clipboardGetter = null,
        Action<string>? clipboardSetter = null,
        Action<bool>? softKeyboard = null)
    {
        _clipboardGetter = clipboardGetter;
        _clipboardSetter = clipboardSetter;
        _softKeyboard = softKeyboard;
        Array.Fill(_gamepadDeviceIds, -1);
    }

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

    public float MouseWheelDelta => 0f;
    public Int2 PrevMousePosition => _previousMousePosition;
    public string InputString => _inputString;
    public event Action<KeyCode, bool>? OnKeyEvent;
    public event Action<MouseButton, float, float, bool, bool>? OnMouseEvent;
    public event Action<TextInputState>? OnTextInput;

    public TextInputState TextInputState => _textInputState;
    public bool IsTextInputSupported => _softKeyboard != null;
    public int TouchCount => _touchCount;

    public TouchPoint GetTouch(int index)
    {
        if ((uint)index >= (uint)_touchCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _touches[index];
    }

    public int GetTouchHistoryCount(int touchIndex)
    {
        if ((uint)touchIndex >= (uint)_touchCount)
            throw new ArgumentOutOfRangeException(nameof(touchIndex));
        return _touchHistoryCounts[touchIndex];
    }

    public TouchHistorySample GetTouchHistory(int touchIndex, int historyIndex)
    {
        int count = GetTouchHistoryCount(touchIndex);
        if ((uint)historyIndex >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(historyIndex));
        return _touchHistory[touchIndex * MaxTouchHistorySamples + historyIndex];
    }

    public void SetTextInputEnabled(bool enabled)
    {
        _textInputState = _textInputState with { IsActive = enabled };
        _softKeyboard?.Invoke(enabled);
        OnTextInput?.Invoke(_textInputState);
    }

    public int LastAndroidInputType { get; private set; } = InputFieldContent.AndroidClassText;

    public void SetTextInputOptions(TouchScreenKeyboardType keyboardType, bool secure)
    {
        LastKeyboardType = keyboardType;
        LastSecureTextEntry = secure;
        LastAndroidInputType = InputFieldContent.AndroidInputType(keyboardType, secure);
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
        if (string.IsNullOrEmpty(_inputString))
            return null;
        char value = _inputString[0];
        _inputString = _inputString.Length == 1 ? string.Empty : _inputString[1..];
        return value;
    }

    public bool GetKey(KeyCode key) => TryGetKeyIndex(key, out int index) && _keys[index];
    public bool GetKeyDown(KeyCode key) => TryGetKeyIndex(key, out int index) && _keyDown[index];
    public bool GetKeyUp(KeyCode key) => TryGetKeyIndex(key, out int index) && _keyUp[index];
    public bool GetMouseButton(int button) => button == 0 && _mouseHeld;
    public bool GetMouseButtonDown(int button) => button == 0 && _mouseDown;
    public bool GetMouseButtonUp(int button) => button == 0 && _mouseUp;
    public void SetCursorVisible(bool visible, int miceIndex = 0) { }
    public void SetCursorShape(PaperUI.PaperCursor shape, int miceIndex = 0) { }
    public int GetGamepadCount() => _gamepadCount;

    public bool IsGamepadConnected(int gamepadIndex) =>
        (uint)gamepadIndex < (uint)_gamepadCount;

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
        (uint)gamepadIndex < (uint)_gamepadCount && (uint)axisIndex < 2u
            ? _gamepadAxes[gamepadIndex * 2 + axisIndex]
            : Float2.Zero;

    public float GetGamepadTrigger(int gamepadIndex, int triggerIndex) =>
        (uint)gamepadIndex < (uint)_gamepadCount && (uint)triggerIndex < 2u
            ? _gamepadTriggers[gamepadIndex * 2 + triggerIndex]
            : 0f;

    public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor) { }

    [HotPath]
    public bool HandleKey(int androidKeyCode, bool pressed)
    {
        if (!TryMapKey(androidKeyCode, out KeyCode key) || !TryGetKeyIndex(key, out int index))
            return false;

        if (pressed)
        {
            if (_keys[index])
                return true;
            _keys[index] = true;
            _keyDown[index] = true;
        }
        else
        {
            if (!_keys[index])
                return true;
            _keys[index] = false;
            _keyUp[index] = true;
        }

        OnKeyEvent?.Invoke(key, pressed);
        return true;
    }

    public void HandleBack()
    {
        HandleKey(4, true);
        HandleKey(4, false);
    }

    /// <summary>Registers an Android controller device before it sends key or motion input.</summary>
    public bool GamepadConnected(int androidDeviceId) => GetOrCreateGamepadSlot(androidDeviceId) >= 0;

    /// <summary>Clears and removes an Android controller device after a hot-unplug event.</summary>
    public void GamepadDisconnected(int androidDeviceId)
    {
        int slot = FindGamepadSlot(androidDeviceId);
        if (slot < 0)
            return;

        for (int current = slot; current + 1 < _gamepadCount; current++)
            CopyGamepadSlot(current + 1, current);
        _gamepadCount--;
        ClearGamepadSlot(_gamepadCount);
    }

    [HotPath]
    public bool HandleGamepadKey(int androidDeviceId, int androidKeyCode, bool pressed)
    {
        if (!TryMapGamepadButton(androidKeyCode, out GamepadButton button))
            return false;

        int slot = GetOrCreateGamepadSlot(androidDeviceId);
        if (slot < 0)
            return false;
        SetGamepadButton(slot, button, pressed);
        return true;
    }

    [HotPath]
    public bool HandleGamepadMotion(
        int androidDeviceId,
        float leftX,
        float leftY,
        float rightX,
        float rightY,
        float leftTrigger,
        float rightTrigger,
        float hatX,
        float hatY)
    {
        int slot = GetOrCreateGamepadSlot(androidDeviceId);
        if (slot < 0)
            return false;

        int axisOffset = slot * 2;
        _gamepadAxes[axisOffset] = new Float2(NormalizeStick(leftX), -NormalizeStick(leftY));
        _gamepadAxes[axisOffset + 1] = new Float2(NormalizeStick(rightX), -NormalizeStick(rightY));
        _gamepadTriggers[axisOffset] = NormalizeTrigger(leftTrigger);
        _gamepadTriggers[axisOffset + 1] = NormalizeTrigger(rightTrigger);
        SetGamepadButton(slot, GamepadButton.DPadLeft, hatX <= -DPadAxisThreshold);
        SetGamepadButton(slot, GamepadButton.DPadRight, hatX >= DPadAxisThreshold);
        SetGamepadButton(slot, GamepadButton.DPadUp, hatY <= -DPadAxisThreshold);
        SetGamepadButton(slot, GamepadButton.DPadDown, hatY >= DPadAxisThreshold);
        return true;
    }

    [HotPath]
    public void TouchBegan(int pointerId, float x, float y, float pressure)
    {
        int index = FindTouch(pointerId);
        if (index < 0)
        {
            if (_touchCount == MaxTouches)
                return;
            index = _touchCount++;
        }
        _touchEnded[index] = false;
        _touchHistoryCounts[index] = 0;
        _touches[index] = new TouchPoint(pointerId, new Float2(x, y), Math.Max(0f, pressure), TouchPhase.Began);
        if (_primaryPointerId < 0)
        {
            _primaryPointerId = pointerId;
            SetMouse(x, y, true, true, false);
        }
    }

    [HotPath]
    public void TouchMoved(int pointerId, float x, float y, float pressure)
    {
        int index = FindTouch(pointerId);
        if (index < 0)
            return;
        _touches[index] = new TouchPoint(pointerId, new Float2(x, y), Math.Max(0f, pressure), TouchPhase.Moved);
        if (pointerId == _primaryPointerId)
            SetMouse(x, y, _mouseHeld, false, false);
    }

    /// <summary>
    /// Adds a coalesced Android motion sample for an active pointer. The newest eight samples are
    /// retained for the current frame so velocity-sensitive callers can avoid losing high-rate input.
    /// </summary>
    [HotPath]
    public void TouchHistory(int pointerId, float x, float y, float pressure, long eventTimeMilliseconds)
    {
        int index = FindTouch(pointerId);
        if (index < 0)
            return;

        int offset = index * MaxTouchHistorySamples;
        int count = _touchHistoryCounts[index];
        if (count == MaxTouchHistorySamples)
        {
            for (int i = 1; i < MaxTouchHistorySamples; i++)
                _touchHistory[offset + i - 1] = _touchHistory[offset + i];
            count--;
        }

        _touchHistory[offset + count] = new TouchHistorySample(
            pointerId,
            new Float2(x, y),
            Math.Max(0f, pressure),
            eventTimeMilliseconds);
        _touchHistoryCounts[index] = count + 1;
    }

    [HotPath]
    public void TouchEnded(int pointerId, float x, float y, float pressure, bool canceled = false)
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
            SetMouse(x, y, false, false, true);
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

        for (int gamepad = 0; gamepad < _gamepadCount; gamepad++)
        {
            for (int button = 0; button < GamepadButtonCount; button++)
            {
                if (_gamepadButtons[gamepad, button])
                {
                    _gamepadButtons[gamepad, button] = false;
                    _gamepadButtonUp[gamepad, button] = true;
                }
            }
            int axisOffset = gamepad * 2;
            _gamepadAxes[axisOffset] = Float2.Zero;
            _gamepadAxes[axisOffset + 1] = Float2.Zero;
            _gamepadTriggers[axisOffset] = 0f;
            _gamepadTriggers[axisOffset + 1] = 0f;
        }

        for (int i = 0; i < _touchCount; i++)
        {
            _touchEnded[i] = true;
            _touchHistoryCounts[i] = 0;
        }
        _touchCount = 0;
        _primaryPointerId = -1;
        bool wasHeld = _mouseHeld;
        SetMouse(_mousePosition.X, _mousePosition.Y, false, false, wasHeld);
        _textInputState = _textInputState with { Composition = string.Empty };
    }

    /// <summary>Commits text from IME without synthesizing a physical key event.</summary>
    public void CommitText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _inputString += text;
        int selectionStart = Math.Clamp(_textInputState.SelectionStart, 0, _textInputState.Text.Length);
        int selectionLength = Math.Clamp(
            _textInputState.SelectionLength,
            0,
            _textInputState.Text.Length - selectionStart);
        string committedText = _textInputState.Text
            .Remove(selectionStart, selectionLength)
            .Insert(selectionStart, text);
        _textInputState = _textInputState with
        {
            Text = committedText,
            Composition = string.Empty,
            SelectionStart = selectionStart + text.Length,
            SelectionLength = 0,
        };
        OnTextInput?.Invoke(_textInputState);
    }

    public void SetComposingText(string text, int cursorPosition)
    {
        text ??= string.Empty;
        _textInputState = _textInputState with
        {
            Composition = text,
        };
        OnTextInput?.Invoke(_textInputState);
    }

    public void DeleteSurroundingText(int beforeLength, int afterLength)
    {
        int selectionStart = Math.Clamp(_textInputState.SelectionStart, 0, _textInputState.Text.Length);
        int selectionEnd = Math.Clamp(
            selectionStart + _textInputState.SelectionLength,
            selectionStart,
            _textInputState.Text.Length);
        int start = Math.Max(0, selectionStart - Math.Max(0, beforeLength));
        int end = Math.Min(_textInputState.Text.Length, selectionEnd + Math.Max(0, afterLength));
        if (end == start)
            return;
        _textInputState = _textInputState with
        {
            Text = _textInputState.Text.Remove(start, end - start),
            SelectionStart = start,
            SelectionLength = 0,
        };
        OnTextInput?.Invoke(_textInputState);
    }

    public void SetSelection(int start, int length)
    {
        int end = start + length;
        int clampedStart = Math.Clamp(Math.Min(start, end), 0, _textInputState.Text.Length);
        int clampedEnd = Math.Clamp(Math.Max(start, end), clampedStart, _textInputState.Text.Length);
        _textInputState = _textInputState with
        {
            SelectionStart = clampedStart,
            SelectionLength = clampedEnd - clampedStart,
        };
        OnTextInput?.Invoke(_textInputState);
    }

    /// <summary>Commits event edges after the game has consumed the current frame.</summary>
    [HotPath]
    public void EndFrame()
    {
        Array.Clear(_keyDown);
        Array.Clear(_keyUp);
        Array.Clear(_gamepadButtonDown);
        Array.Clear(_gamepadButtonUp);
        _mouseDown = false;
        _mouseUp = false;
        _previousMousePosition = _mousePosition;
        _inputString = string.Empty;

        int previousTouchCount = _touchCount;
        int write = 0;
        bool primaryStillActive = false;
        for (int read = 0; read < _touchCount; read++)
        {
            if (_touchEnded[read])
                continue;
            TouchPoint touch = _touches[read];
            primaryStillActive |= touch.PointerId == _primaryPointerId;
            _touches[write] = touch with { Phase = touch.Phase == TouchPhase.Began || touch.Phase == TouchPhase.Moved ? TouchPhase.Stationary : touch.Phase };
            _touchEnded[write] = false;
            write++;
        }
        _touchCount = write;
        for (int index = 0; index < previousTouchCount; index++)
            _touchHistoryCounts[index] = 0;
        if (!primaryStillActive)
            _primaryPointerId = -1;
    }

    private void SetMouse(float x, float y, bool held, bool down, bool up)
    {
        _previousMousePosition = _mousePosition;
        _mousePosition = new Int2((int)x, (int)y);
        _mouseHeld = held;
        _mouseDown |= down;
        _mouseUp |= up;
        if (down || up)
            OnMouseEvent?.Invoke(MouseButton.Left, x, y, held, false);
    }

    private int FindTouch(int pointerId)
    {
        for (int i = 0; i < _touchCount; i++)
            if (_touches[i].PointerId == pointerId)
                return i;
        return -1;
    }

    [HotPath]
    private int GetOrCreateGamepadSlot(int androidDeviceId)
    {
        int existing = FindGamepadSlot(androidDeviceId);
        if (existing >= 0)
            return existing;
        if (_gamepadCount == MaxGamepads)
            return -1;

        int slot = _gamepadCount++;
        _gamepadDeviceIds[slot] = androidDeviceId;
        return slot;
    }

    private int FindGamepadSlot(int androidDeviceId)
    {
        for (int i = 0; i < _gamepadCount; i++)
        {
            if (_gamepadDeviceIds[i] == androidDeviceId)
                return i;
        }
        return -1;
    }

    [HotPath]
    private void SetGamepadButton(int gamepadIndex, GamepadButton button, bool pressed)
    {
        int buttonIndex = (int)button;
        if ((uint)buttonIndex >= GamepadButtonCount || _gamepadButtons[gamepadIndex, buttonIndex] == pressed)
            return;
        _gamepadButtons[gamepadIndex, buttonIndex] = pressed;
        if (pressed)
            _gamepadButtonDown[gamepadIndex, buttonIndex] = true;
        else
            _gamepadButtonUp[gamepadIndex, buttonIndex] = true;
    }

    private void CopyGamepadSlot(int source, int destination)
    {
        _gamepadDeviceIds[destination] = _gamepadDeviceIds[source];
        int sourceOffset = source * 2;
        int destinationOffset = destination * 2;
        _gamepadAxes[destinationOffset] = _gamepadAxes[sourceOffset];
        _gamepadAxes[destinationOffset + 1] = _gamepadAxes[sourceOffset + 1];
        _gamepadTriggers[destinationOffset] = _gamepadTriggers[sourceOffset];
        _gamepadTriggers[destinationOffset + 1] = _gamepadTriggers[sourceOffset + 1];
        for (int button = 0; button < GamepadButtonCount; button++)
        {
            _gamepadButtons[destination, button] = _gamepadButtons[source, button];
            _gamepadButtonDown[destination, button] = _gamepadButtonDown[source, button];
            _gamepadButtonUp[destination, button] = _gamepadButtonUp[source, button];
        }
    }

    private void ClearGamepadSlot(int slot)
    {
        _gamepadDeviceIds[slot] = -1;
        int axisOffset = slot * 2;
        _gamepadAxes[axisOffset] = Float2.Zero;
        _gamepadAxes[axisOffset + 1] = Float2.Zero;
        _gamepadTriggers[axisOffset] = 0f;
        _gamepadTriggers[axisOffset + 1] = 0f;
        for (int button = 0; button < GamepadButtonCount; button++)
        {
            _gamepadButtons[slot, button] = false;
            _gamepadButtonDown[slot, button] = false;
            _gamepadButtonUp[slot, button] = false;
        }
    }

    private bool TryGetGamepadButtonIndex(int gamepadIndex, GamepadButton button, out int buttonIndex)
    {
        buttonIndex = (int)button;
        return (uint)gamepadIndex < (uint)_gamepadCount && (uint)buttonIndex < GamepadButtonCount;
    }

    [HotPath]
    private static float NormalizeStick(float value) =>
        MathF.Abs(value) < GamepadDeadZone ? 0f : Math.Clamp(value, -1f, 1f);

    [HotPath]
    private static float NormalizeTrigger(float value) => Math.Clamp(value, 0f, 1f);

    private static bool TryMapGamepadButton(int androidKeyCode, out GamepadButton button)
    {
        button = androidKeyCode switch
        {
            19 => GamepadButton.DPadUp,
            20 => GamepadButton.DPadDown,
            21 => GamepadButton.DPadLeft,
            22 => GamepadButton.DPadRight,
            96 => GamepadButton.A,
            97 => GamepadButton.B,
            99 => GamepadButton.X,
            100 => GamepadButton.Y,
            102 => GamepadButton.LeftBumper,
            103 => GamepadButton.RightBumper,
            106 => GamepadButton.LeftStick,
            107 => GamepadButton.RightStick,
            108 => GamepadButton.Start,
            109 => GamepadButton.Back,
            110 => GamepadButton.Home,
            _ => GamepadButton.Unknown,
        };
        return button != GamepadButton.Unknown;
    }

    private static bool TryGetKeyIndex(KeyCode key, out int index)
    {
        index = (int)key;
        return (uint)index <= MaxKeyCode;
    }

    private static bool TryMapKey(int androidKeyCode, out KeyCode key)
    {
        if ((uint)(androidKeyCode - 29) <= 25)
        {
            key = (KeyCode)((int)KeyCode.A + androidKeyCode - 29);
            return true;
        }
        if ((uint)(androidKeyCode - 7) <= 9)
        {
            key = (KeyCode)((int)KeyCode.Number0 + androidKeyCode - 7);
            return true;
        }

        key = androidKeyCode switch
        {
            4 => KeyCode.Escape,
            19 => KeyCode.Up,
            20 => KeyCode.Down,
            21 => KeyCode.Left,
            22 => KeyCode.Right,
            23 => KeyCode.Enter,
            61 => KeyCode.Tab,
            62 => KeyCode.Space,
            66 => KeyCode.Enter,
            67 => KeyCode.Backspace,
            111 => KeyCode.Escape,
            112 => KeyCode.Delete,
            57 => KeyCode.AltLeft,
            58 => KeyCode.AltRight,
            59 => KeyCode.ShiftLeft,
            60 => KeyCode.ShiftRight,
            113 => KeyCode.ControlLeft,
            114 => KeyCode.ControlRight,
            117 => KeyCode.SuperLeft,
            118 => KeyCode.SuperRight,
            82 => KeyCode.Menu,
            92 => KeyCode.PageUp,
            93 => KeyCode.PageDown,
            122 => KeyCode.Home,
            123 => KeyCode.End,
            124 => KeyCode.Insert,
            >= 131 and <= 142 => (KeyCode)((int)KeyCode.F1 + androidKeyCode - 131),
            >= 144 and <= 153 => (KeyCode)((int)KeyCode.Keypad0 + androidKeyCode - 144),
            _ => KeyCode.Unknown,
        };
        return key != KeyCode.Unknown;
    }
}
