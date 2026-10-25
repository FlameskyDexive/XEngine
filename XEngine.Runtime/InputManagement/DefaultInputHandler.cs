// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.PaperUI;
using XEngine.Vector;

using Silk.NET.Input;

namespace XEngine.Runtime;

public class DefaultInputHandler : IInputHandler, IDisposable
{
    public IInputContext Context { get; internal set; }

    public IReadOnlyList<IKeyboard> Keyboards => Context.Keyboards;
    public IReadOnlyList<IMouse> Mice => Context.Mice;
    public IReadOnlyList<IJoystick> Joysticks => Context.Joysticks;

    /// <summary>System clipboard text. Empty when the clipboard holds no text (an image, a file
    /// list, or nothing) - GLFW reports that as FormatUnavailable, which must not escape into the
    /// frame loop: menus and text fields query the clipboard while they draw.</summary>
    public string Clipboard
    {
        get
        {
            try { return Context.Keyboards[0].ClipboardText ?? string.Empty; }
            catch (Silk.NET.GLFW.GlfwException) { return string.Empty; }
        }
        set
        {
            Context.Keyboards[0].ClipboardText = value;
        }
    }


    private Int2 _currentMousePos;
    private Int2 _prevMousePos;

    public Int2 PrevMousePosition => _prevMousePos;
    public Int2 MousePosition
    {
        get => _currentMousePos;
        set
        {
            _prevMousePos = value;
            _currentMousePos = value;
            Mice[0].Position = (Float2)value;
        }
    }
    public Float2 MouseDelta
    {
        get
        {
            Int2 delta = _currentMousePos - _prevMousePos;
            return new Float2(delta.X, delta.Y); // Invert Y to match gamepad (up = positive)
        }
    }
    public float MouseWheelDelta => Mice[0].ScrollWheels[0].Y;

    // Enum.GetValues allocates a fresh array per call; these tables are per-frame hot.
    private static readonly KeyCode[] s_allKeys = GetDistinctKeys();
    private static readonly MouseButton[] s_allMouseButtons = Enum.GetValues<MouseButton>();
    private static readonly GamepadButton[] s_allGamepadButtons = Enum.GetValues<GamepadButton>();

    private Dictionary<KeyCode, bool> wasKeyPressed = [];
    private Dictionary<KeyCode, bool> isKeyPressed = [];
    private Dictionary<MouseButton, bool> wasMousePressed = [];
    private Dictionary<MouseButton, bool> isMousePressed = [];

    // Preserve sub-frame taps just like mouse clicks. Key codes are sparse, so index
    // these fixed tables by their native value, rather than by the enum's item count.
    private readonly int[] _pendingKeyTransitions = new int[(int)s_allKeys[^1] + 1];
    private readonly bool[] _queuedKeys = new bool[(int)s_allKeys[^1] + 1];
    private readonly HashSet<IKeyboard> _subscribedKeyboards = new(ReferenceEqualityComparer.Instance);

    // Preserve callback positions as well as edges: polling after a coalesced press/move reads
    // the destination, which would turn a fast drag into a click there. Queues allocate only on
    // unusually large input bursts; drained queues retain at most 1024 entries per button.
    private readonly Queue<MouseTransition>[] _pendingMouseTransitions = CreateMouseTransitionQueues();
    private uint _queuedMouseButtons;
    private uint _pendingMouseTransitionButtons;
    private long _mouseTransitionSequence;
    private Int2 _polledMousePos;
    private readonly HashSet<IMouse> _subscribedMice = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IMouse, Int2> _eventMousePositions = new(ReferenceEqualityComparer.Instance);

    private readonly record struct MouseTransition(Int2 Position, long Sequence);

    private static Queue<MouseTransition>[] CreateMouseTransitionQueues()
    {
        var queues = new Queue<MouseTransition>[s_allMouseButtons.Length];
        for (int i = 0; i < queues.Length; i++) queues[i] = new Queue<MouseTransition>(4);
        return queues;
    }

