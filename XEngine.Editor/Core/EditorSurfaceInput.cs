using System;
using System.Collections.Generic;
using System.Numerics;

using Silk.NET.Input;

using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.Core;

/// <summary>Per-native-window input bridge for a detached Paper surface.</summary>
internal sealed class EditorSurfaceInput : IDisposable, IClipboardHandler
{
    private readonly IInputContext _context;
    private readonly IKeyboard? _keyboard;
    private readonly IMouse? _mouse;
    private readonly Queue<char> _characters = new(4);
    private readonly Queue<MouseTransition> _mouseTransitions = new(4);
    private readonly bool[] _buttonStates = new bool[3];
    private readonly bool[] _capturedButtons = new bool[3];

    private static readonly KeyBinding[] s_keyBindings = CreateKeyBindings();
    private static readonly Dictionary<Silk.NET.Input.Key, int> s_keyIndices = CreateKeyIndices();
    private readonly bool[] _keyStates = new bool[s_keyBindings.Length];
    private readonly bool[] _capturedKeys = new bool[s_keyBindings.Length];
    private readonly int[] _keyTransitions = new int[s_keyBindings.Length];
    private int _pendingKeyTransitions;
    private Vector2 _callbackPosition;
    private Vector2 _deliveredPosition;
    private bool _pointerDirty;
    private float _wheel;
    private bool _hasWheelCallbacks;
    private bool _disposed;

    public bool HasPendingInput => _mouseTransitions.Count > 0 || _pointerDirty
        || _pendingKeyTransitions > 0 || _characters.Count > 0 || _wheel != 0f;

    public string GetClipboardText()
    {
        if (_disposed || _keyboard == null) return string.Empty;
        try { return _keyboard.ClipboardText ?? string.Empty; }
        catch (Silk.NET.GLFW.GlfwException) { return string.Empty; }
    }

    public void SetClipboardText(string text)
    {
        if (!_disposed && _keyboard != null) _keyboard.ClipboardText = text;
    }

    public EditorSurfaceInput(IInputContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (context.Keyboards.Count > 0)
        {
            _keyboard = context.Keyboards[0];
            _keyboard.KeyDown += OnKeyDown;
            _keyboard.KeyUp += OnKeyUp;
            _keyboard.KeyChar += OnKeyChar;
        }
        if (context.Mice.Count > 0)
        {
            _mouse = context.Mice[0];
            _callbackPosition = _mouse.Position;
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.MouseMove += OnMouseMove;
            _mouse.Scroll += OnScroll;
        }
    }

    public void Apply(Paper paper, float xScale, float yScale)
    {
        ArgumentNullException.ThrowIfNull(paper);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ApplyMouse(paper, xScale, yScale);

        while (_characters.TryDequeue(out char character))
            paper.AddInputCharacter(character);
        TrimDrainedQueue(_characters);

        if (_keyboard == null) return;
        for (int i = 0; i < s_keyBindings.Length; i++)
        {
            KeyBinding binding = s_keyBindings[i];
            RecordKey(i, _keyboard.IsKeyPressed((Silk.NET.Input.Key)binding.KeyCode));
            if (_keyTransitions[i] > 0)
            {
                _keyTransitions[i]--;
                _pendingKeyTransitions--;
                _keyStates[i] = !_keyStates[i];
            }
            paper.SetKeyState(binding.PaperKey, _keyStates[i]);
        }
    }

