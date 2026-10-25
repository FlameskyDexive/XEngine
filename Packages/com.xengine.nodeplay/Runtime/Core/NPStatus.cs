namespace XEngine.NodePlay;

/// <summary>Execution state of a node or connection while a graph runs.</summary>
public enum NPStatus
{
    /// <summary>Not evaluated yet this pass (or reset).</summary>
    Resting,
    /// <summary>Condition/branch answered false.</summary>
    Failure,
    /// <summary>Swallowed failure (Optional decorator semantics).</summary>
    Optional,
    /// <summary>Finished successfully.</summary>
    Success,
    /// <summary>In progress across frames.</summary>
    Running
}
