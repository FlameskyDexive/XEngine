using System.Collections.Generic;

using Echo = XEngine.Echo;

using XEngine.NodePlay;
using XEngine.NodePlay.BT;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class BTProbeAction : NPActionTask
{
    public int Ticks = 1;
    public bool Succeed = true;
    public int Begins;
    public int TotalBegins;
    public bool WasAborted;

    private int _t;

    protected override void OnExecute()
    {
        Begins++;
        TotalBegins++;
        _t = 1;
        if (Ticks <= 1)
            EndAction(Succeed);
    }

    protected override void OnUpdate()
    {
        _t++;
        if (_t >= Ticks)
            EndAction(Succeed);
    }

    protected override void OnStop(bool interrupted) => WasAborted = interrupted;

    protected override void OnReset()
    {
        _t = 0;
        Begins = 0;
        WasAborted = false; // TotalBegins intentionally cumulative
    }
}

public sealed class BTProbeCondition : NPConditionTask
{
    public bool Answer = true;
    public int Checks;

    protected override bool OnCheck()
    {
        Checks++;
        return Answer;
    }
}

/// <summary>M8: behaviour-tree composites, decorators, leaves, and graph tick loop.</summary>
public sealed class NodePlayBTTests
{
    private static (NPBehaviourTreeAsset tree, T node) TreeWith<T>(T node, bool prime = true)
        where T : BTNode
    {
        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(node);
        tree.BuildTopology();
        return (tree, node);
    }

    private static NPStatus Tick(NPBehaviourTreeAsset tree, float delta = 0.016f)
    {
        tree.TickRunner(delta);
        return tree.LastRootStatus;
    }

    private static void Wire(NPBehaviourTreeAsset tree, NPNode parent, params NPNode[] children)
    {
        foreach (NPNode child in children)
        {
            tree.AddNode(child);
            Assert.NotNull(tree.Connect(parent, child, out _));
        }

        tree.BuildTopology();
    }

    [Fact]
    public void Sequence_DynamicReactiveScan_AbortsRunningTailOnHeadFailure()
    {
        var cond = new BTConditionNode { Condition = new BTProbeCondition { Answer = true } };
        var tail = new BTActionNode { Action = new BTProbeAction { Ticks = 999 } };
        var seq = new BTSequencer { Dynamic = true };

        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(seq);
        Wire(tree, seq, cond, tail);

        Assert.Equal(NPStatus.Running, Tick(tree));   // head passes, tail runs
        Assert.Null(tail.Action!.Result);

        ((BTProbeCondition)cond.Condition!).Answer = false;
        Assert.Equal(NPStatus.Failure, Tick(tree));   // reactive head fail → abort tail
        Assert.True(((BTProbeAction)tail.Action!).WasAborted);
    }

    [Fact]
    public void Selector_FirstSuccessWins_RestartsOnNextPass()
    {
        var fail = new BTActionNode { Action = new BTProbeAction { Ticks = 1, Succeed = false } };
        var win = new BTActionNode { Action = new BTProbeAction { Ticks = 1 } };
        var sel = new BTSelector();

        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(sel);
        Wire(tree, sel, fail, win);

        Assert.Equal(NPStatus.Success, Tick(tree));
        Assert.Equal(NPStatus.Success, Tick(tree));   // repeat loop re-enters from the top
    }

    [Fact]
    public void Parallel_PoliciesDecideOverallResult()
    {
        var okA = new BTActionNode { Action = new BTProbeAction { Ticks = 1 } };
        var okB = new BTActionNode { Action = new BTProbeAction { Ticks = 1 } };
        var boom = new BTActionNode { Action = new BTProbeAction { Ticks = 1, Succeed = false } };
        var par = new BTParallel { FailPolicy = NPParallelPolicy.One };

        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(par);
        Wire(tree, par, okA, okB, boom);

        Assert.Equal(NPStatus.Failure, Tick(tree));

        // All-fail policy with a single failure still runs (no failure yet met) → Success path
        var par2 = new BTParallel { SucceedPolicy = NPParallelPolicy.All, FailPolicy = NPParallelPolicy.All };
        var tree2 = new NPBehaviourTreeAsset();
        tree2.AddNode(par2);
        Wire(tree2, par2,
            new BTActionNode { Action = new BTProbeAction { Ticks = 1 } },
            new BTActionNode { Action = new BTProbeAction { Ticks = 1 } });
        Assert.Equal(NPStatus.Success, Tick(tree2));
    }

