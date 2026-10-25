using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.Test;

    /// <summary>Keys-only mock input handler (same surface as the M4 tests).</summary>
    internal sealed class MockInputHandler : IInputHandler
    {
        private readonly HashSet<KeyCode> _keys = new();
        private readonly HashSet<KeyCode> _keysDown = new();
        private readonly HashSet<KeyCode> _keysUp = new();

        public void Press(KeyCode key) { if (_keys.Add(key)) _keysDown.Add(key); }
        public void Release(KeyCode key) { if (_keys.Remove(key)) _keysUp.Add(key); }

        /// <summary>Frame boundary — clears this-frame edge sets (the real handler resets
        /// GetKeyDown/GetKeyUp per frame; tests must call this at the end of every tick).</summary>
        public void EndFrame()
        {
            _keysDown.Clear();
            _keysUp.Clear();
        }

        public string Clipboard { get; set; } = "";
        public bool IsAnyKeyDown => _keys.Count > 0;
        public Float2 MouseDelta => Float2.Zero;
        public Int2 MousePosition { get; set; }
        public float MouseWheelDelta => 0f;
        public Int2 PrevMousePosition => Int2.Zero;
        public string InputString => "";
        public char? GetPressedChar() => null;
        public bool GetKey(KeyCode key) => _keys.Contains(key);
        public bool GetKeyDown(KeyCode key) => _keysDown.Contains(key);
        public bool GetKeyUp(KeyCode key) => _keysUp.Contains(key);
        public bool GetMouseButton(int button) => false;
        public bool GetMouseButtonDown(int button) => false;
        public bool GetMouseButtonUp(int button) => false;
        public void SetCursorVisible(bool visible, int miceIndex = 0) { }
        public void SetCursorShape(XEngine.PaperUI.PaperCursor shape, int miceIndex = 0) { }
        public int GetGamepadCount() => 0;
        public bool IsGamepadConnected(int gamepadIndex) => false;
        public bool GetGamepadButton(int gamepadIndex, GamepadButton button) => false;
        public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button) => false;
        public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button) => false;
        public Float2 GetGamepadAxis(int gamepadIndex, int axisIndex) => Float2.Zero;
        public float GetGamepadTrigger(int gamepadIndex, int triggerIndex) => 0f;
        public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor) { }

#pragma warning disable CS0067
        public event System.Action<KeyCode, bool>? OnKeyEvent;
        public event System.Action<MouseButton, float, float, bool, bool>? OnMouseEvent;
#pragma warning restore CS0067
    }
