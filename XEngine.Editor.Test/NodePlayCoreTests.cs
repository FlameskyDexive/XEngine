using System;
using System.Collections.Generic;

using Echo = XEngine.Echo;

using XEngine.NodePlay;
using XEngine.NodePlay.BT;
using XEngine.NodePlay.Dialogue;
using XEngine.NodePlay.FSM;
using XEngine.NodePlay.Tasks;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

// Test doubles: derived nodes living outside the package assembly prove that Echo's `$type`
// envelopes round-trip polymorphic node lists across assembly boundaries, which is exactly how
// BT/FSM/dialogue node payloads survive a save/load cycle.
public sealed class NPProbeMoveNode : NPNode
{
    public float Speed = 1.5f;
    public string TargetName = "crate";
}

public sealed class NPProbeGateNode : NPNode
{
    public bool Open = true;
    public override int MaxInConnections => 1;
}

/// <summary>M1: flat graph data model + Echo text persistence + topology rebuild.</summary>
public sealed class NodePlayCoreTests
{
    private static NPGraphAsset BuildSampleGraph()
    {
        var graph = new NPProbeGraph();
        var move = graph.AddNode(new NPProbeMoveNode { Name = "Chase", Position = new Float2(120, 40), Speed = 3.5f });
        var gate = graph.AddNode(new NPProbeGateNode { Name = "Gate", Position = new Float2(260, 120), Open = false });
        Assert.NotNull(graph.Connect(move, gate, out string? error));
        Assert.Null(error);
        graph.Groups.Add(new NPGroup { Title = "Combat", X = 100, Y = 20 });
        graph.Stickies.Add(new NPSticky { Text = "tune speeds", X = 40, Y = 300 });
        return graph;
    }

    [Fact]
    public void PolymorphicTextRoundtrip_PreservesTypesPayloadAndTopology()
    {
        NPGraphAsset graph = BuildSampleGraph();

        var echo = Echo.Serializer.Serialize(graph);
        string text = echo.WriteToString();

        // Text format stays AI-diffable: the root $type names the module asset and every
        // polymorphic node entry carries its own envelope.
        Assert.Contains("NPProbeGraph", text);
        Assert.Contains("NPProbeMoveNode", text);
        Assert.Contains("NPProbeGateNode", text);
        Assert.Contains("\"Speed\": 3.5F", text);
        Assert.Contains("\"TargetName\": \"crate\"", text);

        // Deterministic serialization → stable diffs for AI patching.
        string second = Echo.Serializer.Serialize(graph).WriteToString();
        Assert.Equal(text, second);

        NPProbeGraph? loaded = Echo.Serializer.Deserialize(Echo.EchoObject.ReadFromString(text), typeof(NPProbeGraph)) as NPProbeGraph;
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Nodes.Count);
        Assert.IsType<NPProbeMoveNode>(loaded.Nodes[0]);
        Assert.IsType<NPProbeGateNode>(loaded.Nodes[1]);

        loaded.BuildTopology();
        var move = Assert.IsType<NPProbeMoveNode>(loaded.Nodes[0]);
        Assert.Equal(3.5f, move.Speed);
        Assert.Equal("crate", move.TargetName);
        Assert.Same(loaded, move.Graph);

        var gate = Assert.IsType<NPProbeGateNode>(loaded.Nodes[1]);
        Assert.False(gate.Open);

        NPConnection wire = Assert.Single(loaded.Connections);
        Assert.Same(wire, Assert.Single(move.OutConnections));
        Assert.Same(wire, Assert.Single(gate.InConnections));
        Assert.Same(gate, wire.Target);

