using XEngine.Editor.GUI;
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

public sealed class DragFeedbackTests : IDisposable
{
    private readonly Paper _main = NewPaper(), _secondary = NewPaper();
    private Paper _owner;
    public DragFeedbackTests()
    {
        _owner = _main;
        DragDrop.Cancel();
        DragDrop.PointerSurfaceFilter = p => ReferenceEquals(p, _owner);
        DragDrop.KeyboardSurfaceFilter = p => ReferenceEquals(p, _main);
    }
    public void Dispose()
    {
        DragDrop.Cancel();
        DragDrop.PointerSurfaceFilter = DragDrop.KeyboardSurfaceFilter = DragDrop.ButtonDownProvider = null;
        DragDrop.PointerOutsideSurfaces = null;
        DragDrop.PointerPositionProvider = null;
        _main.Canvas.Dispose(); _secondary.Canvas.Dispose();
    }

    [Fact]
    public void StaleSecondaryCannotReleaseExpireAcceptOrOverwriteFeedback()
    {
        Begin(_main, held: true); DragDrop.StartDrag(new Payload());
        DragDrop.SetVisualMode(_main, DragVisualMode.Move);
        Begin(_secondary); DragDrop.Update(_secondary);
        DragDrop.SetVisualMode(_secondary, DragVisualMode.Rejected);
        Assert.Null(DragDrop.AcceptDrop<Payload>(true));
        Assert.True(DragDrop.IsDragging); Assert.Equal(DragVisualMode.Move, DragDrop.VisualMode);
        Finish(_secondary); Finish(_main);
        Begin(_main); DragDrop.Update(_main); Assert.True(DragDrop.IsDropFrame);
        Begin(_secondary); DragDrop.Update(_secondary); Assert.True(DragDrop.IsDropFrame);
        Assert.False(DragDrop.IsDropFrameFor(_secondary)); Finish(_secondary);
        Origami.BeginFrame(_main, 0); Assert.NotNull(DragDrop.AcceptDrop<Payload>(true));
        Assert.Null(DragDrop.AcceptDrop<Payload>(true)); Assert.Null(DragDrop.Payload); Finish(_main);
    }

