using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>The Origami table is hosted inside the Project panel's compared paint region. Its
/// own custom draws (sort caret, selection/ping bar) must be versioned, otherwise the region is
/// volatile and recaptures every frame even when nothing on screen changed.</summary>
public sealed class TableVersionedPaintTests
{
    private const float Step = 1f / 60f;

    [Fact]
    public void SortedSelectedTableIsStableAcrossIdenticalFramesAndReactsToSelectionAndSort()
    {
        var paper = NewPaper();
        var observer = new Observer();
        int selected = 0, sortColumn = 0;
        bool ascending = true;
        float ping = 0f;
        // The table draws nothing without a theme font; the pushed theme is thread-local so it
        // cannot leak into tests running in parallel.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var theme = OrigamiTheme.CreateDefaults();
        theme.Font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        using var themeScope = Origami.PushTheme(theme);
        try
        {
            Frame(); Assert.True(observer.Changed);
            Frame();                                     // layout settles over the first two frames
            Frame(); Assert.False(observer.Changed);     // versioned caret + row bar: nothing volatile
            Frame(); Assert.False(observer.Changed);
            selected = 1; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            ascending = false; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            sortColumn = 1; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            ping = 0.5f; Frame(); Assert.True(observer.Changed);   // ping alpha is part of the version
            Frame(); Assert.False(observer.Changed);               // constant alpha: reusable
            ping = 0.25f; Frame(); Assert.True(observer.Changed);
        }
        finally { observer.State.Dispose(); paper.Canvas.Dispose(); }

        void Frame()
        {
            paper.BeginFrame(Step); Origami.BeginFrame(paper, Step);
            using (paper.Box("host").Size(280, 200).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                var table = Origami.Table(paper, "t", selected, i => selected = i)
                    .Column("Name", 2f, sortable: true)
                    .Column("Type", 1f, sortable: true)
                    .Sort(sortColumn, ascending, c => sortColumn = c)
                    .IsPinged(i => i == 2)
                    .PingAlpha(() => ping);
                var ink = System.Drawing.Color.White;
                table.Row().Cell("a", ink).Cell("x", ink);
                table.Row().Cell("b", ink).Cell("y", ink);
                table.Row().Cell("c", ink).Cell("z", ink, EditorIcons.Folder_I);
                table.Show();
            }
            Origami.EndFrame(paper); paper.EndFrame();
        }
    }

    private sealed class Observer : IPaperSubtreePainter
    {
        public readonly PaperSubtreeVisualState State = new();
        public bool Changed;
        public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
        { Changed = State.HasChanged(paper, root); return false; }
    }

    private static Paper NewPaper() => new(new Renderer(), 300, 300, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
