// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// M19: Animator window Layers|Parameters graph editing, Exit destination compile, and layer migrate.
/// </summary>
public class AnimatorWindowTests
{
    [Fact]
    public void EmptyLayers_IsSingleBaseLayer()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });

        Assert.Equal(1, AnimatorControllerEditGraph.EffectiveLayerCount(c));
        Assert.Equal("Base Layer", AnimatorControllerEditGraph.LayerName(c, 0));
        Assert.Same(c.States, AnimatorControllerEditGraph.States(c, 0));
        Assert.Equal("Idle", AnimatorControllerEditGraph.GetDefaultStateName(c, 0));
    }

    [Fact]
    public void AddLayer_MigratesRootGraphAndAppends()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });

        AnimatorControllerEditGraph.AddLayer(c);

        Assert.Equal(2, c.Layers.Count);
        Assert.Equal("Base Layer", c.Layers[0].Name);
        Assert.Equal("Idle", c.Layers[0].DefaultStateName);
        Assert.Single(c.Layers[0].States);
        Assert.Empty(c.States);
        Assert.Equal("New Layer", c.Layers[1].Name);
        Assert.Same(c.Layers[1].States, AnimatorControllerEditGraph.States(c, 1));
    }

    [Fact]
    public void RemoveLayer_KeepsBaseLayer()
    {
        var c = new AnimatorController();
        AnimatorControllerEditGraph.AddLayer(c);
        Assert.False(AnimatorControllerEditGraph.RemoveLayer(c, 0));
        Assert.True(AnimatorControllerEditGraph.RemoveLayer(c, 1));
        Assert.Single(c.Layers);
    }

    [Fact]
    public void Window_SelectLayer_DirtiesGraph()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });
        AnimatorControllerEditGraph.AddLayer(c);

        var window = new AnimatorWindow();
        window.SetController(c);
        Assert.Equal(0, window.SelectedLayerIndex);
        window.SelectLayer(1);
        Assert.Equal(1, window.SelectedLayerIndex);
        Assert.Equal(c, window.Controller);
    }

    [Fact]
    public void Compile_ExitTransition_TargetsDefaultState()
    {
        var idle = new AnimationClip { Name = "idle", Duration = 1f };
        var author = new AnimatorController { DefaultStateName = "Idle" };
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            Motion = new AssetRef<AnimationClip>(idle),
            TransitionNames = { "Idle_To_Exit" },
        });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Idle_To_Exit",
            DestinationStateName = AnimatorController.ExitStateName,
            Duration = 0f,
        });

        var compiled = AnimatorControllerCompiler.Compile(author);
        Assert.Equal(0, compiled.DefaultStateIndex);
        Assert.Equal(0, compiled.Transitions[0].DestinationStateIndex);
    }

    [Fact]
    public void CreateStateMachine_MigratesToRootAndAddsChild()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });

        var sm = AnimatorControllerEditGraph.CreateStateMachine(c, 0, null, "Combat", 200, 80);

        Assert.Equal("Combat", sm.Name);
        Assert.NotNull(c.Layers[0].Root);
        Assert.Single(c.Layers[0].Root!.StateMachines);
        Assert.Equal("Idle", c.Layers[0].Root.DefaultStateName);
        Assert.Empty(c.Layers[0].States);
        Assert.Same(sm, AnimatorControllerEditGraph.ChildMachines(c, 0)[0]);
    }

    [Fact]
    public void MoveStateInto_RelocatesStateAndOutgoingTransition()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });
        c.States.Add(new AnimatorController.State
        {
            Name = "Punch", TransitionNames = { "Punch_Exit" }
        });
        c.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Punch_Exit", DestinationStateName = AnimatorController.ExitStateName
        });
        AnimatorControllerEditGraph.CreateStateMachine(c, 0, null, "Combat", 0, 0);
        Assert.True(AnimatorControllerEditGraph.MoveStateInto(c, 0, null, "Punch", "Combat"));

        var root = AnimatorControllerEditGraph.EffectiveRoot(c, 0);
        Assert.Single(root.States);
        Assert.Equal("Idle", root.States[0].Name);
        var combat = AnimatorControllerEditGraph.FindChild(root, "Combat");
        Assert.NotNull(combat);
        Assert.Equal("Punch", Assert.Single(combat!.States).Name);
        Assert.Equal("Punch_Exit", Assert.Single(combat.Transitions).Name);
        Assert.Empty(root.Transitions);
    }

    [Fact]
    public void Window_DrillInto_UpdatesPath()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });
        AnimatorControllerEditGraph.CreateStateMachine(c, 0, null, "Combat", 0, 0);

        var window = new AnimatorWindow();
        window.SetController(c);
        Assert.Empty(window.StateMachinePath);
        Assert.True(window.DrillInto("Combat"));
        Assert.Equal(new[] { "Combat" }, window.StateMachinePath);
        Assert.True(window.NavigateUp());
        Assert.Empty(window.StateMachinePath);
    }

    [Fact]
    public void RebuildGraph_MergesSameSourceDestIntoOneWire()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State
        {
            Name = "Idle", TransitionNames = { "A", "B" }
        });
        c.States.Add(new AnimatorController.State { Name = "Walk" });
        c.Transitions.Add(new AnimatorController.Transition { Name = "A", DestinationStateName = "Walk" });
        c.Transitions.Add(new AnimatorController.Transition { Name = "B", DestinationStateName = "Walk", Solo = true });

        var window = new AnimatorWindow();
        window.SetController(c);
        window.RebuildGraphNow();
        Assert.Equal(1, window.CountWires("Idle", "Walk"));
        Assert.Equal(new[] { "A", "B" }, window.WireTransitionNames("Idle", "Walk"));
    }

    [Fact]
    public void MoveTransition_RewritesPriorityOrder()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State
        {
            Name = "Idle", TransitionNames = { "A", "B" }
        });
        c.Transitions.Add(new AnimatorController.Transition { Name = "A", DestinationStateName = "Walk" });
        c.Transitions.Add(new AnimatorController.Transition { Name = "B", DestinationStateName = "Walk" });

        Assert.True(AnimatorControllerEditGraph.MoveTransition(c, 0, null, "Idle", "B", -1));
        Assert.Equal(new[] { "B", "A" }, c.States[0].TransitionNames);
    }

    [Fact]
    public void TimelineLayout_PlacesDestAtOffsetAndExitMarker()
    {
        var layout = TransitionTimelineLayout.Compute(
            0, 0, 200, TransitionTimelineLayout.Height,
            duration: 0.2f, offset: 0.25f, exitTime: 0.8f, hasExitTime: true, hasFixedDuration: true);
        Assert.True(layout.DestX > layout.SourceX);
        // View length is 1 + offset (1.25), so ExitX = 0.8 * (200 / 1.25) = 128.
        Assert.InRange(layout.ExitX, 120f, 136f);
        Assert.InRange(layout.DestX, 36f, 44f);
        Assert.Equal(TransitionTimelineLayout.Hit.ExitTime, TransitionTimelineLayout.HitTest(layout, layout.ExitX, layout.SourceY + 2));
        float dur = 0.2f, off = 0.25f, exit = 0.8f;
        TransitionTimelineLayout.ApplyDrag(TransitionTimelineLayout.Hit.Duration, 20f, 200f, layout.ViewLength, ref dur, ref off, ref exit);
        Assert.True(dur > 0.2f);

        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle", TransitionNames = { "T" } });
        c.States.Add(new AnimatorController.State { Name = "Walk" });
        c.Transitions.Add(new AnimatorController.Transition { Name = "T", DestinationStateName = "Walk" });
        var window = new AnimatorWindow();
        window.SetController(c);
        Assert.True(window.SelectTransitionByName("T"));
        Assert.Equal(new[] { "T" }, window.SelectedTransitionNames);
    }

    [Fact]
    public void CreateStateFromClip_AssignsMotionAndUniqueName()
    {
        var clip = new AnimationClip { Name = "Walk" };
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });
        var window = new AnimatorWindow();
        window.SetController(c);
        var created = window.CreateStateFromClip(clip, new Float2(120, 40));
        Assert.NotNull(created);
        Assert.Equal("Walk", created!.Name);
        Assert.Same(clip, created.Motion.Res);
        window.CreateBlendTreeInState("Idle");
        Assert.True(BlendTreeEditorModel.HasTree(c.States[0]));
        Assert.True(window.AutoLiveLink);
    }

    [Fact]
    public void CollectParameterRefs_ListsStateAndTransitionUses()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.Parameters.Add(new AnimatorController.Parameter { Name = "Speed", Type = AnimatorParameterType.Float });
        c.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            SpeedMultiplierParameter = "Speed",
            TransitionNames = { "IdleToWalk" }
        });
        c.Transitions.Add(new AnimatorController.Transition
        {
            Name = "IdleToWalk",
            DestinationStateName = "Idle",
            Conditions = { new AnimatorController.Condition { ParamName = "Speed", Threshold = 0.1f } }
        });
        var window = new AnimatorWindow();
        window.SetController(c);
        var refs = window.CollectParameterRefs("Speed");
        Assert.Contains(refs, r => r.Contains("Idle", StringComparison.Ordinal) && r.Contains("Speed", StringComparison.Ordinal));
        Assert.Contains(refs, r => r.Contains("IdleToWalk", StringComparison.Ordinal));
    }

    [Fact]
    public void TryFindStatePath_FindsNestedState()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });
        c.States.Add(new AnimatorController.State { Name = "Punch" });
        AnimatorControllerEditGraph.CreateStateMachine(c, 0, null, "Combat", 0, 0);
        Assert.True(AnimatorControllerEditGraph.MoveStateInto(c, 0, null, "Punch", "Combat"));
        var path = new List<string>();
        Assert.True(AnimatorControllerEditGraph.TryFindStatePath(c, 0, "Punch", path));
        Assert.Equal(new[] { "Combat" }, path);
        Assert.True(AnimatorControllerEditGraph.TryFindStatePath(c, 0, "Idle", path));
        Assert.Empty(path);
        var window = new AnimatorWindow();
        window.SetController(c);
        window.NavigateToStateMachinePath(new[] { "Combat" });
        Assert.Equal(new[] { "Combat" }, window.StateMachinePath);
    }
}
