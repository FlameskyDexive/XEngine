using System.Collections.Generic;

using Echo = XEngine.Echo;

using XEngine.Editor;
using XEngine.NodePlay;
using XEngine.NodePlay.Editor;
using XEngine.Runtime;
using static XEngine.Editor.Test.NodePlayCoreTests;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>M7: Echo-text clipboard round-trip with id regeneration, and JSON sidecar fidelity.</summary>
public sealed class NodePlayClipboardTests
{
    private static NPProbeGraph BuildSource()
    {
        var graph = new NodePlayCoreTests.NPProbeGraph();
        var a = graph.AddNode(new NPProbeMoveNode { Name = "A", Position = new Float2(0, 0), Speed = 2f });
        var b = graph.AddNode(new NPProbeGateNode { Name = "B", Position = new Float2(200, 80) });
        graph.Connect(a, b, out _);
        graph.Groups.Add(new NPGroup { Title = "Clip", X = -20, Y = -20, Width = 300, Height = 200 });
        graph.Stickies.Add(new NPSticky { Text = "hello", X = 10, Y = 240 });
        return graph;
    }

    [Fact]
    public void CopyPaste_RegeneratesIds_KeepsInternalWireAndOffset()
    {
        NPProbeGraph source = BuildSource();
        string payload = NodePlayClipboard.CopyToText(source,
            new List<NPNode> { source.Nodes[0], source.Nodes[1] },
            new List<NPGroup> { source.Groups[0] },
            new List<NPSticky> { source.Stickies[0] });
        Assert.StartsWith(NodePlayClipboard.Header, payload);

        var target = new NodePlayCoreTests.NPProbeGraph();
        var pastedGroups = new List<NPGroup>();
        var pastedStickies = new List<NPSticky>();
        IReadOnlyList<NPNode> pasted = NodePlayClipboard.PasteInto(target, payload, pastedGroups, pastedStickies);

        Assert.Equal(2, pasted.Count);
        Assert.Equal(1, target.Connections.Count);          // internal wire survived
        Assert.NotEqual(source.Nodes[0].Id, pasted[0].Id);  // ids regenerated
        Assert.NotEqual(source.Nodes[1].Id, pasted[1].Id);
        Assert.Equal(target.Nodes[0].Id, target.Connections[0].FromNodeId);

        // bounding box normalised to the paste offset
        Assert.Equal(NodePlayClipboard.PasteOffset.X, pasted[0].Position.X, 2);
        Assert.Equal(NodePlayClipboard.PasteOffset.Y, pasted[0].Position.Y, 2);

        Assert.Single(pastedGroups);
        Assert.Equal("Clip", pastedGroups[0].Title);
        Assert.NotEqual(source.Groups[0].Id, pastedGroups[0].Id);
        Assert.Single(pastedStickies);
        Assert.Equal("hello", pastedStickies[0].Text);

        // pasted graph is fully wired and consistent
        Assert.Single(target.Nodes[0].OutConnections);
        Assert.Single(target.Nodes[1].InConnections);
    }

    [Fact]
    public void TextToFragment_RejectsForeignPayload()
    {
        Assert.Null(NodePlayClipboard.TextToFragment("", typeof(NodePlayCoreTests.NPProbeGraph)));
        Assert.Null(NodePlayClipboard.TextToFragment("random text", typeof(NodePlayCoreTests.NPProbeGraph)));
    }

    [Fact]
    public void ExportImport_SidecarRoundtripPreservesStructure()
    {
        NPProbeGraph graph = BuildSource();
        string text = Echo.Serializer.Serialize(graph).WriteToString();

        var fresh = new NodePlayCoreTests.NPProbeGraph();
        Echo.Serializer.DeserializeInto(Echo.EchoObject.ReadFromString(text), fresh);
        fresh.BuildTopology();

        Assert.Equal(graph.Nodes.Count, fresh.Nodes.Count);
        Assert.Equal(graph.Connections.Count, fresh.Connections.Count);
        Assert.Equal(graph.Groups[0].Title, fresh.Groups[0].Title);
        Assert.Equal(graph.Stickies[0].Text, fresh.Stickies[0].Text);
        Assert.Equal(graph.Nodes[0].Id, fresh.Nodes[0].Id);
        Assert.Same(fresh, fresh.Nodes[0].Graph);
        Assert.Same(fresh.Connections[0].Target, fresh.Nodes[1]);
    }

    [Fact]
    public void SystemClipboard_ReceivesPayloadWhenEnabled()
    {
        NPProbeGraph graph = BuildSource();
        NodePlayClipboard.Copy(graph,
            new List<NPNode> { graph.Nodes[0] },
            new List<NPGroup>(), new List<NPSticky>());
        Assert.StartsWith(NodePlayClipboard.Header, NodePlayClipboard.LastText);
        // Under NullInputHandler (headless tests) the OS clipboard is a no-op; when a real
        // handler is active the payload must be on it verbatim.
        if (Input.Clipboard.Length > 0)
            Assert.StartsWith(NodePlayClipboard.Header, Input.Clipboard);
    }
}
