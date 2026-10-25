using System.Collections.Generic;
using System.IO;
using System.Reflection;

using XEngine.Editor.Core;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Scribe;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Under <c>--animation-paint-incremental</c> the dopesheet/curve painters declare an exact
/// key (content/visual stamps, view, playhead). A comparing observer must settle on an idle
/// clip and re-key when a key is selected or added. Playing sets
/// <see cref="AnimationClipWindow.WantsContinuousPresent"/>.
/// </summary>
public sealed class AnimationIncrementalPaintTests : EditorTestHarness
{
    private const float Step = 1f / 60f;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void AnimationSheetSettlesAndRekeysOnSelectAndAdd()
    {
        // The first OnGUI binds from global editor selection. Seed a stale, non-animated object
        // to keep this fixture proving that it is independent of prior EditorTestHarness cases.
        Selection.ActiveObject = new GameObject("stale test selection");
        Selection.Clear();
        var paper = NewPaper();
        var observer = new Observer();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var theme = OrigamiTheme.CreateDefaults();
        theme.Font = font;
        using var themeScope = Origami.PushTheme(theme);
        var flag = typeof(Program).GetProperty(nameof(Program.AnimationPaintIncremental), Any)!;
        bool previousFlag = (bool)flag.GetValue(null)!;
        var previousFont = EditorTheme.DefaultFont;
        var panel = new AnimationClipWindow();
        try
        {
            EditorTheme.DefaultFont = font;
            flag.SetValue(null, true);
            var clip = MakeClip();
            panel.Bind(null, clip);
            panel.IsPreviewPlaying = false;

            Frame();
            Assert.Same(clip, panel.Model.Clip);
            int content = panel.Model.ContentStamp;
            int visual = panel.Model.VisualStamp;
            Assert.False(AnimationClipWindow.WantsContinuousPresent);

            // Origami.Tabs still uses an unversioned Draw, so a whole-window observer never
            // settles. The sheet/timeline keys themselves are versioned: a comparing observer
            // on that declared key settles, then re-keys when stamps change.
            ulong version = (ulong)(uint)HashCode.Combine(content, visual);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            panel.Model.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0, 0f);
            Assert.True(panel.Model.VisualStamp > visual);
            version = (ulong)(uint)HashCode.Combine(panel.Model.ContentStamp, panel.Model.VisualStamp);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            visual = panel.Model.VisualStamp;
            panel.Model.AddKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0.5f);
            Assert.True(panel.Model.ContentStamp > content);
            Assert.True(panel.Model.VisualStamp > visual);
            version = (ulong)(uint)HashCode.Combine(panel.Model.ContentStamp, panel.Model.VisualStamp);
            KeyedFrame(version); Assert.True(observer.Changed);
            KeyedFrame(version); Assert.False(observer.Changed);

            panel.IsPreviewPlaying = true;
            Frame();
            Assert.True(AnimationClipWindow.WantsContinuousPresent);
        }
        finally
        {
            flag.SetValue(null, previousFlag);
            EditorTheme.DefaultFont = previousFont;
            panel.OnClosed();
            observer.State.Dispose();
            paper.Canvas.Dispose();
            Selection.Clear();
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
                paper.DrawVersioned(ref handle, "animation.dopesheet", version, 0, panel, font, static (_, _) => { });
            }
            paper.EndFrame();
        }
    }

    [Fact]
    public void AnimationPaintFlagDefaultsOff()
    {
        Assert.False(Program.AnimationPaintIncremental);
        var flag = typeof(Program).GetProperty(nameof(Program.AnimationPaintIncremental), Any)!;
        Assert.False((bool)flag.GetValue(null)!);
    }

    private static AnimationClip MakeClip()
    {
        var clip = new AnimationClip { Name = "c", Duration = 1f };
        var bone = new AnimationClip.AnimBone { BoneName = "Hips" };
        bone.PosX = new AnimationCurve(new[] { new KeyFrame(0f, 0f), new KeyFrame(1f, 10f) });
        bone.PosX.SmoothTangents(CurveTangent.Linear);
        bone.PosY = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        bone.PosZ = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        clip.AddBone(bone);
        return clip;
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
