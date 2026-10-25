using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorToolbarSearchTests
{
    [Theory]
    [InlineData(16f, 11f, 130f)]
    [InlineData(22f, 12f, 240f)]
    [InlineData(16f, 11f, 64f)]
    public void CompactSearchFitsTextAndBothIconSlots(float height, float fontSize, float width)
    {
        using var fixture = new Fixture();
        fixture.Value = "Camera";
        fixture.Frame(height, fontSize, width);
        var row = fixture.Row;
        Assert.Equal(height, row.Data.LayoutHeight, 3);
        Assert.Equal(width, row.Data.LayoutWidth, 3);
        Assert.Equal(3, row.Data.ChildIndices.Count);
        var lead = fixture.Paper.GetElementData(row.Data.ChildIndices[0]);
        var text = fixture.Paper.GetElementData(row.Data.ChildIndices[1]);
        var clear = fixture.Paper.GetElementData(row.Data.ChildIndices[2]);
        Assert.Equal(14f, lead.LayoutWidth, 3);
        Assert.Equal(14f, clear.LayoutWidth, 3);
        Assert.True(text.LayoutWidth > 0);
        Assert.True(text.Y >= row.Data.Y);
        Assert.True(text.Y + text.LayoutHeight <= row.Data.Y + row.Data.LayoutHeight);
        Assert.True(text.X >= lead.X + lead.LayoutWidth);
        Assert.True(text.X + text.LayoutWidth <= clear.X + 0.01f);
        Assert.NotNull(clear.OnClick);
    }

    [Theory]
    [InlineData(16f, 11f)]
    [InlineData(22f, 12f)]
    public void ClearWhileFocusedUpdatesInternalTextAndAllowsContinuedTyping(float height, float fontSize)
    {
        using var fixture = new Fixture();
        fixture.Frame(height, fontSize);
        fixture.Paper.SetFocus(fixture.Row);
        fixture.Frame(height, fontSize, input: () => fixture.Paper.AddInputCharacter("Camera"));
        fixture.Frame(height, fontSize);
        Assert.Equal("Camera", fixture.Value);

        var clear = fixture.Paper.GetElementData(fixture.Row.Data.ChildIndices[2]);
        float x = clear.X + clear.LayoutWidth / 2, y = clear.Y + clear.LayoutHeight / 2;
        fixture.Paper.SetPointerPosition(x, y);
        fixture.Frame(height, fontSize, input: () => fixture.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
        fixture.Frame(height, fontSize, input: () => fixture.Paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false));
        fixture.Frame(height, fontSize);
        Assert.Equal("", fixture.Value);
        Assert.True(fixture.Paper.IsElementFocused(fixture.Row.Data.ID));

        fixture.Frame(height, fontSize, input: () => fixture.Paper.AddInputCharacter("Light"));
        fixture.Frame(height, fontSize);
        Assert.Equal("Light", fixture.Value);
    }

    [Fact]
    public void CompactOptInPreservesOrdinaryFieldMinimumHeight()
    {
        using var fixture = new Fixture();
        fixture.Frame(16, 11, ordinary: true);
        Assert.Equal(20f, fixture.Row.Data.LayoutHeight, 3);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Paper Paper = new(new Renderer(), 320, 100, new FontAtlasSettings { AtlasSize = 32 });
        internal string Value = "";
        private readonly IDisposable _themeScope;
        internal Fixture()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var theme = OrigamiTheme.CreateDefaults();
            theme.Font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
            _themeScope = Origami.PushTheme(theme);
        }

        internal ElementHandle Row
        {
            get
            {
                for (int i = 0; i < Paper.ElementCount; i++)
                    if (Paper.GetElementData(i).IsAHookedParent) return new ElementHandle(Paper, i);
                throw new InvalidOperationException("Missing search row");
            }
        }

        internal void Frame(float height, float fontSize, float width = 130,
            Action? input = null, bool ordinary = false)
        {
            Paper.BeginFrame(1f / 60); Origami.BeginFrame(Paper, 1f / 60);
            input?.Invoke();
            using (Paper.Column("host").Size(320, 100).Enter())
            {
                if (ordinary)
                    Origami.TextField(Paper, "search", Value, v => Value = v).Height(height).Width(width).Show();
                else
                    EditorToolbar.Search(Paper, "search", Value, v => Value = v, width,
                        height: height, fontSize: fontSize).Show();
            }
            Origami.EndFrame(Paper); Paper.EndFrame();
        }

        public void Dispose() { _themeScope.Dispose(); Paper.Canvas.Dispose(); }
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
