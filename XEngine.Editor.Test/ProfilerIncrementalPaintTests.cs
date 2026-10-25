using System.Collections.Generic;
using System.Reflection;

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// The Profiler's chart strips are one custom-painted box. Under
/// <c>--profiler-paint-incremental</c> the painter declares an exact key (history count/head,
/// selected offset, newest sample, plot width, palette), so a compared region can keep a settled
/// historical inspection cached and must re-key when a sample is recorded or the selection moves.
/// The panel is built headlessly under a comparing observer (no render layers, so no real region).
/// </summary>
public sealed class ProfilerIncrementalPaintTests : EditorTestHarness
{
    private const float Step = 1f / 60f;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void ProfilerChartsSettleAndRekeyOnSampleAndSelection()
    {
        var paper = NewPaper();
        var observer = new Observer();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var theme = OrigamiTheme.CreateDefaults();
        theme.Font = font;
        using var themeScope = Origami.PushTheme(theme);
        var flag = typeof(Program).GetProperty(nameof(Program.ProfilerPaintIncremental), Any)!;
        bool previousFlag = (bool)flag.GetValue(null)!;
        var previousFont = EditorTheme.DefaultFont;
        var previousWants = ProfilerPanel.WantsContinuousPresent;
        var panel = new ProfilerPanel();
        try
        {
            EditorTheme.DefaultFont = font;
            flag.SetValue(null, true);
            panel.Recording = false;
            for (int i = 0; i < 8; i++)
                panel.RecordTestSample(8f + i, 10 + i, 4 + i, 1000 + i, 40f + i);
            panel.SelectedOffset = 0;

            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();
            Frame(); Assert.False(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            Assert.False(ProfilerPanel.WantsContinuousPresent);

            panel.SelectedOffset = 3;
            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();
            Frame(); Assert.False(observer.Changed);

            panel.RecordTestSample(16.6f, 20, 8, 2000, 48f);
            Frame(); Assert.True(observer.Changed);
            Frame(); Frame(); Assert.False(observer.Changed);

            panel.ScopeView = 2;
            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();
            Frame(); Assert.False(observer.Changed);

            panel.Recording = true;
            panel.SelectedOffset = -1;
            Frame();
            Assert.True(ProfilerPanel.WantsContinuousPresent);
        }
        finally
        {
            flag.SetValue(null, previousFlag);
            EditorTheme.DefaultFont = previousFont;
            ProfilerPanel.WantsContinuousPresent = previousWants;
            panel.OnClosed();
            observer.State.Dispose();
            paper.Canvas.Dispose();
        }

        void Frame()
        {
            paper.BeginFrame(Step); Origami.BeginFrame(paper, Step);
            using (paper.Box("host").Size(720, 540).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                panel.OnGUI(paper, 720, 540);
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

    private static Paper NewPaper() => new(new Renderer(), 720, 540, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