    [Fact]
    public void TransferUsesHostButtonAndPointerInsteadOfUnpressedTargetPaper()
    {
        bool held = true;
        DragDrop.ButtonDownProvider = _ => held;
        DragDrop.PointerPositionProvider = _ => new Float2(72, 49);
        Begin(_main, true); DragDrop.StartDrag(new Payload()); Finish(_main);
        _owner = _secondary;
        Begin(_secondary); DragDrop.Update(_secondary);
        Assert.True(DragDrop.IsDragging); Assert.Equal(new Float2(72, 49), DragDrop.DragPosition);
        DragDrop.SetVisualMode(_secondary, DragVisualMode.Link); Finish(_secondary);
        held = false;
        Begin(_secondary); DragDrop.Update(_secondary); Assert.True(DragDrop.IsDropFrame); Finish(_secondary);
        Begin(_secondary); Assert.NotNull(DragDrop.AcceptDrop<Payload>(true)); Finish(_secondary);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SourceReleaseCancelsOnlyWhenOutsideEveryHost(bool outside, bool remains)
    {
        DragDrop.PointerOutsideSurfaces = () => outside;
        Begin(_main, true); DragDrop.StartDrag(new Payload()); Finish(_main);
        _owner = _secondary;
        Begin(_main); DragDrop.Update(_main);
        Assert.Equal(remains, DragDrop.IsDragging); Finish(_main);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeCancelsBeforeTargetsBuildAndRestartHasNoOldMode(bool dropFrame)
    {
        Begin(_main, true); DragDrop.StartDrag(new Payload()); DragDrop.SetVisualMode(_main, DragVisualMode.Copy); Finish(_main);
        if (dropFrame) { Begin(_main); DragDrop.Update(_main); Finish(_main); }
        _main.BeginFrame(1f / 60); _main.SetKeyState(PaperKey.Escape, true); Origami.BeginFrame(_main, 0);
        Assert.Null(DragDrop.Payload); Assert.False(DragDrop.IsDropFrame);
        DragDrop.StartDrag(new Payload()); Assert.Equal(DragVisualMode.None, DragDrop.VisualMode); Finish(_main);
    }

    [Fact]
    public void ReleaseExpiresOnNextOwnerFrameAndCloseCancelsOnlyParticipatingPaper()
    {
        Begin(_main, true); DragDrop.StartDrag(new Payload()); Finish(_main);
        DragDrop.CancelForSurface(_secondary); Assert.True(DragDrop.IsDragging);
        Begin(_main); DragDrop.Update(_main); Assert.True(DragDrop.IsDropFrame); Finish(_main);
        Begin(_main); DragDrop.Update(_main); Assert.Null(DragDrop.Payload); Finish(_main);
        Begin(_main, true); DragDrop.StartDrag(new Payload()); DragDrop.CancelForSurface(_main);
        Assert.Null(DragDrop.Payload); Finish(_main);
    }

    [Fact]
    public void ValidationReportsRejectedOrLinkAndLeavingTargetResetsMode()
    {
        using var scope = Origami.PushTheme(EditorTheme.BuildOrigamiTheme());
        Begin(_main, true); DragDrop.StartDrag(new Payload());
        Assert.Null(DragDrop.AcceptDrop<Payload>(true, _ => false)); Assert.Equal(DragVisualMode.Rejected, DragDrop.VisualMode);
        Assert.Null(DragDrop.AcceptDrop<Payload>(true, _ => true)); Assert.Equal(DragVisualMode.Link, DragDrop.VisualMode); Finish(_main);
        Begin(_main, true); Assert.Equal(DragVisualMode.None, DragDrop.VisualMode); Finish(_main);
        Begin(_main); DragDrop.Update(_main); Assert.Null(DragDrop.AcceptDrop<Payload>(true, _ => false));
        Assert.NotNull(DragDrop.Payload); Finish(_main);
    }

    [Fact]
    public void StandaloneValidatorRemainsDropOnlyAndProfileCloneIsPreserved()
    {
        using var scope = Origami.PushTheme(OrigamiTheme.CreateDefaults());
        DragDrop.PointerSurfaceFilter = null;
        int calls = 0;
        Begin(_main, true); DragDrop.StartDrag(new Payload()); DragDrop.AcceptDrop<Payload>(true, _ => { calls++; return true; });
        Assert.Equal(0, calls); Finish(_main);
        Begin(_main); DragDrop.Update(_main); Assert.NotNull(DragDrop.AcceptDrop<Payload>(true, _ => { calls++; return true; }));
        Assert.Equal(1, calls); Finish(_main);
        var editor = EditorTheme.BuildOrigamiTheme();
        Assert.True(editor.Clone().DragFeedback);
        Assert.True(OrigamiTheme.Lerp(OrigamiTheme.CreateDefaults(), editor, 1).DragFeedback);
    }

    [Fact]
    public void MismatchedTypedAlternativeDoesNotEraseCompatibleFieldFeedback()
    {
        using var scope = Origami.PushTheme(EditorTheme.BuildOrigamiTheme());
        Begin(_main, true); DragDrop.StartDrag(new Payload()); DragDrop.SetVisualMode(_main, DragVisualMode.Link);
        Assert.Null(DragDrop.AcceptDrop<OtherPayload>(true)); Assert.Equal(DragVisualMode.Link, DragDrop.VisualMode);
        Assert.Null(DragDrop.AcceptDrop<Payload>(true, _ => false)); Assert.Equal(DragVisualMode.Rejected, DragDrop.VisualMode);
        Finish(_main);
    }

    [Theory]
    [InlineData(DragVisualMode.Copy, "+")]
    [InlineData(DragVisualMode.Move, "→")]
    [InlineData(DragVisualMode.Link, "↗")]
    [InlineData(DragVisualMode.Generic, "✓")]
    [InlineData(DragVisualMode.Rejected, "⊘")]
    [InlineData(DragVisualMode.None, "⊘")]
    public void GhostDrawsModeAndCachedTitleWithoutChangingSelection(DragVisualMode mode, string symbol)
    {
        var theme = EditorTheme.BuildOrigamiTheme();
        theme.Font = new FontFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "XEngine.Editor", "Resources", "Geist-Regular.ttf")));
        using var scope = Origami.PushTheme(theme);
        Begin(_main, true); DragDrop.StartDrag(new Payload()); DragDrop.SetVisualMode(_main, mode); DragDrop.Update(_main);
        DragDrop.DrawVisual(_main);
        Assert.Contains(Enumerable.Range(0, _main.ElementCount).Select(i => _main.GetElementData(i).Paragraph), text => text == symbol);
        Assert.Contains(Enumerable.Range(0, _main.ElementCount).Select(i => _main.GetElementData(i).Paragraph), text => text == "Probe");
        var status = Enumerable.Range(0, _main.ElementCount).Select(i => _main.GetElementData(i)).First(d => d.Paragraph == symbol);
        var style = typeof(ElementData).GetField("_elementStyle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(status)!;
        System.Drawing.Color textColor = (XEngine.Vector.Color)style.GetType().GetMethod("GetTextColor")!.Invoke(style, null)!;
        Assert.Equal((mode is DragVisualMode.None or DragVisualMode.Rejected ? theme.Red.C600 : theme.Ink.C600).ToArgb(), textColor.ToArgb());
        Finish(_main);
    }

