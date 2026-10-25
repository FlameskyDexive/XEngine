using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;

namespace Tests;

public sealed class SubtreeVisualStateTests
{
    [Fact]
    public void ExactFinalInputsDetectTextColorGeometryAndCustomVersionChanges()
    {
        var paper = new Paper(new Renderer(), 200, 200, new FontAtlasSettings());
        var observer = new Observer();
        var font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        string text = "Label";
        float width = 100;
        var color = Color.Red;
        ulong? custom = null;
        bool draw = false;
        try
        {
            Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            text = new string("Label".ToCharArray()); Frame(); Assert.False(observer.Changed);
            text = "Renamed"; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            color = Color.Blue; Frame(); Assert.True(observer.Changed);
            width += .01f; Frame(); Assert.True(observer.Changed);
            draw = true; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.True(observer.Changed); // Unknown callback dependencies cannot be reused.
            custom = 1; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            custom = 2; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            for (int i = 0; i < 1000; i++) observer.State.HasChanged(paper, observer.Root);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) observer.State.HasChanged(paper, observer.Root);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally { observer.State.Dispose(); paper.Canvas.Dispose(); }

        void Frame()
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(width, 80).Text(text, font).BackgroundColor(color).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                paper.CurrentParent.Data.CustomPaintVersion = custom;
                if (draw) paper.Draw(static (_, _) => { });
            }
            paper.EndFrame();
        }
    }

    [Fact]
    public void CommandVersionsAreIndependentAndPooledCommandsLoseTheirOldDeclaration()
    {
        var paper = new Paper(new Renderer(), 200, 200, new FontAtlasSettings());
        var observer = new Observer();
        bool versioned = true, extraUnknown = false;
        string kind = "fill";
        ulong version = 1;
        try
        {
            Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            version++; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            kind = "stroke"; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            extraUnknown = true; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.True(observer.Changed);
            extraUnknown = false; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            versioned = false; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.True(observer.Changed);
        }
        finally { observer.State.Dispose(); paper.Canvas.Dispose(); }

        void Frame()
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(100, 80).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                if (versioned) paper.DrawVersioned(kind, version, static (_, _) => { });
                else paper.Draw(static (_, _) => { });
                if (extraUnknown) paper.Draw(static (_, _) => { });
            }
            paper.EndFrame();
        }
    }

    [Fact]
    public void ResourceKeysCompareIdentityAndAllDetailBits()
    {
        var paper = new Paper(new Renderer(), 100, 100, new FontAtlasSettings());
        var observer = new Observer();
        object source = new object(), dependency = new object();
        ulong detail = 1;
        try
        {
            Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            source = new object(); Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            dependency = new object(); Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            detail |= 1UL << 63; Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
        }
        finally { observer.State.Dispose(); paper.Canvas.Dispose(); }
        void Frame()
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(80, 80).Enter())
            {
                var root = paper.CurrentParent;
                root.Data.SubtreePainter = observer;
                paper.DrawVersioned(ref root, "icon", 1, detail, source, dependency, static (_, _) => { });
            }
            paper.EndFrame();
        }
    }

    [Fact]
    public void TextInputPainterIsVersionedByContentStateAndCaretPhase()
    {
        var paper = new Paper(new Renderer(), 300, 200, new FontAtlasSettings());
        var observer = new Observer();
        var font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        string value = "Hello";
        bool focus = false, readOnly = false;
        var textColor = Color.White;
        try
        {
            Frame(.016f); Assert.True(observer.Changed);
            Frame(.016f); Assert.False(observer.Changed);   // an unfocused field settles
            Frame(.016f); Assert.False(observer.Changed);
            value = "World"; Frame(.016f); Assert.True(observer.Changed);   // content is part of the key
            Frame(.016f); Assert.False(observer.Changed);
            value = new string("World".ToCharArray()); Frame(.016f); Assert.False(observer.Changed);   // by value, not identity
            textColor = Color.Red; Frame(.016f); Assert.True(observer.Changed);
            Frame(.016f); Assert.False(observer.Changed);
            readOnly = true; Frame(.016f); Assert.False(observer.Changed);   // read-only does not change the pixels
            Frame(2f); Assert.False(observer.Changed);   // time passing does not matter without focus

            focus = true; Frame(.016f); Assert.True(observer.Changed);   // caret appears
            // Settle on the current caret phase, then flip it by advancing half a second.
            paper.BeginFrame(.016f); Build(); paper.EndFrame();
            float phaseStart = paper.Time;
            int phase = (int)(phaseStart * 2) % 2;
            float toNextFlip = ((int)(phaseStart * 2) + 1) * 0.5f - phaseStart;
            Frame(MathF.Max(0.001f, toNextFlip - 0.1f)); Assert.False(observer.Changed);   // still the same phase
            Frame(0.2f); Assert.True(observer.Changed); Assert.NotEqual(phase, (int)(paper.Time * 2) % 2);
            Frame(0.05f); Assert.False(observer.Changed);
            Frame(0.5f); Assert.True(observer.Changed);   // next flip

            // Between flips a region reuses its cached paint and the painter never runs; the focused
            // field must still claim the keyboard on those frames (declared at layout time).
            observer.SkipWhenUnchanged = true;
            Frame(0.05f); Assert.False(observer.Changed); Assert.True(observer.Skipped); Assert.True(paper.WantsCaptureKeyboard);
            Frame(0.05f); Assert.True(observer.Skipped); Assert.True(paper.WantsCaptureKeyboard);
            paper.ClearFocus();
            Frame(0.05f); Assert.True(observer.Changed); Assert.False(paper.WantsCaptureKeyboard);   // caret gone, keyboard released
            Frame(0.05f); Assert.True(observer.Skipped); Assert.False(paper.WantsCaptureKeyboard);
        }
        finally { observer.State.Dispose(); paper.Canvas.Dispose(); }

        void Frame(float dt)
        {
            paper.BeginFrame(dt);
            Build();
            paper.EndFrame();
        }
        void Build()
        {
            var settings = ElementBuilder.TextInputSettings.Default;
            settings.Font = font; settings.TextColor = textColor; settings.ReadOnly = readOnly; settings.DoWrap = true;
            using (paper.Box("root").Size(240, 120).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                var field = paper.Box("field").Size(200, 100).FontSize(14).TextArea(value, settings);
                if (focus) { paper.SetFocus(field._handle); focus = false; }
            }
        }
    }

    [Fact]
    public void ClearAndElementLimitDoNotRetainAcceptedState()
    {
        var paper = new Paper(new Renderer(), 200, 200, new FontAtlasSettings());
        using var state = new PaperSubtreeVisualState(1);
        ElementHandle root;
        try
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(100, 100).Enter())
            { root = paper.CurrentParent; paper.Box("child").Size(10, 10); }
            paper.EndFrame();
            Assert.Throws<InvalidOperationException>(() => state.HasChanged(paper, root));
            paper.BeginFrame(.016f);
            using (paper.Box("single").Size(100, 100).Enter()) root = paper.CurrentParent;
            paper.EndFrame();
            Assert.True(state.HasChanged(paper, root));
            Assert.False(state.HasChanged(paper, root));
            state.Clear();
            Assert.True(state.HasChanged(paper, root));
        }
        finally { paper.Canvas.Dispose(); }
    }

    private sealed class Observer : IPaperSubtreePainter
    {
        public readonly PaperSubtreeVisualState State = new();
        public bool Changed;
        /// <summary>When set, an unchanged subtree is reported as painted (cached) so Paper skips its
        /// painters, the way a region reusing its layer does.</summary>
        public bool SkipWhenUnchanged;
        public bool Skipped;
        public ElementHandle Root;
        public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
        { Root = root; Changed = State.HasChanged(paper, root); Skipped = SkipWhenUnchanged && !Changed; return Skipped; }
    }
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
