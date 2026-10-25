using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.NodePlay.Tasks;

// ── Transform batch ───────────────────────────────────────────────────────────────────

[NPName("Set Position", 83)]
[NPCategory("Transform")]
public class NPSetPositionAction : NPActionTask
{
    public NPParameter Position = new() { InlineValue = "0,0,0" };

    protected override void OnExecute()
    {
        if (Agent != null)
            Agent.Transform.Position = NPMoveTowardsAction.ToFloat3(Position.GetValue(Blackboard));
        EndAction(true);
    }
}

[NPName("Translate", 82)]
[NPCategory("Transform")]
public class NPTranslateAction : NPActionTask
{
    public NPParameter Offset = new() { InlineValue = "0,1,0" };

    protected override void OnExecute()
    {
        if (Agent != null)
            Agent.Transform.Position += NPMoveTowardsAction.ToFloat3(Offset.GetValue(Blackboard));
        EndAction(true);
    }
}

[NPName("Set Scale", 81)]
[NPCategory("Transform")]
public class NPSetScaleAction : NPActionTask
{
    public NPParameter Scale = new() { InlineValue = "1,1,1" };

    protected override void OnExecute()
    {
        if (Agent != null)
            Agent.Transform.LocalScale = NPMoveTowardsAction.ToFloat3(Scale.GetValue(Blackboard));
        EndAction(true);
    }
}

[NPName("Get Position", 80)]
[NPCategory("Transform")]
public class NPGetPositionAction : NPActionTask
{
    public string TargetVariable = "position";

