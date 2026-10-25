using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.NodePlay.Dialogue;

/// <summary>Base of all dialogue nodes.</summary>
[NPCategory("Dialogue")]
public abstract class DTNode : NPNode
{
    internal abstract void Execute(NPDialogueAsset graph, NPRunner runner);
}

/// <summary>Speak a line through the subtitles request event (auto-continues without a UI).</summary>
[NPName("Statement", 90)]
[NPCategory("Dialogue")]
public class DTStatementNode : DTNode
{
    public string ActorKey = NPDialogueAsset.InstigatorKey;
    public NPStatement Statement = new();
    public string AudioKey = "";

    internal override void Execute(NPDialogueAsset graph, NPRunner runner)
    {
        INPDialogueActor? actor = graph.ResolveActor(ActorKey, runner);
        var interpolated = new NPStatement
        {
            Text = Statement.Interpolate(graph.GraphBlackboard),
            AudioKey = AudioKey,
            Meta = Statement.Meta,
        };

        var request = new NPSubtitlesRequest
        {
            Actor = actor ?? new NPFallbackActor(ActorKey),
            Statement = interpolated,
            Continue = () => graph.ContinueFrom(this),
        };

        NPDialogueAsset.RaiseSubtitlesRequest(request);
        if (!request.Handled && NPDialogueAsset.AutoPump)
            request.Continue(); // no UI subscribed: keep the conversation moving (headless/tests)
    }

    private sealed class NPFallbackActor : INPDialogueActor
    {
        private readonly string _key;
        public NPFallbackActor(string key) => _key = key;
        public string DisplayName => _key;
        public string PortraitKey => "";
        public string DialogueColor => "#cccccc";
        public Transform Transform => null!;
    }
}

/// <summary>Ask the player to choose; conditions filter offered options.</summary>
[NPName("Multiple Choice", 88)]
[NPCategory("Dialogue")]
public class DTMultipleChoiceNode : DTNode
{
    public string ActorKey = NPDialogueAsset.InstigatorKey;
    public float AvailableTime;
    public List<DTChoiceEntry> Options = new();

    internal override void Execute(NPDialogueAsset graph, NPRunner runner)
    {
        INPDialogueActor? actor = graph.ResolveActor(ActorKey, runner);
        var offered = new List<NPChoiceOption>();
        var pickMap = new List<int>();

        for (int i = 0; i < Options.Count && i < OutConnections.Count; i++)
        {
            DTChoiceEntry entry = Options[i];
            NPConnection wire = OutConnections[i];
            if (wire.Disabled || entry.Condition is { } c
                && (c.InitError != null || c.Bind(runner, graph.GraphBlackboard, graph) != null || !c.Check()))
                continue;
            offered.Add(new NPChoiceOption
            {
                Text = new NPStatement(entry.Text).InterpolateInto(graph.GraphBlackboard),
                Condition = null,
                SaySelection = entry.SaySelection,
            });
            pickMap.Add(i);
        }

        bool entrySays(int i) => i >= 0 && i < Options.Count && Options[i].SaySelection;

        var request = new NPMultipleChoiceRequest
        {
            Actor = actor ?? new DTFallbackActor(ActorKey),
            Options = offered,
            AvailableTime = AvailableTime,
            Pick = choiceIndex =>
            {
                int target = choiceIndex >= 0 && choiceIndex < pickMap.Count ? pickMap[choiceIndex] : -1;
                if (target >= 0 && entrySays(target))
                    graph.SayCurrent(Options[target].Text, ActorKey);
                graph.ContinueFrom(this, target);
            },
        };

            NPDialogueAsset.RaiseMultipleChoiceRequest(request);
        if (!request.Handled && NPDialogueAsset.AutoPump && offered.Count > 0)
            request.Pick(0); // no UI subscribed: take the first offered option (headless/tests)
    }

    private sealed class DTFallbackActor : INPDialogueActor
    {
        private readonly string _key;
        public DTFallbackActor(string key) => _key = key;
        public string DisplayName => _key;
        public string PortraitKey => "";
        public string DialogueColor => "#cccccc";
        public Transform Transform => null!;
    }
}

/// <summary>One option row: spoken text + optional condition + outgoing wire index alignment.</summary>
public class DTChoiceEntry
{
    public string Text = "";
    public NPConditionTask? Condition;
    public bool SaySelection;
}

/// <summary>Branch on a condition: first child = true, second child = false.</summary>
[NPName("Check", 80)]
[NPCategory("Dialogue")]
public class DTCheckNode : DTNode
{
    public NPConditionTask? Condition;

    internal override void Execute(NPDialogueAsset graph, NPRunner runner)
    {
        bool pass = Condition == null
            || (Condition.Bind(runner, graph.GraphBlackboard, graph) == null && Condition.Check());
        int index = pass ? 0 : 1;
        if (index < OutConnections.Count && !OutConnections[index].Disabled
            && OutConnections[index].Target is DTNode taken)
            graph.ExecuteNode(taken, runner);
        else if (OutConnections.Count > 0 && OutConnections[0].Target is DTNode fallback)
            graph.ExecuteNode(fallback, runner);
    }
}

/// <summary>Run an action mid-conversation, then continue.</summary>
[NPName("Dialogue Action", 78)]
[NPCategory("Dialogue")]
public class DTActionNode : DTNode
{
    public NPActionTask? Action;

    internal override void Execute(NPDialogueAsset graph, NPRunner runner)
    {
        if (Action == null)
        {
            graph.ContinueFrom(this);
            return;
        }

        if (Action.Bind(runner, graph.GraphBlackboard, graph) != null)
        {
            graph.ContinueFrom(this);
            return;
        }

        if (Action.Result != null)
            Action.ResetForReuse();
        Action.Tick();
        if (Action.Result == null)
            return; // stays pending; the controller re-executes until it settles
        graph.ContinueFrom(this);
    }
}

/// <summary>Jump to the node whose Comment matches <see cref="Tag"/>.</summary>
[NPName("Jumper", 74)]
[NPCategory("Dialogue")]
public class DTJumperNode : DTNode
{
    public string Tag = "";

    internal override void Execute(NPDialogueAsset graph, NPRunner runner)
    {
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            if (graph.Nodes[i] is DTNode target
                && string.Equals(target.Comment, Tag, StringComparison.OrdinalIgnoreCase))
            {
                graph.ExecuteNode(target, runner);
                return;
            }
        }
    }
}

/// <summary>Ends the conversation (success or failure).</summary>
[NPName("Finish", 70)]
[NPCategory("Dialogue")]
public class DTFinishNode : DTNode
{
    public bool Success = true;

    internal override void Execute(NPDialogueAsset graph, NPRunner runner)
        => graph.FinishDialogue(Success);
}

/// <summary>Runs another dialogue tree inline; continues with its finish status.</summary>
[NPName("Sub Dialogue", 66)]
[NPCategory("Dialogue")]
public class DTSubDialogueNode : DTNode
{
    public NPNestedRuntime Nested = new();

    internal override void Execute(NPDialogueAsset graph, NPRunner runner)
    {
        if (!Nested.IsRunning(graph))
        {
            if (Nested.InstanceOf(graph) != null)
            {
                Nested.ReleaseFor(graph); // previous run finished
            }
            else
            {
                Nested.GetOrStartInstance(graph, graph);
                return; // first frame: enter, continue next tick
            }
        }

        Nested.TickInstances(graph, graph.LastDelta);
        if (Nested.InstanceOf(graph) is NPDialogueAsset sub && sub.IsFinished)
            Nested.FinishInstance(graph);
    }
}
