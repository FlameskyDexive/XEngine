// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Runtime.Platforms.iOS;

/// <summary>Touch action values shared by the generated UIKit host and managed Runtime.</summary>
public enum IosTouchAction
{
    Began = 0,
    Ended = 1,
    Moved = 2,
    Canceled = 3,
}

/// <summary>
/// Platform-neutral state sink for UIKit input callbacks. The generated player feeds integer
/// pointer ids, HID keyboard usages, and <see cref="GamepadButton"/> values so this type stays
/// free of Microsoft.iOS types.
/// </summary>
public sealed class IosInputHandler : IInputHandler, ITouchHistoryInputHandler, ITextInputService
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

    public IosInputHandler(
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

    public void SetTextInputOptions(TouchScreenKeyboardType keyboardType, bool secure)
    {
        LastKeyboardType = keyboardType;
        LastSecureTextEntry = secure;
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

    /// <summary>
    /// Hardware keyboard from <c>UIPress</c>. <paramref name="hidUsage"/> is
    /// <c>UIKeyboardHIDUsage</c> (A=4 … Z=29, 1=30 … 0=39, Return=40, Escape=41, Backspace=42).
    /// </summary>
    [HotPath]
    public bool HandleKey(int hidUsage, bool pressed)
    {
        if (!TryMapHidKey(hidUsage, out KeyCode key) || !TryGetKeyIndex(key, out int index))
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

    public bool GamepadConnected(int deviceId) => GetOrCreateGamepadSlot(deviceId) >= 0;

    public void GamepadDisconnected(int deviceId)
    {
        int slot = FindGamepadSlot(deviceId);
        if (slot < 0)
            return;

        for (int current = slot; current + 1 < _gamepadCount; current++)
            CopyGamepadSlot(current + 1, current);
        _gamepadCount--;
        ClearGamepadSlot(_gamepadCount);
    }

    [HotPath]
    public bool HandleGamepadButton(int deviceId, GamepadButton button, bool pressed)
    {
        if (button == GamepadButton.Unknown)
            return false;
        int slot = GetOrCreateGamepadSlot(deviceId);
        if (slot < 0)
            return false;
        SetGamepadButton(slot, button, pressed);
        return true;
    }

    [HotPath]
    public bool HandleGamepadMotion(
        int deviceId,
        float leftX,
        float leftY,
        float rightX,
        float rightY,
        float leftTrigger,
        float rightTrigger,
        float hatX,
        float hatY)
    {
        int slot = GetOrCreateGamepadSlot(deviceId);
        if (slot < 0)
            return false;

        int axisOffset = slot * 2;
        _gamepadAxes[axisOffset] = new Float2(NormalizeStick(leftX), NormalizeStick(leftY));
        _gamepadAxes[axisOffset + 1] = new Float2(NormalizeStick(rightX), NormalizeStick(rightY));
        _gamepadTriggers[axisOffset] = NormalizeTrigger(leftTrigger);
        _gamepadTriggers[axisOffset + 1] = NormalizeTrigger(rightTrigger);
        SetGamepadButton(slot, GamepadButton.DPadLeft, hatX <= -DPadAxisThreshold);
        SetGamepadButton(slot, GamepadButton.DPadRight, hatX >= DPadAxisThreshold);
        SetGamepadButton(slot, GamepadButton.DPadUp, hatY >= DPadAxisThreshold);
        SetGamepadButton(slot, GamepadButton.DPadDown, hatY <= -DPadAxisThreshold);
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
        _textInputState = _textInputState with { Composition = text };
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
    private int GetOrCreateGamepadSlot(int deviceId)
    {
        int existing = FindGamepadSlot(deviceId);
        if (existing >= 0)
            return existing;
        if (_gamepadCount == MaxGamepads)
            return -1;

        int slot = _gamepadCount++;
        _gamepadDeviceIds[slot] = deviceId;
        return slot;
    }

    private int FindGamepadSlot(int deviceId)
    {
        for (int i = 0; i < _gamepadCount; i++)
        {
            if (_gamepadDeviceIds[i] == deviceId)
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

    private static bool TryGetKeyIndex(KeyCode key, out int index)
    {
        index = (int)key;
        return (uint)index <= MaxKeyCode;
    }

    /// <summary>Maps <c>UIKeyboardHIDUsage</c> values used by <c>UIKey.KeyCode</c>.</summary>
    private static bool TryMapHidKey(int hidUsage, out KeyCode key)
    {
        if ((uint)(hidUsage - 4) <= 25)
        {
            key = (KeyCode)((int)KeyCode.A + hidUsage - 4);
            return true;
        }
        if ((uint)(hidUsage - 30) <= 8)
        {
            key = (KeyCode)((int)KeyCode.Number1 + hidUsage - 30);
            return true;
        }

        key = hidUsage switch
        {
            39 => KeyCode.Number0,
            40 => KeyCode.Enter,
            41 => KeyCode.Escape,
            42 => KeyCode.Backspace,
            43 => KeyCode.Tab,
            44 => KeyCode.Space,
            58 => KeyCode.F1,
            59 => KeyCode.F2,
            60 => KeyCode.F3,
            61 => KeyCode.F4,
            62 => KeyCode.F5,
            63 => KeyCode.F6,
            64 => KeyCode.F7,
            65 => KeyCode.F8,
            66 => KeyCode.F9,
            67 => KeyCode.F10,
            68 => KeyCode.F11,
            69 => KeyCode.F12,
            74 => KeyCode.Home,
            75 => KeyCode.PageUp,
            76 => KeyCode.Delete,
            77 => KeyCode.End,
            78 => KeyCode.PageDown,
            79 => KeyCode.Right,
            80 => KeyCode.Left,
            81 => KeyCode.Down,
            82 => KeyCode.Up,
            224 => KeyCode.ControlLeft,
            225 => KeyCode.ShiftLeft,
            226 => KeyCode.AltLeft,
            227 => KeyCode.SuperLeft,
            228 => KeyCode.ControlRight,
            229 => KeyCode.ShiftRight,
            230 => KeyCode.AltRight,
            231 => KeyCode.SuperRight,
            _ => KeyCode.Unknown,
        };
        return key != KeyCode.Unknown;
    }
}
