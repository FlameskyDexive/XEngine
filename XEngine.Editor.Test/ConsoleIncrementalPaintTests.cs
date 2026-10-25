using System.Reflection;

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Scribe;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// The Console panel's message list is one custom-painted box. Under <c>--console-paint-incremental</c>
/// its painter declares an exact key (store/filter version, visible slice, selection, hover, display
/// options, palette, row font), so a compared paint region can keep the settled list cached and must
/// re-key exactly when a log arrives, the selection moves, a filter changes or Clear runs. The panel
/// is built headlessly under a comparing observer (no render layers, so no real region); the observer
/// sees every visual input the region would.
/// </summary>
public sealed class ConsoleIncrementalPaintTests : EditorTestHarness
{
    private const float Step = 1f / 60f;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void ConsoleListSettlesAndRekeysOnLogSelectionFilterAndClear()
    {
        var paper = NewPaper();
        var observer = new Observer();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var theme = OrigamiTheme.CreateDefaults();
        theme.Font = font;
        using var themeScope = Origami.PushTheme(theme);
        var flag = typeof(Program).GetProperty(nameof(Program.ConsolePaintIncremental), Any)!;
        bool previousFlag = (bool)flag.GetValue(null)!;
        var previousFont = EditorTheme.DefaultFont;
        var panel = new ConsolePanel();
        var type = typeof(ConsolePanel);
        var clear = type.GetMethod("ClearMessages", Any)!;
        var selected = type.GetField("_selectedFilteredIndex", Any)!;
        var showInfo = type.GetField("_showInfo", Any)!;
        var messages = (System.Collections.IList)type.GetField("_messages", Any)!.GetValue(null)!;
        try
        {
            EditorTheme.DefaultFont = font;
            flag.SetValue(null, true);
            clear.Invoke(panel, null);
            for (int i = 0; i < 6; i++) Debug.Log($"console paint test {i}");
            Debug.LogWarning("console paint test warning");
            Assert.Equal(7, messages.Count);

            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();               // toolbar transitions and layout settle
            // Idle list, empty detail, unfocused search field: nothing volatile in the whole panel
            // (the search field's vector magnifier was the last opaque draw; it is versioned now).
            Frame(); Assert.False(observer.Changed);
            Frame(); Assert.False(observer.Changed);

            Debug.Log("console paint test new");                // store version -> filtered list rebuilt -> new key
            Frame(); Assert.True(observer.Changed);
            Frame(); Frame(); Assert.False(observer.Changed);

            selected.SetValue(panel, 1);                        // selection is part of the row key; detail pane shows the entry
            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();               // the detail pane's own copy button transition settles
            Frame(); Assert.False(observer.Changed);            // read-only text area is versioned: no caret, no blink
            Frame(); Assert.False(observer.Changed);

            showInfo.SetValue(panel, false);                    // filter -> only the warning remains
            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();               // the toolbar chip's toggle transition settles
            Frame(); Assert.False(observer.Changed);
            Assert.Equal(1, ((System.Collections.IList)type.GetField("_filteredIndices", Any)!.GetValue(panel)!).Count);
            showInfo.SetValue(panel, true);
            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();
            Frame(); Assert.False(observer.Changed);

            // A full store evicts the oldest entry per new log while Count stays put; the list must
            // still rebuild (this went stale before the store version was part of the trigger).
            for (int i = messages.Count; i < 500; i++) Debug.Log($"console paint fill {i}");
            Frame(); Assert.True(observer.Changed);
            Frame(); Frame(); Assert.False(observer.Changed);
            Assert.Equal(500, messages.Count);
            uint versionBefore = panel.ListVersion;
            Debug.Log("console paint evict");
            Frame(); Assert.True(observer.Changed);
            Assert.Equal(500, messages.Count);
            Assert.True(panel.ListVersion > versionBefore);
            Frame(); Frame(); Assert.False(observer.Changed);

            clear.Invoke(panel, null);
            Frame(); Assert.True(observer.Changed);
            Assert.Equal(0, messages.Count);
            Frame(); Frame(); Assert.False(observer.Changed);
        }
        finally
        {
            flag.SetValue(null, previousFlag);
            EditorTheme.DefaultFont = previousFont;
            clear.Invoke(panel, null);
            panel.OnClosed();
            observer.State.Dispose();
            paper.Canvas.Dispose();
        }

        void Frame()
        {
            paper.BeginFrame(Step); Origami.BeginFrame(paper, Step);
            using (paper.Box("host").Size(720, 360).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                panel.OnGUI(paper, 720, 360);
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

    private static Paper NewPaper() => new(new Renderer(), 720, 360, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
