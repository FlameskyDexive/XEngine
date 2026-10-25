// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>A simple component with undoable fields.</summary>
public sealed class UndoComp : MonoBehaviour
{
    public int Value;
    public string Label = "";
}

/// <summary>
/// A component whose Echo serialization can be made to throw. Re-implements
/// <see cref="ISerializationCallbackReceiver"/> so the throw happens inside
/// Serializer.Serialize — the same failure mode as a user component with broken serialization.
/// </summary>
public sealed class ThrowingSerializationComp : MonoBehaviour, ISerializationCallbackReceiver
{
    public int Value;
    [SerializeIgnore] public bool ThrowOnSerialize;

    public new void OnBeforeSerialize()
    {
        if (ThrowOnSerialize) throw new InvalidOperationException("serialization failed");
    }

    public new void OnAfterDeserialize() { }
}

/// <summary>
/// Tests for the editor <see cref="Undo"/> system - high stakes because a bug here means lost work.
/// Covers property snapshots, action records, structural create/destroy (full restore), identifier
/// survival across destroy/recreate, time-window coalescing, continuous (gizmo) operations, history
/// trimming, and play-mode gating. Pending records are flushed via the public <see cref="Undo.IncrementGroup"/>
/// (same path as the per-frame FlushFrame).
/// </summary>
public class UndoTests : EditorTestHarness
{
    public UndoTests() => Undo.Clear(); // static history persists across tests

    private (Scene scene, GameObject go, UndoComp comp) MakeScene()
    {
        var scene = new Scene();
        var go = new GameObject("GO");
        var comp = go.AddComponent<UndoComp>();
        scene.Add(go);
        Scene.Load(scene);
        return (scene, go, comp);
    }

    private static void DestroyGO(Scene s, GameObject go)
    {
        foreach (var c in go.GetChildrenDeep().ToList())
            s.Remove(c);
        s.Remove(go);
        go.Dispose();
    }

    // ---- Property changes ----

    [Fact]
    public void PropertyChange_Undo_Redo()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 10;
        Undo.Snapshot(comp);
        comp.Value = 20;
        Undo.IncrementGroup();

        Assert.True(Undo.CanUndo);
        Undo.PerformUndo();
        Assert.Equal(10, comp.Value);

