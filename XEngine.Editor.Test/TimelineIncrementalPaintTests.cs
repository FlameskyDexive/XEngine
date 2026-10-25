using System.Collections.Generic;
using System.IO;
using System.Reflection;

using XEngine.Editor;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Scribe;
using XEngine.Timeline;
using XEngine.Timeline.Editor;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Under <c>--timeline-paint-incremental</c> the ruler/sheet painters declare an exact key
/// (content/visual stamps, playhead). A comparing observer must settle on an idle timeline
/// and re-key when a clip is selected or added. Playing sets
/// <see cref="TimelineWindow.WantsContinuousPresent"/>.
/// </summary>
public sealed class TimelineIncrementalPaintTests : EditorTestHarness
{
    private const float Step = 1f / 60f;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void TimelineSheetSettlesAndRekeysOnSelectAndAdd()
    {
        var paper = NewPaper();
        var observer = new Observer();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var theme = OrigamiTheme.CreateDefaults();
        theme.Font = font;
        using var themeScope = Origami.PushTheme(theme);
        var flag = typeof(Program).GetProperty(nameof(Program.TimelinePaintIncremental), Any)!;
        bool previousFlag = (bool)flag.GetValue(null)!;
        var previousFont = EditorTheme.DefaultFont;
        var previousWants = TimelineWindow.WantsContinuousPresent;
        var panel = new TimelineWindow();
        try
        {
            EditorTheme.DefaultFont = font;
            flag.SetValue(null, true);
            panel.SetTimeline(MakeTimeline());
            panel.IsPreviewPlaying = false;

            Frame();
            int content = panel.ContentStamp;
            int visual = panel.VisualStamp;
            Assert.False(TimelineWindow.WantsContinuousPresent);

            ulong version = (ulong)(uint)HashCode.Combine(content, visual);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            panel.ProbeSelectFirstClip();
            Assert.True(panel.VisualStamp > visual);
            version = (ulong)(uint)HashCode.Combine(panel.ContentStamp, panel.VisualStamp);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            visual = panel.VisualStamp;
            content = panel.ContentStamp;
            panel.ProbeAddClip();
            Assert.True(panel.ContentStamp > content);
            Assert.True(panel.VisualStamp > visual);
            version = (ulong)(uint)HashCode.Combine(panel.ContentStamp, panel.VisualStamp);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            panel.IsPreviewPlaying = true;
            Frame();
            Assert.True(TimelineWindow.WantsContinuousPresent);
        }
        finally
        {
            flag.SetValue(null, previousFlag);
            EditorTheme.DefaultFont = previousFont;
            TimelineWindow.WantsContinuousPresent = previousWants;
            if (!previousWants) TimelineWindow.WantsContinuousPresent = false;
            panel.OnClosed();
            observer.State.Dispose();
            paper.Canvas.Dispose();
        }

        void Frame()
        {
            paper.BeginFrame(Step); Origami.BeginFrame(paper, Step);
            using (paper.Box("host").Size(720, 540).Enter())
                panel.OnGUI(paper, 720, 540);
            Origami.EndFrame(paper); paper.EndFrame();
        }

        void KeyedFrame(ulong version)
        {
            paper.BeginFrame(Step);
            using (paper.Box("tl_key").Size(200, 24).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                var handle = paper.CurrentParent;
                paper.DrawVersioned(ref handle, "timeline.sheet", version, 0, panel, font, static (_, _) => { });
            }
            paper.EndFrame();
        }
    }

    [Fact]
    public void TimelinePaintFlagDefaultsOff()
    {
        Assert.False(Program.TimelinePaintIncremental);
        var flag = typeof(Program).GetProperty(nameof(Program.TimelinePaintIncremental), Any)!;
        Assert.False((bool)flag.GetValue(null)!);
    }

    private static TimelineAsset MakeTimeline()
    {
        var ta = new TimelineAsset { Name = "probe", Duration = 2 };
        var track = new AnimationTrack { Name = "Anim" };
        track.Clips.Add(new AnimationTrackClip
        {
            Schedule = new TimelineClipSchedule { Start = 0, Duration = 1 }
        });
        ta.Tracks.Add(track);
        return ta;
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
