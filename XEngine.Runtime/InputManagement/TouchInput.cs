// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime;

public enum TouchPhase
{
    Began,
    Moved,
    Stationary,
    Ended,
    Canceled,
}

public readonly record struct TouchPoint(
    int PointerId,
    Float2 Position,
    float Pressure,
    TouchPhase Phase);

/// <summary>
/// A historical touch sample reported by a platform before the current <see cref="TouchPoint"/>.
/// Samples are valid until the next input-frame boundary and are ordered from oldest to newest.
/// </summary>
public readonly record struct TouchHistorySample(
    int PointerId,
    Float2 Position,
    float Pressure,
    long EventTimeMilliseconds);

public readonly record struct TextInputState(
    string Text,
    string Composition,
    int SelectionStart,
    int SelectionLength,
    bool IsActive);

public interface ITouchInputHandler
{
    int TouchCount { get; }
    TouchPoint GetTouch(int index);
}

/// <summary>
/// Optional touch capability for platforms that expose coalesced motion samples.
/// </summary>
public interface ITouchHistoryInputHandler : ITouchInputHandler
{
    int GetTouchHistoryCount(int touchIndex);
    TouchHistorySample GetTouchHistory(int touchIndex, int historyIndex);
}

public interface ITextInputHandler
{
    TextInputState TextInputState { get; }
    event Action<TextInputState> OnTextInput;
    bool IsTextInputSupported { get; }
    void SetTextInputEnabled(bool enabled);
}

/// <summary>
/// XGUI text-input contract. It extends the existing platform handler with clipboard access so
/// InputField can use the same IME/soft-keyboard path on desktop, Android, and HarmonyOS.
/// </summary>
public interface ITextInputService : ITextInputHandler
{
    string Clipboard { get; set; }

    /// <summary>
    /// Seeds the platform text editor with the committed text and UTF-16 selection range owned by
    /// the focused XGUI control. Platform IME callbacks use this state for subsequent commits and
    /// surrounding-text edits; the composition string is always reset when a new field takes focus.
    /// </summary>
    void SetTextInputState(string text, int selectionStart, int selectionLength);

    /// <summary>Selects the platform soft-keyboard layout and secure-entry mode for the focused field.</summary>
    void SetTextInputOptions(XEngine.Runtime.UI.TouchScreenKeyboardType keyboardType, bool secure);
}
