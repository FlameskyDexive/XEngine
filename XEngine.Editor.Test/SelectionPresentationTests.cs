using Color = System.Drawing.Color;
using System.Reflection;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class SelectionPresentationTests
{
    [Theory]
    [InlineData(200, 200, false, true)]
    [InlineData(100, 200, true, false)]
    [InlineData(0, 200, true, true)]
    [InlineData(200, 0, false, false)]
    public void NativeSurfaceFocusRecoversMissedCallbackAndRejectsStaleFocus(int foreground, int host, bool callback, bool expected)
        => Assert.Equal(expected, XEngine.Editor.Core.EditorNativeSurfaceManager.ComputeSurfaceFocused(foreground, host, callback));

    [Fact]
    public void ActualHierarchySearchClickKeepsSelectionAndTypingDoesNotClearIt()
    {
        var paper = NewPaper();
        var panel = new XEngine.Editor.GUI.Panels.HierarchyPanel();
        var theme = FontTheme(editor: true);
        var previousFont = EditorTheme.DefaultFont;
        var marker = new object();
        using var scope = Origami.PushTheme(theme);
        try
        {
            EditorTheme.DefaultFont = theme.Font;
            XEngine.Editor.Core.Selection.Select(marker);
            long version = XEngine.Editor.Core.Selection.ChangeVersion;
            Frame(); Frame();
            var search = Enumerable.Range(0, paper.ElementCount).Select(i => paper.GetElementData(i))
                .First(d => d.IsAHookedParent);
            float x = search.X + search.LayoutWidth / 2, y = search.Y + search.LayoutHeight / 2;
            paper.SetPointerPosition(x, y);
            Frame(() => paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
            Frame(() => paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false));
            Frame();
            Assert.Same(marker, XEngine.Editor.Core.Selection.ActiveObject);
            Assert.Equal(version, XEngine.Editor.Core.Selection.ChangeVersion);
            Frame(() => paper.AddInputCharacter("camera")); Frame();
            Assert.Same(marker, XEngine.Editor.Core.Selection.ActiveObject);
        }
        finally
        {
            panel.OnClosed(); paper.Canvas.Dispose();
            XEngine.Editor.Core.Selection.Clear(); EditorTheme.DefaultFont = previousFont;
        }
        void Frame(Action? input = null)
        {
            paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60);
            input?.Invoke(); panel.OnGUI(paper, 300, 260);
            Origami.EndFrame(paper); paper.EndFrame();
        }
    }

    [Fact]
    public void DetachedFocusInvalidatesPresentationOnceAndDetachClearsFocus()
    {
        var host = new XEngine.Editor.Core.EditorSurfaceHost(1, new DockSpace(new DockNode()), default);
        Assert.False(host.IsNativeFocused);
        host.SetNativeFocused(true);
        Assert.True(host.FrameScheduler.ShouldRender(0));
        Assert.False(host.FrameScheduler.ShouldRender(1));
        host.SetNativeFocused(true);
        Assert.False(host.FrameScheduler.ShouldRender(2));
        host.DetachNativeWindow();
        Assert.False(host.IsNativeFocused);
        Assert.True(host.FrameScheduler.ShouldRender(3));
        Assert.False(host.FrameScheduler.ShouldRender(4));
    }

    [Fact]
    public void CollectionFocusExcludesSiblingSearchAndInspectorAndReturnsWithoutSelectionChanges()
    {
        var paper = NewPaper();
        string focus = "row";
        bool focused = false;
        try
        {
            Frame(); Frame(); Assert.True(focused);
            focus = "search"; Frame(); Frame(); Assert.False(focused);
            focus = "inspector"; Frame(); Frame(); Assert.False(focused);
            focus = "row"; Frame(); Frame(); Assert.True(focused);
        }
        finally { paper.Canvas.Dispose(); }

        void Frame()
        {
            paper.BeginFrame(1f / 60);
            using (paper.Column("panel").Size(300, 220).Enter())
            {
                using (paper.Box("search").Size(200, 20).Enter())
                    if (focus == "search") paper.SetFocus();
                using (paper.Column("collection").Size(200, 100).Enter())
                {
                    focused = EditorSelectionPresentation.IsFocused(paper);
                    using (paper.Box("row").Size(200, 20).Enter())
                        if (focus == "row") paper.SetFocus();
                }
                using (paper.Box("inspector").Size(200, 20).Enter())
                    if (focus == "inspector") paper.SetFocus();
            }
            paper.EndFrame();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TreeAndTableRepaintOnceOnFocusAndStyleChangeAndKeepMultiSelection(bool table)
    {
        var paper = NewPaper();
        var observer = new Observer();
        var theme = FontTheme(editor: true);
        bool focused = true;
        int callbacks = 0;
        var nodes = new List<TreeNode>
        {
            new() { Id = "a", Label = "first" },
            new() { Id = "b", Label = "second" },
            new() { Id = "c", Label = "unselected" },
        };
        using var scope = Origami.PushTheme(theme);
        using var tree = Origami.Tree(paper, "tree", 280, 160).Nodes(nodes)
            .IsSelected(n => n.Id != "c").OnSelect(_ => callbacks++);
        try
        {
            Frame(); Frame(); Frame();
            Assert.False(observer.Changed);
            AssertSelectedText(Color.White);
            focused = false; Frame(); Assert.True(observer.Changed);
            AssertSelectedText(Color.FromArgb(210, 210, 210));
            if (table) Assert.Equal(2, RowFillCount(Color.FromArgb(77, 77, 77)));
            Frame(); Assert.False(observer.Changed);
            focused = true; Frame(); Assert.True(observer.Changed);
            AssertSelectedText(Color.White);
            Frame(); Assert.False(observer.Changed);
            focused = false; Frame(); Frame();
            theme.SelectionStyle = new(Color.DarkGray, Color.White, Color.Yellow);
            Frame(); Assert.True(observer.Changed); AssertSelectedText(Color.Yellow);
            if (table) Assert.Equal(2, RowFillCount(Color.DarkGray));
            Frame(); Assert.False(observer.Changed);
            Assert.Equal(0, callbacks); // presentation never invokes a selection mutation
        }
        finally { observer.State.Dispose(); paper.Canvas.Dispose(); }

        void Frame()
        {
            paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60);
            using (paper.Box("host").Size(280, 200).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                if (table)
                {
                    var builder = Origami.Table(paper, "table", -1, _ => callbacks++)
                        .Column("Name", 1).IsSelected(i => i < 2).SelectionFocused(focused);
                    foreach (var node in nodes) builder.Row().Cell(node.Label, Color.Pink);
                    builder.Show();
                }
                else tree.SelectionFocused(focused).Show();
            }
            Origami.EndFrame(paper); paper.EndFrame();
        }

        void AssertSelectedText(Color expected)
        {
            Assert.Equal(expected.ToArgb(), TextColor(paper, "first").ToArgb());
            Assert.Equal(expected.ToArgb(), TextColor(paper, "second").ToArgb());
            Assert.NotEqual(expected.ToArgb(), TextColor(paper, "unselected").ToArgb());
        }

        int RowFillCount(Color expected)
        {
            int count = 0;
            for (int i = 0; i < paper.ElementCount; i++)
                if (ReadStyleColor(paper.GetElementData(i), "GetBackgroundColor").ToArgb() == expected.ToArgb()) count++;
            return count;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandaloneWidgetsPreserveTheirOriginalColorsWhenHostIsInactive(bool table)
    {
        var paper = NewPaper();
        var theme = FontTheme(editor: false);
        using var scope = Origami.PushTheme(theme);
        try
        {
            Frame(true); var active = TextColor(paper, "selected");
            Frame(false); Assert.Equal(active.ToArgb(), TextColor(paper, "selected").ToArgb());
            Assert.Null(theme.SelectionStyle);
            if (table) Assert.Equal(Color.Pink.ToArgb(), active.ToArgb());
            else Assert.Equal(theme.Ink.C600.ToArgb(), active.ToArgb());
        }
        finally { paper.Canvas.Dispose(); }

        void Frame(bool focused)
        {
            paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60);
            if (table)
            {
                var builder = Origami.Table(paper, "table", 0, _ => { }).Column("Name", 1).SelectionFocused(focused);
                builder.Row().Cell("selected", Color.Pink); builder.Show();
            }
            else
            {
                using var tree = Origami.Tree(paper, "tree", 280, 160)
                    .Nodes([new() { Id = "a", Label = "selected" }]).IsSelected(_ => true).SelectionFocused(focused);
                tree.Show();
            }
            Origami.EndFrame(paper); paper.EndFrame();
        }
    }

    [Fact]
    public void ThemeCloneAndTransitionPreserveEditorSelectionProfileAndAllowRemoval()
    {
        var original = FontTheme(editor: true);
        var clone = original.Clone();
        Assert.Equal(original.SelectionStyle, clone.SelectionStyle);
        clone.SelectionStyle = new(Color.Black, Color.White, Color.Gray);
        Assert.NotEqual(original.SelectionStyle, clone.SelectionStyle);
        var midpoint = OrigamiTheme.Lerp(original, clone, .5f);
        Assert.NotEqual(original.SelectionStyle, midpoint.SelectionStyle);
        Assert.NotEqual(clone.SelectionStyle, midpoint.SelectionStyle);
        Assert.Null(OrigamiTheme.Lerp(original, OrigamiTheme.CreateDefaults(), 1).SelectionStyle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectCustomGridAndSubassetCardsChangeColorsWithoutChangingSelection(bool subasset)
    {
        var paper = NewPaper();
        var panel = new XEngine.Editor.GUI.Panels.ProjectPanel();
        var item = new XEngine.Editor.GUI.Panels.ContentItem
        { Name = "Fixture", RelativePath = "Fixture.mesh", TypeLabel = "Mesh", Guid = Guid.NewGuid(), IsSubAsset = subasset };
        var objects = new List<object> { item };
        var theme = FontTheme(editor: true);
        var focus = panel.GetType().GetField("_contentSelectionFocused", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var draw = panel.GetType().GetMethod(subasset ? "DrawSubThumb" : "DrawGridItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var scope = Origami.PushTheme(theme);
        try
        {
            XEngine.Editor.Core.Selection.Select(item);
            long version = XEngine.Editor.Core.Selection.ChangeVersion;
            Frame(true); Assert.Equal(Color.White.ToArgb(), TextColor(paper, "Fixture").ToArgb());
            Frame(false); Assert.Equal(Color.FromArgb(210, 210, 210).ToArgb(), TextColor(paper, "Fixture").ToArgb());
            Frame(true); Assert.Equal(Color.White.ToArgb(), TextColor(paper, "Fixture").ToArgb());
            Assert.Same(item, XEngine.Editor.Core.Selection.ActiveObject);
            Assert.Equal(version, XEngine.Editor.Core.Selection.ChangeVersion);
        }
        finally { XEngine.Editor.Core.Selection.Clear(); panel.OnClosed(); paper.Canvas.Dispose(); }

        void Frame(bool focused)
        {
            focus.SetValue(panel, focused);
            paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60);
            using (paper.Column("host").Size(280, 200).Enter())
                draw.Invoke(panel, subasset ? [paper, theme.Font!, item, 0, objects]
                    : [paper, theme.Font!, "grid", item, 0, objects, 72f]);
            Origami.EndFrame(paper); paper.EndFrame();
        }
    }

    private static OrigamiTheme FontTheme(bool editor)
    {
        var theme = editor ? EditorTheme.BuildOrigamiTheme() : OrigamiTheme.CreateDefaults();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        theme.Font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        return theme;
    }

    private static readonly FieldInfo StyleField = typeof(ElementData).GetField("_elementStyle", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static Color ReadStyleColor(ElementData data, string method)
    {
        object style = StyleField.GetValue(data)!;
        return (XEngine.Vector.Color)style.GetType().GetMethod(method)!.Invoke(style, null)!;
    }
    private static Color TextColor(Paper paper, string text)
    {
        for (int i = 0; i < paper.ElementCount; i++)
        {
            var data = paper.GetElementData(i);
            if (data.Paragraph == text) return ReadStyleColor(data, "GetTextColor");
        }
        throw new InvalidOperationException("Missing text: " + text);
    }
    private sealed class Observer : IPaperSubtreePainter
    {
        public readonly PaperSubtreeVisualState State = new();
        public bool Changed;
        public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
        { Changed = State.HasChanged(paper, root); return false; }
    }
    private static Paper NewPaper() => new(new Renderer(), 300, 260, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => texture is XEngine.Runtime.Resources.Texture2D t
            ? new Int2((int)t.Width, (int)t.Height) : (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
