using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Rosetta;
using Xunit;
using Fixture = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed class InspectorMultiObjectHeaderTests
{
    [Theory]
    [InlineData(240f)]
    [InlineData(335f)]
    [InlineData(520f)]
    public void HeaderHasCompactRowsCountAndContainedControls(float width)
    {
        using var f = new Fixture(width); Select(f);
        var name = NameRow(f); var icon = f.Text(EditorIcons.Cubes);
        Assert.Equal(20, name.LayoutHeight);
        Assert.InRange(name.LayoutWidth, 20, width);
        Assert.Equal(24, icon.LayoutWidth); Assert.Equal(24, icon.LayoutHeight);
        Assert.True(icon.X + icon.LayoutWidth <= name.X);
        var count = f.Text("(2)"); Assert.Equal(name.Y, count.Y);
        Assert.True(count.X >= name.X + name.LayoutWidth);
        var tag = f.Text(Loc.Get("inspector.tag")); Assert.Equal(name.Y + 22, tag.Y);
        foreach (var e in f.Elements())
            if (e.Y < name.Y + 42 && e.LayoutWidth > 0)
            {
                Assert.True(e.X >= -.01f, $"left {e.Paragraph}: {e.X}");
                Assert.True(e.X + e.LayoutWidth <= width + .01f, $"right {e.Paragraph}: {e.X + e.LayoutWidth}");
            }
        f.Click(name.X + 10, name.Y + 10);
        Assert.Equal("", Draft(f).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NameDraftCommitsOneGroupOnEnterOrBlur(bool blur)
    {
        using var f = new Fixture(335); var (a, b) = Select(f);
        BeginDraft(f, "Anbi_Parts");
        Assert.Equal("Anbi_Body", a.Name); Assert.Equal("Anbi_Face", b.Name);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        if (blur) f.Click(300, 850); else Key(f, PaperKey.Enter);
        Assert.Equal("Anbi_Parts", a.Name); Assert.Equal(a.Name, b.Name);
        Assert.True(EditorSceneManager.IsDirty);
        Undo.PerformUndo(); f.Frame();
        Assert.Equal("Anbi_Body", a.Name); Assert.Equal("Anbi_Face", b.Name);
        Assert.False(Undo.CanUndo); Assert.Equal("", Draft(f).Value);
        Undo.PerformRedo(); f.Frame();
        Assert.Equal("Anbi_Parts", a.Name); Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Name, Draft(f).Value); Assert.False(Undo.CanRedo);
    }

    [Theory]
    [InlineData("Rejected", true)]
    [InlineData("   ", false)]
    public void CancelOrBlankDraftPreservesValuesDirtyAndRedo(string text, bool cancel)
    {
        using var f = new Fixture(335); var (a, b) = Select(f);
        BeginDraft(f, "Committed"); Key(f, PaperKey.Enter);
        Undo.PerformUndo(); f.Frame(); EditorSceneManager.IsDirty = false;
        BeginDraft(f, text); Key(f, cancel ? PaperKey.Escape : PaperKey.Enter);
        Assert.Equal("Anbi_Body", a.Name); Assert.Equal("Anbi_Face", b.Name);
        Assert.False(EditorSceneManager.IsDirty); Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo);
    }

    [Fact]
    public void SeparateRenamesHaveSeparateUndoStepsAndUnchangedNameKeepsRedo()
    {
        using var f = new Fixture(335); var (a, b) = Select(f);
        BeginDraft(f, "First"); Key(f, PaperKey.Enter);
        BeginDraft(f, "Second"); Key(f, PaperKey.Enter);
        Undo.PerformUndo(); f.Frame(); Assert.Equal("First", a.Name); Assert.Equal(a.Name, b.Name);
        EditorSceneManager.IsDirty = false;
        BeginDraft(f, "First"); Key(f, PaperKey.Enter);
        Assert.False(EditorSceneManager.IsDirty); Assert.True(Undo.CanRedo);
        Undo.PerformUndo(); f.Frame();
        Assert.Equal("Anbi_Body", a.Name); Assert.Equal("Anbi_Face", b.Name); Assert.False(Undo.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedActiveOrStaticClickAppliesAllAndRestoresIndividualValues(bool isStatic)
    {
        using var f = new Fixture(335); var (a, b) = Select(f);
        if (isStatic) a.IsStatic = true; else b.Enabled = false;
        f.Frame(); f.Frame();
        var name = NameRow(f);
        var toggle = f.Elements().Single(e => e.OnClick != null && Math.Abs(e.Y - name.Y) < .1f
            && (isStatic ? e.X >= name.X + name.LayoutWidth + 10 : e.X < name.X && e.LayoutWidth < 30));
        f.Click(toggle.X + 8, toggle.Y + 10);
        Assert.True(isStatic ? a.IsStatic && b.IsStatic : a.Enabled && b.Enabled);
        Assert.True(EditorSceneManager.IsDirty); Assert.Equal(2, Selection.Count);
        Undo.PerformUndo(); f.Frame();
        Assert.True(isStatic ? a.IsStatic && !b.IsStatic : a.Enabled && !b.Enabled);
        Assert.False(Undo.CanUndo);
        Undo.PerformRedo(); f.Frame();
        Assert.True(isStatic ? a.IsStatic && b.IsStatic : a.Enabled && b.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedTagOrLayerMenuRestoresIndividualValuesAndEqualChoiceKeepsRedo(bool layer)
    {
        using var f = new Fixture(335); var (a, b) = Select(f);
        if (layer) b.LayerIndex = 2; else b.Tag = "Player";
        f.Frame(); f.Frame();
        Assert.Equal(1, f.Elements().Count(e => e.Paragraph == "—"));
        OpenMenu(f, layer);
        Choose(f, layer ? "Default" : "Untagged");
        if (layer) Assert.Equal(0, b.LayerIndex); else Assert.Equal(a.Tag, b.Tag);
        Assert.True(EditorSceneManager.IsDirty);
        Undo.PerformUndo(); f.Frame();
        Assert.Equal(0, a.LayerIndex); Assert.Equal(layer ? 2 : 0, b.LayerIndex);
        Assert.Equal("Untagged", a.Tag); Assert.Equal(layer ? "Untagged" : "Player", b.Tag);
        Assert.False(Undo.CanUndo); Undo.PerformRedo(); f.Frame();
        // Add another change and Undo it, leaving a Redo chain over uniform values.
        OpenMenu(f, layer); Choose(f, layer ? "Ignore Raycast" : "Player");
        Undo.PerformUndo(); f.Frame(); EditorSceneManager.IsDirty = false;
        OpenMenu(f, layer); Choose(f, layer ? "Default" : "Untagged");
        Assert.False(EditorSceneManager.IsDirty); Assert.True(Undo.CanRedo);
    }

    [Fact]
    public void DraftDoesNotFollowChangedSelectionWithSameFirstObjectAndCount()
    {
        using var f = new Fixture(335); var (a, b) = Select(f);
        var c = new GameObject("Anbi_Hair"); f.Scene.Add(c);
        BeginDraft(f, "OldDraft"); var oldId = NameRow(f).ID;
        Selection.Select(a); Selection.AddToSelection(c); f.Frame(); f.Frame();
        Assert.NotEqual(oldId, NameRow(f).ID); Key(f, PaperKey.Enter);
        Assert.Equal("Anbi_Body", a.Name); Assert.Equal("Anbi_Face", b.Name); Assert.Equal("Anbi_Hair", c.Name);
        Assert.False(Undo.CanUndo);
        BeginDraft(f, "NewDraft"); Key(f, PaperKey.Enter);
        Assert.Equal("NewDraft", a.Name); Assert.Equal("NewDraft", c.Name); Assert.Equal("Anbi_Face", b.Name);
    }

    [Fact]
    public void OpenMenuOwnsDisplayedTargetsAfterInspectorReusesSelectionList()
    {
        using var f = new Fixture(335); var (a, b) = Select(f);
        var c = new GameObject("Anbi_Hair"); f.Scene.Add(c);
        OpenMenu(f, false);
        Selection.Select(a); Selection.AddToSelection(c); f.Frame(); f.Frame();
        Choose(f, "Player");
        Assert.Equal("Player", a.Tag); Assert.Equal("Player", b.Tag); Assert.Equal("Untagged", c.Tag);
        Undo.PerformUndo(); f.Frame();
        Assert.Equal("Untagged", a.Tag); Assert.Equal("Untagged", b.Tag);
    }

    [Fact]
    public void RemovedTargetRejectsEntirePendingNameCommit()
    {
        using var f = new Fixture(335); var (a, b) = Select(f);
        BeginDraft(f, "Stale"); f.Scene.Remove(b); b.Dispose();
        f.Paper.ClearFocus(); Undo.FlushFrame();
        Assert.Equal("Anbi_Body", a.Name); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    private static (GameObject, GameObject) Select(Fixture f)
    {
        var a = new GameObject("Anbi_Body"); var b = new GameObject("Anbi_Face");
        f.Scene.Add(a); f.Scene.Add(b); Selection.Select(a); Selection.AddToSelection(b);
        f.Frame(); f.Frame(); EditorSceneManager.IsDirty = false; Undo.Clear(); return (a, b);
    }
    private static ElementData NameRow(Fixture f) => f.Elements().First(e => e.IsAHookedParent);
    private static ElementBuilder.TextInputState Draft(Fixture f)
    {
        var row = NameRow(f);
        int child = row.ChildIndices.First(i => f.Paper.GetElementData(i).IsHookedToParent);
        return f.Paper.GetElementStorage(new ElementHandle(f.Paper, child), "TextInputState", new ElementBuilder.TextInputState());
    }
    private static void BeginDraft(Fixture f, string value)
    {
        var row = NameRow(f); f.Click(row.X + 10, row.Y + 10);
        f.Frame(() => f.Paper.AddInputCharacter(value)); f.Frame();
    }
    private static void Key(Fixture f, PaperKey key)
    {
        f.Frame(() => f.Paper.SetKeyState(key, true)); f.Frame(() => f.Paper.SetKeyState(key, false)); f.Frame();
    }
    private static void OpenMenu(Fixture f, bool layer)
    {
        var label = f.Text(Loc.Get(layer ? "inspector.layer" : "inspector.tag")); f.Click(label.X + 3, label.Y + 10);
        Assert.True(ContextMenu.IsOpen);
    }
    private static void Choose(Fixture f, string value)
    {
        var item = f.Elements().Last(e => e.Paragraph == value);
        f.Click(item.X + 5, item.Y + item.LayoutHeight / 2);
        Assert.False(ContextMenu.IsOpen);
    }
}