    // Gamepad state tracking (per device)
    private Dictionary<int, Dictionary<GamepadButton, bool>> wasGamepadButtonPressed = [];
    private Dictionary<int, Dictionary<GamepadButton, bool>> isGamepadButtonPressed = [];

    private Queue<char> pressedChars { get; set; } = new();

    // Characters typed this frame, accumulated as KeyChar events arrive (during DoEvents) and cleared at
    // the frame boundary (LateUpdate). Unlike the pressedChars queue this is read non-destructively, so
    // any number of consumers - Paper, GameObject UI, user UI - can all see the same input each frame.
    private string _inputString = string.Empty;

    public event Action<KeyCode, bool> OnKeyEvent;
    public event Action<MouseButton, float, float, bool, bool> OnMouseEvent;

    /// <summary>
    /// Hot-reload: remove every event subscription whose delegate is bound to the collectible script
    /// assembly context (a user <c>Input.OnKeyEvent += ...</c> made outside a component lifecycle
    /// would otherwise pin the whole ALC and force an editor restart). Delegates owned by the
    /// engine/editor are kept.
    /// </summary>
    internal void RemoveCollectibleBoundSubscribers()
    {
        OnKeyEvent = (Action<KeyCode, bool>?)RemoveCollectibleBound(OnKeyEvent);
        OnMouseEvent = (Action<MouseButton, float, float, bool, bool>?)RemoveCollectibleBound(OnMouseEvent);
    }

    private static Delegate? RemoveCollectibleBound(Delegate? multicast)
    {
        if (multicast == null) return null;

        Delegate? kept = null;
        foreach (Delegate single in multicast.GetInvocationList())
        {
            bool collectible = (single.Target?.GetType().Assembly ?? single.Method.DeclaringType?.Assembly)?.IsCollectible == true;
            if (!collectible)
                kept = Delegate.Combine(kept, single);
        }
        return kept;
    }

    public bool IsAnyKeyDown => isKeyPressed.ContainsValue(true);

    public DefaultInputHandler(IInputContext context)
    {
        Context = context;
        _prevMousePos = (Int2)(Float2)Mice[0].Position;
        _currentMousePos = (Int2)(Float2)Mice[0].Position;

        // initialize key states
        foreach (KeyCode key in s_allKeys)
        {
            if (key != KeyCode.Unknown)
            {
                wasKeyPressed[key] = false;
                isKeyPressed[key] = false;
            }
        }

        foreach (MouseButton button in s_allMouseButtons)
        {
            if (button != MouseButton.Unknown)
            {
                wasMousePressed[button] = false;
                isMousePressed[button] = false;
            }
        }

        // Initialize gamepad state for all connected gamepads
        for (int i = 0; i < Context.Gamepads.Count; i++)
        {
            InitializeGamepadState(i);
        }

        IReadOnlyList<IKeyboard> keyboards = Keyboards;
        for (int i = 0; i < keyboards.Count; i++)
            SubscribeKeyboard(keyboards[i]);

        IReadOnlyList<IMouse> mice = Mice;
        for (int i = 0; i < mice.Count; i++)
            SubscribeMouse(mice[i]);
        Context.ConnectionChanged += OnDeviceConnectionChanged;

        UpdateKeyStates();
    }

