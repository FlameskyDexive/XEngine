using System;
using System.Collections.Generic;

namespace XEngine.WebSurface;

/// <summary>One UTF-16 code-unit selection range inside the edited text.</summary>
public readonly record struct WebSurfaceTextRange(int Start, int Length)
{
    public static WebSurfaceTextRange Empty(int at) => new(at, 0);
    public bool IsEmpty => Length == 0;
}

/// <summary>Origin of a text-session operation (who produced it).</summary>
public enum WebSurfaceTextSource : byte
{
    /// <summary>The engine / an engine-side soft keyboard.</summary>
    Engine = 0,
    /// <summary>The platform's native IME or the page itself.</summary>
    Platform = 1,
}

/// <summary>A single edit applied inside a text session.</summary>
public readonly record struct WebSurfaceTextEdit(
    WebSurfaceTextSource Source,
    WebSurfaceTextEditKind Kind,
    int Start,
    int Length,
    string Text,
    WebSurfaceTextRange SelectionAfter);

public enum WebSurfaceTextEditKind : byte
{
    /// <summary>IME composition update (not yet committed text).</summary>
    Compose,
    /// <summary>Insert/replace the range with text (commits composition as well).</summary>
    Replace,
    /// <summary>Remove the range (Delete/backspace).</summary>
    Delete,
    /// <summary>Selection changed without text change.</summary>
    Select,
}

/// <summary>
/// B6 text session: the explicit contract between the engine and a platform's IME/text entry for
/// one focused editable element. Operations pass text, UTF-16 selection, composition, deletion
/// and replacement — never raw key events (the platform owns native IME; the page draws the
/// result into its canvas). The session keeps the mirrored document state (text + selection),
/// validates UTF-16 indices and records every applied edit so the backend can forward a single
/// replayable op list. Hide/blur/pause/dispose must End the session.
/// </summary>
public sealed class WebSurfaceTextSession
{
    private readonly List<WebSurfaceTextEdit> _edits = [];
    private string _text = string.Empty;
    private WebSurfaceTextRange _selection;
    private bool _ended;

    private WebSurfaceTextSession() { }

    public static WebSurfaceTextSession Begin(string initialText, WebSurfaceTextRange initialSelection)
    {
        string text = initialText ?? string.Empty;
        if (!IsValidRange(text, initialSelection.Start, initialSelection.Length))
            throw new ArgumentOutOfRangeException(nameof(initialSelection));
        return new WebSurfaceTextSession { _text = text, _selection = initialSelection };
    }

    /// <summary>Mirrored document text (UTF-16 code units).</summary>
    public string Text => _text;

    /// <summary>Mirrored selection in UTF-16 code units.</summary>
    public WebSurfaceTextRange Selection => _selection;

    /// <summary>Edits applied since Begin, in order (for forwarding/replay).</summary>
    public IReadOnlyList<WebSurfaceTextEdit> Edits => _edits;

    public bool IsActive => !_ended;

    /// <summary>IME composition update: replaces the current composition range without committing.</summary>
    public void Compose(WebSurfaceTextSource source, int start, int length, string composition, WebSurfaceTextRange selectionAfter)
        => Apply(new WebSurfaceTextEdit(source, WebSurfaceTextEditKind.Compose, start, length, composition ?? string.Empty, selectionAfter));

    /// <summary>Inserts/replaces text and commits (an active composition is committed implicitly).</summary>
    public void Replace(WebSurfaceTextSource source, int start, int length, string text, WebSurfaceTextRange selectionAfter)
        => Apply(new WebSurfaceTextEdit(source, WebSurfaceTextEditKind.Replace, start, length, text ?? string.Empty, selectionAfter));

    /// <summary>Removes a range (backspace/delete).</summary>
    public void Delete(WebSurfaceTextSource source, int start, int length, WebSurfaceTextRange selectionAfter)
        => Apply(new WebSurfaceTextEdit(source, WebSurfaceTextEditKind.Delete, start, length, string.Empty, selectionAfter));

    /// <summary>Selection change without text change.</summary>
    public void Select(WebSurfaceTextSource source, WebSurfaceTextRange selection)
        => Apply(new WebSurfaceTextEdit(source, WebSurfaceTextEditKind.Select, selection.Start, selection.Length, string.Empty, selection));

    /// <summary>Ends the session (blur/hide/pause/dispose); further edits throw.</summary>
    public void End() => _ended = true;

    private void Apply(WebSurfaceTextEdit edit)
    {
        if (_ended)
            throw new InvalidOperationException("The text session has ended.");

        // The removed range is validated against the PRE-edit text (RemoveRange); the selection
        // is validated against the POST-edit text.
        string resultText = edit.Kind switch
        {
            WebSurfaceTextEditKind.Delete => RemoveRange(edit.Start, edit.Length),
            WebSurfaceTextEditKind.Compose or WebSurfaceTextEditKind.Replace =>
                RemoveRange(edit.Start, edit.Length).Insert(edit.Start, edit.Text),
            _ => _text,
        };

        if (!IsValidRange(resultText, edit.SelectionAfter.Start, edit.SelectionAfter.Length))
            throw new ArgumentOutOfRangeException(nameof(edit), $"Selection range [{edit.SelectionAfter.Start}..{edit.SelectionAfter.Start + edit.SelectionAfter.Length}) exceeds text length {resultText.Length}.");

        if (edit.Kind != WebSurfaceTextEditKind.Select)
            _text = resultText;
        _selection = edit.SelectionAfter;
        _edits.Add(edit);
    }

    private string RemoveRange(int start, int length)
    {
        ValidateInText(_text, start, length, "range");
        return _text.Remove(start, length);
    }

    private static bool IsValidRange(string text, int start, int length) =>
        start >= 0 && length >= 0 && start + length <= text.Length;

    private void ValidateInText(string text, int start, int length, string parameter)
    {
        if (!IsValidRange(text, start, length))
            throw new ArgumentOutOfRangeException(
                parameter,
                $"Range [{start}..{start + length}) exceeds text length {text.Length}.");
    }
}
