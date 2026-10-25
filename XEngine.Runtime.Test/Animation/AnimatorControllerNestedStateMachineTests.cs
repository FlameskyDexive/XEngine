// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>M32: nested StateMachine authoring compiles to a flat runtime graph (Unity 5 Entry/Exit).</summary>
public class AnimatorControllerNestedStateMachineTests : RuntimeTestBase
{
    private static AnimatorController BuildNestedIdleCombat()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f, duration: 1f);
        var punch = AnimationTestHelpers.MakeConstantPosClip("punch", "Hips", posX: 10f, duration: 1f);

        var combat = new AnimatorController.StateMachine
        {
            Name = "Combat",
            PosX = 280,
            PosY = 80,
            DefaultStateName = "Punch",
        };
        combat.States.Add(new AnimatorController.State
        {
            Name = "Punch",
            Speed = 1f,
            IsLooping = false,
            Motion = new AssetRef<AnimationClip>(punch),
            TransitionNames = { "Punch_To_Exit" },
        });
        combat.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Punch_To_Exit",
            DestinationStateName = AnimatorController.ExitStateName,
            Duration = 0f,
            HasExitTime = true,
            ExitTime = 0.9f,
        });

        var root = new AnimatorController.StateMachine
        {
            Name = "Base Layer",
            DefaultStateName = "Idle",
        };
        root.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            Speed = 1f,
            IsLooping = true,
            Motion = new AssetRef<AnimationClip>(idle),
            TransitionNames = { "Idle_To_Combat" },
        });
        root.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Idle_To_Combat",
            DestinationStateName = "Combat",
            Duration = 0f,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Attack" } },
        });
        root.StateMachines.Add(combat);

        var author = new AnimatorController { Name = "Nested" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Attack", Type = AnimatorParameterType.Trigger });
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "Base Layer",
            Weight = 1f,
            DefaultStateName = "Idle",
            Root = root,
        });
        return author;
    }

    [Fact]
    public void Compile_FlattensNestedStates()
    {
        var author = BuildNestedIdleCombat();
        var c = AnimatorControllerCompiler.Compile(author);

        Assert.Equal(2, c.States.Length);
        Assert.Equal(AnimationNameHash.Hash("Idle"), c.States[0].NameHash);
        Assert.Equal(AnimationNameHash.Hash("Punch"), c.States[1].NameHash);
        Assert.Equal(0, c.DefaultStateIndex);
        Assert.Equal(2, c.Layers[0].StatesCount);
    }

    [Fact]
    public void Compile_TransitionIntoChildMachine_LandsOnDefault()
    {
        var author = BuildNestedIdleCombat();
        var c = AnimatorControllerCompiler.Compile(author);

        Assert.Equal(1, c.States[0].TransitionsCount);
        Assert.Equal(1, c.Transitions[c.States[0].TransitionsStart].DestinationStateIndex);
    }

    [Fact]
    public void Compile_ExitFromChild_ReturnsToParentEntry()
    {
        var author = BuildNestedIdleCombat();
        var c = AnimatorControllerCompiler.Compile(author);

        Assert.Equal(1, c.States[1].TransitionsCount);
        Assert.Equal(0, c.Transitions[c.States[1].TransitionsStart].DestinationStateIndex);
    }

    [Fact]
    public void Compile_UnconditionalEntry_OverridesDefault()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f);
        var walk = AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", 1f);
        var author = new AnimatorController { Name = "e" };
        var root = new AnimatorController.StateMachine { Name = "Base Layer", DefaultStateName = "Idle" };
        root.States.Add(new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(idle) });
        root.States.Add(new AnimatorController.State { Name = "Walk", Motion = new AssetRef<AnimationClip>(walk) });
        root.EntryTransitions.Add(new AnimatorController.Transition
        {
            Name = "Entry_Walk",
            DestinationStateName = "Walk",
            Duration = 0f,
        });
        author.Layers.Add(new AnimatorController.Layer { Name = "Base Layer", Root = root, DefaultStateName = "Idle" });

        var c = AnimatorControllerCompiler.Compile(author);
        Assert.Equal(1, c.DefaultStateIndex);
        Assert.Equal(AnimationNameHash.Hash("Walk"), c.States[c.DefaultStateIndex].NameHash);
    }

    [Fact]
    public void Compile_StateMachineOutgoing_RewritesChildExit()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f);
        var punch = AnimationTestHelpers.MakeConstantPosClip("punch", "Hips", 10f);
        var recover = AnimationTestHelpers.MakeConstantPosClip("recover", "Hips", 5f);

        var combat = new AnimatorController.StateMachine { Name = "Combat", DefaultStateName = "Punch" };
        combat.States.Add(new AnimatorController.State
        {
            Name = "Punch",
            Motion = new AssetRef<AnimationClip>(punch),
            TransitionNames = { "Punch_Exit" },
        });
        combat.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Punch_Exit",
            DestinationStateName = AnimatorController.ExitStateName,
            Duration = 0f,
        });

        var root = new AnimatorController.StateMachine { Name = "Base Layer", DefaultStateName = "Idle" };
        root.States.Add(new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(idle) });
        root.States.Add(new AnimatorController.State { Name = "Recover", Motion = new AssetRef<AnimationClip>(recover) });
        root.StateMachines.Add(combat);
        root.StateMachineTransitions.Add(new AnimatorController.Transition
        {
            Name = "Combat_To_Recover",
            SourceStateMachineName = "Combat",
            DestinationStateName = "Recover",
            Duration = 0f,
        });
        var author = new AnimatorController { Name = "out" };
        author.Layers.Add(new AnimatorController.Layer { Name = "Base Layer", Root = root });

        var c = AnimatorControllerCompiler.Compile(author);
        int punchIdx = -1;
        int recoverIdx = -1;
        for (int i = 0; i < c.States.Length; i++)
        {
            if (c.States[i].NameHash == AnimationNameHash.Hash("Punch")) punchIdx = i;
            if (c.States[i].NameHash == AnimationNameHash.Hash("Recover")) recoverIdx = i;
        }
        Assert.True(punchIdx >= 0 && recoverIdx >= 0);
        Assert.Equal(recoverIdx, c.Transitions[c.States[punchIdx].TransitionsStart].DestinationStateIndex);
    }

    [Fact]
    public void Echo_OldFlatFormat_RoundTripsThenCompilesLikeNested()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f);
        var attack = AnimationTestHelpers.MakeConstantPosClip("attack", "Hips", 10f);
        var flat = new AnimatorController { Name = "Flat", DefaultStateName = "Idle" };
        flat.States.Add(new AnimatorController.State
        {
            Name = "Idle", Motion = new AssetRef<AnimationClip>(idle), TransitionNames = { "Idle_To_Attack" }
        });
        flat.States.Add(new AnimatorController.State { Name = "Attack", Motion = new AssetRef<AnimationClip>(attack) });
        flat.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Idle_To_Attack", DestinationStateName = "Attack", Duration = 0f
        });

        EchoObject echo = Serializer.Serialize(flat, TypeMode.Aggressive);
        string text = echo.WriteToString();
        Assert.DoesNotContain("StateMachines", text);

        var loaded = Serializer.Deserialize<AnimatorController>(EchoObject.ReadFromString(text));
        Assert.NotNull(loaded);
        Assert.Null(loaded!.Layers.Count > 0 ? loaded.Layers[0].Root : null);
        var compiled = AnimatorControllerCompiler.Compile(loaded);
        Assert.Equal(2, compiled.States.Length);
        Assert.Equal(0, compiled.DefaultStateIndex);

        var nested = Serializer.Deserialize<AnimatorController>(echo);
        Assert.NotNull(nested);
        nested!.Layers.Add(new AnimatorController.Layer
        {
            Name = "Base Layer",
            DefaultStateName = nested.DefaultStateName,
            Root = new AnimatorController.StateMachine
            {
                Name = "Base Layer",
                DefaultStateName = nested.DefaultStateName,
                States = nested.States,
                Transitions = nested.Transitions,
                AnyStateTransitions = nested.AnyStateTransitions,
            },
        });
        nested.States = new();
        nested.Transitions = new();
        nested.AnyStateTransitions = new();
        nested.DefaultStateName = string.Empty;

        EchoObject again = Serializer.Serialize(nested, TypeMode.Aggressive);
        var reloaded = Serializer.Deserialize<AnimatorController>(again);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.Layers[0].Root);
        var c2 = AnimatorControllerCompiler.Compile(reloaded);
        Assert.Equal(2, c2.States.Length);
        Assert.Equal(compiled.States[0].NameHash, c2.States[0].NameHash);
    }

    [Fact]
    public void GetClipReferences_WalksNestedMachines()
    {
        var author = BuildNestedIdleCombat();
        var refs = author.GetClipReferences();
        Assert.Equal(2, refs.Count);
    }
}
