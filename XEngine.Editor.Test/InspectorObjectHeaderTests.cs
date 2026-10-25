using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.Editor.GUI.SceneView;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using Xunit;
using Fixture = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed class InspectorObjectHeaderTests
{
    [Theory]
    [InlineData(240f)]
    [InlineData(335f)]
    [InlineData(520f)]
    public void HeaderKeepsTwoCompactRowsAndAvailableNameWidth(float width)
    {
        using var f = new Fixture(width);
        var go = Select(f);
        var row = NameRow(f);
        Assert.Equal(20, row.LayoutHeight);
        Assert.InRange(row.LayoutWidth, 60, width);
        var icon = f.Elements().Single(e => e.Paragraph == EditorIcons.Circle);
        Assert.Equal(24, icon.LayoutWidth); Assert.Equal(24, icon.LayoutHeight);
        Assert.True(icon.X + 24 <= row.X);
        foreach (var e in f.Elements())
            if (e.Y < row.Y + 42 && e.LayoutWidth > 0)
            {
                Assert.True(e.X >= -.01f, $"left {e.Paragraph}: {e.X}");
                Assert.True(e.X + e.LayoutWidth <= width + .01f, $"right {e.Paragraph}: {e.X + e.LayoutWidth}");
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraftCommitsOnceOnEnterOrOutsideClickAndUndoRestoresIt(bool outsideClick)
    {
        using var f = new Fixture(335);
        var go = Select(f); BeginDraft(f, "Anbi_Body_Renamed");
        Assert.Equal("Anbi_Body", go.Name);
        Assert.False(EditorSceneManager.IsDirty); Assert.False(Undo.CanUndo);
        Assert.Equal("Anbi_Body_Renamed", Draft(f).Value);
        if (outsideClick) f.Click(300, 850); else Key(f, PaperKey.Enter);
        Assert.Equal("Anbi_Body_Renamed", go.Name);
        Assert.True(EditorSceneManager.IsDirty); Assert.True(Undo.CanUndo);
        Assert.False(f.Paper.IsElementFocused(NameRow(f).ID));
        f.Frame(); f.Frame();
        Undo.PerformUndo(); f.Frame();
        Assert.Equal("Anbi_Body", go.Name); Assert.False(Undo.CanUndo);
        Assert.Equal(go.Name, Draft(f).Value);
        Undo.PerformRedo(); f.Frame();
        Assert.Equal("Anbi_Body_Renamed", go.Name); Assert.False(Undo.CanRedo);
        Assert.Equal(go.Name, Draft(f).Value);
    }

    [Fact]
    public void SeparateConfirmedRenamesAreSeparateUndoSteps()
    {
        using var f = new Fixture(335);
        var go = Select(f);
        BeginDraft(f, "First"); Key(f, PaperKey.Enter);
        BeginDraft(f, "Second"); Key(f, PaperKey.Enter);
        Undo.PerformUndo(); f.Frame(); Assert.Equal("First", go.Name);
        Undo.PerformUndo(); f.Frame(); Assert.Equal("Anbi_Body", go.Name);
        Assert.False(Undo.CanUndo);
    }

    [Theory]
    [InlineData("Unwanted", true)]
    [InlineData("   ", false)]
    [InlineData("Anbi_Body", false)]
    public void CancelBlankOrUnchangedDraftDoesNotDirtyOrRecordUndo(string draft, bool cancel)
    {
        using var f = new Fixture(335);
        var go = Select(f);
        long version = EditorSceneManager.ChangeVersion;
        BeginDraft(f, draft); Key(f, cancel ? PaperKey.Escape : PaperKey.Enter);
        Assert.Equal("Anbi_Body", go.Name); Assert.Equal(go.Name, Draft(f).Value);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        Assert.Equal(version, EditorSceneManager.ChangeVersion);
    }

    [Fact]
    public void SelectionSwitchDropsOrphanedDraftAndNeverRenamesNewTarget()
    {
        using var f = new Fixture(335);
        var first = Select(f); var next = new GameObject("Anbi_Face"); f.Scene.Add(next);
        BeginDraft(f, "OldDraft");
        Selection.Select(next); f.Frame(); f.Frame();
        Key(f, PaperKey.Enter);
        Assert.Equal("Anbi_Body", first.Name); Assert.Equal("Anbi_Face", next.Name);
        Selection.Select(first); f.Frame(); f.Frame();
        Assert.Equal(first.Name, Draft(f).Value); Assert.False(Undo.CanUndo);
        BeginDraft(f, "NewDraft"); Key(f, PaperKey.Enter);
        Assert.Equal("NewDraft", first.Name); Assert.Equal("Anbi_Face", next.Name);
    }

    [Fact]
    public void RemovedTargetRejectsPendingBlurCallback()
    {
        using var f = new Fixture(335);
        var go = Select(f); BeginDraft(f, "RemovedDraft");
        f.Scene.Remove(go); go.Dispose();
        f.Paper.ClearFocus(); Undo.FlushFrame();
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        Selection.Clear(); f.Frame();
    }

    [Fact]
    public void ConfirmedRenameAndUndoRedoQueueRepaintButDraftAndCancelDoNot()
    {
        using var f = new Fixture(335);
        var instance = typeof(EditorApplication).GetProperty(nameof(EditorApplication.Instance))!;
        var previous = EditorApplication.Instance;
        var editor = new EditorApplication();
        instance.SetValue(null, editor);
        try
        {
            var go = Select(f);
            editor.FrameScheduler.ShouldRender(0); // Drain selection's repaint.
            BeginDraft(f, "Anbi_Body_Renamed");
            Assert.False(editor.FrameScheduler.ShouldRender(1));
            Key(f, PaperKey.Escape);
            Assert.False(editor.FrameScheduler.ShouldRender(2));
            BeginDraft(f, "Anbi_Body_Renamed"); Key(f, PaperKey.Enter);
            Assert.True(editor.FrameScheduler.ShouldRender(3));
            Assert.Equal(EditorSurfaceDirtyReason.ExplicitRepaint, editor.FrameScheduler.LastRenderReasons);
            Assert.False(editor.FrameScheduler.ShouldRender(4));
            Undo.PerformUndo(); f.Frame();
            Assert.Equal("Anbi_Body", go.Name);
            Assert.True(editor.FrameScheduler.ShouldRender(5));
            Assert.False(editor.FrameScheduler.ShouldRender(6));
            Undo.PerformRedo(); f.Frame();
            Assert.Equal("Anbi_Body_Renamed", go.Name);
            Assert.True(editor.FrameScheduler.ShouldRender(7));
            Assert.False(editor.FrameScheduler.ShouldRender(8));
            EditorSceneManager.IsDirty = false;
            Assert.False(editor.FrameScheduler.ShouldRender(9));
        }
        finally { instance.SetValue(null, previous); }
    }

    private static GameObject Select(Fixture f)
    {
        var go = new GameObject("Anbi_Body"); f.Scene.Add(go);
        Selection.Select(go); f.Frame(); f.Frame();
        EditorSceneManager.IsDirty = false; Undo.Clear();
        return go;
    }
    private static ElementData NameRow(Fixture f) => f.Elements().First(e => e.IsAHookedParent);
    private static ElementBuilder.TextInputState Draft(Fixture f)
    {
        var row = NameRow(f);
        var child = row.ChildIndices.First(i => f.Paper.GetElementData(i).IsHookedToParent);
        return f.Paper.GetElementStorage(new ElementHandle(f.Paper, child), "TextInputState",
            new ElementBuilder.TextInputState { Value = (Selection.ActiveObject as GameObject)?.Name ?? "" });
    }
    private static void BeginDraft(Fixture f, string draft)
    {
        var row = NameRow(f); f.Click(row.X + 10, row.Y + 10);
        f.Frame(() => f.Paper.AddInputCharacter(draft)); f.Frame();
    }
    private static void Key(Fixture f, PaperKey key)
    {
        f.Frame(() => f.Paper.SetKeyState(key, true));
        f.Frame(() => f.Paper.SetKeyState(key, false)); f.Frame();
    }
}
