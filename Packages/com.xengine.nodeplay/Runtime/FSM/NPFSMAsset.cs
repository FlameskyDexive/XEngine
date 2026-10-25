using XEngine.Runtime;

namespace XEngine.NodePlay.FSM;

/// <summary>
/// Hierarchical state machine graph. The start state is an explicit id (no index shuffling);
/// transitions and their call modes live on connection payloads.
/// </summary>
[CreateAssetMenu("NodePlay/State Machine", Extension = NPGraphAsset.FileExtension, Order = 82)]
public partial class NPFSMAsset : NPGraphAsset
{
    /// <summary>Id of the state entered on Start; empty = prime node.</summary>
    public string StartStateId = "";

    public NPFSMAsset() => Module = "fsm";

    public override void OnValidate()
    {
        base.OnValidate();
        if (!string.IsNullOrEmpty(StartStateId) && GetNode(StartStateId) == null)
            StartStateId = "";
    }
}
