// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime;

/// <summary>
/// Programmatic input injection for automated playtests / agent-driven QA (runtime_eval,
/// headless runs). Held keys/buttons are OR-composed into the <see cref="Input"/> facade reads,
/// so gameplay actions cannot distinguish an injected press from a physical one. Zero cost when
/// nothing is injected (empty-set checks). Not persisted across scene loads by design.
/// </summary>
public static class InputInjector
{
    private static readonly HashSet<KeyCode> _held = [];
    private static readonly HashSet<KeyCode> _pressedThisFrame = [];
    private static readonly HashSet<KeyCode> _releasedThisFrame = [];
    private static readonly HashSet<KeyCode> _pendingTaps = [];
    private static readonly HashSet<KeyCode> _activeTaps = [];

    /// <summary>Queues a one-sample hold for UI events occurring after input sampling.</summary>
    public static void Tap(KeyCode key) => _pendingTaps.Add(key);
    private static readonly HashSet<int> _mouseHeld = [];
    private static readonly HashSet<int> _mousePressedThisFrame = [];
    private static readonly HashSet<int> _mouseReleasedThisFrame = [];

    /// <summary>True while any injected button exists (gates the facade's extra lookups).</summary>
    internal static bool HasAny => _held.Count > 0 || _pressedThisFrame.Count > 0 ||
                                   _releasedThisFrame.Count > 0 || _mouseHeld.Count > 0 ||
                                   _mousePressedThisFrame.Count > 0 || _mouseReleasedThisFrame.Count > 0;

    /// <summary>Marks <paramref name="key"/> as held (repeats every frame) until Release.</summary>
    public static void Press(KeyCode key)
    {
        if (_held.Add(key))
            _pressedThisFrame.Add(key);
    }

    /// <summary>Releases a previously pressed key and marks the release edge.</summary>
    public static void Release(KeyCode key)
    {
        _pendingTaps.Remove(key);
        _activeTaps.Remove(key);
        if (_held.Remove(key))
            _releasedThisFrame.Add(key);
    }

    /// <summary>Holds/clears an injected mouse button (0 = left).</summary>
    public static void SetMouseButton(int button, bool down)
    {
        if (down)
        {
            if (_mouseHeld.Add(button))
                _mousePressedThisFrame.Add(button);
        }
        else if (_mouseHeld.Remove(button))
        {
            _mouseReleasedThisFrame.Add(button);
        }
    }

    /// <summary>Clears everything and advances the edge buffers (called once per frame).</summary>
    internal static void NewFrame()
    {
        _pressedThisFrame.Clear();
        _releasedThisFrame.Clear();
        _mousePressedThisFrame.Clear();
        _mouseReleasedThisFrame.Clear();
        foreach (KeyCode key in _activeTaps)
        {
            if (_held.Remove(key)) _releasedThisFrame.Add(key);
        }
        _activeTaps.Clear();
        foreach (KeyCode key in _pendingTaps)
        {
            if (_held.Add(key))
            {
                _pressedThisFrame.Add(key);
                _activeTaps.Add(key);
            }
        }
        _pendingTaps.Clear();
    }

    internal static bool IsHeld(KeyCode key) => _held.Contains(key);
    internal static bool ConsumePressedEdge(KeyCode key) => _pressedThisFrame.Contains(key);
    internal static bool ConsumeReleasedEdge(KeyCode key) => _releasedThisFrame.Contains(key);
    internal static bool IsMouseHeld(int b) => _mouseHeld.Contains(b);
    internal static bool ConsumeMousePressed(int b) => _mousePressedThisFrame.Contains(b);
    internal static bool ConsumeMouseReleased(int b) => _mouseReleasedThisFrame.Contains(b);
}
