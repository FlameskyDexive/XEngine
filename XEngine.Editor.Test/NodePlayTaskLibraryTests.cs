using System.Collections.Generic;

using XEngine.NodePlay;
using XEngine.NodePlay.Tasks;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>M18 batch A: world/flow/condition built-ins over a live GameObject agent.</summary>
public sealed class NodePlayTaskLibraryTests
{
    private static (NPRunner runner, NPBlackboardSource bb) AgentWithBB()
    {
        var go = new GameObject("LibProbe");
        var runner = go.AddComponent<NPRunner>();
        runner.OnEnableAction = NPRunAction.DoNothing;
        runner.OnDisableAction = NPRunAction.DoNothing;
        var bb = new NPBlackboardSource { Identifier = "Lib" };
        bb.Add("pos", "3,0,4");
        bb.Add("n", 5);
        bb.Add("flag", false);
        return (runner, bb);
    }

    private static void Tick(NPActionTask task, NPRunner runner, INPBlackboard bb)
    {
        Assert.Null(task.Bind(runner, bb, null!));
        task.ResetForReuse();
        for (int i = 0; i < 30 && task.Result == null; i++)
            task.Tick();
    }

    [Fact]
    public void SetName_SetActive_Roundtrip()
    {
        var (runner, bb) = AgentWithBB();

        var setName = new NPSetNameAction { Name = new NPParameter { InlineValue = "Hero-01" } };
        Tick(setName, runner, bb);
        Assert.Equal("Hero-01", runner.Name);

        var setActive = new NPSetActiveAction { Active = new NPParameter { InlineValue = false } };
        Tick(setActive, runner, bb);
        Assert.False(runner.Enabled);
    }

    [Fact]
    public void MoveTowards_LookAt_ArriveAndFacing()
    {
        var (runner, bb) = AgentWithBB();

        var move = new NPMoveTowardsAction
        {
            TargetPosition = new NPParameter { VariableName = "pos" },
            Speed = new NPParameter { InlineValue = 50f },
            ArriveRadius = 0.2f,
        };
        Tick(move, runner, bb);
        Assert.True(move.Result == true);

        var look = new NPLookAtAction { TargetPosition = new NPParameter { InlineValue = "0,0,10" } };
        Tick(look, runner, bb);
        Assert.True(look.Result == true);
        Assert.True(MathF.Abs(runner.Transform.Rotation.Y) > 0.01f
                    || MathF.Abs(runner.Transform.Rotation.W) > 0.01f);
    }

    [Fact]
    public void AddVariable_LongPathAndBoolBranch()
    {
        var (runner, bb) = AgentWithBB();

        var addInt = new NPAddVariableAction
        {
            Target = new NPParameter { VariableName = "n" },
            Delta = new NPParameter { InlineValue = 3 },
        };
        Tick(addInt, runner, bb);
        Assert.Equal(8, bb.GetValue("n", 0));

        var addBool = new NPAddVariableAction
        {
            Target = new NPParameter { VariableName = "flag" },
            Delta = new NPParameter { InlineValue = true },
        };
        Tick(addBool, runner, bb);
        Assert.Equal(1, bb.GetValue("flag", 0));
    }

    [Fact]
    public void Conditions_Distance_HasComponent_HasVar_Probability_WithinTime()
    {
        var (runner, bb) = AgentWithBB();

        var dist = new NPDistanceCondition
        {
            TargetPosition = new NPParameter { InlineValue = "0,0,0" },
            Radius = new NPParameter { InlineValue = 10f },
            Inside = true,
        };
        Assert.Null(dist.Bind(runner, bb, null!));
        Assert.True(dist.Check(), "distance 0 within 10");

        var far = new NPDistanceCondition
        {
            TargetPosition = new NPParameter { InlineValue = "50,0,0" },
            Radius = new NPParameter { InlineValue = 1f },
            Inside = false,
        };
        Assert.True(far.Bind(runner, bb, null!) == null, "far bind");
        Assert.True(far.Check(), "far check");

        var hasRunner = new NPHasComponentCondition { ComponentTypeName = "XEngine.NodePlay.NPRunner" };
        Assert.True(hasRunner.Bind(runner, bb, null!) == null && hasRunner.Check());
        var hasMissing = new NPHasComponentCondition { ComponentTypeName = "XEngine.NodePlay.NPGhost" };
        Assert.True(hasMissing.Bind(runner, bb, null!) == null, "bind missing");
        Assert.False(hasMissing.Check(), "missing component should fail");

        var hasVar = new NPHasVariableCondition { VariableName = "n" };
        Assert.True(hasVar.Bind(runner, bb, null!) == null && hasVar.Check());
        var hasMissingVar = new NPHasVariableCondition { VariableName = "zzz" };
        Assert.False(hasMissingVar.Check());

        var prob = new NPProbabilityCondition { Chance = new NPParameter { InlineValue = 1f } };
        Assert.True(prob.Check());

        var host = new NodePlayCoreTests.NPProbeGraph();
        var within = new NPWithinTimeCondition { Seconds = new NPParameter { InlineValue = 999f } };
        Assert.True(within.Bind(runner, bb, host) == null && within.Check(), "within 999s");
    }

    [Fact]
    public void DeleteVariable_FlowMarkers()
    {
        var (runner, bb) = AgentWithBB();

        var del = new NPDeleteVariableAction { VariableName = "n" };
        Tick(del, runner, bb);
        Assert.Null(bb.Get("n"));

        var fail = new NPFailAction();
        Tick(fail, runner, bb);
        Assert.False(fail.Result);

        var ok = new NPSucceedAction();
        Tick(ok, runner, bb);
        Assert.True(ok.Result);
    }
}

