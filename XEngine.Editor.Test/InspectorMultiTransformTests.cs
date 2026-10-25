using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Vector;
using Xunit;
using Fixture = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed class InspectorMultiTransformTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly Fixture F;
        internal readonly GameObject A = new("Anbi_Body"), B = new("Anbi_Face");
        internal Harness(float width = 335)
        {
            F = new Fixture(width) { FlushUndoBeforeDraw = true };
            F.Scene.Add(A); F.Scene.Add(B);
            A.Transform.LocalPosition = new(1, 2, 3); B.Transform.LocalPosition = new(1, 8, 9);
            A.Transform.LocalEulerAngles = new(10, 20, 30); B.Transform.LocalEulerAngles = new(15, 25, 35);
            A.Transform.LocalScale = new(1, 2, 3); B.Transform.LocalScale = new(4, 5, 6);
            Selection.Select(A); Selection.AddToSelection(B); F.Frame(); F.Frame();
            Undo.Clear(); EditorSceneManager.IsDirty = false;
        }
        internal ElementData Cell(int property, int axis)
        {
            var label = F.Text(Loc.Get(property == 0 ? "inspector.position" : property == 1 ? "inspector.rotation" : "inspector.scale"));
            return F.Elements().Where(e => e.IsAHookedParent && MathF.Abs(e.Y - label.Y) < .1f && e.X > label.X).ElementAt(axis);
        }
        internal void Focus(int property = 0, int axis = 0)
        {
            var cell = Cell(property, axis);
            F.Paper.SetFocus(F.Paper.FindElementByID(cell.ID)); F.Frame(); SelectAll();
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
        internal (float x, float y) Scrub(int axis = 1)
        {
            var cell = Cell(0, axis); var label = F.Text(axis == 0 ? "X" : axis == 1 ? "Y" : "Z", cell.Y);
            float x = label.X + label.LayoutWidth / 2, y = label.Y + label.LayoutHeight / 2;
            F.Paper.SetPointerPosition(x, y); F.Frame();
            F.Frame(() => F.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
            F.Frame(() => F.Paper.SetPointerPosition(x + 20, y)); F.Frame();
            return (x + 20, y);
        }
        internal void Release((float x, float y) p)
        {
            F.Frame(() => F.Paper.SetPointerState(PaperMouseBtn.Left, p.x, p.y, false, false)); F.Frame();
        }
        internal static Float3 Read(GameObject go, int property) => property == 0 ? go.Transform.LocalPosition
            : property == 1 ? go.Transform.LocalEulerAngles : go.Transform.LocalScale;
        public void Dispose() => F.Dispose();
    }

    [Theory]
    [InlineData(240f)] [InlineData(335f)] [InlineData(520f)]
    public void MixedDisplayIsPerAxisAndFieldsStayInsideInspector(float width)
    {
        using var h = new Harness(width);
        for (int axis = 0; axis < 3; axis++)
        {
            h.Focus(0, axis);
            var cell = h.Cell(0, axis); var child = h.F.Paper.GetElementData(cell.ChildIndices.First(i => h.F.Paper.GetElementData(i).IsHookedToParent));
            var state = h.F.Paper.GetElementStorage(h.F.Paper.FindElementByID(child.ID), "TextInputState", new ElementBuilder.TextInputState());
            Assert.Equal(axis == 0 ? "1" : "", state.Value);
            Assert.InRange(cell.X + cell.LayoutWidth, 0, width + .01f);
            h.Key(PaperKey.Escape);
        }
        Assert.DoesNotContain(h.F.Elements(), e => e.Paragraph?.Contains("(mixed)") == true);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    public void AxisPreviewCommitsOneGroupAndPreservesEachOtherAxis(int property, int axis)
    {
        using var h = new Harness(); var a = Harness.Read(h.A, property); var b = Harness.Read(h.B, property);
        h.Focus(property, axis); h.Type("42");
        CheckAxis(a, Harness.Read(h.A, property), axis, 42); CheckAxis(b, Harness.Read(h.B, property), axis, 42);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        h.SelectAll(); h.Type("43"); for (int i = 0; i < 40; i++) h.F.Frame();
        h.Key(PaperKey.Enter); Assert.True(EditorSceneManager.IsDirty);
        Undo.PerformUndo(); h.F.Frame(); Near(a, Harness.Read(h.A, property)); Near(b, Harness.Read(h.B, property));
        Assert.False(Undo.CanUndo); Undo.PerformRedo();
        CheckAxis(a, Harness.Read(h.A, property), axis, 43); CheckAxis(b, Harness.Read(h.B, property), axis, 43);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EscapeAndCommittedUndoPreserveUnrelatedExternalAxes(bool cancel)
    {
        using var h = new Harness(); h.Focus(0, 1); h.Type("27");
        h.A.Transform.LocalPosition = new(99, 27, 3); h.B.Transform.LocalPosition = new(98, 27, 9);
        h.Key(cancel ? PaperKey.Escape : PaperKey.Enter);
        if (!cancel) Undo.PerformUndo();
        Assert.Equal(new Float3(99, 2, 3), h.A.Transform.LocalPosition);
        Assert.Equal(new Float3(98, 8, 9), h.B.Transform.LocalPosition); Assert.False(Undo.CanUndo);
        if (cancel) Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void ConsecutiveTextAndBlurEditsHaveIndependentSteps()
    {
        using var h = new Harness(); h.Focus(); h.Type("10"); h.Key(PaperKey.Enter);
        h.Focus(); h.Type("11"); h.F.Click(300, 850);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(10, h.A.Transform.LocalPosition.X); Assert.Equal(10, h.B.Transform.LocalPosition.X);
        Undo.PerformUndo(); Assert.Equal(1, h.A.Transform.LocalPosition.X); Assert.Equal(1, h.B.Transform.LocalPosition.X);
        Assert.False(Undo.CanUndo);
    }

    [Theory]
    [InlineData("nonsense", false)] [InlineData("1", false)] [InlineData("17", true)]
    public void InvalidUnchangedAndCanceledEditsKeepRedo(string text, bool cancel)
    {
        using var h = new Harness(); h.Focus(); h.Type("10"); h.Key(PaperKey.Enter); Undo.PerformUndo(); h.F.Frame();
        EditorSceneManager.IsDirty = false; h.Focus(); h.Type(text); h.Key(cancel ? PaperKey.Escape : PaperKey.Enter);
        Assert.Equal(new Float3(1, 2, 3), h.A.Transform.LocalPosition); Assert.Equal(new Float3(1, 8, 9), h.B.Transform.LocalPosition);
        Assert.False(EditorSceneManager.IsDirty); Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ScrubIsOneGroupAndEscapeOrHostLossRollsBackEveryTarget(bool hostLoss)
    {
        using var h = new Harness(); var p = h.Scrub();
        Assert.NotEqual(2, h.A.Transform.LocalPosition.Y); Assert.Equal(h.A.Transform.LocalPosition.Y, h.B.Transform.LocalPosition.Y);
        Assert.Equal(3, h.A.Transform.LocalPosition.Z); Assert.Equal(9, h.B.Transform.LocalPosition.Z);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        if (hostLoss) { h.F.Paper.CancelDragEdit(); h.F.Frame(); } else h.Key(PaperKey.Escape);
        Assert.Equal(2, h.A.Transform.LocalPosition.Y); Assert.Equal(8, h.B.Transform.LocalPosition.Y);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty); h.Release(p);
        p = h.Scrub(); float value = h.A.Transform.LocalPosition.Y; h.Release(p); Assert.True(Undo.CanUndo);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(2, h.A.Transform.LocalPosition.Y); Assert.Equal(8, h.B.Transform.LocalPosition.Y);
        Assert.False(Undo.CanUndo); Undo.PerformRedo(); Assert.Equal(value, h.A.Transform.LocalPosition.Y); Assert.Equal(value, h.B.Transform.LocalPosition.Y);
    }

    [Fact]
    public void SelectionChangeWithSameFirstObjectAndCountCommitsOldSetOnly()
    {
        using var h = new Harness(); h.Focus(0, 1); h.Type("27"); var oldId = h.Cell(0, 1).ID;
        var c = new GameObject("Anbi_Hair"); h.F.Scene.Add(c);
        Selection.Select(h.A); Selection.AddToSelection(c); h.F.Frame(); h.F.Frame();
        Assert.NotEqual(oldId, h.Cell(0, 1).ID); h.Key(PaperKey.Escape);
        Assert.Equal(27, h.A.Transform.LocalPosition.Y); Assert.Equal(27, h.B.Transform.LocalPosition.Y); Assert.Equal(Float3.Zero, c.Transform.LocalPosition);
        Undo.PerformUndo(); Assert.Equal(2, h.A.Transform.LocalPosition.Y); Assert.Equal(8, h.B.Transform.LocalPosition.Y); Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void RemovedTargetCancelsSurvivingPreviewAndRejectsFurtherWrites()
    {
        using var h = new Harness(); h.Focus(0, 1); h.Type("27"); h.F.Scene.Remove(h.B); h.B.Dispose(); h.F.Frame();
        Assert.Equal(2, h.A.Transform.LocalPosition.Y); h.Type("9"); h.Key(PaperKey.Enter);
        Assert.Equal(2, h.A.Transform.LocalPosition.Y); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void SaveAndUndoBoundariesFinishPreviewWithoutMergingNextEdit()
    {
        using var h = new Harness(); h.Focus(0, 1); h.Type("16"); Undo.CommitNumericEdits();
        Assert.True(Undo.CanUndo); h.SelectAll(); h.Type("17"); h.Key(PaperKey.Tab);
        Undo.PerformUndo(); Assert.Equal(16, h.A.Transform.LocalPosition.Y); Assert.Equal(16, h.B.Transform.LocalPosition.Y);
        Undo.PerformUndo(); Assert.Equal(2, h.A.Transform.LocalPosition.Y); Assert.Equal(8, h.B.Transform.LocalPosition.Y);
        Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void ReplacementInstanceIsNotMutatedByPreviewCancellation()
    {
        using var h = new Harness(); h.Focus(0, 1); h.Type("27");
        var replacement = new GameObject("Replacement");
        typeof(GameObject).GetField("_identifier", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(replacement, h.B.Identifier);
        h.F.Scene.Remove(h.B); h.B.Dispose(); h.F.Scene.Add(replacement);
        replacement.Transform.LocalPosition = new(100, 200, 300); h.F.Frame(); h.Key(PaperKey.Escape);
        Assert.Equal(new Float3(100, 200, 300), replacement.Transform.LocalPosition);
        Assert.Equal(new Float3(1, 2, 3), h.A.Transform.LocalPosition); Assert.False(Undo.CanUndo);
        Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void ConsecutiveScrubsHaveSeparateUndoSteps()
    {
        using var h = new Harness(); var p = h.Scrub(); float first = h.A.Transform.LocalPosition.Y; h.Release(p);
        p = h.Scrub(); float second = h.A.Transform.LocalPosition.Y; h.Release(p);
        Assert.NotEqual(first, second); Undo.PerformUndo(); h.F.Frame();
        Assert.Equal(first, h.A.Transform.LocalPosition.Y); Assert.Equal(first, h.B.Transform.LocalPosition.Y);
        Undo.PerformUndo(); Assert.Equal(2, h.A.Transform.LocalPosition.Y); Assert.Equal(8, h.B.Transform.LocalPosition.Y);
        Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void HiddenInspectorCommitsAndClearedSurfaceReleasesCachedSet()
    {
        using var h = new Harness(); h.Focus(0, 1); h.Type("27");
        h.F.Paper.BeginFrame(1f / 60); h.F.Paper.EndFrame(); Undo.FlushFrame();
        Assert.True(Undo.CanUndo); Undo.PerformUndo();
        Assert.Equal(2, h.A.Transform.LocalPosition.Y); Assert.Equal(8, h.B.Transform.LocalPosition.Y);
        var list = new[] { h.A, h.B };
        IReadOnlyList<GameObject> old;
        using (var scope = Undo.BeginNumericFields(list, h.F.Paper)) old = scope.Targets!;
        h.F.Paper.BeginFrame(1f / 60); h.F.Paper.EndFrame();
        h.F.Paper.BeginFrame(1f / 60); h.F.Paper.EndFrame();
        using (var scope = Undo.BeginNumericFields(list, h.F.Paper)) Assert.NotSame(old, scope.Targets);
    }

    [Fact]
    public void WarmMultiScopeIsAllocationFreeAndOwnsTheInputList()
    {
        using var h = new Harness(); var list = new List<GameObject> { h.A, h.B };
        IReadOnlyList<GameObject> captured; string identity;
        using (var scope = Undo.BeginNumericFields(list, h.F.Paper)) { captured = scope.Targets!; identity = scope.Identity; }
        list[1] = new GameObject("Other"); h.F.Scene.Add(list[1]);
        Assert.Same(h.B, captured[1]);
        using (var scope = Undo.BeginNumericFields(list, h.F.Paper)) Assert.NotEqual(identity, scope.Identity);
        for (int i = 0; i < 4096; i++) using (Undo.BeginNumericFields(list, h.F.Paper)) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) using (Undo.BeginNumericFields(list, h.F.Paper)) { }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void CheckAxis(Float3 before, Float3 after, int axis, float value)
    {
        if (axis == 0) before.X = value; else if (axis == 1) before.Y = value; else before.Z = value; Near(before, after);
    }
    private static void Near(Float3 expected, Float3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0, .001f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, .001f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, .001f);
    }
}
