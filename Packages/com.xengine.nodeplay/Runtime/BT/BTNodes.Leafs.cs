using System;

using XEngine.Echo;

namespace XEngine.NodePlay.BT;

/// <summary>Leaf running one <see cref="NPActionTask"/> (cooperative poll contract).</summary>
[NPName("Action", 95)]
[NPCategory("Leafs")]
public class BTActionNode : BTLeaf
{
    public NPActionTask? Action;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Action == null)
            return NPStatus.Success;

        // Bind every tick: cheap same-agent early-out inside, and guarantees fresh scopes see it.
        if (Action.Bind(runner, graph.GraphBlackboard, graph) != null)
        {
            NPGraphConsole.Warn(graph, $"'{DisplayName}' action init: {Action.InitError}", Id);
            return NPStatus.Failure;
        }

        // A previously finished invocation must be re-armed before it can run again.
        if (Action.Result != null)
            Action.ResetForReuse();

        Action.Tick();
        return Action.Result switch
        {
            null => NPStatus.Running,
            true => NPStatus.Success,
            _ => NPStatus.Failure,
        };
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph)
    {
        if (Action == null)
            return;
        if (Action.Result == null)
            Action.Abort();
        else
            Action.ResetForReuse();
    }
}

/// <summary>Leaf evaluating one condition; invert handled by the task itself.</summary>
[NPName("Condition", 93)]
[NPCategory("Leafs")]
public class BTConditionNode : BTLeaf
{
    public NPConditionTask? Condition;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Condition == null)
            return NPStatus.Success;
        if (Condition.Bind(runner, graph.GraphBlackboard, graph) != null)
        {
            NPGraphConsole.Warn(graph, $"'{DisplayName}' condition init: {Condition.InitError}", Id);
            return NPStatus.Failure;
        }

        return Condition.Check() ? NPStatus.Success : NPStatus.Failure;
    }
}

/// <summary>
/// Runs another behaviour-tree asset as a subtree (Write-In/Read-Out mappings supported).
/// Completes when the nested tree's root settles with <see cref="NPBehaviourTreeAsset.Repeat"/>
/// disabled in the child; with Repeat children the subtree runs forever (status Running).
/// </summary>
[NPName("Sub Tree", 60)]
[NPCategory("Nested")]
[NPDescription("Runs another behaviour tree with Write-In/Read-Out variable mapping; settles with the child root.")]
public class BTSubTreeNode : BTLeaf
{
    public NPNestedRuntime Nested = new();

    private NPBehaviourTreeAsset? _instance;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        _instance ??= Nested.GetOrStartInstance(graph, graph) as NPBehaviourTreeAsset;
        if (_instance == null)
            return NPStatus.Failure;

        Nested.TickInstances(graph, graph.ElapsedDelta());

        if (!_instance.Repeat && _instance.LastRootStatus is NPStatus.Success or NPStatus.Failure)
        {
            NPStatus settled = _instance.LastRootStatus;
            Nested.FinishInstance(graph);
            Nested.ReleaseFor(graph);
            _instance = null;
            return settled;
        }

        return NPStatus.Running;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph)
    {
        if (_instance != null)
        {
            Nested.ReleaseFor(graph);
            _instance = null;
        }
    }
}
