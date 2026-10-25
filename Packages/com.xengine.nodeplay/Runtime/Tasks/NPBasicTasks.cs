using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.NodePlay.Tasks;

/// <summary>Suspend for <see cref="Seconds"/> (scaled time), then succeed.</summary>
[NPName("Wait", 90)]
[NPCategory("Actions")]
public class NPWaitAction : NPActionTask
{
    public NPParameter Seconds = new() { InlineValue = 1f };

    private float _left;

    protected override void OnExecute()
    {
        _left = Seconds.GetValue(Blackboard!, 1f);
    }

    protected override void OnUpdate()
    {
        _left -= DeltaOfGraph();
        if (_left <= 0f)
            EndAction(true);
    }


    protected override void OnReset() => _left = 0f;
}

/// <summary>Write <see cref="SourceValue"/> into the bound variable, then succeed.</summary>
[NPName("Set Variable", 88)]
[NPCategory("Actions")]
public class NPSetVariableAction : NPActionTask
{
    public NPParameter Target = new();
    public NPParameter SourceValue = new();

    protected override void OnExecute()
    {
        Target.SetValue(Blackboard, SourceValue.GetValue(Blackboard));
        EndAction(true);
    }
}

/// <summary>Emit a log line (optionally with a variable payload) for console/debug evidence.</summary>
[NPName("Log Message", 86)]
[NPCategory("Actions")]
public class NPLogAction : NPActionTask
{
    public string Message = "log";
    public bool AppendVariable;
    public NPParameter Payload = new();

    protected override void OnExecute()
    {
        if (AppendVariable)
            Debug.Log("[NodePlay] " + Message + ": " + Payload.GetValue(Blackboard, ""));
        else
            Debug.Log("[NodePlay] " + Message);
        EndAction(true);
    }
}

/// <summary>
/// Move the agent toward a target position until inside <see cref="ArriveRadius"/>; needs a
/// Transform-bearing agent (the runner component provides one).
/// </summary>
[NPName("Move Towards", 84)]
[NPCategory("Actions")]
public class NPMoveTowardsAction : NPActionTask
{
    public NPParameter TargetPosition = new();
    public NPParameter Speed = new() { InlineValue = 2f };
    public float ArriveRadius = 0.35f;

    protected override void OnExecute()
    {
    }

    protected override void OnUpdate()
    {
        if (Agent == null)
        {
            EndAction(false);
            return;
        }

        Float3 target = ToFloat3(TargetPosition.GetValue(Blackboard));
        Transform transform = Agent.Transform;
        Float3 pos = transform.Position;
        if (Float3.Distance(pos, target) <= ArriveRadius)
        {
            EndAction(true);
            return;
        }

        float step = Speed.GetValue(Blackboard, 2f) * DeltaOfGraph();
        transform.Position = Float3.MoveTowards(pos, target, step);
    }

    internal static Float3 ToFloat3(object raw) => raw switch
    {
        Float3 f => f,
        string s => Parse(s),
        _ => default,
    };

    private static Float3 Parse(string s)
    {
        // "x,y,z" — forgiving split, invariant floats only (demo/AI authored format).
        var parts = s.Split(',');
        if (parts.Length != 3
            || !float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out float z))
            return default;
        return new Float3(x, y, z);
    }
}

/// <summary>Compare a parameter against a constant: ==, !=, >, <, >=, <= (numeric or string).</summary>
[NPName("Compare", 92)]
[NPCategory("Conditions")]
public class NPCompareCondition : NPConditionTask
{
    public NPParameter Value = new();
    public string Operator = ">";
    public NPParameter Threshold = new() { InlineValue = 0f };

    protected override bool OnCheck()
    {
        object left = Value.GetValue(Blackboard, (object)"");
        object right = Threshold.GetValue(Blackboard, (object)"");

        double? ln = ToDouble(left);
        double? rn = ToDouble(right);
        if (ln.HasValue && rn.HasValue)
            return Compare(ln.Value, rn.Value);

        string ls = left is bool lb ? (lb ? "true" : "false") : left?.ToString() ?? "";
        string rs = right is bool rb ? (rb ? "true" : "false") : right?.ToString() ?? "";
        int cmp = string.CompareOrdinal(ls, rs);
        return Operator switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            _ => false,
        };
    }

    private bool Compare(double l, double r) => Operator switch
    {
        "==" => l == r,
        "!=" => l != r,
        ">" => l > r,
        "<" => l < r,
        ">=" => l >= r,
        "<=" => l <= r,
        _ => false,
    };

    private static double? ToDouble(object v) => v switch
    {
        int i => i,
        long l => l,
        float f => f,
        double d => d,
        short s => s,
        byte b => b,
        bool bo => bo ? 1d : 0d,
        _ => null,
    };
}
