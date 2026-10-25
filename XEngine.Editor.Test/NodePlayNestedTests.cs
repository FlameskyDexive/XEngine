using System.Collections.Generic;

using XEngine.NodePlay;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class NPProbeSubGraph : NPGraphAsset
{
    public int StartedCount;
    public int TickCount;
    public readonly List<string> HandledEvents = new();

    public NPProbeSubGraph()
    {
        Module = "bt";
        GraphName = "Probe Sub";
        AddNode(new NPRelayNode { Name = "SubRoot" });
        LocalBlackboard.Add("inA", 0f, isPublic: true);
        LocalBlackboard.Add("outB", 100f, isPublic: true);
    }

    protected internal override void OnStarted(NPRunner runner)
    {
        base.OnStarted(runner);
        StartedCount++;
    }

    protected internal override void TickRunner(float delta)
    {
        base.TickRunner(delta);
        TickCount++;
        LocalBlackboard.SetValue("outB", LocalBlackboard.GetValue("outB", 0f) + 1f);
    }

    protected internal override void HandleEvent(string name, object? value)
    {
        base.HandleEvent(name, value);
        HandledEvents.Add(name);
    }
}

/// <summary>M5: nested-subgraph instances with Write-In / Read-Out mapping and per-owner isolation.</summary>
public sealed class NodePlayNestedTests
{
    private static (NPProbeHost host, NPBlackboardSource parentBb) Build()
    {
        var parentGraph = new ProbeParentGraph();
        var parentBb = new NPBlackboardSource { Identifier = "Parent" };
        parentBb.Add("request", 10f);
        parentBb.Add("observed", -1f);
        parentGraph.GraphBlackboard = parentBb;

        var host = new NPProbeHost { ParentRef = parentGraph };
        host.SubGraphOverride = new NPProbeSubGraph();
        host.Mappings.Add(new NPMappingParameter
        {
            Mode = NPMappingMode.WriteIn,
            SubVariableName = "inA",
            ParentParameter = new NPParameter { VariableName = "request" },
        });
        host.Mappings.Add(new NPMappingParameter
        {
            Mode = NPMappingMode.ReadOut,
            SubVariableName = "outB",
            ParentParameter = new NPParameter { VariableName = "observed" },
        });

        parentGraph.AddNode(host);
        parentGraph.BuildTopology();
        return (host, parentBb);
    }

    [Fact]
    public void Nested_InstanceIsolation_WriteInReadOutLiveFlow()
    {
        (NPProbeHost host, NPBlackboardSource parentBb) setup = Build();

        object ownerA = new();
        object ownerB = new();

        var instA = Assert.IsType<NPProbeSubGraph>(setup.host.StartForTest(ownerA));
        Assert.NotSame(instA, setup.host.SubGraphOverride);            // clone per owner start
        Assert.Equal(1, instA.StartedCount);
        Assert.Equal(10f, instA.LocalBlackboard.GetValue("inA", 0f)); // Write-In applied

        setup.host.TickForTest(ownerA, 1f);
        setup.host.TickForTest(ownerA, 1f);
        Assert.Equal(102f, instA.LocalBlackboard.GetValue("outB", 0f));
        Assert.Equal(102f, setup.parentBb.GetValue<float>("observed", -1f)); // Read-Out live

        // second owner runs an isolated clone from the same source
        var instB = Assert.IsType<NPProbeSubGraph>(setup.host.StartForTest(ownerB));
        Assert.NotSame(instA, instB);
        Assert.Equal(100f, instB.LocalBlackboard.GetValue("outB", 0f));     // fresh state
        setup.host.TickForTest(ownerB, 1f);
        Assert.Equal(101f, instB.LocalBlackboard.GetValue("outB", 0f));
        Assert.Equal(101f, setup.parentBb.GetValue<float>("observed", -1f)); // last writer wins (B live)
        Assert.Equal(102f, instA.LocalBlackboard.GetValue("outB", 0f));     // A's state untouched

        // release detaches A's read-out; further ticks for that key are no-ops and do not resurge A's value
        setup.host.ReleaseFor(ownerA);
        setup.host.TickForTest(ownerA, 1f);
        Assert.Equal(101f, setup.parentBb.GetValue<float>("observed", -1f));

        // asset-level teardown clears remaining owners
        setup.host.ReleaseAll();
        Assert.Null(setup.host.InstanceOf(ownerB));
        Assert.False(setup.host.IsRunning(ownerB));
    }
}

/// <summary>Host exposing the nested runtime for tests.</summary>
public sealed class NPProbeHost : NPNestedNode
{
    public required NPGraphAsset ParentRef;

    private NPNestedRuntime R => BindNested();

    public NPGraphAsset? StartForTest(object key) => R.GetOrStartInstance(key, ParentRef);

    public void TickForTest(object key, float d) => R.TickInstances(key, d);

    public void ReleaseFor(object key) => R.ReleaseFor(key);

    public void ReleaseAll() => R.ReleaseAll();

    public NPGraphAsset? InstanceOf(object key) => R.InstanceOf(key);

    public bool IsRunning(object key) => R.IsRunning(key);

    public override string DisplayName => "Host";
}

public sealed class ProbeParentGraph : NPGraphAsset
{
    public ProbeParentGraph() => Module = "bt";
}