/// <summary>M18 batch B: transform/time/math/bb/gameobject/debug built-ins.</summary>
public sealed class NodePlayTaskLibraryBTests
{
    private static (NPRunner runner, NPBlackboardSource bb) AgentWithBB()
    {
        var go = new GameObject("LibBProbe");
        var runner = go.AddComponent<NPRunner>();
        runner.OnEnableAction = NPRunAction.DoNothing;
        runner.OnDisableAction = NPRunAction.DoNothing;
        var bb = new NPBlackboardSource { Identifier = "LibB" };
        bb.Add("a", 2f);
        bb.Add("b", 8f);
        bb.Add("name", "Crate");
        return (runner, bb);
    }

    private static void Tick(NPActionTask task, NPRunner runner, INPBlackboard bb)
    {
        Assert.Null(task.Bind(runner, bb, null!));
        task.ResetForReuse();
        task.Tick();
    }

    [Fact]
    public void TransformBatch_SetPos_Scale_GetPos()
    {
        var (runner, bb) = AgentWithBB();

        var setPos = new NPSetPositionAction { Position = new NPParameter { InlineValue = "1,2,3" } };
        Tick(setPos, runner, bb);
        Assert.Equal(new Float3(1, 2, 3), runner.Transform.Position);

        var scale = new NPSetScaleAction { Scale = new NPParameter { InlineValue = "2,2,2" } };
        Tick(scale, runner, bb);
        Assert.Equal(new Float3(2, 2, 2), runner.Transform.LocalScale);

        var translate = new NPTranslateAction { Offset = new NPParameter { InlineValue = "1,0,0" } };
        Tick(translate, runner, bb);
        Assert.Equal(new Float3(2, 2, 3), runner.Transform.Position);

        var getPos = new NPGetPositionAction { TargetVariable = "pp" };
        Tick(getPos, runner, bb);
        Assert.Contains("2,", bb.GetValue("pp", ""));
    }

    [Fact]
    public void MathBatch_Random_Lerp_Clamp()
    {
        var (runner, bb) = AgentWithBB();

        var ri = new NPRandomIntAction
        {
            Min = new NPParameter { InlineValue = 5 },
            Max = new NPParameter { InlineValue = 10 },
            TargetVariable = "ri",
        };
        Tick(ri, runner, bb);
        int riVal = bb.GetValue("ri", -1);
        // Vector.RNG.Range(int, int) includes both endpoints.
        Assert.InRange(riVal, 5, 10);

        var lerp = new NPLerpVariableAction
        {
            From = new NPParameter { VariableName = "a" },
            To = new NPParameter { VariableName = "b" },
            Alpha = new NPParameter { InlineValue = 0.5f },
            TargetVariable = "lerped",
        };
        Tick(lerp, runner, bb);
        Assert.Equal(5f, bb.GetValue("lerped", 0f)); // (2+8)/2

        bb.Add("hp", 250f);
        var clamp = new NPClampVariableAction
        {
            TargetVariable = "hp",
            Min = new NPParameter { InlineValue = 0f },
            Max = new NPParameter { InlineValue = 100f },
        };
        Tick(clamp, runner, bb);
        Assert.Equal(100f, bb.GetValue("hp", 0f));
    }

    [Fact]
    public void BlackboardBatch_Copy_Swap()
    {
        var (runner, bb) = AgentWithBB();

        var copy = new NPCopyVariableAction { FromVariable = "a", ToVariable = "copyOfA" };
        Tick(copy, runner, bb);
        Assert.Equal(2f, bb.GetValue("copyOfA", 0f));

        bb.Add("x", "first");
        bb.Add("y", "second");
        var swap = new NPSwapVariablesAction { VariableA = "x", VariableB = "y" };
        Tick(swap, runner, bb);
        Assert.Equal("second", bb.GetValue("x", ""));
        Assert.Equal("first", bb.GetValue("y", ""));
    }

    [Fact]
    public void GameObjectBatch_GetName_LogVariable_WarnIf()
    {
        var (runner, bb) = AgentWithBB();

        var getName = new NPGetNameAction { TargetVariable = "agentName" };
        Tick(getName, runner, bb);
        Assert.Equal(runner.Name, bb.GetValue("agentName", "<missing>"));

        var logVar = new NPLogVariableAction { Prefix = "check", VariableName = "a" };
        Tick(logVar, runner, bb);
        Assert.True(logVar.Result == true);

        var warnIf = new NPWarnIfAction
        {
            Condition = new NPBoolIsCondition
            {
                Value = new NPParameter { VariableName = "a" },
                Expected = true,
            },
            Message = "a should not be truthy",
        };
        Tick(warnIf, runner, bb);
        Assert.True(warnIf.Result == true);
    }

    [Fact]
    public void BoolIs_WasWithin()
    {
        var (runner, bb) = AgentWithBB();
        var host = new NodePlayCoreTests.NPProbeGraph();

        var boolIs = new NPBoolIsCondition { Value = new NPParameter { InlineValue = true }, Expected = false };
        Assert.True(boolIs.Bind(runner, bb, host) == null && !boolIs.Check());

        var within = new NPWasWithinSecondsCondition { Seconds = new NPParameter { InlineValue = 999f } };
        Assert.True(within.Bind(runner, bb, host) == null);
        Assert.True(within.Check());           // primed first check
        Assert.False(within.Check());          // throttled
    }
}