    [Fact]
    public void LongDragTitleStaysInsideBottomRightViewport()
    {
        var theme = EditorTheme.BuildOrigamiTheme();
        theme.Font = new FontFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "XEngine.Editor", "Resources", "Geist-Regular.ttf")));
        using var scope = Origami.PushTheme(theme);
        Begin(_main, true); _main.SetPointerPosition(298, 258); DragDrop.StartDrag(new LongPayload());
        DragDrop.Update(_main); DragDrop.DrawVisual(_main); Finish(_main);
        var title = Enumerable.Range(0, _main.ElementCount).Select(i => _main.GetElementData(i)).First(d => d.Paragraph?.StartsWith("Long") == true);
        Assert.True(title.X >= 0 && title.X + title.LayoutWidth <= 300);
        Assert.True(title.Y >= 0 && title.Y + title.LayoutHeight <= 260);
    }

    [Fact]
    public void ReferenceAndHierarchyFeedbackRejectMissingComponentAndCycles()
    {
        var parent = new GameObject("parent"); var child = new GameObject("child"); child.SetParent(parent);
        try
        {
            var payload = new GameObjectDragPayload(parent);
            Assert.False(EditorDragFeedback.CanHierarchyDrop(payload, parent, true));
            Assert.False(EditorDragFeedback.CanHierarchyDrop(payload, child, true));
            Assert.True(EditorDragFeedback.CanHierarchyDrop(new GameObjectDragPayload(child), parent, true));
            Assert.True(EditorDragFeedback.CompatibleReference(payload, typeof(GameObject)));
            Assert.False(EditorDragFeedback.CompatibleReference(payload, typeof(DerivedGameObject)));
            Assert.False(EditorDragFeedback.CompatibleReference(payload, typeof(Camera)));
            Assert.True(EditorDragFeedback.CompatibleReference(payload, typeof(Transform)));
        }
        finally { child.Dispose(); parent.Dispose(); }
    }

    [Fact]
    public void ReferenceFeedbackAcceptsActualDerivedGameObjectInstance()
    {
        var derived = new DerivedGameObject();
        try
        {
            var payload = new GameObjectDragPayload(derived);
            Assert.True(EditorDragFeedback.CompatibleReference(payload, typeof(GameObject)));
            Assert.True(EditorDragFeedback.CompatibleReference(payload, typeof(DerivedGameObject)));
        }
        finally { derived.Dispose(); }
    }

    [Theory]
    [InlineData(null, DragVisualMode.Rejected)]
    [InlineData(typeof(XEngine.Runtime.Resources.Material), DragVisualMode.Rejected)]
    [InlineData(typeof(XEngine.Runtime.Resources.Mesh), DragVisualMode.Copy)]
    [InlineData(typeof(XEngine.Runtime.Resources.Sprite), DragVisualMode.Copy)]
    [InlineData(typeof(XEngine.Runtime.Resources.PrefabAsset), DragVisualMode.Copy)]
    [InlineData(typeof(XEngine.Runtime.Resources.Scene), DragVisualMode.Link)]
    public void HierarchyAssetFeedbackOnlyAdvertisesImplementedHandlers(Type? type, DragVisualMode expected)
        => Assert.Equal(expected, EditorDragFeedback.HierarchyAssetMode(type));

    private static void Begin(Paper paper, bool held = false)
    { paper.BeginFrame(1f / 60); paper.SetPointerState(PaperMouseBtn.Left, 20, 20, held, false); Origami.BeginFrame(paper, 0); }
    private static void Finish(Paper paper) => paper.EndFrame();
    private sealed class Payload : DragPayload { public override string DisplayName => "Probe"; }
    private sealed class DerivedGameObject : GameObject { }
    private sealed class OtherPayload : DragPayload { public override string DisplayName => "Other"; }
    private sealed class LongPayload : DragPayload { public override string DisplayName => "Long" + new string('x', 300); }
    private static Paper NewPaper() => new(new Renderer(), 300, 260, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
