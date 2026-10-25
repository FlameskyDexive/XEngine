using System.Collections.Generic;

using XEngine.NodePlay;
using XEngine.NodePlay.Dialogue;
using XEngine.NodePlay.Tasks;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class DTProbeActor : INPDialogueActor
{
    public string Name = "John";
    public string DisplayName => Name;
    public string PortraitKey => "";
    public string DialogueColor => "#ffffff";
    public Transform Transform => null!;
}

/// <summary>M16: dialogue playback — auto pump, UI handled path, choices, interpolation, sub trees.</summary>
public sealed class NodePlayDialogueTests
{
    private static (NPDialogueAsset graph, List<string> spoken, List<string> finished) BuildJohnDae()
    {
        var graph = new NPDialogueAsset { GraphName = "John & Dae" };
        graph.ActorParameters.Add(new NPActorParameter { Key = "john", Name = "John" });
        graph.ActorParameters.Add(new NPActorParameter { Key = "dae", Name = "Dae" });

        var greet = graph.AddNode(new DTStatementNode
        {
            Name = "Greet",
            ActorKey = "john",
            Statement = new NPStatement("Hey [player], seen my [item]?"),
        });
        var reply = graph.AddNode(new DTStatementNode
        {
            Name = "Reply",
            ActorKey = "dae",
            Statement = new NPStatement("What's in it for me?"),
        });
        var choice = graph.AddNode(new DTMultipleChoiceNode { Name = "Ask" });
        choice.Options.Add(new DTChoiceEntry { Text = "Nothing." });                 // wire 0 → rude
        choice.Options.Add(new DTChoiceEntry { Text = "Gold.", SaySelection = true }); // wire 1 → nice
        var rude = graph.AddNode(new DTStatementNode { Name = "Rude", ActorKey = "dae", Statement = new NPStatement("Hmph.") });
        var nice = graph.AddNode(new DTStatementNode { Name = "Nice", ActorKey = "dae", Statement = new NPStatement("Deal.") });
        var finish = graph.AddNode(new DTFinishNode { Name = "Bye" });

        var bb = new NPBlackboardSource { Identifier = "D" };
        bb.Add("player", "Hero");
        bb.Add("item", "wrench");
        graph.GraphBlackboard = bb;

        Connect(graph, greet, reply);
        Connect(graph, reply, choice);
        Connect(graph, choice, rude);  // wire 0
        Connect(graph, choice, nice);  // wire 1
        Connect(graph, rude, finish);
        Connect(graph, nice, finish);
        return (graph, new List<string>(), new List<string>());
    }

    private static void Connect(NPDialogueAsset g, NPNode a, NPNode b)
    {
        g.Connect(a, b, out _);
        g.BuildTopology();
    }

    [Fact]
    public void AutoPump_FlowsToFinish_AndSubscribesSeenEveryLine()
    {
        (NPDialogueAsset graph, List<string> spoken, List<string> finished) setup = BuildJohnDae();
        var lines = setup.spoken;

        NPSubtitlesRequest? last = null;
        void Handler(NPSubtitlesRequest r)
        {
            r.Handled = true;
            last = r;
            lines.Add(r.Actor.DisplayName + ": " + r.Statement.Text);
            r.Continue();
        }

        NPDialogueAsset.OnSubtitlesRequest += Handler;
        try
        {
            bool? outcome = null;
            var runner = NewRunner();
            setup.graph.StartDialogue(runner, null, ok => outcome = ok);
            if (outcome != true)
            throw new InvalidOperationException($"AUTO diag spoken=[{string.Join(" | ", lines)}] outcome={outcome}");
        Assert.True(outcome == true);
            Assert.Equal(3, lines.Count);
            Assert.StartsWith("John: Hey Hero, seen my wrench?", lines[0]);
            Assert.StartsWith("Dae: What's in it for me?", lines[1]);
            Assert.StartsWith("Dae: Hmph.", lines[2]);
            Assert.True(setup.graph.IsFinished);
        }
        finally
        {
            NPDialogueAsset.OnSubtitlesRequest -= Handler;
        }
    }

