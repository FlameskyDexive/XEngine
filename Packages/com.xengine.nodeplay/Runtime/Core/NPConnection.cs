using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.NodePlay;

/// <summary>
/// A directed wire between two nodes, persisted flat inside <see cref="NPGraphAsset.Connections"/>
/// by stable node id so the text format stays trivially diffable and patchable. Runtime traversal
/// uses the resolved <see cref="Source"/>/<see cref="Target"/> references plus the adjacency lists
/// built by <see cref="NPGraphAsset.BuildTopology"/>; per-module payloads (FSM transition
/// conditions, call modes) subclass this type.
/// </summary>
public class NPConnection
{
    public string Id = NPNode.NewId();
    public string FromNodeId = "";
    public string ToNodeId = "";
    public bool Disabled;

    /// <summary>Ordered reroute points (graph space) between output and input.</summary>
    public List<Float2> Points = new();

    public NPNode Source = null!;
    public NPNode Target = null!;
    public NPStatus Status = NPStatus.Resting;

    public override string ToString() => $"{Source} -> {Target}";
}