    [Fact]
    public void PrioritySelector_BestScoreWins_AndRunningBranchIsSticky()
    {
        var low = new BTActionNode { Action = new BTProbeAction { Ticks = 1 } };
        var sticky = new BTActionNode { Action = new BTProbeAction { Ticks = 999 } };
        var pri = new BTPrioritySelector();

        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(pri);
        Wire(tree, pri, low, sticky);
        pri.Weights.Add(new NPParameter { InlineValue = 1f });
        pri.Weights.Add(new NPParameter { InlineValue = 9f });

        Assert.Equal(NPStatus.Running, Tick(tree));   // 9-score branch captured, runs forever
        pri.Weights[1] = new NPParameter { InlineValue = 1f };  // now ties… sticky keeps running branch
        Assert.Equal(NPStatus.Running, Tick(tree));
        Assert.Equal(1, ((BTProbeAction)sticky.Action!).Begins); // still the same invocation
    }

    [Fact]
    public void Timeout_AbortsChildPastLimit_AndCooldownlessRepeat()
    {
        var forever = new BTActionNode { Action = new BTProbeAction { Ticks = 999 } };
        var timeout = new BTTimeout { Seconds = 0.05f };
        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(timeout);
        Wire(tree, timeout, forever);

        Assert.Equal(NPStatus.Running, Tick(tree, 0.02f));
        Assert.Equal(NPStatus.Running, Tick(tree, 0.02f));
        Assert.Equal(NPStatus.Failure, Tick(tree, 0.02f));  // 0.06 > 0.05
        Assert.True(((BTProbeAction)forever.Action!).WasAborted);
    }

    [Fact]
    public void Repeater_RunsChildNTimes_ThenSucceeds()
    {
        var claps = new BTActionNode { Action = new BTProbeAction { Ticks = 1 } };
        var rep = new BTRepeater { Times = 3 };
        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(rep);
        Wire(tree, rep, claps);

        Tick(tree);
        Assert.Equal(NPStatus.Running, tree.LastRootStatus);
        Tick(tree);
        Assert.Equal(NPStatus.Running, tree.LastRootStatus);
        Tick(tree); // third completion settles
        Assert.Equal(NPStatus.Success, tree.LastRootStatus);
        Assert.Equal(3, ((BTProbeAction)claps.Action!).TotalBegins);
    }

    [Fact]
    public void Inverter_Optional_Setter_LeafSemantics()
    {
        var inv = new BTInverter();
        var failing = new BTActionNode { Action = new BTProbeAction { Ticks = 1, Succeed = false } };
        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(inv);
        Wire(tree, inv, failing);
        Assert.Equal(NPStatus.Success, Tick(tree));

        var opt = new BTOptional();
        var tree2 = new NPBehaviourTreeAsset();
        tree2.AddNode(opt);
        Wire(tree2, opt, new BTActionNode { Action = new BTProbeAction { Ticks = 1, Succeed = false } });
        Assert.Equal(NPStatus.Optional, Tick(tree2));

        var bb = new NPBlackboardSource { Identifier = "T" };
        bb.Add("goal", "");
        var setter = new BTSetter
        {
            Target = new NPParameter { VariableName = "goal" },
            Source = new NPParameter { InlineValue = "door" },
        };
        var tree3 = new NPBehaviourTreeAsset();
        tree3.AddNode(setter);
        tree3.BuildTopology();
        tree3.GraphBlackboard = bb;
        tree3.AddNode(new BTActionNode()); // decoy child (setter without child succeeds)
        Assert.Equal(NPStatus.Success, Tick(tree3));
        Assert.Equal("door", bb.GetValue("goal", "")); // bound target written
    }

    [Fact]
    public void Iterator_FeedsItemsIntoBranchScope()
    {
        var bb = new NPBlackboardSource { Identifier = "T" };
        bb.Add("items", new List<string> { "a", "b" });

        var capture = new BTActionNode { Action = new BTItemCapture() };
        var iter = new BTIterator
        {
            List = new NPParameter { VariableName = "items" },
            ItemName = "item",
        };
        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(iter);
        Wire(tree, iter, capture);
        tree.GraphBlackboard = bb;

        Tick(tree);
        Tick(tree);
        Assert.Equal("b", ((BTItemCapture)capture.Action!).Seen);
    }

    [Fact]
    public void Guard_MutexBlocksSecondBranch_ReleasesOnAbort()
    {
        var holder = new BTActionNode { Action = new BTProbeAction { Ticks = 999 } };
        var guardA = new BTGuard { Token = "door" };
        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(guardA);
        Wire(tree, guardA, holder);

        // second guard with the same token, evaluated standalone against the same tree registry
        var rivalChild = new BTActionNode { Action = new BTProbeAction { Ticks = 1 } };
        var guardB = new BTGuard { Token = "door" };
        tree.AddNode(guardB);
        tree.AddNode(rivalChild);
        Assert.NotNull(tree.Connect(guardB, rivalChild, out _));
        tree.BuildTopology();

        Assert.Equal(NPStatus.Running, Tick(tree));          // guardA holds the token
        Assert.Equal(NPStatus.Failure, tree.ExecuteNode(guardB, tree.OwnerRunner!));

        tree.ResetBranch(guardA);                            // abort releases
        Assert.Equal(NPStatus.Success, tree.ExecuteNode(guardB, tree.OwnerRunner!));
    }