    [Fact]
    public void UiHandled_Choice_ConditionFiltersAndSaySelection()
    {
        (NPDialogueAsset graph, List<string> spoken, List<string> finished) setup = BuildJohnDae();
        var spoken = setup.spoken;

        var subs = new List<NPSubtitlesRequest>();
        var choices = new List<NPMultipleChoiceRequest>();

        void OnSub(NPSubtitlesRequest r) { r.Handled = true; subs.Add(r); r.Continue(); }
        void OnChoice(NPMultipleChoiceRequest r)
        {
            r.Handled = true;
            choices.Add(r);
            if (r.Options.Count == 2)
                r.Pick(1); // gold path
            else
                r.Pick(0);
        }

        NPDialogueAsset.OnSubtitlesRequest += OnSub;
        NPDialogueAsset.OnMultipleChoiceRequest += OnChoice;
        try
        {
            var runner = NewRunner();
            setup.graph.StartDialogue(runner, new DTProbeActor { Name = "John" });

            Assert.Single(choices);                    // one Ask choice request
            Assert.Equal(2, choices[0].Options.Count); // both options pass (no conditions)
            Assert.Equal(4, subs.Count);
            Assert.Contains("Gold.", subs[2].Statement.Text);
            Assert.Contains("Deal.", subs[^1].Statement.Text);
            Assert.True(setup.graph.IsFinished);
            Assert.Equal("John", subs[0].Actor.DisplayName); // SELF resolved to instigator actor
        }
        finally
        {
            NPDialogueAsset.OnSubtitlesRequest -= OnSub;
            NPDialogueAsset.OnMultipleChoiceRequest -= OnChoice;
        }
    }

    [Fact]
    public void CheckNode_BranchesTrueFalse()
    {
        var graph = new NPDialogueAsset { GraphName = "Check" };
        graph.GraphBlackboard = new NPBlackboardSource { Identifier = "C" };
        graph.GraphBlackboard.Add("rich", false);

        var check = graph.AddNode(new DTCheckNode
        {
            Condition = new NPCompareCondition
            {
                Value = new NPParameter { VariableName = "rich" },
                Operator = "==",
                Threshold = new NPParameter { InlineValue = "true" },
            },
        });
        var rich = graph.AddNode(new DTFinishNode { Name = "Rich" });
        var poor = graph.AddNode(new DTFinishNode { Name = "Poor" });
        graph.Connect(check, rich, out _);
        graph.Connect(check, poor, out _);
        graph.BuildTopology();

        var runner = NewRunner();
        graph.OnStarted(runner);
        graph.ExecuteNode(check, runner);
        Assert.Equal("Poor", graph.LastRouted!.DisplayName);

        graph.GraphBlackboard.SetValue("rich", true);
        graph.ExecuteNode(check, runner);
        Assert.Equal("Rich", graph.LastRouted!.DisplayName);
    }

    [Fact]
    public void SubDialogue_RunsInlineAndReleases()
    {
        var parent = new NPDialogueAsset { GraphName = "Parent" };
        var sub = new NPDialogueAsset { GraphName = "Sub" };
        sub.AddNode(new DTFinishNode());
        sub.BuildTopology();

        var node = new DTSubDialogueNode();
        node.Nested.SubGraphOverride = sub;
        parent.AddNode(node);
        parent.BuildTopology();
        parent.GraphBlackboard = parent.LocalBlackboard;

        var runner = NewRunner();
        parent.OnStarted(runner);
        parent.ExecuteNode(node, runner);       // enter sub
        Assert.True(node.Nested.IsRunning(parent));
        parent.ExecuteNode(node, runner);       // sub ticks → finish node settles
        Assert.False(node.Nested.IsRunning(parent));
    }

    private static NPRunner NewRunner()
    {
        var go = new GameObject("DlgProbe");
        var runner = go.AddComponent<NPRunner>();
        runner.OnEnableAction = NPRunAction.DoNothing;
        runner.OnDisableAction = NPRunAction.DoNothing;
        return runner;
    }
}
