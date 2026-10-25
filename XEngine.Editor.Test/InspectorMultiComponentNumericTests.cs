using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using Xunit;
using Fixture = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;
using Numeric = XEngine.Editor.Test.InspectorNumericTransactionTests.NumericComponent;

namespace XEngine.Editor.Test;

public sealed class InspectorMultiComponentNumericTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly Fixture F;
        internal readonly GameObject A = new("Anbi_Body"), B = new("Anbi_Face");
        internal readonly Numeric CA, CB;
        internal Harness(float width = 335)
        {
            F = new Fixture(width) { FlushUndoBeforeDraw = true };
            EditorRegistries.Initialize(); BuiltInFieldDrawers.Register(EditorApplication.PropertyGridConfig.Drawers);
            EditorApplication.PropertyGridConfig.OnBeginRoot = target => Undo.SnapshotIfInteracting(target, F.Paper);
            CA = A.AddComponent<Numeric>(); CB = B.AddComponent<Numeric>(); CB.Amount = 20;
            F.Scene.Add(A); F.Scene.Add(B); Selection.Select(A); Selection.AddToSelection(B);
            F.Frame(); F.Frame(); Undo.Clear(); EditorSceneManager.IsDirty = false;
        }
        internal ElementData Cell(string label = "Amount")
        {
            var text = F.Text(label);
            return F.Elements().Single(e => e.IsAHookedParent && MathF.Abs(e.Y - text.Y) < .1f && e.X > text.X);
        }
        internal void Focus(string label = "Amount")
        {
            F.Paper.SetFocus(F.Paper.FindElementByID(Cell(label).ID)); F.Frame(); SelectAll();
        }
        internal void SelectAll()
        {
            F.Frame(() => { F.Paper.SetKeyState(PaperKey.LeftControl, true); F.Paper.SetKeyState(PaperKey.A, true); });
            F.Frame(() => { F.Paper.SetKeyState(PaperKey.LeftControl, false); F.Paper.SetKeyState(PaperKey.A, false); });
        }
        internal void Type(string text) { F.Frame(() => F.Paper.AddInputCharacter(text)); F.Frame(); }
        internal void Key(PaperKey key)
        {
            F.Frame(() => F.Paper.SetKeyState(key, true)); F.Frame(() => F.Paper.SetKeyState(key, false)); F.Frame();
        }
        internal (float x, float y) Scrub()
        {
            var label = F.Text("Amount"); float x = label.X + 20, y = label.Y + label.LayoutHeight / 2;
            F.Paper.SetPointerPosition(x, y); F.Frame();
            F.Frame(() => F.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
            F.Frame(() => F.Paper.SetPointerPosition(x + 40, y)); F.Frame(); return (x + 40, y);
        }
        internal void Release((float x, float y) p)
        {
            F.Frame(() => F.Paper.SetPointerState(PaperMouseBtn.Left, p.x, p.y, false, false)); F.Frame();
        }
        public void Dispose() => F.Dispose();
    }

    [Theory] [InlineData(240f)] [InlineData(335f)] [InlineData(520f)]
    public void ScalarMixedDashAndGeometry(float width)
    {
        using var h = new Harness(width); h.Focus(); var cell = h.Cell();
        var child = h.F.Paper.GetElementData(cell.ChildIndices.First(i => h.F.Paper.GetElementData(i).IsHookedToParent));
        var state = h.F.Paper.GetElementStorage(h.F.Paper.FindElementByID(child.ID), "TextInputState", new ElementBuilder.TextInputState());
        Assert.Equal("", state.Value); Assert.InRange(cell.X + cell.LayoutWidth, 1, width + .01f);
        h.Key(PaperKey.Escape); Assert.Equal(10, h.CA.Amount); Assert.Equal(20, h.CB.Amount);
    }

    [Theory] [InlineData(PaperKey.Enter)] [InlineData(PaperKey.Tab)]
    public void PreviewAndCommitGroupEachComponent(PaperKey boundary)
    {
        using var h = new Harness(); h.Focus(); h.Type("27");
        Assert.Equal(27, h.CA.Amount); Assert.Equal(27, h.CB.Amount);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        for (int i = 0; i < 40; i++) h.F.Frame(); h.SelectAll(); h.Type("28"); h.Key(boundary);
        Assert.True(EditorSceneManager.IsDirty); Undo.PerformUndo(); h.F.Frame();
        Assert.Equal(10, h.CA.Amount); Assert.Equal(20, h.CB.Amount); Assert.False(Undo.CanUndo);
        Undo.PerformRedo(); Assert.Equal(28, h.CA.Amount); Assert.Equal(28, h.CB.Amount);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void CancelAndUndoPreserveUnrelatedFieldsAndArrays(bool cancel)
    {
        using var h = new Harness(); h.Focus(); h.Type("27");
        h.CA.Label = "external A"; h.CB.Label = "external B"; h.CA.Bindings[0] = 55; h.CB.Bindings[1] = 66;
        h.Key(cancel ? PaperKey.Escape : PaperKey.Enter); if (!cancel) Undo.PerformUndo();
        Assert.Equal(10, h.CA.Amount); Assert.Equal(20, h.CB.Amount);
        Assert.Equal("external A", h.CA.Label); Assert.Equal("external B", h.CB.Label);
        Assert.Equal(55, h.CA.Bindings[0]); Assert.Equal(66, h.CB.Bindings[1]);
        Assert.False(Undo.CanUndo); if (cancel) Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void BlurCommitsAndSeparateTextAndScrubGesturesNeverMerge()
    {
        using var h = new Harness(); h.Focus(); h.Type("30"); h.F.Click(300, 860);
        h.Focus(); h.Type("31"); h.Key(PaperKey.Enter);
        var p = h.Scrub(); float first = h.CA.Amount; h.Release(p);
        p = h.Scrub(); Assert.NotEqual(first, h.CA.Amount); h.Release(p);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(first, h.CA.Amount); Assert.Equal(first, h.CB.Amount);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(31, h.CA.Amount); Assert.Equal(31, h.CB.Amount);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(30, h.CA.Amount);
        Undo.PerformUndo(); Assert.Equal(10, h.CA.Amount); Assert.Equal(20, h.CB.Amount); Assert.False(Undo.CanUndo);
    }

    [Theory] [InlineData("bad", false)] [InlineData("10", false)] [InlineData("27", true)]
    public void InvalidNoOpAndCancelPreserveRedo(string text, bool cancel)
    {
        using var h = new Harness(); h.CB.Amount = 10; h.F.Frame();
        Undo.RegisterAction("seed", () => { }, () => { }); Undo.FlushFrame(); Undo.PerformUndo();
        EditorSceneManager.IsDirty = false;
        h.Focus(); h.Type(text); h.Key(cancel ? PaperKey.Escape : PaperKey.Enter);
        Assert.Equal(10, h.CA.Amount); Assert.Equal(10, h.CB.Amount);
        Assert.True(Undo.CanRedo); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void HeldScrubEscapeOrHostCancelRollsBackEachComponent(bool hostCancel)
    {
        using var h = new Harness(); h.Focus(); h.Type("80"); h.Key(PaperKey.Enter);
        Undo.PerformUndo(); h.F.Frame(); EditorSceneManager.IsDirty = false;
        var p = h.Scrub(); Assert.NotEqual(10, h.CA.Amount); h.CA.Label = "external";
        if (hostCancel) { h.F.Paper.CancelDragEdit(); h.F.Frame(); } else h.Key(PaperKey.Escape);
        Assert.Equal(10, h.CA.Amount); Assert.Equal(20, h.CB.Amount); Assert.Equal("external", h.CA.Label);
        Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo); Assert.False(EditorSceneManager.IsDirty);
        h.Release(p); Assert.Equal(10, h.CA.Amount); Undo.PerformRedo(); Assert.Equal(80, h.CA.Amount);
    }

    [Fact]
    public void SaveBoundaryCommitsPreviewOnlyOnce()
    {
        using var h = new Harness(); h.Focus(); h.Type("80"); Undo.CommitNumericEdits();
        Assert.True(Undo.CanUndo); h.Key(PaperKey.Enter); Undo.PerformUndo();
        Assert.Equal(10, h.CA.Amount); Assert.Equal(20, h.CB.Amount); Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void SelectionChangedWithSameFirstAndCountCommitsOldSetOnly()
    {
        using var h = new Harness(); h.Focus(); h.Type("27");
        var c = new GameObject("Third"); var cc = c.AddComponent<Numeric>(); cc.Amount = 90; h.F.Scene.Add(c);
        Selection.Clear(); Selection.Select(h.A); Selection.AddToSelection(c); h.F.Frame();
        Assert.Equal(27, h.CA.Amount); Assert.Equal(27, h.CB.Amount); Assert.Equal(90, cc.Amount);
        Undo.PerformUndo(); Assert.Equal(10, h.CA.Amount); Assert.Equal(20, h.CB.Amount); Assert.Equal(90, cc.Amount);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RemovedOrReplacedComponentCancelsSurvivorsWithoutMutatingReplacement(bool replace)
    {
        using var h = new Harness(); h.Focus(); h.Type("27"); var id = h.CB.Identifier;
        h.B.RemoveComponent(h.CB); Numeric? replacement = null;
        if (replace)
        {
            replacement = h.B.AddComponent<Numeric>();
            typeof(MonoBehaviour).GetField("_identifier", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(replacement, id);
            replacement.Amount = 99;
        }
        h.F.Frame(); h.Key(PaperKey.Escape);
        Assert.Equal(10, h.CA.Amount); if (replace) Assert.Equal(99, replacement!.Amount);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void HiddenSurfaceCommitsAndPublishesOneNetDeltaPerComponent()
    {
        using var h = new Harness(); var changed = new List<object>();
        void OnChanged(Undo.PropertyChangedEventArgs e) => changed.Add(e.Target);
        Undo.OnPropertyChanged += OnChanged;
        try
        {
            h.Focus(); h.Type("27"); Assert.Empty(changed);
            h.F.Paper.BeginFrame(1f / 60); h.F.Paper.EndFrame(); Undo.FlushFrame();
            Assert.Equal(2, changed.Count); Assert.Contains(h.CA, changed); Assert.Contains(h.CB, changed);
            Undo.PerformUndo(); Assert.Equal(10, h.CA.Amount); Assert.Equal(20, h.CB.Amount);
        }
        finally { Undo.OnPropertyChanged -= OnChanged; }
    }

    [Fact]
    public void MultipleSectionScopesStayStableOwnListsAndReleaseWhenUnused()
    {
        using var h = new Harness(); var targets = new List<object> { h.CA, h.CB };
        IReadOnlyList<object> captured; string first, second;
        using (var scope = Undo.BeginComponentNumericFields("one", targets, h.F.Paper)) { captured = scope.ComponentTargets!; first = scope.Identity; }
        using (var scope = Undo.BeginComponentNumericFields("two", targets, h.F.Paper)) second = scope.Identity;
        Assert.NotEqual(first, second);
        for (int i = 0; i < 4096; i++)
        {
            using (var scope = Undo.BeginComponentNumericFields("one", targets, h.F.Paper)) Assert.Equal(first, scope.Identity);
            using (var scope = Undo.BeginComponentNumericFields("two", targets, h.F.Paper)) Assert.Equal(second, scope.Identity);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) using (Undo.BeginComponentNumericFields("one", targets, h.F.Paper)) { }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        targets[1] = h.CA; Assert.Same(h.CB, captured[1]);
        using (var scope = Undo.BeginComponentNumericFields("one", targets, h.F.Paper)) Assert.NotEqual(first, scope.Identity);
        h.F.Paper.BeginFrame(1f / 60); h.F.Paper.EndFrame(); h.F.Paper.BeginFrame(1f / 60); h.F.Paper.EndFrame();
        using (var scope = Undo.BeginComponentNumericFields("two", new object[] { h.CA, h.CB }, h.F.Paper)) Assert.NotEqual(second, scope.Identity);
    }

    private sealed class ValueTarget { public float Amount; }
    private sealed class CaptureDrawer : FieldDrawer
    {
        internal Action<object?>? Apply;
        public override void Draw(Paper paper, string id, object? value, Type type, Action<object?> change, int depth) => Apply = change;
    }

    [Fact]
    public void FieldCallbacksOwnTheirTargetsAcrossPapersAndReusedLists()
    {
        using var first = new Fixture(335); using var second = new Fixture(335);
        var config = new PropertyGridConfig(); var drawer = new CaptureDrawer(); config.Drawers.Register<float>(drawer);
        var a = new ValueTarget(); var b = new ValueTarget(); var c = new ValueTarget(); var list = new List<object> { a, b };
        void Draw(Paper paper, IReadOnlyList<object> targets)
        {
            paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60);
            PropertyGridRenderer.DrawTargets(paper, "same_id", targets, config, null, null, 0);
            Origami.EndFrame(paper); paper.EndFrame();
        }
        Draw(first.Paper, list); var original = drawer.Apply!;
        Draw(second.Paper, new object[] { c }); original(12f);
        Assert.Equal(12, a.Amount); Assert.Equal(12, b.Amount); Assert.Equal(0, c.Amount);
        list[1] = c; Draw(first.Paper, list); original(15f);
        Assert.Equal(15, a.Amount); Assert.Equal(15, b.Amount); Assert.Equal(0, c.Amount);
        drawer.Apply!(21f); Assert.Equal(21, a.Amount); Assert.Equal(21, c.Amount); Assert.Equal(15, b.Amount);
    }
}