        Assert.True(Undo.CanRedo);
        Undo.PerformRedo();
        Assert.Equal(20, comp.Value);
    }

    [Fact]
    public void PropertyChange_MultipleFieldsSameFrame_OneStep()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 1; comp.Label = "a";
        Undo.Snapshot(comp);
        comp.Value = 2; comp.Label = "b";
        Undo.IncrementGroup();

        Undo.PerformUndo();
        Assert.Equal(1, comp.Value);
        Assert.Equal("a", comp.Label);
        Assert.False(Undo.CanUndo); // single step covered both fields
    }

    [Fact]
    public void NoOpChange_DoesNotCreateStep()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 5;
        Undo.Snapshot(comp);
        // no mutation
        Undo.IncrementGroup();

        Assert.False(Undo.CanUndo);
    }

    // ---- A0-cont: provenance-bearing change events (Layer 0) ----

    [Fact]
    public void PropertyChanged_RaisesEvent_WithProvenanceBearingDelta()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 10;
        Undo.Snapshot(comp);

        // Subscribe and capture the structured change event. Push a tool provenance so we can assert
        // it round-trips through the event.
        Undo.PropertyChangedEventArgs? captured = null;
        void Handler(Undo.PropertyChangedEventArgs e) => captured = e;
        Undo.OnPropertyChanged += Handler;
        try
        {
            Undo.PushProvenance(XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-object-patch", "raise camera", "key-7"));
            comp.Value = 42;
            Undo.IncrementGroup(); // flush
            Undo.PopProvenance();
        }
        finally
        {
            Undo.OnPropertyChanged -= Handler;
        }

        Assert.NotNull(captured);
        Assert.Equal("xengine-cli.scene-object-patch", XEngine.Data.Provenance.ProvenancedDelta
            .GetProvenance(captured!.Delta)!.Author);
        Assert.Equal("raise camera", XEngine.Data.Provenance.ProvenancedDelta
            .GetProvenance(captured.Delta)!.Intent);
        Assert.Equal(comp.Identifier, captured.TargetIdentifier);
    }

    [Fact]
    public void NoSubscribers_DeltaPathIsSkipped_AndUndoStillWorks()
    {
        // When nothing subscribes to OnPropertyChanged, the editor pays zero delta cost but undo
        // behavior is unchanged. This guards the cost-skip optimization.
        var (_, _, comp) = MakeScene();
        comp.Value = 1;
        Undo.Snapshot(comp);
        comp.Value = 9;
        Undo.IncrementGroup(); // flush, no subscribers

        Assert.True(Undo.CanUndo);
        Undo.PerformUndo();
        Assert.Equal(1, comp.Value);
    }

    // ---- A4: ChangeBus aggregates the change stream ----

    [Fact]
    public void ChangeBus_RelaysUndoPropertyChanges()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 7;
        Undo.Snapshot(comp);

        Undo.PropertyChangedEventArgs? busEvent = null;
        void Handler(Undo.PropertyChangedEventArgs e) => busEvent = e;
        ChangeBus.OnChange += Handler;
        try
        {
            comp.Value = 77;
            Undo.IncrementGroup(); // flush → Undo.OnPropertyChanged → ChangeBus.OnChange
        }
        finally
        {
            ChangeBus.OnChange -= Handler;
        }

        Assert.NotNull(busEvent);
        Assert.Equal(Undo.ChangeKind.Property, busEvent!.Kind);
        Assert.Equal(comp.Identifier, busEvent.TargetIdentifier);
        // DrainRecent also saw it.
        Assert.Contains(ChangeBus.DrainRecent(), e => e.TargetIdentifier == comp.Identifier);
    }

    // ---- A2: ChangeJournalService end-to-end (editor change → on-disk journal) ----

    [Fact]
    public async Task ChangeJournalService_RecordsEditorChangesToDisk()
    {
        // End-to-end: wire the journal service, mutate a component, flush, and confirm the change
        // landed in the on-disk journal with the correct provenance.
        string tempProject = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "xengine-journal-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new ChangeJournalService(tempProject, "test-project");

            var (_, _, comp) = MakeScene();
            comp.Value = 100;
            Undo.Snapshot(comp);

            Undo.PushProvenance(XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-object-patch", "set value to 200", "k-e2e"));
            comp.Value = 200;
            Undo.IncrementGroup(); // flush → fires OnPropertyChanged → journal append
            Undo.PopProvenance();

            // Give the fire-and-forget append a moment to complete.
            await System.Threading.Tasks.Task.Delay(150);

            var records = await service.Journal.QueryAsync(
                new XEngine.Data.Provenance.JournalQuery());
            Assert.NotEmpty(records);
            var rec = records[0];
            Assert.Equal("xengine-cli.scene-object-patch", rec.Provenance.Author);
            Assert.Contains("200", rec.Provenance.Intent);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempProject, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NewAction_ClearsRedoStack()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 0;
        Undo.Snapshot(comp);
        comp.Value = 1;
        Undo.IncrementGroup();

        Undo.PerformUndo();
        Assert.True(Undo.CanRedo);

        Undo.RegisterAction("New", () => { }, () => { });
        Undo.IncrementGroup();

        Assert.False(Undo.CanRedo);
    }

    // ---- Generic actions ----

    [Fact]
    public void RegisterAction_Undo_Redo()
    {
        MakeScene();
        int x = 1; // simulate the immediate write
        Undo.RegisterAction("A", undo: () => x = 0, redo: () => x = 1);
        Undo.IncrementGroup();

        Undo.PerformUndo();
        Assert.Equal(0, x);
        Undo.PerformRedo();
        Assert.Equal(1, x);
    }

    [Fact]
    public void RegisterActionGroup_OneStep_UndoesAllInReverse()
    {
        MakeScene();
        int x = 11; // both already applied
        var actions = new List<(Action undo, Action redo)>
        {
            (() => x -= 1, () => x += 1),
            (() => x -= 10, () => x += 10),
        };
        Undo.RegisterActionGroup("Group", actions);
        Undo.IncrementGroup();

        Undo.PerformUndo();
        Assert.Equal(0, x); // one step undid both
        Assert.False(Undo.CanUndo);

        Undo.PerformRedo();
        Assert.Equal(11, x);
    }

    // ---- Structural: create ----

    [Fact]
    public void RegisterCreatedObject_Undo_Destroys_Redo_Recreates()
    {
        var scene = new Scene();
        Scene.Load(scene);
        Undo.Clear();

        var go = new GameObject("Created");
        go.AddComponent<UndoComp>().Value = 7;
        scene.Add(go);
        Guid goId = go.Identifier;

        Undo.RegisterCreatedObject(go, "Create");
        Undo.IncrementGroup();

        Undo.PerformUndo();
        Assert.Null(Undo.FindGO(goId));

        Undo.PerformRedo();
        var restored = Undo.FindGO(goId);
        Assert.NotNull(restored);
        Assert.Equal(7, restored!.GetComponent<UndoComp>()!.Value);
    }

    [Fact]
    public void RegisterCreatedObjectGroup_SingleUndoRemovesAll_SingleRedoRestoresAll()
    {
        var scene = new Scene();
        Scene.Load(scene);
        Undo.Clear();

        // Two top-level roots (one with a child) — the shape of a template plus its canvas popup.
        var rootA = new GameObject("A");
        rootA.AddComponent<UndoComp>().Value = 1;
        var childA = new GameObject("A Child");
        childA.SetParent(rootA);
        scene.Add(rootA);
        var rootB = new GameObject("B");
        scene.Add(rootB);
        Guid rootAId = rootA.Identifier;
        Guid childAId = childA.Identifier;
        Guid rootBId = rootB.Identifier;

        Undo.RegisterCreatedObjectGroup([rootA, rootB], "Create Template");
        Undo.IncrementGroup();

        Assert.True(Undo.CanUndo);
        Undo.PerformUndo(); // ONE undo removes the whole batch, subtrees included
        Assert.Null(Undo.FindGO(rootAId));
        Assert.Null(Undo.FindGO(childAId));
        Assert.Null(Undo.FindGO(rootBId));
        Assert.False(Undo.CanUndo); // it really was a single step

        Undo.PerformRedo(); // ONE redo brings everything back with identifiers intact
        var restoredA = Undo.FindGO(rootAId);
        Assert.NotNull(restoredA);
        Assert.Equal(1, restoredA!.GetComponent<UndoComp>()!.Value);
        Assert.Single(restoredA.Children);
        Assert.Equal(childAId, restoredA.Children[0].Identifier);
        Assert.NotNull(Undo.FindGO(rootBId));
    }

    // ---- Flush resilience: a throwing component must not poison the per-frame flush ----

    [Fact]
    public void CreatedObjectFlush_SerializationThrows_ClearsPendingAndKeepsOtherRecords()
    {
        var scene = new Scene();
        Scene.Load(scene);
        Undo.Clear();

        var bad = new GameObject("Bad");
        bad.AddComponent<ThrowingSerializationComp>().ThrowOnSerialize = true;
        scene.Add(bad);
        var good = new GameObject("Good");
        scene.Add(good);
        Guid goodId = good.Identifier;

        Undo.RegisterCreatedObject(bad, "Create Bad");
        Undo.RegisterCreatedObject(good, "Create Good");

        // The flush runs every frame from BeginGui: it must swallow the bad record instead of
        // rethrowing forever (which used to blank the whole editor GUI).
        Undo.IncrementGroup();

        Assert.True(Undo.CanUndo);
        Undo.PerformUndo(); // the healthy record still made it into the stack
        Assert.Null(Undo.FindGO(goodId));

        // The bad record was dropped, not retried: nothing left pending, flushing again is a no-op.
        Assert.False(Undo.CanUndo);
        Undo.IncrementGroup();
        Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void SnapshotFlush_SerializationThrows_ClearsPendingAndKeepsOtherSnapshots()
    {
        var scene = new Scene();
        var badGo = new GameObject("Bad");
        var bad = badGo.AddComponent<ThrowingSerializationComp>();
        var goodGo = new GameObject("Good");
        var good = goodGo.AddComponent<UndoComp>();
        scene.Add(badGo);
        scene.Add(goodGo);
        Scene.Load(scene);
        Undo.Clear();

        good.Value = 1;
        Undo.Snapshot(bad);  // 'before' state serializes fine (flag still off)
        Undo.Snapshot(good);
        bad.ThrowOnSerialize = true; // the 'after' serialization at flush time now throws
        bad.Value = 5;
        good.Value = 2;

        Undo.IncrementGroup(); // must not throw

        Assert.True(Undo.CanUndo);
        Undo.PerformUndo();
        Assert.Equal(1, good.Value); // the healthy change was recorded
        Assert.Equal(5, bad.Value);  // the bad change is kept, just not undoable

        // Pending snapshots were cleared, so the next flush cannot rethrow.
        Undo.IncrementGroup();
        Assert.False(Undo.CanUndo);
    }

    // ---- Structural: destroy (the lost-work case) ----

    [Fact]
    public void RegisterDestroyObject_Undo_RestoresComponentDataAndIdentifier()
    {
        var (scene, go, comp) = MakeScene();
        comp.Value = 42; comp.Label = "hi";
        Guid goId = go.Identifier;
        Guid compId = comp.Identifier;

        Undo.RegisterDestroyObject(go, "Delete");
        DestroyGO(scene, go);
        Undo.IncrementGroup();

        Assert.Null(Undo.FindGO(goId));

        Undo.PerformUndo();
        var restored = Undo.FindGO(goId);
        Assert.NotNull(restored);
        var rc = restored!.GetComponent<UndoComp>()!;
        Assert.Equal(42, rc.Value);
        Assert.Equal("hi", rc.Label);
        Assert.Equal(compId, rc.Identifier); // identifier preserved so future records still resolve
    }

    [Fact]
    public void RegisterDestroyObject_Undo_RestoresChildren()
    {
        var scene = new Scene();
        Scene.Load(scene);
        Undo.Clear();

        var parent = new GameObject("Parent");
        parent.AddComponent<UndoComp>().Value = 1;
        var child = new GameObject("Child");
        child.AddComponent<UndoComp>().Value = 2;
        child.SetParent(parent);
        scene.Add(parent);

        Guid parentId = parent.Identifier;
        Guid childId = child.Identifier;

        Undo.RegisterDestroyObject(parent, "Delete");
        DestroyGO(scene, parent);
        Undo.IncrementGroup();

        Undo.PerformUndo();
        var rParent = Undo.FindGO(parentId);
        Assert.NotNull(rParent);
        Assert.Single(rParent!.Children);
        var rChild = rParent.Children[0];
        Assert.Equal(childId, rChild.Identifier);
        Assert.Equal(2, rChild.GetComponent<UndoComp>()!.Value);
    }

    [Fact]
    public void PropertyUndo_SurvivesDestroyAndRecreate()
    {
        var (scene, go, comp) = MakeScene();
        comp.Value = 10;
        Undo.Snapshot(comp);
        comp.Value = 20;
        Undo.IncrementGroup(); // property step (resolves comp by identifier)

        Guid goId = go.Identifier;
        Undo.RegisterDestroyObject(go, "Delete");
        DestroyGO(scene, go);
        Undo.IncrementGroup(); // destroy step

        Undo.PerformUndo(); // undo destroy -> recreate go (same identifiers), comp.Value == 20
        var restored = Undo.FindGO(goId);
        Assert.NotNull(restored);
        Assert.Equal(20, restored!.GetComponent<UndoComp>()!.Value);

        Undo.PerformUndo(); // undo property change -> resolves the RECREATED comp by identifier
        Assert.Equal(10, restored.GetComponent<UndoComp>()!.Value);
    }

    // ---- Coalescing ----

    [Fact]
    public void ContinuousPropertyEdits_CoalesceIntoOneStep()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 0;
        Undo.Snapshot(comp);
        comp.Value = 1;
        Undo.IncrementGroup(); // step: before 0, after 1

        Undo.Snapshot(comp);
        comp.Value = 2;
        Undo.IncrementGroup(); // coalesces (chain 1->2, same target, within window)

        Undo.PerformUndo();
        Assert.Equal(0, comp.Value); // back to the original, single step
        Assert.False(Undo.CanUndo);
    }

    // ---- History trimming ----

    [Fact]
    public void MaxSteps_TrimsOldestSteps()
    {
        MakeScene();
        int prev = Undo.MaxSteps;
        Undo.MaxSteps = 3;
        try
        {
            for (int i = 0; i < 5; i++)
            {
                Undo.RegisterAction($"a{i}", () => { }, () => { });
                Undo.IncrementGroup();
            }

            int undos = 0;
            while (Undo.CanUndo && undos < 20) { Undo.PerformUndo(); undos++; }
            Assert.Equal(3, undos);
        }
        finally { Undo.MaxSteps = prev; }
    }

    [Fact]
    public void Clear_EmptiesHistory()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 0; Undo.Snapshot(comp); comp.Value = 1; Undo.IncrementGroup();
        Assert.True(Undo.CanUndo);

        Undo.Clear();

        Assert.False(Undo.CanUndo);
        Assert.False(Undo.CanRedo);
    }

    // ---- Continuous (gizmo drag) ----

    [Fact]
    public void Continuous_End_PushesOneStep_UndoRestoresStart_RedoReappliesEnd()
    {
        var (_, go, _) = MakeScene();
        go.Transform.LocalPosition = new Float3(0, 0, 0);

        Undo.BeginContinuous(new[] { go }, "Move");
        go.Transform.LocalPosition = new Float3(5, 0, 0);
        Undo.EndContinuous();

        Assert.True(Undo.CanUndo);
        Undo.PerformUndo();
        Assert.Equal(0, go.Transform.LocalPosition.X, 4);

        Undo.PerformRedo();
        Assert.Equal(5, go.Transform.LocalPosition.X, 4);
    }

    [Fact]
    public void CancelContinuous_RestoresStart_NoStep()
    {
        var (_, go, _) = MakeScene();
        go.Transform.LocalPosition = new Float3(0, 0, 0);

        Undo.BeginContinuous(new[] { go }, "Move");
        go.Transform.LocalPosition = new Float3(5, 0, 0);
        Undo.CancelContinuous();

        Assert.Equal(0, go.Transform.LocalPosition.X, 4);
        Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void PerformUndo_DuringContinuous_CancelsInsteadOfUndoing()
    {
        var (_, go, _) = MakeScene();
        go.Transform.LocalPosition = new Float3(0, 0, 0);

        Undo.BeginContinuous(new[] { go }, "Move");
        go.Transform.LocalPosition = new Float3(5, 0, 0);
        Undo.PerformUndo(); // cancels the drag rather than popping the stack

        Assert.Equal(0, go.Transform.LocalPosition.X, 4);
        Assert.False(Undo.IsContinuous);
        Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void Continuous_NoMovement_PushesNoStep()
    {
        var (_, go, _) = MakeScene();
        Undo.BeginContinuous(new[] { go }, "Move");
        // no movement
        Undo.EndContinuous();

        Assert.False(Undo.CanUndo);
    }

    // ---- Interaction between actions and snapshots ----

    [Fact]
    public void ActionAndSnapshotSameFrame_PropertySnapshotIsDiscarded()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 10;
        Undo.Snapshot(comp);
        comp.Value = 20;
        Undo.RegisterAction("Some Action", () => { }, () => { }); // an explicit action this frame
        Undo.IncrementGroup();

        // Only the action step exists; the property snapshot was dropped to avoid a duplicate record.
        Undo.PerformUndo();
        Assert.Equal(20, comp.Value); // property change was NOT tracked
        Assert.False(Undo.CanUndo);   // exactly one step
    }

    // ---- Play-mode gating ----

    [Fact]
    public void WhenPlaying_RecordingIsNoOp()
    {
        bool prev = Application.IsPlaying;
        Application.IsPlaying = true;
        try
        {
            var (_, _, comp) = MakeScene();
            comp.Value = 10;
            Undo.Snapshot(comp);
            comp.Value = 20;
            Undo.IncrementGroup();

            Assert.False(Undo.CanUndo);
        }
        finally { Application.IsPlaying = prev; }
    }

    [Fact]
    public void Undo_OnEmptyHistory_IsSafeNoOp()
    {
        MakeScene();
        Assert.False(Undo.CanUndo);
        Undo.PerformUndo(); // must not throw
        Undo.PerformRedo();
        Assert.False(Undo.CanUndo);
    }

    // ---- Coalescing boundaries ----

    [Fact]
    public void PropertyEdits_OutsideTimeWindow_StayAsSeparateSteps()
    {
        var (_, _, comp) = MakeScene();
        comp.Value = 0;
        Undo.Snapshot(comp);
        comp.Value = 1;
        Undo.IncrementGroup();

        System.Threading.Thread.Sleep(350); // exceed the 300ms coalesce window

        Undo.Snapshot(comp);
        comp.Value = 2;
        Undo.IncrementGroup();

        // Two distinct steps - granular undo must survive a time gap, not collapse into one.
        Undo.PerformUndo();
        Assert.Equal(1, comp.Value);
        Undo.PerformUndo();
        Assert.Equal(0, comp.Value);
    }

    [Fact]
    public void RegisterCoalescableAction_MergesAndKeepsOriginalUndoLatestRedo()
    {
        MakeScene();
        int x = 1;
        Undo.RegisterCoalescableAction("Edit", () => x = 0, () => x = 1);
        Undo.IncrementGroup();

        x = 2;
        Undo.RegisterCoalescableAction("Edit", () => x = 0, () => x = 2); // coalesces inline with the top

        Undo.PerformUndo();
        Assert.Equal(0, x);            // original undo
        Assert.False(Undo.CanUndo);    // single coalesced step

        Undo.PerformRedo();
        Assert.Equal(2, x);            // latest redo value
    }

    // ---- Multi-object helpers ----

    [Fact]
    public void ApplyGameObjectChanges_AppliesToAll_AsOneUndoStep()
    {
        var scene = new Scene();
        var a = new GameObject("A");
        var b = new GameObject("B");
        scene.Add(a); scene.Add(b);
        Scene.Load(scene);
        Undo.Clear();

        Undo.ApplyGameObjectChanges(new[] { a, b }, "Rename", g => g.Name, (g, v) => g.Name = v, "Renamed");
        Assert.Equal("Renamed", a.Name);
        Assert.Equal("Renamed", b.Name);
        Undo.IncrementGroup();

        Undo.PerformUndo();
        Assert.Equal("A", a.Name);
        Assert.Equal("B", b.Name);
        Assert.False(Undo.CanUndo); // single grouped step

        Undo.PerformRedo();
        Assert.Equal("Renamed", a.Name);
        Assert.Equal("Renamed", b.Name);
    }

    // ---- Robustness ----

    [Fact]
    public void PropertyUndo_OnDestroyedTarget_IsSafeNoOp()
    {
        var (scene, go, comp) = MakeScene();
        comp.Value = 0;
        Undo.Snapshot(comp);
        comp.Value = 1;
        Undo.IncrementGroup();

        DestroyGO(scene, go); // destroy without registering undo - the record's target is now gone

        Undo.PerformUndo(); // must not throw; resolves to null and no-ops
        Assert.True(Undo.CanRedo);
    }

    [Fact]
    public void Continuous_MultipleTargets_RestoredTogether()
    {
        var scene = new Scene();
        var a = new GameObject("A");
        var b = new GameObject("B");
        scene.Add(a); scene.Add(b);
        Scene.Load(scene);
        Undo.Clear();
        a.Transform.LocalPosition = Float3.Zero;
        b.Transform.LocalPosition = Float3.Zero;

        Undo.BeginContinuous(new[] { a, b }, "Move");
        a.Transform.LocalPosition = new Float3(5, 0, 0);
        b.Transform.LocalPosition = new Float3(0, 7, 0);
        Undo.EndContinuous();

        Undo.PerformUndo();
        Assert.Equal(0, a.Transform.LocalPosition.X, 4);
        Assert.Equal(0, b.Transform.LocalPosition.Y, 4);
        Assert.False(Undo.CanUndo); // one step for both targets
    }

    [Fact]
    public void SnapshotIfInteracting_WithoutInput_DoesNotRecord()
    {
        var (_, _, comp) = MakeScene();
        Input.PushHandler(new UndoTestMouseInput());
        try
        {
            comp.Value = 1;
            Undo.SnapshotIfInteracting(comp);
            comp.Value = 2;
            Undo.IncrementGroup();
        }
        finally
        {
            Input.PopHandler();
        }

        Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void SnapshotIfInteracting_WithMouseDown_Records()
    {
        var (_, _, comp) = MakeScene();
        var input = new UndoTestMouseInput { HeldLeft = true };
        Input.PushHandler(input);
        try
        {
            comp.Value = 1;
            Undo.SnapshotIfInteracting(comp);
            comp.Value = 2;
            Undo.IncrementGroup();
        }
        finally
        {
            Input.PopHandler();
        }

        Assert.True(Undo.CanUndo);
        Undo.PerformUndo();
        Assert.Equal(1, comp.Value);
    }

    [Fact]
    public void SnapshotIfInteracting_AfterSelectionChange_DoesNotRecord()
    {
        var (_, go, comp) = MakeScene();
        Input.PushHandler(new UndoTestMouseInput { HeldLeft = true });
        try
        {
            Selection.Select(go);
            comp.Value = 1;
            Undo.SnapshotIfInteracting(comp);
            comp.Value = 2;
            Undo.IncrementGroup();
        }
        finally
        {
            Input.PopHandler();
        }

        Assert.False(Undo.CanUndo);
    }

    [Fact]
    public void SelectionGestureSkipsSerializationAcrossFrames_ThenReleaseAllowsMouseEdit()
    {
        var (scene, go, _) = MakeScene();
        var comp = go.AddComponent<ThrowingSerializationComp>();
        var input = new UndoTestMouseInput { HeldLeft = true };
        Input.PushHandler(input);
        try
        {
            Selection.Select(go);
            comp.ThrowOnSerialize = true;
            for (int i = 0; i < 3; i++)
            {
                Undo.SnapshotIfInteracting(comp); // Must not serialize even on a held selection.
                Undo.FlushFrame();
            }
            Assert.False(Undo.CanUndo);
            input.HeldLeft = false;
            Undo.FlushFrame(); // Hidden Inspector: release must still expire suppression.
            comp.ThrowOnSerialize = false;
            comp.Value = 10;
            input.HeldLeft = true;
            Undo.SnapshotIfInteracting(comp);
            comp.Value = 20;
            Undo.FlushFrame();
            Undo.PerformUndo(); Assert.Equal(10, comp.Value);
            Undo.PerformRedo(); Assert.Equal(20, comp.Value);
        }
        finally { Input.PopHandler(); }
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("paste")]
    [InlineData("cut")]
    [InlineData("backspace")]
    [InlineData("delete")]
    [InlineData("shift-insert")]
    [InlineData("enter")]
    [InlineData("space")]
    public void FirstKeyboardEditAfterSelectionReleaseRecordsWithoutReselection(string edit)
    {
        var (_, go, comp) = MakeScene();
        var input = new UndoTestMouseInput { HeldLeft = true };
        Input.PushHandler(input);
        try
        {
            Selection.Select(go);
            Undo.SnapshotIfInteracting(comp);
            Undo.FlushFrame();
            input.HeldLeft = false;
            // Deliberately no idle Inspector draw between release and keyboard edit.
            input.Text = edit == "typed" ? "2" : "";
            input.Ctrl = edit is "paste" or "cut";
            input.Shift = edit == "shift-insert";
            input.Key = edit switch
            {
                "paste" => KeyCode.V, "cut" => KeyCode.X, "backspace" => KeyCode.Backspace,
                "delete" => KeyCode.Delete, "shift-insert" => KeyCode.Insert,
                "enter" => KeyCode.Enter, "space" => KeyCode.Space, _ => KeyCode.Unknown
            };
            comp.Value = 1;
            Undo.SnapshotIfInteracting(comp);
            comp.Value = 2;
            Undo.FlushFrame();
            Assert.True(Undo.CanUndo);
            Undo.PerformUndo(); Assert.Equal(1, comp.Value);
            Undo.PerformRedo(); Assert.Equal(2, comp.Value);
        }
        finally { Input.PopHandler(); }
    }

    [Fact]
    public void ProgrammaticSelectionDoesNotSuppressNextMouseEdit()
    {
        var (_, go, comp) = MakeScene();
        var input = new UndoTestMouseInput();
        Input.PushHandler(input);
        try
        {
            Selection.Select(go);
            input.HeldLeft = true;
            Undo.SnapshotIfInteracting(comp);
            comp.Value = 2;
            Undo.FlushFrame();
            Undo.PerformUndo(); Assert.Equal(0, comp.Value);
        }
        finally { Input.PopHandler(); }
    }

    [Fact]
    public void UnrelatedButtonCannotExtendSelectionSuppression_AndIdleDoesNotSerialize()
    {
        var (_, go, _) = MakeScene();
        var comp = go.AddComponent<ThrowingSerializationComp>();
        var input = new UndoTestMouseInput { HeldLeft = true };
        Input.PushHandler(input);
        try
        {
            Selection.Select(go);
            input.HeldLeft = false;
            comp.ThrowOnSerialize = true;
            Undo.SnapshotIfInteracting(comp); Undo.FlushFrame(); // Idle after release: no serialization.
            input.HeldRight = true;
            comp.ThrowOnSerialize = false;
            Undo.SnapshotIfInteracting(comp);
            comp.Value = 5; Undo.FlushFrame();
            Assert.True(Undo.CanUndo);
            Undo.PerformUndo(); Assert.Equal(0, comp.Value);
        }
        finally { Input.PopHandler(); }
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("paste")]
    [InlineData("cut")]
    [InlineData("backspace")]
    [InlineData("delete")]
    public void ActualInspectorFieldInputAfterSelectionCanUndoAndRedo(string edit)
    {
        EditorRegistries.Initialize();
        using var f = new InspectorComponentHeaderTests.Fixture(420);
        EditorApplication.PropertyGridConfig.OnBeginRoot = target => Undo.SnapshotIfInteracting(target, f.Paper);
        BuiltInFieldDrawers.Register(EditorApplication.PropertyGridConfig.Drawers);
        var go = new GameObject("ModelBody"); var comp = go.AddComponent<UndoComp>();
        comp.Value = 10; comp.Label = "model"; f.Scene.Add(go);
        var input = new UndoTestMouseInput { HeldLeft = true };
        Input.PushHandler(input);
        try
        {
            Selection.Select(go); f.Frame();
            input.HeldLeft = false; f.Frame();
            string label = edit is "typed" or "paste" ? "Value" : "Label";
            var text = f.Text(label);
            int field = -1;
            for (int i = 0; i < f.Paper.ElementCount; i++)
            {
                var row = f.Paper.GetElementData(i);
                if (row.IsAHookedParent && MathF.Abs(row.Y - text.Y) < .1f && row.X > text.X)
                { field = i; break; }
            }
            Assert.True(field >= 0, $"Missing {label} input");
            f.Paper.SetFocus(new ElementHandle(f.Paper, field)); f.Frame();
            f.Frame(() => { f.Paper.SetKeyState(PaperKey.LeftControl, true); f.Paper.SetKeyState(PaperKey.A, true); });
            f.Frame(() => { f.Paper.SetKeyState(PaperKey.LeftControl, false); f.Paper.SetKeyState(PaperKey.A, false); });
            Assert.False(Undo.CanUndo);
            EditorSceneManager.IsDirty = false;
            long version = EditorSceneManager.ChangeVersion;
            // Runtime input is already cleared by Window.LateUpdate before GUI rendering.
            // Only Paper carries this edit; duplicating it in the fake Input would hide the bug.
            var key = edit switch { "paste" => PaperKey.V, "cut" => PaperKey.X, "backspace" => PaperKey.Backspace,
                "delete" => PaperKey.Delete, _ => PaperKey.Unknown };
            f.Paper.SetClipboardHandler(new UndoTestClipboard());
            f.Frame(() =>
            {
                if (edit == "typed") f.Paper.AddInputCharacter("12");
                else { f.Paper.SetKeyState(PaperKey.LeftControl, edit is "paste" or "cut"); f.Paper.SetKeyState(key, true); }
            });
            input.Text = ""; input.Ctrl = false; input.Key = KeyCode.Unknown;
            f.Frame(() => { f.Paper.SetKeyState(PaperKey.LeftControl, false); if (edit != "typed") f.Paper.SetKeyState(key, false); });
            Assert.Equal(label == "Value" ? 12 : 10, comp.Value);
            Assert.Equal(label == "Value" ? "model" : "", comp.Label);
            if (label == "Value")
            {
                // Numeric text now previews until its explicit input boundary.
                Assert.False(Undo.CanUndo); Assert.False(EditorSceneManager.IsDirty);
                f.Frame(() => f.Paper.SetKeyState(PaperKey.Enter, true));
                f.Frame(() => f.Paper.SetKeyState(PaperKey.Enter, false));
            }
            Assert.True(Undo.CanUndo);
            Assert.True(EditorSceneManager.IsDirty);
            Assert.True(EditorSceneManager.ChangeVersion > version);
            Undo.PerformUndo(); f.Frame();
            Assert.Equal(10, comp.Value); Assert.Equal("model", comp.Label); Assert.False(Undo.CanUndo);
            Undo.PerformRedo(); f.Frame();
            Assert.Equal(label == "Value" ? 12 : 10, comp.Value);
            Assert.Equal(label == "Value" ? "model" : "", comp.Label);
        }
        finally { Input.PopHandler(); }
    }

    [Fact]
    public void ExplicitPaperUsesOnlyItsOwnBufferedInput_AndIdleGateAllocatesNothing()
    {
        using var first = new InspectorComponentHeaderTests.Fixture(335);
        using var second = new InspectorComponentHeaderTests.Fixture(335);
        var go = new GameObject("ModelBody"); var comp = go.AddComponent<ThrowingSerializationComp>();
        second.Scene.Add(go);
        Input.PushHandler(new UndoTestMouseInput { Text = "2", HeldLeft = true });
        try
        {
            // Runtime input and the other surface are active. The explicit surface is idle.
            first.Paper.AddInputCharacter("2"); comp.ThrowOnSerialize = true;
            for (int i = 0; i < 1024; i++) Undo.SnapshotIfInteracting(comp, second.Paper);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4096; i++) Undo.SnapshotIfInteracting(comp, second.Paper);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Undo.FlushFrame(); Assert.False(Undo.CanUndo);
            comp.ThrowOnSerialize = false;
            Undo.SnapshotIfInteracting(comp, first.Paper);
            comp.Value = 3; Undo.FlushFrame();
            Undo.PerformUndo(); Assert.Equal(0, comp.Value);
        }
        finally { Input.PopHandler(); }
    }

    private sealed class UndoTestClipboard : IClipboardHandler
    {
        public string GetClipboardText() => "12";
        public void SetClipboardText(string text) { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PropertyDiffDirtiesOnlyCurrentScene_AndNoOpDoesNotDirty(bool attached)
    {
        var (scene, go, comp) = MakeScene();
        if (!attached) scene.Remove(go);
        EditorSceneManager.IsDirty = false;
        long version = EditorSceneManager.ChangeVersion;
        Undo.Snapshot(comp); Undo.FlushFrame();
        Assert.False(EditorSceneManager.IsDirty); Assert.Equal(version, EditorSceneManager.ChangeVersion);
        Undo.Snapshot(comp); comp.Value = 1; Undo.FlushFrame();
        Assert.Equal(attached, EditorSceneManager.IsDirty);
        Assert.Equal(attached ? version + 1 : version, EditorSceneManager.ChangeVersion);
        Undo.Snapshot(comp); comp.Value = 2; Undo.FlushFrame();
        Assert.Equal(attached ? version + 2 : version, EditorSceneManager.ChangeVersion);
        if (attached)
        {
            Undo.PerformUndo(); Assert.Equal(0, comp.Value); // Existing coalescing remains intact.
        }
    }
}

file sealed class UndoTestMouseInput : IInputHandler
{
    public bool HeldLeft;
    public bool HeldRight;
    public string Text = "";
    public bool Ctrl;
    public bool Shift;
    public KeyCode Key = KeyCode.Unknown;

    public string Clipboard { get => ""; set { } }
    public bool IsAnyKeyDown => false;
    public Float2 MouseDelta => Float2.Zero;
    public Int2 MousePosition { get => Int2.Zero; set { } }
    public float MouseWheelDelta => 0f;
    public Int2 PrevMousePosition => Int2.Zero;

    public event Action<KeyCode, bool> OnKeyEvent { add { } remove { } }
    public event Action<MouseButton, float, float, bool, bool> OnMouseEvent { add { } remove { } }

    public char? GetPressedChar() => null;
    public string InputString => Text;
    public bool GetKey(KeyCode key) => key == Key || (Ctrl && key == KeyCode.ControlLeft)
        || (Shift && key == KeyCode.ShiftLeft);
    public bool GetKeyDown(KeyCode key) => key == Key;
    public bool GetKeyUp(KeyCode key) => false;
    public bool GetMouseButton(int button) => (button == 0 && HeldLeft) || (button == 1 && HeldRight);
    public bool GetMouseButtonDown(int button) => false;
    public bool GetMouseButtonUp(int button) => false;
    public void SetCursorVisible(bool visible, int miceIndex = 0) { }
    public void SetCursorShape(PaperCursor shape, int miceIndex = 0) { }
    public int GetGamepadCount() => 0;
    public bool IsGamepadConnected(int gamepadIndex) => false;
    public bool GetGamepadButton(int gamepadIndex, GamepadButton button) => false;
    public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button) => false;
    public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button) => false;
    public Float2 GetGamepadAxis(int gamepadIndex, int axisIndex) => Float2.Zero;
    public float GetGamepadTrigger(int gamepadIndex, int triggerIndex) => 0f;
    public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor) { }
}