    public void SetCursor(PaperCursor cursor)
    {
        if (_mouse == null || _mouse.Cursor.CursorMode != CursorMode.Normal)
            return;

        _mouse.Cursor.StandardCursor = cursor switch
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_keyboard != null)
        {
            _keyboard.KeyDown -= OnKeyDown;
            _keyboard.KeyUp -= OnKeyUp;
            _keyboard.KeyChar -= OnKeyChar;
        }
        if (_mouse != null)
        {
            _mouse.MouseDown -= OnMouseDown;
            _mouse.MouseUp -= OnMouseUp;
            _mouse.MouseMove -= OnMouseMove;
            _mouse.Scroll -= OnScroll;
        }
        _characters.Clear();
        _mouseTransitions.Clear();
        TrimDrainedQueue(_characters);
        TrimDrainedQueue(_mouseTransitions);
        Array.Clear(_keyTransitions);
        _pendingKeyTransitions = 0;
        _pointerDirty = false;
        _wheel = 0f;
        _context.Dispose();
    }

    private void ApplyMouse(Paper paper, float xScale, float yScale)
    {
        Vector2 polledPosition = _mouse?.Position ?? Vector2.Zero;
        for (int i = 0; i < _capturedButtons.Length; i++)
            RecordMouse(i, _mouse?.IsButtonPressed((Silk.NET.Input.MouseButton)i) == true, polledPosition);

        Vector2 position = polledPosition;
        bool deliverEdge = false;
        MouseTransition transition = default;
        if (_mouseTransitions.TryPeek(out transition))
        {
            position = transition.Position;
            // A complete drag may arrive between two Paper frames. Give the held button a
            // movement frame at the release position before Paper sees the release edge.
            deliverEdge = transition.Down || !_buttonStates[transition.Button]
                || _deliveredPosition == position;
        }
        float x = position.X * xScale, y = position.Y * yScale;
        paper.SetPointerPosition(x, y);
        if (deliverEdge)
        {
            _mouseTransitions.Dequeue();
            _buttonStates[transition.Button] = transition.Down;
            paper.SetPointerState(ToPaperButton(transition.Button), x, y, transition.Down, false);
            TrimDrainedQueue(_mouseTransitions);
        }
        _deliveredPosition = position;
        _pointerDirty = position != polledPosition;

        float wheel = _wheel;
        _wheel = 0f;
        if (!_hasWheelCallbacks && _mouse is { ScrollWheels.Count: > 0 })
            wheel = _mouse.ScrollWheels[0].Y;
        if (wheel != 0f) paper.SetPointerWheel(wheel);
    }

    private void OnMouseDown(IMouse _, Silk.NET.Input.MouseButton button)
        => RecordMouse((int)button, true, _callbackPosition);

    private void OnMouseUp(IMouse _, Silk.NET.Input.MouseButton button)
        => RecordMouse((int)button, false, _callbackPosition);

    private void OnMouseMove(IMouse _, Vector2 position)
    {
        _callbackPosition = position;
        _pointerDirty = true;
    }

    private void RecordMouse(int button, bool down, Vector2 position)
    {
        if ((uint)button >= (uint)_capturedButtons.Length || _capturedButtons[button] == down) return;
        _capturedButtons[button] = down;
        _mouseTransitions.Enqueue(new MouseTransition(button, down, position));
    }

    private void OnScroll(IMouse _, ScrollWheel wheel)
    {
        _hasWheelCallbacks = true;
        _wheel += wheel.Y;
    }

    private void OnKeyDown(IKeyboard _, Silk.NET.Input.Key key, int scanCode) => RecordKey(key, true);
    private void OnKeyUp(IKeyboard _, Silk.NET.Input.Key key, int scanCode) => RecordKey(key, false);

    private void RecordKey(Silk.NET.Input.Key key, bool down)
    {
        if (s_keyIndices.TryGetValue(key, out int index)) RecordKey(index, down);
    }

    private void RecordKey(int index, bool down)
    {
        if (_capturedKeys[index] == down) return;
        _capturedKeys[index] = down;
        _keyTransitions[index]++;
        _pendingKeyTransitions++;
    }

    private void OnKeyChar(IKeyboard _, char character) => _characters.Enqueue(character);

    private static PaperMouseBtn ToPaperButton(int button) => button switch
    {
        (int)Silk.NET.Input.MouseButton.Left => PaperMouseBtn.Left,
        (int)Silk.NET.Input.MouseButton.Middle => PaperMouseBtn.Middle,
        _ => PaperMouseBtn.Right,
    };

    private static void TrimDrainedQueue<T>(Queue<T> queue)
    {
        if (queue.Count != 0 || queue.Capacity <= 1024) return;
        queue.TrimExcess();
        queue.EnsureCapacity(4);
    }

    private static Dictionary<Silk.NET.Input.Key, int> CreateKeyIndices()
    {
        var indices = new Dictionary<Silk.NET.Input.Key, int>(s_keyBindings.Length);
        for (int i = 0; i < s_keyBindings.Length; i++)
            indices.Add((Silk.NET.Input.Key)s_keyBindings[i].KeyCode, i);
        return indices;
    }

    private static KeyBinding[] CreateKeyBindings()
    {
        var bindings = new List<KeyBinding>();
        var mapped = new HashSet<PaperKey>();
        foreach (KeyCode key in Enum.GetValues<KeyCode>())
        {
            if (key == KeyCode.Unknown || !Enum.TryParse(key.ToString(), out PaperKey paperKey))
                continue;
            if (mapped.Add(paperKey))
                bindings.Add(new KeyBinding(key, paperKey));
        }

        Add(KeyCode.Equal, PaperKey.Equals);
        Add(KeyCode.BackSlash, PaperKey.Backslash);
        Add(KeyCode.GraveAccent, PaperKey.Grave);
        Add(KeyCode.KeypadEqual, PaperKey.KeypadEquals);
        Add(KeyCode.Number0, PaperKey.Num0);
        Add(KeyCode.Number1, PaperKey.Num1);
        Add(KeyCode.Number2, PaperKey.Num2);
        Add(KeyCode.Number3, PaperKey.Num3);
        Add(KeyCode.Number4, PaperKey.Num4);
        Add(KeyCode.Number5, PaperKey.Num5);
        Add(KeyCode.Number6, PaperKey.Num6);
        Add(KeyCode.Number7, PaperKey.Num7);
        Add(KeyCode.Number8, PaperKey.Num8);
        Add(KeyCode.Number9, PaperKey.Num9);
        Add(KeyCode.KeypadSubtract, PaperKey.KeypadMinus);
        Add(KeyCode.KeypadAdd, PaperKey.KeypadPlus);
        Add(KeyCode.ShiftLeft, PaperKey.LeftShift);
        Add(KeyCode.ShiftRight, PaperKey.RightShift);
        Add(KeyCode.AltLeft, PaperKey.LeftAlt);
        Add(KeyCode.AltRight, PaperKey.RightAlt);
        Add(KeyCode.ControlLeft, PaperKey.LeftControl);
        Add(KeyCode.ControlRight, PaperKey.RightControl);
        Add(KeyCode.SuperLeft, PaperKey.LeftSuper);
        Add(KeyCode.SuperRight, PaperKey.RightSuper);

        return bindings.ToArray();

        void Add(KeyCode key, PaperKey paperKey)
        {
            if (mapped.Add(paperKey))
                bindings.Add(new KeyBinding(key, paperKey));
        }
    }

    private readonly record struct KeyBinding(KeyCode KeyCode, PaperKey PaperKey);
    private readonly record struct MouseTransition(int Button, bool Down, Vector2 Position);
}
