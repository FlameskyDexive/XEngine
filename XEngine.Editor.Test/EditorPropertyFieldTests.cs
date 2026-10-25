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

public sealed class EditorPropertyFieldTests
{
    [Theory]
    [InlineData(true, false, .6f)]
    [InlineData(false, false, 1f)]
    [InlineData(true, true, 0f)]
    [InlineData(false, true, 0f)]
    public void PrefixScrubIsOptInClampedAndReadOnlySafe(bool editor, bool readOnly, float expected)
    {
        using var f = new Fixture(editor); float value = 0;
        void Draw() => Origami.NumericField<float>(f.Paper, "scrub", value, v => value = v)
            .Min(-1).Max(1).ReadOnly(readOnly).DraggableLabel("X", System.Drawing.Color.Red).Show();
        f.Frame(Draw);
        ElementData handle = default;
        for (int i = 0; i < f.Paper.ElementCount; i++)
            if (f.Paper.GetElementData(i).Paragraph == "X") handle = f.Paper.GetElementData(i);
        float x = handle.X + handle.LayoutWidth / 2, y = handle.Y + handle.LayoutHeight / 2;
        f.Paper.SetPointerPosition(x, y); f.Frame(Draw);
        f.Frame(Draw, () => f.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
        f.Frame(Draw, () => f.Paper.SetPointerPosition(x + 20, y));
        f.Frame(Draw, () => f.Paper.SetPointerState(PaperMouseBtn.Left, x + 20, y, false, false));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(260f)]
    [InlineData(420f)]
    public void EditorRowsContainScalarTextAndVectorAtTwentyPixelPitch(float width)
    {
        using var f = new Fixture(width: width);
        f.Frame(() =>
        {
            EditorGUI.Row(f.Paper, "a", "Position", () =>
                Origami.Float3Field(f.Paper, "v", new Float3(1, 2, 3), static _ => { }).Show());
            EditorGUI.Row(f.Paper, "b", "Amount", () =>
                Origami.NumericField<float>(f.Paper, "n", 12, static _ => { }).Show());
            EditorGUI.Row(f.Paper, "c", "Name", () =>
                Origami.TextField(f.Paper, "t", "A long value extending beyond the field", static _ => { }).Show());
        });
        var rows = f.FieldRows();
        Assert.Equal(5, rows.Count);
        foreach (var row in rows) AssertContained(f.Paper, row, 18, 12);
        Assert.Equal(rows[0].Y, rows[1].Y, 3);
        Assert.Equal(rows[0].Y, rows[2].Y, 3);
        Assert.Equal(20f, rows[3].Y - rows[0].Y, 3);
        Assert.Equal(20f, rows[4].Y - rows[3].Y, 3);
        var text = f.Paper.GetElementData(rows[4].ChildIndices[0]);
        var end = f.Paper.GetElementData(rows[4].ChildIndices[1]);
        Assert.Equal(3f, text.X - rows[4].X, 3);
        Assert.Equal(3f, end.LayoutWidth, 3);
    }

    [Fact]
    public void ReflectionPropertyGridUsesTheSamePitchAsHandDrawnRows()
    {
        using var f = new Fixture();
        var config = new PropertyGridConfig();
        BuiltInFieldDrawers.Register(config.Drawers);
        f.Frame(() => PropertyGridRenderer.Draw(f.Paper, "grid", new Form(), config, null, null, 0));
        var rows = f.FieldRows();
        Assert.Equal(5, rows.Count);
        foreach (var row in rows) AssertContained(f.Paper, row, 18, 12);
        Assert.Equal(20f, rows[3].Y - rows[0].Y, 3);
        Assert.Equal(20f, rows[4].Y - rows[3].Y, 3);
    }

    [Theory]
    [InlineData(true, 1f, 18f)]
    [InlineData(true, 32f, 32f)]
    [InlineData(false, 1f, 20f)]
    [InlineData(false, 32f, 32f)]
    public void ExplicitHeightHonorsProfileMinimumAndStandaloneDefaults(bool editor, float requested, float expected)
    {
        using var f = new Fixture(editor);
        f.Frame(() =>
        {
            Origami.NumericField<int>(f.Paper, "n", 42, static _ => { }).Height(requested).Show();
            Origami.TextField(f.Paper, "t", "value", static _ => { }).Height(requested).Show();
        });
        var rows = f.FieldRows();
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
            AssertContained(f.Paper, row, expected, editor ? 12 : f.Theme.Metrics.FontSize);
        var text = f.Paper.GetElementData(rows[1].ChildIndices[0]);
        var end = f.Paper.GetElementData(rows[1].ChildIndices[1]);
        Assert.Equal(editor ? 3f : 8f, text.X - rows[1].X, 3);
        Assert.Equal(editor ? 3f : 6f, end.LayoutWidth, 3);
    }

    [Fact]
    public void ToolbarStyleKeepsItsExplicitHeightAndFontWithEditorProfile()
    {
        using var f = new Fixture();
        f.Frame(() => EditorToolbar.Search(f.Paper, "search", "", static _ => { }, 130,
            height: 16, fontSize: 11).Show());
        AssertContained(f.Paper, Assert.Single(f.FieldRows()), 16, 11);
    }

    [Fact]
    public void MetricsCloneAndTransitionRetainValueProfileAndNullDefault()
    {
        var a = new OrigamiMetrics { Fields = new(18, 12, 3, 3, 2, 0), EditorNumericScrub = true };
        var copy = a.Clone();
        Assert.Equal(a.Fields, copy.Fields);
        Assert.True(copy.EditorNumericScrub);
        copy.Fields = new(22, 16, 7, 7, 6, 4);
        Assert.Equal(18f, a.Fields!.Value.MinimumHeight);
        Assert.Equal(new OrigamiFieldMetrics(20, 14, 5, 5, 4, 2), OrigamiMetrics.Lerp(a, copy, .5f).Fields);
        var defaults = new OrigamiMetrics();
        Assert.Null(defaults.Clone().Fields);
        Assert.False(defaults.EditorNumericScrub);
        Assert.True(OrigamiMetrics.Lerp(defaults, a, 1).EditorNumericScrub);
        Assert.Null(OrigamiMetrics.Lerp(defaults, defaults, .5f).Fields);
        Assert.Null(OrigamiMetrics.Lerp(defaults, a, 0).Fields);
        Assert.Equal(a.Fields, OrigamiMetrics.Lerp(defaults, a, 1).Fields);
        Assert.Null(OrigamiMetrics.Lerp(a, defaults, 1).Fields);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DenseNumericFieldKeepsLiveUpdatesEnterFocusAndReadOnlyBehavior(bool readOnly)
    {
        using var f = new Fixture();
        float value = 5;
        int changes = 0;
        void Draw() => Origami.NumericField<float>(f.Paper, "n", value, v => { value = v; changes++; })
            .ReadOnly(readOnly).SelectAllOnFocus().Show();
        f.Frame(Draw);
        f.Paper.SetFocus(new ElementHandle(f.Paper, f.FirstFieldIndex()));
        f.Frame(Draw);
        f.Frame(Draw, () => f.Paper.AddInputCharacter("12"));
        f.Frame(Draw);
        // Existing numeric fields apply valid input live; Enter commits focus.
        Assert.Equal(readOnly ? 5f : 12f, value);
        f.Frame(Draw, () => f.Paper.SetKeyState(PaperKey.Enter, true));
        f.Frame(Draw, () => f.Paper.SetKeyState(PaperKey.Enter, false));
        f.Frame(Draw);
        Assert.Equal(readOnly ? 5f : 12f, value);
        if (readOnly) Assert.Equal(0, changes);
        else Assert.True(changes > 0);
        Assert.False(f.Paper.IsElementFocused(f.Paper.GetElementData(f.FirstFieldIndex()).ID));
    }

    [Theory]
    [InlineData(PaperKey.Enter, false, false)]
    [InlineData(PaperKey.Escape, false, false)]
    [InlineData(PaperKey.Enter, true, false)]
    [InlineData(PaperKey.Enter, false, true)]
    public void DelayedPlainTextKeepsDraftUntilCommitAndSupportsTabAndReadOnly(PaperKey key, bool tab, bool readOnly)
    {
        using var f = new Fixture();
        string value = "Original"; int changes = 0;
        void Draw()
        {
            Origami.TextField(f.Paper, "delayed", value, v => { value = v; changes++; })
                .Delayed().ReadOnly(readOnly).SelectAllOnFocus().Show();
            Origami.TextField(f.Paper, "other", "Other", static _ => { }).Show();
        }
        f.Frame(Draw); f.Paper.SetFocus(new ElementHandle(f.Paper, f.FirstFieldIndex()));
        f.Frame(Draw); f.Frame(Draw, () => f.Paper.AddInputCharacter("Draft")); f.Frame(Draw);
        Assert.Equal("Original", value); Assert.Equal(0, changes);
        var commitKey = tab ? PaperKey.Tab : key;
        f.Frame(Draw, () => f.Paper.SetKeyState(commitKey, true));
        Assert.Equal(!readOnly && key != PaperKey.Escape, f.Paper.NeedsFollowUpFrame);
        f.Frame(Draw, () => f.Paper.SetKeyState(commitKey, false)); f.Frame(Draw);
        Assert.False(f.Paper.NeedsFollowUpFrame);
        Assert.Equal(readOnly || key == PaperKey.Escape ? "Original" : "Draft", value);
        Assert.Equal(readOnly || key == PaperKey.Escape ? 0 : 1, changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedClearAndAutocompleteUpdateTheDraftBeforeCommit(bool autocomplete)
    {
        using var f = new Fixture();
        string value = "An"; int changes = 0;
        void Draw()
        {
            var field = Origami.TextField(f.Paper, "delayed", value, v => { value = v; changes++; })
                .Delayed().SelectAllOnFocus();
            if (autocomplete) field.AutoComplete(new[] { "Anbi_Body", "Anbi_Face" });
            else field.ClearButton();
            field.Show();
        }
        f.Frame(Draw); f.Paper.SetFocus(new ElementHandle(f.Paper, f.FirstFieldIndex())); f.Frame(Draw);
        if (autocomplete)
        {
            f.Frame(Draw, () => f.Paper.AddInputCharacter("Face")); f.Frame(Draw);
            f.Frame(Draw, () => f.Paper.SetKeyState(PaperKey.Enter, true));
            f.Frame(Draw, () => f.Paper.SetKeyState(PaperKey.Enter, false));
        }
        else
        {
            var row = f.FieldRows()[0];
            var clear = row.ChildIndices.Select(i => f.Paper.GetElementData(i)).Single(e => e.OnClick != null);
            float x = clear.X + clear.LayoutWidth / 2, y = clear.Y + clear.LayoutHeight / 2;
            f.Paper.SetPointerPosition(x, y); f.Frame(Draw);
            f.Frame(Draw, () => f.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
            f.Frame(Draw, () => f.Paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false));
            f.Frame(Draw); Assert.Equal("An", value); Assert.Equal(0, changes);
            f.Paper.ClearFocus();
        }
        f.Frame(Draw);
        Assert.Equal(autocomplete ? "Anbi_Face" : "", value); Assert.Equal(1, changes);
    }

    private static void AssertContained(Paper paper, ElementData row, float height, float textHeight)
    {
        Assert.Equal(height, row.LayoutHeight, 3);
        Assert.True(row.LayoutWidth > 0);
        bool foundText = false;
        foreach (int index in row.ChildIndices)
        {
            var child = paper.GetElementData(index);
            Assert.True(child.Y >= row.Y - .01f);
            Assert.True(child.Y + child.LayoutHeight <= row.Y + row.LayoutHeight + .01f);
            Assert.True(child.X >= row.X - .01f);
            Assert.True(child.X + child.LayoutWidth <= row.X + row.LayoutWidth + .01f);
            if (!child.IsHookedToParent) continue;
            foundText = true;
            Assert.Equal(textHeight, child.LayoutHeight, 3);
        }
        Assert.True(foundText);
    }

    private sealed class Form
    {
        public Float3 Position = new(1, 2, 3);
        public float Amount = 12;
        public string Name = "A long value extending beyond the field";
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Paper Paper;
        internal readonly OrigamiTheme Theme;
        private readonly IDisposable _themeScope;
        private readonly FontFile? _previousFont;
        private readonly float _width;

        internal Fixture(bool editor = true, float width = 420)
        {
            _width = width;
            Paper = new Paper(new Renderer(), width, 180, new FontAtlasSettings { AtlasSize = 32 });
            _previousFont = EditorTheme.DefaultFont;
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            EditorTheme.DefaultFont = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
            Theme = editor ? EditorTheme.BuildOrigamiTheme() : OrigamiTheme.CreateDefaults();
            Theme.Font = EditorTheme.DefaultFont;
            _themeScope = Origami.PushTheme(Theme);
        }

        internal void Frame(Action draw, Action? input = null)
        {
            Paper.BeginFrame(1f / 60); Origami.BeginFrame(Paper, 1f / 60);
            input?.Invoke();
            using (Paper.Column("host").Size(_width, 180).Enter()) draw();
            Origami.EndFrame(Paper); Paper.EndFrame();
        }

        internal List<ElementData> FieldRows()
        {
            List<ElementData> rows = [];
            for (int i = 0; i < Paper.ElementCount; i++)
                if (Paper.GetElementData(i).IsAHookedParent) rows.Add(Paper.GetElementData(i));
            return rows;
        }

        internal int FirstFieldIndex()
        {
            for (int i = 0; i < Paper.ElementCount; i++)
                if (Paper.GetElementData(i).IsAHookedParent) return i;
            throw new InvalidOperationException("Missing field row");
        }

        public void Dispose()
        {
            _themeScope.Dispose();
            EditorTheme.DefaultFont = _previousFont;
            Paper.Canvas.Dispose();
        }
    }

    private sealed class Renderer : ICanvasRenderer
    {
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
        public void Dispose() { }
    }
}
