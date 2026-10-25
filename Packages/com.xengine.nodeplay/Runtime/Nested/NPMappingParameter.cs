namespace XEngine.NodePlay;

/// <summary>Direction of a subgraph parameter mapping.</summary>
public enum NPMappingMode
{
    /// <summary>Copy parent value into the subgraph variable on start.</summary>
    WriteIn,
    /// <summary>Live-publish subgraph changes back into the parent (subscribes until release).</summary>
    ReadOut,
}

/// <summary>
/// One entry of a nested-subgraph parameter mapping (the analogue of BBMappingParameter):
/// <see cref="ParentParameter"/> supplies/absorbs values in the owning graph's scope while
/// <see cref="SubVariableName"/> names the exposed variable inside the nested graph.
/// </summary>
public class NPMappingParameter
{
    public string SubVariableName = "";
    /// <summary>Inline-or-bound value evaluated against the owning graph's blackboard.</summary>
    public NPParameter ParentParameter = new();
    public NPMappingMode Mode = NPMappingMode.WriteIn;

    /// <summary>Only mappings whose sub variable is marked public count as valid; others warn.</summary>
    public override string ToString()
        => $"{Mode}: {_parentSummary()} {(Mode == NPMappingMode.WriteIn ? "→" : "←")} {SubVariableName}";

    private string _parentSummary()
        => ParentParameter.IsBound ? ParentParameter.VariableName : "(inline)";
}
