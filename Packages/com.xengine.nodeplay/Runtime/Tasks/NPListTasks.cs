using System.Collections.Generic;

namespace XEngine.NodePlay;

/// <summary>
/// Runs child actions back-to-back in order; finishes Success when all succeed, Failure as soon
/// as one fails. Children are persisted polymorphically — editing tools append/remove entries.
/// </summary>
[NPName("Action List", -8)]
[NPCategory("Utility")]
public class NPActionList : NPActionTask
{
    public List<NPActionTask> Actions = new();

    private int _cursor;
    private bool _childFailed;

    public override string DisplayName => "Action List";

    protected override void OnExecute()
    {
        _cursor = 0;
        _childFailed = false;
        BeginChild();
    }

    protected override void OnUpdate() => TickChild();

    private void BeginChild()
    {
        while (_cursor < Actions.Count)
        {
            NPActionTask child = Actions[_cursor];
            if (!ReferenceEquals(child.Agent, Agent))
                child.Bind(Agent, Blackboard, OwnerSystem);
            if (child.InitError != null)
            {
                _childFailed = true;
                EndAction(false);
                return;
            }

            child.Tick();
            if (child.Result == true)
            {
                _cursor++;
                continue; // next child starts this very tick
            }

            if (child.Result == false)
            {
                _childFailed = true;
                EndAction(false);
                return;
            }

            return; // child running; wait for the next tick
        }

        EndAction(!_childFailed);
    }

    private void TickChild()
    {
        if (_cursor >= Actions.Count)
        {
            EndAction(true);
            return;
        }

        NPActionTask child = Actions[_cursor];
        child.Tick();
        if (child.Result == false)
        {
            _childFailed = true;
            EndAction(false);
        }
        else if (child.Result == true)
        {
            _cursor++;
            BeginChild();
        }
    }

    protected override void OnReset()
    {
        for (int i = 0; i < Actions.Count; i++)
            Actions[i].ResetForReuse();
        _cursor = 0;
        _childFailed = false;
    }
}

/// <summary>True when every child condition answers true (short-circuits on first failure).</summary>
[NPName("Condition List", -8)]
[NPCategory("Utility")]
public class NPConditionList : NPConditionTask
{
    public List<NPConditionTask> Conditions = new();

    public override string DisplayName => "Condition List";

    public NPConditionList() { }

    public NPConditionList(params NPConditionTask[] conditions) => Conditions.AddRange(conditions);

    protected override bool OnCheck()
    {
        for (int i = 0; i < Conditions.Count; i++)
        {
            NPConditionTask condition = Conditions[i];
            if (!ReferenceEquals(condition.Agent, Agent))
                condition.Bind(Agent, Blackboard, OwnerSystem);
            if (condition.InitError != null || !condition.Check())
                return false;
        }

        return true;
    }

    protected override void ResetForReuse()
    {
        for (int i = 0; i < Conditions.Count; i++)
            Conditions[i].Disable();
    }
}