    protected override void OnExecute()
    {
        if (Agent != null && Blackboard is NPBlackboardSource bb)
        {
            bb.Ensure(TargetVariable, "0,0,0");
            var p = Agent.Transform.Position;
            bb.SetValue(TargetVariable, p.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," +
                                        p.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," +
                                        p.Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        EndAction(true);
    }
}

// ── Time batch ────────────────────────────────────────────────────────────────────────

[NPName("Wait Realtime", 78)]
[NPCategory("Time")]
public class NPWaitRealtimeAction : NPWaitRealtimeBase
{
}

public abstract class NPWaitRealtimeBase : NPActionTask
{
    public NPParameter Seconds = new() { InlineValue = 1f };

    private float _left;
    private DateTime _deadline;

    protected override void OnExecute()
    {
        _left = Seconds.GetValue(Blackboard!, 1f);
        _deadline = DateTime.UtcNow.AddSeconds(_left);
    }

    protected override void OnUpdate()
    {
        if (DateTime.UtcNow >= _deadline)
            EndAction(true);
    }

    protected override void OnReset() => _left = 0f;
}

[NPName("Set Time Scale", 76)]
[NPCategory("Time")]
public class NPSetTimeScaleAction : NPActionTask
{
    public NPParameter Scale = new() { InlineValue = 1f };

    protected override void OnExecute()
    {
        XEngine.Runtime.Time.TimeScale = Math.Clamp(Scale.GetValue(Blackboard, 1f), 0f, 100f);
        EndAction(true);
    }
}

// ── Math batch ────────────────────────────────────────────────────────────────────────

[NPName("Random Int", 74)]
[NPCategory("Math")]
public class NPRandomIntAction : NPActionTask
{
    public NPParameter Min = new() { InlineValue = 0 };
    public NPParameter Max = new() { InlineValue = 100 };
    public string TargetVariable = "randomInt";

    protected override void OnExecute()
    {
        int min = (int)ToLong(Min.GetValue(Blackboard, 0));
        int max = (int)ToLong(Max.GetValue(Blackboard, 100));
        if (max <= min)
            max = min + 1;
        int value = Vector.RNG.Shared.Range(min, max);
        (Blackboard as NPBlackboardSource)?.Ensure(TargetVariable, 0);
        Blackboard?.SetValue(TargetVariable, value);
        EndAction(true);
    }

    private static long ToLong(object v) => v switch
    {
        int i => i, long l => l, float f => (long)f, double d => (long)d, bool b => b ? 1 : 0, _ => 0,
    };
}

[NPName("Random Float", 73)]
[NPCategory("Math")]
public class NPRandomFloatAction : NPActionTask
{
    public NPParameter Min = new() { InlineValue = 0f };
    public NPParameter Max = new() { InlineValue = 1f };
    public string TargetVariable = "randomFloat";

    protected override void OnExecute()
    {
        float min = Min.GetValue(Blackboard, 0f);
        float max = Max.GetValue(Blackboard, 1f);
        if (max <= min)
            max = min + 0.001f;
        float value = Vector.RNG.Shared.Range(min, max);
        (Blackboard as NPBlackboardSource)?.Ensure(TargetVariable, 0f);
        Blackboard?.SetValue(TargetVariable, value);
        EndAction(true);
    }
}

[NPName("Lerp Variable", 72)]
[NPCategory("Math")]
public class NPLerpVariableAction : NPActionTask
{
    public NPParameter From = new() { InlineValue = 0f };
    public NPParameter To = new() { InlineValue = 1f };
    public NPParameter Alpha = new() { InlineValue = 0.5f };
    public string TargetVariable = "lerped";

    protected override void OnExecute()
    {
        INPBlackboard bb = Blackboard!;
        float a = From.GetValue(bb, 0f);
        float b = To.GetValue(bb, 1f);
        float t = Math.Clamp(Alpha.GetValue(bb, 0.5f), 0f, 1f);
        bb.Ensure(TargetVariable, 0f);
        bb.SetValue(TargetVariable, a + (b - a) * t);
        EndAction(true);
    }
}

[NPName("Clamp Variable", 71)]
[NPCategory("Math")]
public class NPClampVariableAction : NPActionTask
{
    public string TargetVariable = "";
    public NPParameter Min = new() { InlineValue = 0f };
    public NPParameter Max = new() { InlineValue = 100f };

    protected override void OnExecute()
    {
        INPBlackboard bb = Blackboard!;
        var targetParam = new NPParameter { VariableName = TargetVariable };
        float v = NPTypeConverter.Convert(targetParam.GetValue(bb), typeof(float)) as float? ?? 0f;
        float clamped = Math.Clamp(v, Min.GetValue(bb, 0f), Max.GetValue(bb, 100f));
        targetParam.SetValue(bb, clamped);
        EndAction(true);
    }
}

[NPName("Frame To Variable", 75)]
[NPCategory("Time")]
public class NPFrameToVariableAction : NPActionTask
{
    public string TargetVariable = "frame";

    protected override void OnExecute()
    {
        (Blackboard as NPBlackboardSource)?.Ensure(TargetVariable, 0);
        Blackboard?.SetValue(TargetVariable, (long)XEngine.Runtime.Time.FrameCount);
        EndAction(true);
    }
}

// ── Blackboard batch ──────────────────────────────────────────────────────────────────

[NPName("Copy Variable", 70)]
[NPCategory("Blackboard")]
public class NPCopyVariableAction : NPActionTask
{
    public string FromVariable = "";
    public string ToVariable = "";

    protected override void OnExecute()
    {
        if (Blackboard is NPBlackboardSource source && source.Get(FromVariable) is { } value)
        {
            source.Ensure(ToVariable, value.Value);
            source.SetValue(ToVariable, value.Value);
        }

        EndAction(true);
    }
}

[NPName("Swap Variables", 69)]
[NPCategory("Blackboard")]
public class NPSwapVariablesAction : NPActionTask
{
    public string VariableA = "";
    public string VariableB = "";

    protected override void OnExecute()
    {
        if (Blackboard is NPBlackboardSource source
            && source.Get(VariableA) is { } a && source.Get(VariableB) is { } b)
        {
            object tmp = a.Value;
            a.SetRawValue(b.Value);
            b.SetRawValue(tmp);
        }

        EndAction(true);
    }
}

// ── GameObject batch ──────────────────────────────────────────────────────────────────

[NPName("Find By Name", 66)]
[NPCategory("GameObject")]
public class NPFindByNameAction : NPActionTask
{
    public NPParameter TargetName = new();
    public string FoundPositionVariable = "foundPos";

    protected override void OnExecute()
    {
        string name = TargetName.GetValue(Blackboard, "").ToString() ?? "";
        GameObject? found = FindInScene(name);
        if (found != null && Blackboard is NPBlackboardSource bb)
        {
            bb.Ensure(FoundPositionVariable, "0,0,0");
            var p = found.Transform.Position;
            bb.SetValue(FoundPositionVariable, p.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," +
                                                p.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," +
                                                p.Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        EndAction(found != null);

        static GameObject? FindInScene(string target)
        {
            Scene? scene = Scene.Current;
            if (scene == null)
                return null;

            foreach (GameObject obj in scene.AllObjects)
            {
                if (obj.Name == target)
                    return obj;
            }

            return null;
        }
    }
}

[NPName("Get Name", 65)]
[NPCategory("GameObject")]
public class NPGetNameAction : NPActionTask
{
    public string TargetVariable = "agentName";

    protected override void OnExecute()
    {
        if (Agent != null && Blackboard is NPBlackboardSource bb)
        {
            bb.Ensure(TargetVariable, "");
            bb.SetValue(TargetVariable, Agent.Name);
        }

        EndAction(true);
    }
}

// ── Debug batch ───────────────────────────────────────────────────────────────────────

[NPName("Log Variable", 64)]
[NPCategory("Debug")]
public class NPLogVariableAction : NPActionTask
{
    public string Prefix = "value";
    public string VariableName = "";

    protected override void OnExecute()
    {
        string value = Blackboard?.Get(VariableName)?.Value?.ToString() ?? "<none>";
        Debug.Log("[NodePlay] " + Prefix + " " + VariableName + " = " + value);
        EndAction(true);
    }
}

[NPName("Warn If", 62)]
[NPCategory("Debug")]
public class NPWarnIfAction : NPActionTask
{
    public NPConditionTask? Condition;
    public string Message = "check failed";

    protected override void OnExecute()
    {
        if (Condition != null
            && Condition.Bind(Agent, Blackboard, OwnerSystem) == null
            && !Condition.Check())
        {
            XEngine.Runtime.Debug.LogWarning("[NodePlay] " + Message);
        }

        EndAction(true);
    }
}

// ── Condition batch B ─────────────────────────────────────────────────────────────────

[NPName("Bool Is", 84)]
[NPCategory("Conditions")]
public class NPBoolIsCondition : NPConditionTask
{
    public NPParameter Value = new();
    public bool Expected = true;

    protected override bool OnCheck() => Value.GetValue(Blackboard, !Expected) == Expected;
}

[NPName("Was Within Seconds", 70)]
[NPCategory("Conditions")]
public class NPWasWithinSecondsCondition : NPConditionTask
{
    public NPParameter Seconds = new() { InlineValue = 1f };

    private bool _primed;
    private float _sinceLastTrue;

    protected override bool OnCheck()
    {
        // edge-trigger style: true on the first check after (re)bind, then throttled
        if (!_primed)
        {
            _primed = true;
            _sinceLastTrue = 0f;
            return true;
        }

        _sinceLastTrue += (OwnerSystem as NPGraphAsset)?.LastDelta ?? 0f;
        if (_sinceLastTrue >= Seconds.GetValue(Blackboard, 1f))
        {
            _sinceLastTrue = 0f;
            return true;
        }

        return false;
    }

    protected override void ResetForReuse() => _primed = false;
}
