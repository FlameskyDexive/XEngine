using XEngine.NodePlay;

namespace XEngine.NodePlay.BT;

/// <summary>
/// Typed owner for behaviour trees — identical runtime to <see cref="NPRunner"/>, adding a
/// convenience accessor for component scripts (StartBehaviour/StopBehaviour/SendEvent inherited).
/// </summary>
public class NPBehaviourTreeOwner : NPRunner
{
    public NPBehaviourTreeAsset? Tree => RunningGraph as NPBehaviourTreeAsset;
}
