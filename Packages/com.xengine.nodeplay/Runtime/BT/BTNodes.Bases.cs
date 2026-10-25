using System;

namespace XEngine.NodePlay.BT;

/// <summary>Base of every behaviour-tree node: single input by default, tree-style layout.</summary>
[NPCategory("Behaviour Tree")]
public abstract class BTNode : NPNode
{
    public override int MaxInConnections => 1;

    /// <summary>Evaluate one tick; implementation owns child recursion via graph.ExecuteNode.</summary>
    internal abstract NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner);

    /// <summary>Called when a Running node is interrupted by reactive re-evaluation or reset.</summary>
    internal virtual void OnAbort(NPBehaviourTreeAsset graph)
    {
    }

    protected static NPRunner RunnerOf(NPBehaviourTreeAsset graph) => graph.OwnerRunner;
}

/// <summary>A node with ordered children (fan-out).</summary>
public abstract class BTComposite : BTNode
{
    internal int EnabledChildCount()
    {
        int n = 0;
        for (int i = 0; i < OutConnections.Count; i++)
        {
            NPConnection wire = OutConnections[i];
            if (!wire.Disabled && !wire.Target.Disabled)
                n++;
        }

        return n;
    }

    internal NPNode? EnabledChildAt(int logicalIndex)
    {
        int seen = 0;
        for (int i = 0; i < OutConnections.Count; i++)
        {
            NPConnection wire = OutConnections[i];
            if (wire.Disabled || wire.Target.Disabled)
                continue;
            if (seen++ == logicalIndex)
                return wire.Target;
        }

        return null;
    }
}

/// <summary>A node wrapping exactly one child (the decorated connection).</summary>
public abstract class BTDecorator : BTNode
{
    public override int MaxOutConnections => 1;

    /// <summary>The single child, or null.</summary>
    protected NPNode? Child
    {
        get
        {
            for (int i = 0; i < OutConnections.Count; i++)
            {
                if (!OutConnections[i].Disabled)
                    return OutConnections[i].Target;
            }

            return null;
        }
    }
}

/// <summary>A terminal node: actions/conditions/subtrees. No children by default.</summary>
public abstract class BTLeaf : BTNode
{
    public override int MaxOutConnections => 0;
}