    [Fact]
    public void Interruptor_AbortsWhenConditionTurnsTrue()
    {
        var watchdog = new BTProbeCondition { Answer = false };
        var inner = new BTActionNode { Action = new BTProbeAction { Ticks = 999 } };
        var interruptor = new BTInterruptor { Condition = watchdog };

        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(interruptor);
        Wire(tree, interruptor, inner);

        Assert.Equal(NPStatus.Running, Tick(tree));
        watchdog.Answer = true;
        Assert.Equal(NPStatus.Failure, Tick(tree));
        Assert.True(((BTProbeAction)inner.Action!).WasAborted);
    }

    [Fact]
    public void SubTree_WriteInSeeds_ReadOutFlows_CompletesFromChildSettle()
    {
        // child: non-repeating tree whose root action succeeds instantly
        var child = new NPBehaviourTreeAsset { Repeat = false };
        var childAction = new BTActionNode { Action = new BTProbeWriteOut() };
        child.AddNode(childAction);
        child.LocalBlackboard.Add("target", "", isPublic: true);
        child.LocalBlackboard.Add("outDone", "", isPublic: true);
        child.BuildTopology();

        var sub = new BTSubTreeNode();
        sub.Nested.SubGraphOverride = child;
        sub.Nested.Mappings.Add(new NPMappingParameter
        {
            Mode = NPMappingMode.WriteIn,
            SubVariableName = "target",
            ParentParameter = new NPParameter { VariableName = "parentTarget" },
        });
        sub.Nested.Mappings.Add(new NPMappingParameter
        {
            Mode = NPMappingMode.ReadOut,
            SubVariableName = "outDone",
            ParentParameter = new NPParameter { VariableName = "observed" },
        });

        var bb = new NPBlackboardSource { Identifier = "P" };
        bb.Add("parentTarget", "crate");
        bb.Add("observed", "");

        var parent = new NPBehaviourTreeAsset { Repeat = false };
        parent.AddNode(sub);
        parent.BuildTopology();
        parent.GraphBlackboard = bb;

        Assert.Equal(NPStatus.Success, Tick(parent));
        Assert.Equal("done:crate", bb.GetValue("observed", "?"));
    }

    [Fact]
    public void DisabledNodes_SkipThrough_AndBreakpointsRaise()
    {
        var blocked = new BTActionNode { Disabled = true };
        var passthrough = new BTActionNode { Action = new BTProbeAction { Ticks = 1 } };
        var seq = new BTSequencer();
        var tree = new NPBehaviourTreeAsset();
        tree.AddNode(seq);
        Wire(tree, seq, blocked, passthrough);

        NPBehaviourTreeAsset.OnBreakpointHit += CaptureBreakpoint;
        try
        {
            passthrough.Breakpoint = true;
            Assert.Equal(NPStatus.Success, Tick(tree));
            Assert.NotNull(_captured);
            Assert.Same(passthrough, _captured);
        }
        finally
        {
            NPBehaviourTreeAsset.OnBreakpointHit -= CaptureBreakpoint;
            _captured = null;
        }
    }

    private static NPNode? _captured;

    private static void CaptureBreakpoint(NPBehaviourTreeAsset graph, NPNode node) => _captured = node;
}

/// <summary>Writes "done" into the subgraph's outDone variable, then succeeds.</summary>
public sealed class BTProbeWriteOut : NPActionTask
{
    protected override void OnExecute()
    {
        OwnerSystemHack();
        EndAction(true);
    }

    public static string? LastWritten;

    private void OwnerSystemHack()
    {
        if (OwnerSystem is NPBehaviourTreeAsset tree)
        {
            string target = tree.LocalBlackboard.GetValue("target", "");
            LastWritten = "done:" + target;
            tree.LocalBlackboard.SetValue("outDone", LastWritten);
        }
    }
}

/// <summary>Remembers the last "item" variable value seen while ticking.</summary>
public sealed class BTItemCapture : NPActionTask
{
    public string Seen = "";

    protected override void OnExecute()
    {
        Capture();
        EndAction(true);
    }

    private void Capture()
    {
        if (Blackboard is NPBlackboardSource bb)
            Seen = bb.GetValue("item", "");
    }
}
