using System.Collections.Generic;
using System.IO;
using System.Reflection;

using XEngine.Animation;
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
/// Under <c>--animator-paint-incremental</c> the canvas key (content/visual stamps, view)
/// must settle on an idle controller and re-key when a state is selected or added.
/// Playing sets <see cref="AnimatorWindow.WantsContinuousPresent"/>.
/// </summary>
public sealed class AnimatorIncrementalPaintTests : EditorTestHarness
{
    private const float Step = 1f / 60f;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void AnimatorCanvasSettlesAndRekeysOnSelectAndAdd()
    {
        var paper = NewPaper();
        var observer = new Observer();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var theme = OrigamiTheme.CreateDefaults();
        theme.Font = font;
        using var themeScope = Origami.PushTheme(theme);
        var flag = typeof(Program).GetProperty(nameof(Program.AnimatorPaintIncremental), Any)!;
        bool previousFlag = (bool)flag.GetValue(null)!;
        var previousFont = EditorTheme.DefaultFont;
        var previousWants = AnimatorWindow.WantsContinuousPresent;
        var panel = new AnimatorWindow();
        try
        {
            EditorTheme.DefaultFont = font;
            flag.SetValue(null, true);
            panel.SetController(MakeController());
            panel.IsPreviewPlaying = false;

            Frame();
            int content = panel.ContentStamp;
            int visual = panel.VisualStamp;
            Assert.False(AnimatorWindow.WantsContinuousPresent);

            ulong version = (ulong)(uint)HashCode.Combine(content, visual);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            panel.ProbeSelectFirstState();
            Assert.True(panel.VisualStamp > visual);
            version = (ulong)(uint)HashCode.Combine(panel.ContentStamp, panel.VisualStamp);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            visual = panel.VisualStamp;
            content = panel.ContentStamp;
            panel.ProbeAddState();
            Assert.True(panel.ContentStamp > content);
            version = (ulong)(uint)HashCode.Combine(panel.ContentStamp, panel.VisualStamp);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            panel.IsPreviewPlaying = true;
            Assert.True(AnimatorWindow.WantsContinuousPresent);
        }
        finally
        {
            flag.SetValue(null, previousFlag);
            EditorTheme.DefaultFont = previousFont;
            AnimatorWindow.WantsContinuousPresent = previousWants;
            if (!previousWants) AnimatorWindow.WantsContinuousPresent = false;
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
            using (paper.Box("anim_key").Size(200, 24).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                var handle = paper.CurrentParent;
                paper.DrawVersioned(ref handle, "animator.canvas", version, 0, panel, font, static (_, _) => { });
            }
            paper.EndFrame();
        }
    }

    [Fact]
    public void AnimatorPaintFlagDefaultsOff()
    {
        Assert.False(Program.AnimatorPaintIncremental);
        var flag = typeof(Program).GetProperty(nameof(Program.AnimatorPaintIncremental), Any)!;
        Assert.False((bool)flag.GetValue(null)!);
    }

    private static AnimatorController MakeController()
    {
        var controller = new AnimatorController { Name = "probe", DefaultStateName = "Idle" };
        controller.States.Add(new AnimatorController.State
        {
            Name = "Idle", Speed = 1f, IsLooping = true, PosX = 40f, PosY = 40f
        });
        return controller;
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
