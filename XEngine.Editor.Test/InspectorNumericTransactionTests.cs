using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class InspectorNumericTransactionTests
{
    public sealed class NumericComponent : MonoBehaviour
    {
        public float Amount = 10;
        public Float3 Vector = new(1, 2, 3);
        public string Label = "model";
        public int[] Bindings = [1, 2, 3];
        public uint Mask;
        public byte Small = 250;
    }

    private sealed class Harness : IDisposable
    {
        internal readonly InspectorComponentHeaderTests.Fixture F = new(420);
        internal readonly GameObject Go = new("Anbi_Body");
        internal readonly NumericComponent Component;
        internal Harness()
        {
            F.FlushUndoBeforeDraw = true; // Match live Game.Render: BeginFrame -> BeginGui/Undo -> controls.
            EditorRegistries.Initialize();
            BuiltInFieldDrawers.Register(EditorApplication.PropertyGridConfig.Drawers);
            EditorApplication.PropertyGridConfig.OnBeginRoot = target => Undo.SnapshotIfInteracting(target, F.Paper);
            Component = Go.AddComponent<NumericComponent>(); F.Scene.Add(Go);
            Selection.Select(Go); F.Frame(); F.Frame(); EditorSceneManager.IsDirty = false;
        }
        internal void Focus(string label, int axis = 0)
        {
            var text = F.Text(label == "Position" ? XEngine.Rosetta.Loc.Get("inspector.position") : label);
            var indices = new List<int>();
            for (int i = 0; i < F.Paper.ElementCount; i++)
            {
                var row = F.Paper.GetElementData(i);
                if (row.IsAHookedParent && MathF.Abs(row.Y - text.Y) < .1f && row.X > text.X) indices.Add(i);
            }
            F.Paper.SetFocus(new ElementHandle(F.Paper, indices[axis])); F.Frame(); SelectAll();
        }
        internal void SelectAll()
        {
            F.Frame(() => { F.Paper.SetKeyState(PaperKey.LeftControl, true); F.Paper.SetKeyState(PaperKey.A, true); });
            F.Frame(() => { F.Paper.SetKeyState(PaperKey.LeftControl, false); F.Paper.SetKeyState(PaperKey.A, false); });
        }
        internal void Type(string raw) { F.Frame(() => F.Paper.AddInputCharacter(raw)); F.Frame(); }
        internal (float x, float y) StartScrub(string field, float dx = 20, float dy = 0)
        {
            var label = F.Text(field == "Position" ? XEngine.Rosetta.Loc.Get("inspector.position") : field);
            var handle = field == "Position" ? F.Text("X", label.Y) : label;
            float x = handle.X + handle.LayoutWidth * .5f, y = handle.Y + handle.LayoutHeight * .5f;
            F.Paper.SetPointerPosition(x, y); F.Frame();
            F.Frame(() => F.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
            F.Frame(() => F.Paper.SetPointerPosition(x + dx, y + dy)); F.Frame();
            return (x + dx, y + dy);
        }
        internal void ReleaseScrub((float x, float y) p)
        {
            F.Frame(() => F.Paper.SetPointerState(PaperMouseBtn.Left, p.x, p.y, false, false)); F.Frame();
        }
        internal void Key(PaperKey key)
        {
            F.Frame(() => F.Paper.SetKeyState(key, true));
            F.Frame(() => F.Paper.SetKeyState(key, false)); F.Frame();
        }
        public void Dispose() => F.Dispose();
    }

    [Theory]
    [InlineData(PaperKey.Enter)]
    [InlineData(PaperKey.Tab)]
    public void LivePreviewCommitsOneStepIndependentOfTypingDelay(PaperKey boundary)
    {
        using var h = new Harness(); h.Focus("Amount"); h.Type("12");
        Assert.Equal(12, h.Component.Amount); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        // Simulate arbitrarily long idle frames inside the same focus transaction.
        for (int i = 0; i < 40; i++) h.F.Frame();
        h.SelectAll(); h.Type("123"); Assert.Equal(123, h.Component.Amount);
        h.Key(boundary); Assert.True(Undo.CanUndo); Assert.True(EditorSceneManager.IsDirty);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(10, h.Component.Amount); Assert.False(Undo.CanUndo);
        Undo.PerformRedo(); Assert.Equal(123, h.Component.Amount);
    }

    [Theory]
    [InlineData("Amount", 0)]
    [InlineData("Vector", 1)]
    [InlineData("Position", 0)]
    public void EscapeRestoresComponentVectorAndExplicitTransformWithoutHistory(string field, int axis)
    {
        using var h = new Harness(); h.Focus(field, axis); h.Type("27");
        Assert.False(Undo.CanUndo); h.Key(PaperKey.Escape);
        Assert.Equal(10, h.Component.Amount); Assert.Equal(new Float3(1, 2, 3), h.Component.Vector);
        Assert.Equal(Float3.Zero, h.Go.Transform.LocalPosition); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        Assert.Equal(0, h.F.Paper.FocusedElementId);
    }

    [Fact]
    public void ConsecutiveCommitsNeverMergeEvenWithinThreeHundredMilliseconds()
    {
        using var h = new Harness(); h.Focus("Amount"); h.Type("12"); h.Key(PaperKey.Enter);
        h.Focus("Amount"); h.Type("13"); h.Key(PaperKey.Enter);
        Undo.PerformUndo(); Assert.Equal(12, h.Component.Amount); Assert.True(Undo.CanUndo);
        Undo.PerformUndo(); Assert.Equal(10, h.Component.Amount); Assert.False(Undo.CanUndo);
        Undo.PerformRedo(); Undo.PerformRedo(); Assert.Equal(13, h.Component.Amount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelAndCommittedDeltaPreserveUnrelatedFieldAndRedo(bool cancel)
    {
        using var h = new Harness();
        h.Focus("Amount"); h.Type("11"); h.Key(PaperKey.Enter); Undo.PerformUndo(); h.F.Frame();
        Assert.True(Undo.CanRedo);
        h.Focus("Amount"); h.Type("25");
        var bindings = h.Component.Bindings;
        // External change while a numeric preview is open belongs to a separate edit.
        h.Component.Label = "external";
        h.Key(cancel ? PaperKey.Escape : PaperKey.Enter);
        Assert.Equal("external", h.Component.Label);
        Assert.Same(bindings, h.Component.Bindings);
        if (cancel)
        {
            Assert.True(Undo.CanRedo); Assert.False(Undo.CanUndo);
            Undo.PerformRedo(); Assert.Equal(11, h.Component.Amount); Assert.Equal("external", h.Component.Label);
            Assert.Same(bindings, h.Component.Bindings);
        }
        else
        {
            Assert.False(Undo.CanRedo); Undo.PerformUndo();
            Assert.Equal(10, h.Component.Amount); Assert.Equal("external", h.Component.Label);
            Undo.PerformRedo(); Assert.Equal(25, h.Component.Amount); Assert.Equal("external", h.Component.Label);
            Assert.Same(bindings, h.Component.Bindings);
        }
    }

    [Fact]
    public void BlurSelectionChangeAndTargetDisappearanceDoNotEditNextObject()
    {
        using var h = new Harness(); h.Focus("Position"); h.Type("7");
        var other = new GameObject("Other"); h.F.Scene.Add(other);
        Selection.Select(other); h.F.Frame(); h.Key(PaperKey.Escape);
        Assert.Equal(new Float3(7, 0, 0), h.Go.Transform.LocalPosition); Assert.Equal(Float3.Zero, other.Transform.LocalPosition);
        Undo.PerformUndo(); Assert.Equal(Float3.Zero, h.Go.Transform.LocalPosition);
        Selection.Select(h.Go); h.F.Frame(); h.Focus("Amount"); h.Type("14");
        h.F.Paper.ClearFocus(); h.F.Frame(); Undo.PerformUndo(); Assert.Equal(10, h.Component.Amount);
        h.Focus("Amount"); h.Type("15"); EngineObject.Destroy(h.Go); h.F.Frame();
        Assert.False(Undo.CanUndo);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("10")]
    public void InvalidAndUnchangedSessionsDoNotDirtyOrClearRedo(string raw)
    {
        using var h = new Harness();
        h.Focus("Amount"); h.Type("11"); h.Key(PaperKey.Enter); Undo.PerformUndo(); h.F.Frame();
        EditorSceneManager.IsDirty = false; h.Focus("Amount"); h.Type(raw); h.Key(PaperKey.Enter);
        Assert.Equal(10, h.Component.Amount); Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void SaveBoundaryAndSceneHistoryClearSeparateSubsequentInput()
    {
        using var h = new Harness(); h.Focus("Amount"); h.Type("16");
        Undo.CommitNumericEdits(); Assert.True(Undo.CanUndo);
        h.SelectAll(); h.Type("17"); h.Key(PaperKey.Enter);
        Undo.PerformUndo(); Assert.Equal(16, h.Component.Amount);
        Undo.PerformUndo(); Assert.Equal(10, h.Component.Amount);
        h.Focus("Amount"); h.Type("18"); Undo.Clear(); h.Key(PaperKey.Escape);
        Assert.Equal(18, h.Component.Amount); Assert.False(Undo.CanUndo); Assert.False(Undo.CanRedo);
    }

    [Fact]
    public void NestedDeltaCancelPreservesOtherAxisAndExistingDirtyState()
    {
        using var h = new Harness(); EditorSceneManager.IsDirty = true;
        h.Focus("Vector", 1); h.Type("27");
        h.Component.Vector = new Float3(1, 27, 9);
        h.Key(PaperKey.Escape);
        Assert.Equal(new Float3(1, 2, 9), h.Component.Vector);
        Assert.True(EditorSceneManager.IsDirty); Assert.False(Undo.CanUndo);
        h.Focus("Position"); h.Type("7");
        h.Go.Transform.LocalPosition = new Float3(7, 9, 0);
        h.Key(PaperKey.Escape);
        Assert.Equal(new Float3(0, 9, 0), h.Go.Transform.LocalPosition); Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void HiddenInspectorCommitsAndPublishesOneNetDelta()
    {
        using var h = new Harness(); int events = 0;
        void Changed(Undo.PropertyChangedEventArgs evt) { if (ReferenceEquals(evt.Target, h.Component)) events++; }
        Undo.OnPropertyChanged += Changed;
        try
        {
            h.Focus("Amount"); h.Type("125"); Assert.Equal(0, events);
            h.F.Paper.BeginFrame(1f / 60); Origami.BeginFrame(h.F.Paper, 1f / 60);
            using (h.F.Paper.Column("host").Size(420, 900).Enter()) { }
            Origami.EndFrame(h.F.Paper); h.F.Paper.EndFrame(); Undo.FlushFrame();
            Assert.Equal(1, events); Assert.True(Undo.CanUndo);
            Undo.PerformUndo(); Assert.Equal(10, h.Component.Amount); Assert.False(Undo.CanUndo);
        }
        finally { Undo.OnPropertyChanged -= Changed; }
    }

    [Fact]
    public void MultiObjectRampInputKeepsPerTargetValuesAfterDrawingScopeEnds()
    {
        using var h = new Harness();
        var other = new GameObject("Anbi_Face");
        var component = other.AddComponent<NumericComponent>();
        h.F.Scene.Add(other); Selection.AddToSelection(other); h.F.Frame(); h.F.Frame();
        h.Focus("Amount"); h.Type("L(2,8)");
        Assert.Equal(2, h.Component.Amount); Assert.Equal(8, component.Amount);
        Assert.Equal(2, Selection.Count);
    }

    [Fact]
    public void WarmNumericDrawingScopeAllocatesZeroAndDoesNotSerialize()
    {
        using var h = new Harness();
        var throwing = h.Go.AddComponent<ThrowingSerializationComp>(); throwing.ThrowOnSerialize = true;
        for (int i = 0; i < 8192; i++) using (Undo.BeginNumericFields(throwing, h.F.Paper)) { }
        long minimum = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4096; i++) using (Undo.BeginNumericFields(throwing, h.F.Paper)) { }
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.Equal(0, minimum); Assert.False(Undo.CanUndo);
    }

    [Theory]
    [InlineData("Amount")]
    [InlineData("Position")]
    public void ScrubPreviewsUntilReleaseAndEachGestureHasIndependentHistory(string field)
    {
        using var h = new Harness();
        float Read() => field == "Amount" ? h.Component.Amount : h.Go.Transform.LocalPosition.X;
        float start = Read();
        var p = h.StartScrub(field); float first = Read(); Assert.NotEqual(start, first);
        Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
        h.ReleaseScrub(p); Assert.True(Undo.CanUndo); Assert.True(EditorSceneManager.IsDirty);
        p = h.StartScrub(field); float second = Read(); h.ReleaseScrub(p);
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(first, Read());
        Undo.PerformUndo(); h.F.Frame(); Assert.Equal(start, Read()); Assert.False(Undo.CanUndo);
        Undo.PerformRedo(); Undo.PerformRedo(); Assert.Equal(second, Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrubEscapeOrHostCancellationPreservesOtherDataAndRedo(bool hostLoss)
    {
        using var h = new Harness(); h.Focus("Amount"); h.Type("11"); h.Key(PaperKey.Enter);
        Undo.PerformUndo(); h.F.Frame(); EditorSceneManager.IsDirty = false;
        var p = h.StartScrub("Amount"); var bindings = h.Component.Bindings;
        h.Component.Label = "external";
        if (hostLoss) { h.F.Paper.CancelDragEdit(); h.F.Frame(); } else h.Key(PaperKey.Escape);
        Assert.Equal(10, h.Component.Amount); Assert.Equal("external", h.Component.Label);
        Assert.Same(bindings, h.Component.Bindings); Assert.True(Undo.CanRedo); Assert.False(Undo.CanUndo);
        Assert.False(EditorSceneManager.IsDirty); Assert.Equal(0, h.F.Paper.ActiveElementId);
        h.F.Frame(() => h.F.Paper.SetPointerPosition(p.x + 30, p.y)); h.ReleaseScrub((p.x + 30, p.y));
        Assert.Equal(10, h.Component.Amount); Undo.PerformRedo(); Assert.Equal(11, h.Component.Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFocusLossCancelsOwningPrimaryOrSecondaryPaper(bool secondary)
    {
        using var h = new Harness();
        h.Focus("Amount"); h.Type("11"); h.Key(PaperKey.Enter);
        Undo.PerformUndo(); h.F.Frame(); EditorSceneManager.IsDirty = false;
        var p = h.StartScrub("Amount");
        if (secondary)
        {
            var host = new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf()), default);
            typeof(EditorSurfaceHost).GetField("_paper", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(host, h.F.Paper);
            host.SetNativeFocused(true);
            host.SetNativeFocused(false);
        }
        else
        {
            var editor = new EditorApplication();
            typeof(Game).GetField("_paper", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(editor, h.F.Paper);
            typeof(EditorApplication).GetMethod("OnEditorSurfaceFocusChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(editor, [false]);
        }
        h.F.Frame();
        Assert.Equal(10, h.Component.Amount); Assert.Equal(0, h.F.Paper.ActiveElementId);
        Assert.False(EditorSceneManager.IsDirty); Assert.False(Undo.CanUndo); Assert.True(Undo.CanRedo);
        h.ReleaseScrub(p); Undo.PerformRedo(); Assert.Equal(11, h.Component.Amount);
    }

    [Theory]
    [InlineData(PaperKey.LeftShift, 4)]
    [InlineData(PaperKey.RightAlt, .25)]
    [InlineData(PaperKey.LeftControl, 1)]
    public void ScrubUsesObservedEditorModifiersAndSensitivity(PaperKey modifier, double scale)
    {
        using var h = new Harness();
        h.F.Frame(() => h.F.Paper.SetKeyState(modifier, true));
        var p = h.StartScrub("Amount");
        Assert.Equal((float)Math.Round(10 + 20 * Math.Sqrt(10) * .03 * scale, 2, MidpointRounding.AwayFromZero), h.Component.Amount);
        h.ReleaseScrub(p);
    }

    [Fact]
    public void ScrubUpwardDeltaAndBackToStartAreAHistoryNoOp()
    {
        using var h = new Harness(); var p = h.StartScrub("Position", 0, -20);
        Assert.Equal(.6f, h.Go.Transform.LocalPosition.X);
        h.F.Frame(() => h.F.Paper.SetPointerPosition(p.x, p.y + 20));
        h.ReleaseScrub((p.x, p.y + 20));
        Assert.Equal(Float3.Zero, h.Go.Transform.LocalPosition); Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
    }

    [Fact]
    public void ScrubSelectionChangeCommitsOldObjectAndClearAbandonsHistory()
    {
        using var h = new Harness(); var p = h.StartScrub("Amount"); float preview = h.Component.Amount;
        var other = new GameObject("Other"); h.F.Scene.Add(other); Selection.Select(other); h.F.Frame();
        h.Key(PaperKey.Escape); Assert.Equal(preview, h.Component.Amount);
        Assert.Equal(Float3.Zero, other.Transform.LocalPosition); Undo.PerformUndo(); Assert.Equal(10, h.Component.Amount);
        h.ReleaseScrub(p); Selection.Select(h.Go); h.F.Frame(); h.F.Frame();
        p = h.StartScrub("Amount"); preview = h.Component.Amount; Undo.Clear(); h.Key(PaperKey.Escape);
        Assert.Equal(preview, h.Component.Amount); Assert.False(Undo.CanUndo); Assert.False(Undo.CanRedo);
        h.ReleaseScrub(p);
    }

    [Fact]
    public void ScrubSaturatesUnsignedLimitsAndIgnoresNonFiniteStart()
    {
        using var h = new Harness();
        var p = h.StartScrub("Mask", -20); h.ReleaseScrub(p);
        Assert.Equal(0u, h.Component.Mask); Assert.False(Undo.CanUndo);
        p = h.StartScrub("Small", 20); Assert.Equal((byte)255, h.Component.Small); h.Key(PaperKey.Escape);
        Assert.Equal((byte)250, h.Component.Small); Assert.False(Undo.CanUndo); h.ReleaseScrub(p);
        h.Component.Amount = float.PositiveInfinity; h.F.Frame();
        p = h.StartScrub("Amount"); h.ReleaseScrub(p);
        Assert.Equal(float.PositiveInfinity, h.Component.Amount); Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void ScrubKeepsFractionalMotionAndPreservesOtherTransformAxisOnCancel()
    {
        using var h = new Harness(); var p = h.StartScrub("Mask", 5);
        h.F.Frame(() => h.F.Paper.SetKeyState(PaperKey.LeftAlt, true));
        for (int i = 1; i <= 4; i++) h.F.Frame(() => h.F.Paper.SetPointerPosition(p.x + i, p.y));
        Assert.Equal(6u, h.Component.Mask); h.Key(PaperKey.Escape); h.ReleaseScrub((p.x + 4, p.y));
        h.F.Frame(() => h.F.Paper.SetKeyState(PaperKey.LeftAlt, false));
        p = h.StartScrub("Position");
        h.Go.Transform.LocalPosition = new Float3(h.Go.Transform.LocalPosition.X, 9, 0);
        h.Key(PaperKey.Escape); Assert.Equal(new Float3(0, 9, 0), h.Go.Transform.LocalPosition);
        Assert.False(Undo.CanUndo); h.ReleaseScrub(p);
    }

    [Fact]
    public void PrefixPressEndsTextPreviewBeforeStartingItsOwnGesture()
    {
        using var h = new Harness(); h.Focus("Position"); h.Type("2"); Assert.False(Undo.CanUndo);
        var p = h.StartScrub("Position"); Assert.True(Undo.CanUndo);
        h.ReleaseScrub(p); Undo.PerformUndo(); Assert.Equal(2, h.Go.Transform.LocalPosition.X);
        Undo.PerformUndo(); Assert.Equal(0, h.Go.Transform.LocalPosition.X); Assert.False(Undo.CanUndo);
    }
}
