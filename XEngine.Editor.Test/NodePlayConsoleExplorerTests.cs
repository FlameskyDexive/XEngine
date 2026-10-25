using System.Collections.Generic;

using XEngine.NodePlay;
using XEngine.NodePlay.BT;
using XEngine.NodePlay.Editor;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class NodePlayConsoleExplorerTests
{
    [Fact]
    public void Console_RingCapturesSeveritiesAndClears()
    {
        NPGraphConsole.Clear();
        var graph = new NPBehaviourTreeAsset { GraphName = "C" };
        NPGraphConsole.Log(graph, "info", "hello");
        NPGraphConsole.Warn(graph, "broken", nodeId: "node1");

        var all = NPGraphConsole.All;
        Assert.Equal(2, all.Count);
        Assert.Equal("warn", all[^1].Severity);
        Assert.Equal("node1", all[^1].NodeId);
        Assert.Equal("C", all[^1].GraphName);

        NPGraphConsole.Clear();
        Assert.Empty(NPGraphConsole.All);
    }

    [Fact]
    public void Explorer_FilterMatchesNameCommentAndType()
    {
        var tree = new NPBehaviourTreeAsset();
        var a = tree.AddNode(new BTActionNode { Name = "Fetch Crate" });
        var b = tree.AddNode(new BTConditionNode { Comment = "gate keeper" });
        var c = tree.AddNode(new NPRelayNode());

        Assert.Equal(3, NodePlayExplorer.Filter(tree.Nodes, "").Count);
        Assert.Single(NodePlayExplorer.Filter(tree.Nodes, "crate"));
        Assert.Single(NodePlayExplorer.Filter(tree.Nodes, "keeper"));
        Assert.Single(NodePlayExplorer.Filter(tree.Nodes, "relay"));
        Assert.Empty(NodePlayExplorer.Filter(tree.Nodes, "zzz"));
    }

    [Fact]
    public void Refactor_RenameVariable_CascadesThroughParametersAndMappings()
    {
        var graph = new NPBehaviourTreeAsset { GraphName = "R" };
        graph.LocalBlackboard.Add("hp", 100f, isPublic: true);

        var setter = new BTSetter
        {
            Target = new NPParameter { VariableName = "hp" },
            Source = new NPParameter { InlineValue = 1f },
        };
        var iter = new BTIterator
        {
            List = new NPParameter { VariableName = "hp" },
            ItemName = "item",
        };
        var nested = new BTSubTreeNode();
        nested.Nested.Mappings.Add(new NPMappingParameter
        {
            Mode = NPMappingMode.ReadOut,
            SubVariableName = "out",
            ParentParameter = new NPParameter { VariableName = "hp" },
        });

        graph.AddNode(setter);
        graph.AddNode(iter);
        graph.AddNode(nested);

        int changed = NodePlayRefactor.RenameVariable(graph, "hp", "health");

        Assert.Equal("health", graph.LocalBlackboard.Get("health")!.Name);
        Assert.Null(graph.LocalBlackboard.Get("hp"));
        Assert.Equal("health", setter.Target.VariableName);
        Assert.Equal("health", iter.List.VariableName);
        Assert.Equal("health", nested.Nested.Mappings[0].ParentParameter.VariableName);
        Assert.True(changed >= 4); // variable + setter + iterator + mapping
    }
}
