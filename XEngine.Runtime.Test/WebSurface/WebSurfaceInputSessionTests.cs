using System;
using System.Collections.Generic;
using XEngine.WebSurface;
using Xunit;

namespace XEngine.Runtime.Test.WebSurface;

/// <summary>
/// B6 — input contracts beyond the pointer path: keyboard focus gating (a focused surface
/// consumes keys so they do not fall through to XGUI navigation), forwarded key events and the
/// UTF-16 text session (composition, replace, delete, select; edit journal; session end).
/// </summary>
public sealed class WebSurfaceInputSessionTests
{
    private sealed class RecordingBackend : IWebSurfaceBackend
    {
        public WebSurfaceCapabilities Capabilities { get; } = WebSurfaceCapabilities.CpuBaseline("fake");
        public event Action<string>? Failed;
        public event Action? Ready;
        public List<WebSurfacePointerEvent> Pointers { get; } = [];
        public List<WebSurfaceKeyboardEvent> Keys { get; } = [];
        public void Load(WebSurfaceAsset asset, IWebSurfaceFrameSink sink) { }
        public void Pause() { }
        public void Resume() { }
        public void Reload() { }
        public bool RequestFrame() => false;
        public void SendPointer(WebSurfacePointerEvent pointerEvent) => Pointers.Add(pointerEvent);
        public void SendKeyboard(WebSurfaceKeyboardEvent keyboardEvent) => Keys.Add(keyboardEvent);
        public void Dispose() { }
    }

    // ------------------------------------------------------------------ keyboard focus

    [Fact]
    public void Keys_AreDroppedWithoutFocus_AndForwardedWhenFocused()
    {
        var backend = new RecordingBackend();
        var input = new WebSurfaceInput(() => 128, () => 128, backend);

        Assert.False(input.HasKeyboardFocus);
        Assert.False(input.ForwardKey(WebSurfaceKeyKind.Down, "a", 'a', WebSurfaceKeyModifiers.None));
        Assert.Empty(backend.Keys);

        input.AcquireKeyboardFocus();
        Assert.True(input.HasKeyboardFocus);
        Assert.True(input.ForwardKey(WebSurfaceKeyKind.Down, "a", 'a', WebSurfaceKeyModifiers.None));
        Assert.True(input.ForwardKey(WebSurfaceKeyKind.Up, "a", 'a', WebSurfaceKeyModifiers.None));
        Assert.Equal(2, backend.Keys.Count);
        Assert.Equal('a', backend.Keys[0].Character);
        Assert.Equal("a", backend.Keys[0].Key);
    }

    [Fact]
    public void ReleaseFocus_EndsTextSession_AndStopsKeyForwarding()
    {
        var backend = new RecordingBackend();
        var input = new WebSurfaceInput(() => 64, () => 64, backend);
        input.AcquireKeyboardFocus();
        var session = input.BeginTextSession("hello", WebSurfaceTextRange.Empty(5));
        Assert.True(session.IsActive);

        input.ReleaseKeyboardFocus();

        Assert.False(input.HasKeyboardFocus);
        Assert.False(session.IsActive);
        Assert.False(input.ForwardKey(WebSurfaceKeyKind.Down, "b", 'b', WebSurfaceKeyModifiers.None));
        Assert.Empty(backend.Keys);
        Assert.Null(input.TextSession);
    }

    [Fact]
    public void ReleaseCapture_AlsoEndsTextSession()
    {
        var input = new WebSurfaceInput(() => 64, () => 64);
        input.AcquireKeyboardFocus();
        var session = input.BeginTextSession("", WebSurfaceTextRange.Empty(0));
        input.ReleaseCapture();
        Assert.False(session.IsActive);
        Assert.False(input.HasKeyboardFocus);
    }

    [Fact]
    public void PointerDown_AcquiresCapture_UpReleases_ThenOutsideMovesAreRejected()
    {
        var backend = new RecordingBackend();
        var input = new WebSurfaceInput(() => 64, () => 64, backend);

        Assert.True(input.ForwardPointer(WebSurfacePointerKind.Down, 10, 10, 64, 64, pointerId: 3));
        Assert.True(input.Captured);
        // Drag far outside: captured drags keep going (clamped into the viewport).
        Assert.True(input.ForwardPointer(WebSurfacePointerKind.Move, 500, -20, 64, 64, pointerId: 3));
        Assert.True(input.ForwardPointer(WebSurfacePointerKind.Up, 500, -20, 64, 64, pointerId: 3));
        Assert.False(input.Captured);
        // Now outside moves without capture are rejected again.
        Assert.False(input.ForwardPointer(WebSurfacePointerKind.Move, 500, -20, 64, 64, pointerId: 3));
        Assert.Equal(3, backend.Pointers.Count);
    }

