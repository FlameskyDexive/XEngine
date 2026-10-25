using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.NodePlay.Tasks;

// ── GameObject / Transform ────────────────────────────────────────────────────────────

/// <summary>Set the agent's name.</summary>
[NPName("Set Name", 82)]
[NPCategory("GameObject")]
public class NPSetNameAction : NPActionTask
{
    public NPParameter Name = new() { InlineValue = "Object" };

    protected override void OnExecute()
    {
        if (Agent != null)
            Agent.Name = Name.GetValue(Blackboard, "Object");
        EndAction(true);
    }
}

/// <summary>Set the agent active-state (enabled flag).</summary>
[NPName("Set Active", 81)]
[NPCategory("GameObject")]
public class NPSetActiveAction : NPActionTask
{
    public NPParameter Active = new() { InlineValue = true };

    protected override void OnExecute()
    {
        if (Agent != null)
            Agent.Enabled = Active.GetValue(Blackboard, true);
        EndAction(true);
    }
}

/// <summary>Destroy a component of the given type on the agent (runtime removal).</summary>
[NPName("Remove Component", 78)]
[NPCategory("GameObject")]
public class NPRemoveComponentAction : NPActionTask
{
    public string ComponentTypeName = "";

    protected override void OnExecute()
    {
        Type? type = RuntimeUtils.FindType(ComponentTypeName);
        if (Agent != null && type != null)
        {
            foreach (MonoBehaviour component in Agent.GetComponents(type))
            {
                component.Dispose();
                break;
            }
        }

        EndAction(true);
    }
}

/// <summary>Look at a target position (instant yaw snap — no smoothing).</summary>
[NPName("Look At", 76)]
[NPCategory("Transform")]
public class NPLookAtAction : NPActionTask
{
    public NPParameter TargetPosition = new();

    protected override void OnExecute()
    {
        if (Agent == null)
        {
            EndAction(false);
            return;
        }

        Float3 target = NPMoveTowardsAction.ToFloat3(TargetPosition.GetValue(Blackboard));
        Float3 delta = target - Agent.Transform.Position;
        if (MathF.Abs(delta.X) > 0.001f || MathF.Abs(delta.Z) > 0.001f)
        {
            float yaw = MathF.Atan2(delta.X, delta.Z);
            Agent.Transform.Rotation = Quaternion.AngleAxis(yaw, Float3.UnitY);
        }

        EndAction(true);
    }
}

// ── Blackboard ────────────────────────────────────────────────────────────────────────

/// <summary>Add a numeric delta into a variable.</summary>
[NPName("Add To Variable", 80)]
[NPCategory("Blackboard")]
public class NPAddVariableAction : NPActionTask
{
    public NPParameter Target = new();
    public NPParameter Delta = new() { InlineValue = 1f };

    protected override void OnExecute()
    {
        INPBlackboard bb = Blackboard!;
        double current = ToDouble(Target.GetValue(bb, 0d));
        double add = ToDouble(Delta.GetValue(bb, 0d));
        object result = current == Math.Floor(current) && add == Math.Floor(add)
            ? (object)(long)(current + add)
            : current + add;
        Target.SetValue(bb, result);
        EndAction(true);
    }

    private static double ToDouble(object v) => v switch
    {
        int i => i, long l => l, float f => f, double d => d, short s => s, byte b => b,
        bool bo => bo ? 1 : 0, _ => 0,
    };
}

/// <summary>Remove a variable from the graph scope.</summary>
[NPName("Delete Variable", 79)]
[NPCategory("Blackboard")]
public class NPDeleteVariableAction : NPActionTask
{
    public string VariableName = "";

    protected override void OnExecute()
    {
        if (Blackboard is NPBlackboardSource source)
            source.Remove(VariableName);
        EndAction(true);
    }
}

// ── Flow / debug ──────────────────────────────────────────────────────────────────────

