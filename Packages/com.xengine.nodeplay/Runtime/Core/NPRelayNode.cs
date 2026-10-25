namespace XEngine.NodePlay;

/// <summary>
/// Module-neutral pass-through pin ("relay"/reroute): forwards execution status to its single
/// child unchanged. Every graph module may use it to keep long wires tidy; the editor renders
/// it as a compact pill so canvas layout survives refactors.
/// </summary>
public class NPRelayNode : NPNode
{
    public override int MaxInConnections => 1;
    public override int MaxOutConnections => 1;

    public override string DisplayName => string.IsNullOrEmpty(Name) ? "Relay" : Name;
}