    // ------------------------------------------------------------------ text session

    [Fact]
    public void TextSession_ComposeThenCommit_BuildsTextAndJournal()
    {
        var session = WebSurfaceTextSession.Begin("name: ", WebSurfaceTextRange.Empty(6));

        // IME composition of 中文 over an empty range at the caret.
        session.Compose(WebSurfaceTextSource.Engine, 6, 0, "中", WebSurfaceTextRange.Empty(7));
        Assert.Equal("name: 中", session.Text);

        // Composition grows.
        session.Compose(WebSurfaceTextSource.Engine, 6, 1, "中文", WebSurfaceTextRange.Empty(8));
        Assert.Equal("name: 中文", session.Text);

        // Commit replaces the composition with the final text.
        session.Replace(WebSurfaceTextSource.Engine, 6, 2, "中文", WebSurfaceTextRange.Empty(8));
        Assert.Equal("name: 中文", session.Text);
        Assert.True(session.IsActive);

        Assert.Equal(3, session.Edits.Count);
        Assert.Equal(WebSurfaceTextEditKind.Compose, session.Edits[0].Kind);
        Assert.Equal(WebSurfaceTextEditKind.Replace, session.Edits[2].Kind);
        Assert.Equal("中文", session.Edits[2].Text);
    }

    [Fact]
    public void TextSession_DeleteAndSelect_UpdateMirroredState()
    {
        var session = WebSurfaceTextSession.Begin("abcdef", new WebSurfaceTextRange(0, 6));

        session.Delete(WebSurfaceTextSource.Platform, 0, 3, WebSurfaceTextRange.Empty(0));
        Assert.Equal("def", session.Text);

        session.Select(WebSurfaceTextSource.Platform, new WebSurfaceTextRange(1, 2));
        Assert.Equal(new WebSurfaceTextRange(1, 2), session.Selection);

        Assert.Equal(2, session.Edits.Count);
        Assert.Equal(WebSurfaceTextEditKind.Delete, session.Edits[0].Kind);
        Assert.Equal(WebSurfaceTextEditKind.Select, session.Edits[1].Kind);
    }

    [Fact]
    public void TextSession_RejectsOutOfRangeEdits()
    {
        var session = WebSurfaceTextSession.Begin("ab", WebSurfaceTextRange.Empty(2));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Delete(WebSurfaceTextSource.Engine, 1, 5, WebSurfaceTextRange.Empty(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Replace(WebSurfaceTextSource.Engine, 0, 2, "x", new WebSurfaceTextRange(9, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WebSurfaceTextSession.Begin("ab", new WebSurfaceTextRange(0, 3)));

        // Failed edits leave the mirrored state untouched and are not journaled.
        Assert.Equal("ab", session.Text);
        Assert.Empty(session.Edits);
    }

    [Fact]
    public void TextSession_EndRejectsFurtherEdits()
    {
        var session = WebSurfaceTextSession.Begin("", WebSurfaceTextRange.Empty(0));
        session.End();
        Assert.False(session.IsActive);
        Assert.Throws<InvalidOperationException>(() =>
            session.Replace(WebSurfaceTextSource.Engine, 0, 0, "x", WebSurfaceTextRange.Empty(1)));
        Assert.Empty(session.Edits);
    }

    [Fact]
    public void TextSession_EditsCarrySource()
    {
        var session = WebSurfaceTextSession.Begin("", WebSurfaceTextRange.Empty(0));
        session.Compose(WebSurfaceTextSource.Platform, 0, 0, "ni", WebSurfaceTextRange.Empty(2));
        session.Replace(WebSurfaceTextSource.Engine, 0, 2, "你", WebSurfaceTextRange.Empty(1));

        Assert.Equal(WebSurfaceTextSource.Platform, session.Edits[0].Source);
        Assert.Equal(WebSurfaceTextSource.Engine, session.Edits[1].Source);
        Assert.Equal("你", session.Text);
    }
}