/// <summary>Draw a debug line between two points for one frame (Scene gizmos pass).</summary>
[NPName("Debug Line", 72)]
[NPCategory("Debug")]
public class NPDebugLineAction : NPActionTask
{
    public NPParameter From = new();
    public NPParameter To = new();

    protected override void OnExecute()
    {
        // Engine Debug gizmos API lands with the debug-draw pass; log the segment meanwhile.
        Debug.Log("[NodePlay] line " + NPMoveTowardsAction.ToFloat3(From.GetValue(Blackboard)) +
                  " -> " + NPMoveTowardsAction.ToFloat3(To.GetValue(Blackboard)));
        EndAction(true);
    }
}

/// <summary>Fail immediately (explicit failure marker for selectors).</summary>
[NPName("Fail", 70)]
[NPCategory("Flow")]
public class NPFailAction : NPActionTask
{
    protected override void OnExecute() => EndAction(false);
}

/// <summary>Succeed immediately (explicit success marker).</summary>
[NPName("Succeed", 69)]
[NPCategory("Flow")]
public class NPSucceedAction : NPActionTask
{
    protected override void OnExecute() => EndAction(true);
}

// ── Conditions ────────────────────────────────────────────────────────────────────────

/// <summary>True when a variable exists in scope.</summary>
[NPName("Has Variable", 88)]
[NPCategory("Conditions")]
public class NPHasVariableCondition : NPConditionTask
{
    public string VariableName = "";
    public bool MustBePublic;

    protected override bool OnCheck()
    {
        if (Blackboard is not NPBlackboardSource source)
            return false;
        NPVariable? variable = source.Get(VariableName);
        return variable != null && (!MustBePublic || variable.IsPublic);
    }
}

/// <summary>Random pass/fail with probability 0..1.</summary>
[NPName("Probability", 80)]
[NPCategory("Conditions")]
public class NPProbabilityCondition : NPConditionTask
{
    public NPParameter Chance = new() { InlineValue = 0.5f };

    protected override bool OnCheck()
    {
        float chance = Math.Clamp(Chance.GetValue(Blackboard, 0.5f), 0f, 1f);
        return (XEngine.Runtime.Time.FrameCount * 2654435761u % 1000u) / 1000f < chance;
    }
}

/// <summary>True while the owning graph has been running less than Seconds.</summary>
[NPName("Within Time", 76)]
[NPCategory("Conditions")]
public class NPWithinTimeCondition : NPConditionTask
{
    public NPParameter Seconds = new() { InlineValue = 5f };

    protected override bool OnCheck()
        => OwnerSystem != null && OwnerSystem.ElapsedTime < Seconds.GetValue(Blackboard, 5f);
}

/// <summary>Distance check between agent and a target position.</summary>
[NPName("Distance Check", 86)]
[NPCategory("Conditions")]
public class NPDistanceCondition : NPConditionTask
{
    public NPParameter TargetPosition = new();
    public NPParameter Radius = new() { InlineValue = 3f };
    public bool Inside = true;

    protected override bool OnCheck()
    {
        if (Agent == null)
            return false;
        Float3 target = NPMoveTowardsAction.ToFloat3(TargetPosition.GetValue(Blackboard));
        float distance = Float3.Distance(Agent.Transform.Position, target);
        return Inside ? distance <= Radius.GetValue(Blackboard, 3f) : distance > Radius.GetValue(Blackboard, 3f);
    }
}

/// <summary>True when the agent has a component of the given type.</summary>
[NPName("Has Component", 74)]
[NPCategory("Conditions")]
public class NPHasComponentCondition : NPConditionTask
{
    public string ComponentTypeName = "";

    protected override bool OnCheck()
    {
        if (Agent == null)
            return false;
        Type? type = RuntimeUtils.FindType(ComponentTypeName);
        if (type == null)
            return false;
        foreach (MonoBehaviour _ in Agent.GetComponents(type))
            return true;
        return false;
    }
}
