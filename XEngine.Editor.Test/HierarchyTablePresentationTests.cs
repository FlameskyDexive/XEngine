using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class HierarchyTablePresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualPanelUsesTableBoundsAndLeftActionsWithoutChangingSelection(bool indexed)
    {
        var previousScene = Scene.Current;
        var previousFont = EditorTheme.DefaultFont;
        bool previousIndexed = Program.IncrementalHierarchy;
        bool previousEditor = Application.IsEditor;
        var scene = new Scene { Name = "TableScene" };
        var first = new GameObject("FirstTableObject");
        var child = new GameObject("TableChild") { LabelIcon = 1 };
        child.SetParent(first); scene.Add(first);
        var other = new GameObject("SecondTableObject"); scene.Add(other);
        var panel = new HierarchyPanel();
        var paper = NewPaper(325, 300);
        typeof(Scene).GetProperty(nameof(Scene.Current))!.SetValue(null, scene);
        typeof(Program).GetProperty(nameof(Program.IncrementalHierarchy))!.SetValue(null, indexed);
        Application.IsEditor = true;
        EditorTheme.DefaultFont = Font();
        using var theme = Origami.PushTheme(EditorTheme.BuildOrigamiTheme());
        using var surface = Origami.PushOffscreenSurface();
        try
        {
            Selection.Clear(); Undo.Clear(); SceneVisibilityManager.Reset();
            var expands = (Dictionary<string, bool>)typeof(HierarchyPanel)
                .GetField("_expandStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
            Frame(); expands[first.Identifier.ToString()] = true;
            panel.OnScriptReloadCleanup();
            for (int i = 0; i < 20; i++) Frame();
            var header = Text(XEngine.Rosetta.Loc.Get("inspector.name"));
            var sceneName = Text(scene.Name);
            var firstName = Text(first.Name);
            var childName = Text(child.Name);
            var otherName = Text(other.Name);
            Assert.Equal(20, header.LayoutHeight);
            Assert.Equal(40, header.X);
            Assert.Equal(16, sceneName.LayoutHeight);
            Assert.Equal(16, firstName.Y - sceneName.Y);
            Assert.Equal(16, childName.Y - firstName.Y);
            Assert.Equal(16, otherName.Y - childName.Y);
            Assert.Equal(90, firstName.X); // Name includes its4px inner inset.
            Assert.Equal(14, childName.X - firstName.X);
            var visibility = Glyph(EditorIcons.Eye, firstName.Y);
            var picking = Glyph(EditorIcons.Hand, firstName.Y);
            Assert.Equal(0, visibility.X); Assert.Equal(20, visibility.LayoutWidth);
            Assert.Equal(20, picking.X); Assert.Equal(20, picking.LayoutWidth);
            Click(10, firstName.Y + 8); Frame();
            Assert.True(SceneVisibilityManager.IsHidden(first));
            Assert.False(SceneVisibilityManager.IsHidden(other));
            Assert.Null(Selection.ActiveObject); Assert.True(first.Enabled);
            Undo.PerformUndo(); Frame(); Assert.False(SceneVisibilityManager.IsHidden(first));
            Undo.PerformRedo(); Frame(); Assert.True(SceneVisibilityManager.IsHidden(first));
            Click(30, firstName.Y + 8); Frame();
            Assert.False(SceneVisibilityManager.IsPickingEnabled(first));
            Assert.True(SceneVisibilityManager.IsPickingEnabled(other));
            Assert.Null(Selection.ActiveObject); Assert.True(first.Enabled);
            Undo.PerformUndo(); Frame(); Assert.True(SceneVisibilityManager.IsPickingEnabled(first));
            Click(120, firstName.Y + 8); Frame(); Assert.Same(first, Selection.ActiveObject);
            // Folding the scene changes presentation, not the selected identity.
            Click(52, sceneName.Y + 8); Frame();
            Assert.Same(first, Selection.ActiveObject);
            Assert.DoesNotContain(Elements(paper), e => e.Paragraph == first.Name);
            Click(52, sceneName.Y + 8);
            for (int i = 0; i < 20; i++) Frame();
            Assert.Same(first, Selection.ActiveObject);
            Assert.Equal(16, Text(child.Name).Y - Text(first.Name).Y);
            // Switching the diagnostic mode must replace the model even when all
            // scene/selection versions are unchanged. A stale index can otherwise
            // address pooled nodes after the scene shrinks.
            long builds = panel.TreeModelBuilds;
            typeof(Program).GetProperty(nameof(Program.IncrementalHierarchy))!.SetValue(null, !indexed);
            Frame();
            Assert.Equal(builds + 1, panel.TreeModelBuilds);
            Assert.Equal(!indexed, HasRowIndex());
            scene.Remove(other); other.Dispose(); Frame();
            Assert.DoesNotContain(Elements(paper), e => e.Paragraph == other.Name);
            typeof(Program).GetProperty(nameof(Program.IncrementalHierarchy))!.SetValue(null, indexed);
            Frame(); Assert.Equal(indexed, HasRowIndex());
            Assert.Same(first, Selection.ActiveObject);
        }
        finally
        {
            panel.OnClosed(); paper.Canvas.Dispose(); Selection.Clear(); Undo.Clear(); SceneVisibilityManager.Reset();
            typeof(Scene).GetProperty(nameof(Scene.Current))!.SetValue(null, previousScene);
            scene.Dispose(); EditorTheme.DefaultFont = previousFont; Application.IsEditor = previousEditor;
            typeof(Program).GetProperty(nameof(Program.IncrementalHierarchy))!.SetValue(null, previousIndexed);
        }
        void Frame(Action? input = null)
        {
            paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60); input?.Invoke();
            using (paper.Column("host").Size(325, 300).Enter()) panel.OnGUI(paper, 325, 300);
            Origami.EndFrame(paper); paper.EndFrame();
        }
        void Click(float x, float y)
        {
            paper.SetPointerPosition(x, y); Frame();
            Frame(() => paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
            Frame(() => paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false));
        }
        ElementData Text(string text) => Elements(paper).Single(e => e.Paragraph == text);
        ElementData Glyph(string glyph, float y) => Elements(paper).Single(e => e.Paragraph == glyph && MathF.Abs(e.Y - y) < .1f);
        bool HasRowIndex() => typeof(HierarchyPanel).GetField("_rowIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel) != null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenLeadingColumnsStayFixedAndKeepReboundClickIdentity(bool indexed)
    {
        var paper = NewPaper(200, 100);
        var nodes = new List<TreeNode> { new() { Id = "first", Depth = 0, HasChildren = true, DefaultExpanded = true }, new() { Id = "deep", Depth = 1 } };
        var columns = new Dictionary<string, float>();
        float labelX = 0;
        string? clicked = null, selected = null;
        using var tree = Origami.Tree(paper, "frozen_table", 200, 100).Nodes(nodes)
            .RowHeight(16).BaseIndent(20).TopPadding(0).Padding(0).ContentWidth(600)
            .LeadingRowContent(40, (p, n, _, _, _) => p.Box("cell_" + n.Id).Size(40, 16)
                .OnPostLayout((_, r) => columns[n.Id] = (float)r.Min.X)
                .StopEventPropagation().OnClick(n.Id, (id, _) => clicked = id))
            .CustomRowContent((p, n, _, _) => p.Box("name_" + n.Id).Height(16)
                .OnPostLayout((_, r) => { if (n.Id != "deep") labelX = (float)r.Min.X; }))
            .OnSelect(e => selected = e.Node.Id);
        if (indexed) tree.RowIndex(new HierarchyRowIndex(nodes, new(), new()));
        try
        {
            Frame(); Frame(); Assert.Equal(72, labelX);
            Assert.Equal(0, columns["first"]); Assert.Equal(0, columns["deep"]);
            Origami.ScrollTo("frozen_table_scroll", new Float2(200, 0));
            for (int i = 0; i < 100; i++) Frame();
            Assert.True(labelX < 0); Assert.InRange(columns["first"], -.01f, .01f);
            Assert.InRange(columns["deep"], -.01f, .01f);
            Click(); Assert.Equal("first", clicked); Assert.Null(selected);
            nodes[0] = new TreeNode { Id = "replacement", HasChildren = true, DefaultExpanded = true };
            if (indexed) tree.RowIndex(new HierarchyRowIndex(nodes, new(), new()));
            Frame(); Frame(); Click(); Assert.Equal("replacement", clicked); Assert.Null(selected);
            tree.LeadingRowContent(0, null).BaseIndent(26).ContentWidth(200);
            Frame(); Frame(); Assert.Equal(38, labelX);
        }
        finally { paper.Canvas.Dispose(); }
        void Frame(Action? input = null)
        {
            paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60); input?.Invoke();
            tree.Show(); Origami.EndFrame(paper); paper.EndFrame();
        }
        void Click()
        {
            paper.SetPointerPosition(10, 8); Frame();
            Frame(() => paper.SetPointerState(PaperMouseBtn.Left, 10, 8, true, false));
            Frame(() => paper.SetPointerState(PaperMouseBtn.Left, 10, 8, false, false)); Frame();
        }
    }

    private static List<ElementData> Elements(Paper paper)
    {
        var result = new List<ElementData>();
        for (int i = 0; i < paper.ElementCount; i++) result.Add(paper.GetElementData(i));
        return result;
    }
    private static FontFile Font() => new(Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
    private static Paper NewPaper(int width, int height) => new(new Renderer(), width, height, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