        // First added node became prime and survives the roundtrip.
        Assert.Equal(move.Id, loaded.PrimeNodeId);
        Assert.Same(move, loaded.PrimeNode);
    }

    [Fact]
    public void CloneGraph_IsDeepCopyWithIndependentTopology()
    {
        NPGraphAsset source = BuildSampleGraph();
        NPProbeGraph clone = source.CloneGraph<NPProbeGraph>();

        Assert.Equal(source.Nodes.Count, clone.Nodes.Count);
        Assert.NotSame(source.Nodes[0], clone.Nodes[0]);
        Assert.Equal(source.Nodes[0].Id, clone.Nodes[0].Id); // instance clones keep ids for mapping

        NPConnection originalWire = Assert.Single(source.Connections);
        NPConnection clonedWire = Assert.Single(clone.Connections);
        Assert.Equal(originalWire.FromNodeId, clonedWire.FromNodeId);

        clone.BuildTopology();
        Assert.Same(clone.Nodes[1], clonedWire.Target);
        Assert.Same(clone, clone.Nodes[0].Graph);

        // Mutating the clone must not leak into the source.
        clone.RemoveNode(clone.Nodes[1]);
        Assert.Empty(clone.Connections);
        Assert.Single(source.Connections);
        Assert.Equal(2, source.Nodes.Count);
    }

    [Fact]
    public void Connect_RespectsPortLimitsAndModuleValidation()
    {
        var graph = new NPProbeGraph();
        var gateA = graph.AddNode(new NPProbeGateNode());
        var other = graph.AddNode(new NPProbeMoveNode());
        var extra = graph.AddNode(new NPProbeMoveNode());

        Assert.NotNull(graph.Connect(other, gateA, out _));
        Assert.Null(graph.Connect(extra, gateA, out string? error)); // MaxIn=1 on the gate
        Assert.False(string.IsNullOrEmpty(error));

        Assert.Null(graph.Connect(gateA, gateA, out string? selfError));
        Assert.Contains("itself", selfError);
    }

    [Fact]
    public void RemoveNode_DropsWiresAndRepairsPrime()
    {
        NPGraphAsset graph = BuildSampleGraph();
        var survivor = graph.AddNode(new NPProbeMoveNode());
        NPNode prime = graph.PrimeNode!;
        NPNode child = graph.Nodes[1];

        graph.RemoveNode(child);
        Assert.Empty(graph.Connections);
        Assert.Empty(prime.OutConnections);

        graph.RemoveNode(prime);
        Assert.Empty(graph.PrimeNodeId);
        Assert.Single(graph.Nodes);
        Assert.Same(survivor, graph.PrimeNode); // fallback to first remaining
    }

    [Fact]
    public void OnValidate_PrunesDanglingWiresAndRepairsIds()
    {
        var graph = new NPProbeGraph();
        var a = graph.AddNode(new NPProbeMoveNode());
        var b = graph.AddNode(new NPProbeGateNode());
        graph.Connect(a, b, out _);

        // A wire referencing a vanished endpoint is pruned; the live wire is untouched.
        ConnectionsList(graph).Add(new NPConnection { FromNodeId = "ghost", ToNodeId = a.Id });
        graph.OnValidate();
        NPConnection live = Assert.Single(graph.Connections);
        Assert.Equal(a.Id, live.FromNodeId);
        Assert.Equal(b.Id, live.ToNodeId);

        // A node persisted with an empty id gets one minted (kept unique among its siblings).
        var orphan = new NPProbeMoveNode { Id = "" };
        graph.Nodes.Add(orphan);
        graph.OnValidate();
        Assert.False(string.IsNullOrEmpty(orphan.Id));
    }

    private static List<NPConnection> ConnectionsList(NPGraphAsset g) => g.Connections;

    /// <summary>Concrete module-flavoured graph for tests (BT variants get their own asset type).</summary>
    public sealed class NPProbeGraph : NPGraphAsset
    {
        public NPProbeGraph() => Module = "bt";
    }

    /// <summary>
    /// Regenerates the live acceptance project's demo asset so the running editor always imports
    /// a file written by the engine's own serializer (canonical Echo text, real $type envelopes).
    /// </summary>
    [Fact]
    public void Writes_AcceptanceProject_DemoGraph()
    {
        string repo = FindAcceptanceProjectRoot();
        if (repo == null)
            return; // acceptance project only exists on dev machines

        var graph = new NPBehaviourTreeAsset
        {
            GraphName = "Demo/M1 Data Model",
        };
        var relay = graph.AddNode(new NPRelayNode
        {
            Name = "Entry Relay",
            Position = new Float2(80f, 60f),
            Comment = "M1 acceptance demo node",
        });
        graph.LocalBlackboard.Add("speed", 2.5f, isPublic: true);
        graph.Groups.Add(new NPGroup { Title = "Intro", X = 40f, Y = 20f });
        graph.Stickies.Add(new NPSticky { Text = "Flat Echo text: AI-patchable.", X = 40f, Y = 240f });

        string dir = Path.Combine(repo, "Assets", "NodePlay");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "DemoBT.nodeplay"),
            Echo.Serializer.Serialize(graph).WriteToString());

        WriteSoldierChaseGraph(dir);
        WriteDoorFsmGraph(dir);
        WriteJohnDaeDialogue(dir);
    }

    /// <summary>M16/M17: John & Dae multi-actor dialogue — greet → reply → choice → finish.</summary>
    private static void WriteJohnDaeDialogue(string dir)
    {
        var dlg = new NPDialogueAsset { GraphName = "Demo/John & Dae" };
        dlg.ActorParameters.Add(new NPActorParameter { Key = "john", Name = "John" });
        dlg.ActorParameters.Add(new NPActorParameter { Key = "dae", Name = "Dae" });

        var greet = dlg.AddNode(new DTStatementNode
        {
            Name = "Greet",
            ActorKey = "john",
            Statement = new NPStatement("Hey [player], seen my [item]?"),
            Position = new Float2(60, 40),
        });
        var reply = dlg.AddNode(new DTStatementNode
        {
            Name = "Reply",
            ActorKey = "dae",
            Statement = new NPStatement("What's in it for me?"),
            Position = new Float2(60, 160),
        });
        var choice = dlg.AddNode(new DTMultipleChoiceNode
        {
            Name = "Ask",
            ActorKey = "john",
            Position = new Float2(60, 280),
        });
        choice.Options.Add(new DTChoiceEntry { Text = "Nothing." });
        choice.Options.Add(new DTChoiceEntry { Text = "Gold.", SaySelection = true });

        var rude = dlg.AddNode(new DTStatementNode
        {
            Name = "Rude",
            ActorKey = "dae",
            Statement = new NPStatement("Hmph."),
            Position = new Float2(300, 400),
        });
        var nice = dlg.AddNode(new DTStatementNode
        {
            Name = "Nice",
            ActorKey = "dae",
            Statement = new NPStatement("Deal."),
            Position = new Float2(560, 400),
        });
        var bye = dlg.AddNode(new DTFinishNode { Name = "Bye", Position = new Float2(430, 540) });

        dlg.LocalBlackboard.Add("player", "Hero", isPublic: true);
        dlg.LocalBlackboard.Add("item", "wrench", isPublic: true);

        dlg.Connect(greet, reply, out _);
        dlg.Connect(reply, choice, out _);
        dlg.Connect(choice, rude, out _);
        dlg.Connect(choice, nice, out _);
        dlg.Connect(rude, bye, out _);
        dlg.Connect(nice, bye, out _);

        File.WriteAllText(
            Path.Combine(dir, "JohnDae.nodeplay"),
            Echo.Serializer.Serialize(dlg).WriteToString());
    }

    /// <summary>M15: door FSM — Idle ⇄ Open with a conditional transition and an OnFinish close.</summary>
    private static void WriteDoorFsmGraph(string dir)
    {
        var fsm = new NPFSMAsset { GraphName = "Demo/Door FSM", StartStateId = "" };

        var idle = new FSMState { Name = "Idle", Position = new Float2(380, 140) };
        var open = new FSMState { Name = "Open", Position = new Float2(800, 140) };
        open.OnUpdateActions.Add(new NPWaitAction { Seconds = new NPParameter { InlineValue = 1.5f } });
        fsm.AddNode(idle);
        fsm.AddNode(open);
        fsm.LocalBlackboard.Add("playerNear", false, isPublic: true);
        fsm.BuildTopology();
        fsm.StartStateId = idle.Id;

        var toOpen = (FSMConnection)fsm.Connect(idle, open, out _)!;
        toOpen.Condition = new NPCompareCondition
        {
            Value = new NPParameter { VariableName = "playerNear" },
            Operator = "==",
            Threshold = new NPParameter { InlineValue = "true" },
        };
        var toClose = (FSMConnection)fsm.Connect(open, idle, out _)!; // OnFinish edge

        File.WriteAllText(
            Path.Combine(dir, "DoorFsm.nodeplay"),
            Echo.Serializer.Serialize(fsm).WriteToString());
    }

    /// <summary>
    /// M9 vertical slice: reactive soldier BT — Selector(dynamic) over a gated Chase branch and
    /// a looping Patrol iterator; flipping bb "mode" to "chase" aborts patrol mid-travel.
    /// </summary>
    private static void WriteSoldierChaseGraph(string dir)
    {
        var soldier = new NPBehaviourTreeAsset { Repeat = true, GraphName = "Demo/Soldier Chase" };

        var root = soldier.AddNode(new BTSelector { Dynamic = true, Name = "Root", Position = new Float2(360, 0) });

        var chase = new BTSequencer { Name = "Chase", Dynamic = true, Position = new Float2(100, 150) };
        var seesTarget = new BTConditionNode
        {
            Name = "mode==chase",
            Condition = new NPCompareCondition
            {
                Value = new NPParameter { VariableName = "mode" },
                Operator = "==",
                Threshold = new NPParameter { InlineValue = "chase" },
            },
            Position = new Float2(20, 300),
        };
        var chaseLog = new BTActionNode { Name = "Log Chase", Action = new NPLogAction { Message = "Chasing crate" }, Position = new Float2(105, 450) };
        var move = new BTActionNode
        {
            Name = "Move To Target",
            Action = new NPMoveTowardsAction
            {
                TargetPosition = new NPParameter { VariableName = "targetPos" },
                Speed = new NPParameter { VariableName = "speed" },
            },
            Position = new Float2(200, 600),
        };
        soldier.AddNode(chase);
        soldier.AddNode(seesTarget);
        soldier.AddNode(chaseLog);
        soldier.AddNode(move);
        AssertConnect(soldier, root, chase);
        AssertConnect(soldier, chase, seesTarget);
        AssertConnect(soldier, chase, chaseLog);
        AssertConnect(soldier, chase, move);

        var patrol = new BTIterator
        {
            Name = "Patrol",
            List = new NPParameter { VariableName = "waypoints" },
            ItemName = "targetPos",
            Loop = true,
            Position = new Float2(640, 150),
        };
        var patrolMove = new BTActionNode
        {
            Name = "Walk",
            Action = new NPMoveTowardsAction
            {
                TargetPosition = new NPParameter { VariableName = "targetPos" },
                Speed = new NPParameter { VariableName = "speed" },
            },
            Position = new Float2(640, 320),
        };
        soldier.AddNode(patrol);
        soldier.AddNode(patrolMove);
        AssertConnect(soldier, root, patrol);
        AssertConnect(soldier, patrol, patrolMove);

        soldier.LocalBlackboard.Add("mode", "patrol", isPublic: true);
        soldier.LocalBlackboard.Add("targetPos", "0,0,0", isPublic: true);
        soldier.LocalBlackboard.Add("speed", 2.5f, isPublic: true);
        soldier.LocalBlackboard.Add("waypoints", new List<string> { "0,0,0", "6,0,0" }, isPublic: true);

        File.WriteAllText(
            Path.Combine(dir, "SoldierChase.nodeplay"),
            Echo.Serializer.Serialize(soldier).WriteToString());
    }

    private static void AssertConnect(NPGraphAsset graph, NPNode from, NPNode to)
    {
        if (graph.Connect(from, to, out string? error) == null)
            throw new InvalidOperationException($"demo wire rejected: {error}");
    }

    private static string? FindAcceptanceProjectRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return Path.Combine(current.FullName, "NodePlayTestProject");
            current = current.Parent;
        }

        return null;
    }
}