    private static KeyCode[] GetDistinctKeys()
    {
        KeyCode[] keys = Enum.GetValues<KeyCode>();
        int count = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            KeyCode key = keys[i];
            if (key == KeyCode.Unknown) continue;
            // Enum.GetValues sorts by unsigned value; aliases are adjacent. Updating
            // Number0 and D0 twice otherwise erases the same key's down/up edge.
            if (count == 0 || keys[count - 1] != key)
                keys[count++] = key;
        }
        Array.Resize(ref keys, count);
        return keys;
    }

    private void SubscribeKeyboard(IKeyboard keyboard)
    {
        if (!_subscribedKeyboards.Add(keyboard)) return;
        keyboard.KeyDown += OnKeyDown;
        keyboard.KeyUp += OnKeyUp;
        keyboard.KeyChar += OnKeyChar;
    }

    private void OnKeyDown(IKeyboard keyboard, Silk.NET.Input.Key key, int scanCode)
        => RecordDeviceKeyTransition(keyboard, key, true);

    private void OnKeyUp(IKeyboard keyboard, Silk.NET.Input.Key key, int scanCode)
        => RecordDeviceKeyTransition(keyboard, key, false);

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        pressedChars.Enqueue(c);
        _inputString += c;
    }

    private void RecordDeviceKeyTransition(IKeyboard keyboard, Silk.NET.Input.Key key, bool pressed)
    {
        IReadOnlyList<IKeyboard> keyboards = Keyboards;
        for (int i = 0; !pressed && i < keyboards.Count; i++)
        {
            if (!ReferenceEquals(keyboards[i], keyboard) && keyboards[i].IsKeyPressed(key))
                pressed = true;
        }
        RecordKeyTransition((KeyCode)key, pressed);
    }

    private void RecordKeyTransition(KeyCode key, bool pressed)
    {
        int index = (int)key;
        if ((uint)index >= (uint)_queuedKeys.Length || !isKeyPressed.ContainsKey(key)) return;
        if (_queuedKeys[index] == pressed) return;
        _queuedKeys[index] = pressed;
        _pendingKeyTransitions[index]++;
    }

    private void SubscribeMouse(IMouse mouse)
    {
        if (!_subscribedMice.Add(mouse)) return;
        _eventMousePositions[mouse] = (Int2)(Float2)mouse.Position;
        mouse.MouseDown += OnMouseDown;
        mouse.MouseUp += OnMouseUp;
        mouse.MouseMove += OnMouseMove;
    }

    private void OnDeviceConnectionChanged(IInputDevice device, bool connected)
    {
        if (device is IKeyboard keyboard)
        {
            if (connected)
                SubscribeKeyboard(keyboard);
            else if (_subscribedKeyboards.Remove(keyboard))
            {
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyUp -= OnKeyUp;
                keyboard.KeyChar -= OnKeyChar;
            }
        }
        if (device is not IMouse mouse) return;
        if (connected)
            SubscribeMouse(mouse);
        else if (_subscribedMice.Remove(mouse))
        {
            mouse.MouseDown -= OnMouseDown;
            mouse.MouseUp -= OnMouseUp;
            mouse.MouseMove -= OnMouseMove;
            _eventMousePositions.Remove(mouse);
        }
    }

    private void OnMouseDown(IMouse mouse, Silk.NET.Input.MouseButton button)
        => RecordDeviceMouseTransition(mouse, button, true);

    private void OnMouseUp(IMouse mouse, Silk.NET.Input.MouseButton button)
        => RecordDeviceMouseTransition(mouse, button, false);

    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
        => _eventMousePositions[mouse] = (Int2)(Float2)position;

    private void RecordDeviceMouseTransition(IMouse mouse, Silk.NET.Input.MouseButton button, bool pressed)
    {
        if ((uint)(int)button > (uint)MouseButton.Button12) return;
        // A release on one device must not release a button still held by another device.
        IReadOnlyList<IMouse> mice = Mice;
        for (int i = 0; !pressed && i < mice.Count; i++)
        {
            if (!ReferenceEquals(mice[i], mouse) && mice[i].IsButtonPressed(button))
                pressed = true;
        }
        // mouse.Position may already report a later queued move. MouseMove callbacks give us
        // the position preceding this button callback in the device's ordered event stream.
        RecordMouseTransition((MouseButton)button, pressed,
            _eventMousePositions.TryGetValue(mouse, out Int2 position) ? position : (Int2)(Float2)mouse.Position);
    }

    private void RecordMouseTransition(MouseButton button, bool pressed, Int2 position)
    {
        int index = (int)button;
        if ((uint)index > (uint)MouseButton.Button12) return;
        uint bit = 1u << index;
        if (((_queuedMouseButtons & bit) != 0) == pressed) return;
        _queuedMouseButtons ^= bit;
        _pendingMouseTransitions[index].Enqueue(new MouseTransition(position, _mouseTransitionSequence++));
        _pendingMouseTransitionButtons |= bit;
    }

    private void InitializeGamepadState(int gamepadIndex)
    {
        wasGamepadButtonPressed[gamepadIndex] = [];
        isGamepadButtonPressed[gamepadIndex] = [];

        foreach (GamepadButton button in s_allGamepadButtons)
        {
            if (button != GamepadButton.Unknown)
            {
                wasGamepadButtonPressed[gamepadIndex][button] = false;
                isGamepadButtonPressed[gamepadIndex][button] = false;
            }
        }
    }

    internal void LateUpdate()
    {
        // Typed characters live for exactly one frame: they arrive during DoEvents, are read (non
        // destructively) by every consumer during Update, and are cleared here at the frame boundary.
        _inputString = string.Empty;
        pressedChars.Clear();

        _prevMousePos = _currentMousePos;
        _polledMousePos = (Int2)(Float2)Mice[0].Position;
        _currentMousePos = _polledMousePos;
        long firstSequence = long.MaxValue;
        uint pendingButtons = _pendingMouseTransitionButtons;
        while (pendingButtons != 0)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(pendingButtons);
            pendingButtons &= pendingButtons - 1;
            MouseTransition pending = _pendingMouseTransitions[i].Peek();
            if (pending.Sequence < firstSequence)
            {
                firstSequence = pending.Sequence;
                _currentMousePos = pending.Position;
            }
        }
        if (!_prevMousePos.Equals(_currentMousePos))
        {
            if (isMousePressed[MouseButton.Left])
                OnMouseEvent?.Invoke(MouseButton.Left, MousePosition.X, MousePosition.Y, false, true);
            else if (isMousePressed[MouseButton.Right])
                OnMouseEvent?.Invoke(MouseButton.Right, MousePosition.X, MousePosition.Y, false, true);
            else if (isMousePressed[MouseButton.Middle])
                OnMouseEvent?.Invoke(MouseButton.Middle, MousePosition.X, MousePosition.Y, false, true);
            else
                OnMouseEvent?.Invoke(MouseButton.Unknown, MousePosition.X, MousePosition.Y, false, true);
        }
        UpdateKeyStates();
    }

    // Update the state of each key
    private void UpdateKeyStates()
    {
        // Hoisted out of the per-key scans: Silk.NET's device collections walk a class enumerator,
        // so nesting foreach over Keyboards/Mice inside the ~140-key/button loops allocated per
        // tick — even when the editor renders nothing (one overnight heap dump held 923k dead
        // IKeyboard enumerators from this path).
        IReadOnlyList<IKeyboard> keyboards = Keyboards;
        int keyboardCount = keyboards.Count;
        foreach (KeyCode key in s_allKeys)
        {
            if (key == KeyCode.Unknown)
                continue;

            wasKeyPressed[key] = isKeyPressed[key];
            bool physicallyPressed = false;
            for (int i = 0; i < keyboardCount; i++)
            {
                if (keyboards[i].IsKeyPressed((Silk.NET.Input.Key)key))
                {
                    physicallyPressed = true;
                    break;
                }
            }

            RecordKeyTransition(key, physicallyPressed);
            int index = (int)key;
            if (_pendingKeyTransitions[index] > 0)
            {
                _pendingKeyTransitions[index]--;
                isKeyPressed[key] = !wasKeyPressed[key];
            }
            else
                isKeyPressed[key] = physicallyPressed;

            if (wasKeyPressed[key] != isKeyPressed[key])
                OnKeyEvent?.Invoke(key, isKeyPressed[key]);
        }

        IReadOnlyList<IMouse> mice = Mice;
        int mouseCount = mice.Count;
        foreach (MouseButton button in s_allMouseButtons)
        {
            if (button == MouseButton.Unknown)
                continue;

            wasMousePressed[button] = isMousePressed[button];
            bool physicallyPressed = false;
            for (int i = 0; i < mouseCount; i++)
            {
                if (mice[i].IsButtonPressed((Silk.NET.Input.MouseButton)button))
                {
                    physicallyPressed = true;
                    break;
                }
            }
            // Polling remains a fallback for devices without callbacks and disconnect releases.
            // It reconciles the final physical state, but never erases the intervening edges.
            RecordMouseTransition(button, physicallyPressed, _polledMousePos);
            int index = (int)button;
            uint bit = 1u << index;
            if ((_pendingMouseTransitionButtons & bit) != 0)
            {
                Queue<MouseTransition> pending = _pendingMouseTransitions[index];
                // Present the final held movement before releasing. Paper needs a held frame
                // to dispatch dragging even when press, move and release arrived in one pump.
                bool moveBeforeRelease = wasMousePressed[button] && !_currentMousePos.Equals(_prevMousePos);
                if (!moveBeforeRelease && pending.Peek().Position.Equals(_currentMousePos))
                {
                    pending.Dequeue();
                    isMousePressed[button] = !wasMousePressed[button];
                    if (pending.Count == 0)
                    {
                        _pendingMouseTransitionButtons &= ~bit;
                        if (pending.Capacity > 1024)
                        {
                            pending.TrimExcess();
                            pending.EnsureCapacity(4);
                        }
                    }
                }
            }
            else
                isMousePressed[button] = physicallyPressed;
            if (wasMousePressed[button] != isMousePressed[button])
                OnMouseEvent?.Invoke(button, MousePosition.X, MousePosition.Y, isMousePressed[button], false);
        }

        // Update gamepad button states
        for (int gamepadIndex = 0; gamepadIndex < Context.Gamepads.Count; gamepadIndex++)
        {
            if (!Context.Gamepads[gamepadIndex].IsConnected)
                continue;

            // Initialize if needed
            if (!isGamepadButtonPressed.ContainsKey(gamepadIndex))
                InitializeGamepadState(gamepadIndex);

            IGamepad gamepad = Context.Gamepads[gamepadIndex];
            foreach (GamepadButton button in s_allGamepadButtons)
            {
                if (button != GamepadButton.Unknown)
                {
                    wasGamepadButtonPressed[gamepadIndex][button] = isGamepadButtonPressed[gamepadIndex][button];
                    isGamepadButtonPressed[gamepadIndex][button] = false;

                    // Check if button is pressed
                    int buttonIndex = (int)button;
                    if (buttonIndex < gamepad.Buttons.Count && gamepad.Buttons[buttonIndex].Pressed)
                    {
                        isGamepadButtonPressed[gamepadIndex][button] = true;
                    }
                }
            }
        }
    }

    public char? GetPressedChar()
    {
        if (pressedChars.TryDequeue(out char c))
            return c;
        return null;
    }

    public string InputString => _inputString;

    // TryGetValue (not the indexer) so an unmapped key/button - e.g. KeyCode.Unknown, which isn't in
    // the dictionaries - returns false instead of throwing KeyNotFoundException.
    private static bool Down(Dictionary<KeyCode, bool> map, KeyCode key) => map.TryGetValue(key, out var v) && v;
    private static bool Down(Dictionary<MouseButton, bool> map, MouseButton btn) => map.TryGetValue(btn, out var v) && v;

    public bool GetKey(KeyCode key) => Down(isKeyPressed, key);

    public bool GetKeyDown(KeyCode key) => Down(isKeyPressed, key) && !Down(wasKeyPressed, key);

    public bool GetKeyUp(KeyCode key) => !Down(isKeyPressed, key) && Down(wasKeyPressed, key);

    public bool GetMouseButton(int button) => Down(isMousePressed, (MouseButton)button);

    public bool GetMouseButtonDown(int button) => Down(isMousePressed, (MouseButton)button) && !Down(wasMousePressed, (MouseButton)button);

    public bool GetMouseButtonUp(int button) => !Down(isMousePressed, (MouseButton)button) && Down(wasMousePressed, (MouseButton)button);

    public void SetCursorVisible(bool visible, int miceIndex = 0) => Mice[miceIndex].Cursor.CursorMode = visible ? CursorMode.Normal : CursorMode.Disabled;

    public void SetCursorShape(PaperCursor shape, int miceIndex = 0)
    {
        ICursor cursor = Mice[miceIndex].Cursor;
        // Only relevant while the cursor is visible; don't fight a locked/hidden cursor (FPS mode).
        if (cursor.CursorMode != CursorMode.Normal)
            return;

        // Silk.NET has no grab/help shapes, so those fall back to the hand or the arrow.
        cursor.StandardCursor = shape switch
        {
            PaperCursor.Pointer or PaperCursor.Grab or PaperCursor.Grabbing => StandardCursor.Hand,
            PaperCursor.Text => StandardCursor.IBeam,
            PaperCursor.Crosshair => StandardCursor.Crosshair,
            PaperCursor.ResizeHorizontal => StandardCursor.HResize,
            PaperCursor.ResizeVertical => StandardCursor.VResize,
            PaperCursor.ResizeNWSE => StandardCursor.NwseResize,
            PaperCursor.ResizeNESW => StandardCursor.NeswResize,
            PaperCursor.ResizeAll => StandardCursor.ResizeAll,
            PaperCursor.NotAllowed => StandardCursor.NotAllowed,
            PaperCursor.Wait => StandardCursor.Wait,
            _ => StandardCursor.Default,
        };
    }

    // Gamepad methods implementation
    public int GetGamepadCount() => Context.Gamepads.Count;

    public bool IsGamepadConnected(int gamepadIndex)
    {
        return gamepadIndex >= 0 && gamepadIndex < Context.Gamepads.Count && Context.Gamepads[gamepadIndex].IsConnected;
    }

    public bool GetGamepadButton(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex) || !isGamepadButtonPressed.ContainsKey(gamepadIndex))
            return false;
        return isGamepadButtonPressed[gamepadIndex].GetValueOrDefault(button, false);
    }

    public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex) || !isGamepadButtonPressed.ContainsKey(gamepadIndex))
            return false;
        return isGamepadButtonPressed[gamepadIndex].GetValueOrDefault(button, false) &&
               !wasGamepadButtonPressed[gamepadIndex].GetValueOrDefault(button, false);
    }

    public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex) || !isGamepadButtonPressed.ContainsKey(gamepadIndex))
            return false;
        return !isGamepadButtonPressed[gamepadIndex].GetValueOrDefault(button, false) &&
               wasGamepadButtonPressed[gamepadIndex].GetValueOrDefault(button, false);
    }

    public Float2 GetGamepadAxis(int gamepadIndex, int axisIndex)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return Float2.Zero;

        IGamepad gamepad = Context.Gamepads[gamepadIndex];
        if (axisIndex < 0 || axisIndex >= gamepad.Thumbsticks.Count)
            return Float2.Zero;

        Thumbstick thumbstick = gamepad.Thumbsticks[axisIndex];
        return new Float2(thumbstick.X, thumbstick.Y); // We flip y to make UP on the stick positive
    }

    public float GetGamepadTrigger(int gamepadIndex, int triggerIndex)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return 0.0f;

        IGamepad gamepad = Context.Gamepads[gamepadIndex];
        if (triggerIndex < 0 || triggerIndex >= gamepad.Triggers.Count)
            return 0.0f;

        return gamepad.Triggers[triggerIndex].Position;
    }

    public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return;

        IGamepad gamepad = Context.Gamepads[gamepadIndex];
        if (gamepad.VibrationMotors.Count >= 2)
        {
            gamepad.VibrationMotors[0].Speed = (float)leftMotor;
            gamepad.VibrationMotors[1].Speed = (float)rightMotor;
        }
    }

    public void Dispose()
    {
        Context.ConnectionChanged -= OnDeviceConnectionChanged;
        foreach (IKeyboard keyboard in _subscribedKeyboards)
        {
            keyboard.KeyDown -= OnKeyDown;
            keyboard.KeyUp -= OnKeyUp;
            keyboard.KeyChar -= OnKeyChar;
        }
        _subscribedKeyboards.Clear();
        foreach (IMouse mouse in _subscribedMice)
        {
            mouse.MouseDown -= OnMouseDown;
            mouse.MouseUp -= OnMouseUp;
            mouse.MouseMove -= OnMouseMove;
        }
        _subscribedMice.Clear();
        _eventMousePositions.Clear();
        Context.Dispose();
    }
}
