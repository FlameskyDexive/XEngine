using XEngine.NodePlay;
using XEngine.NodePlay.BT;
using XEngine.NodePlay.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class NodePlayTaskWizardTests
{
    [Fact]
    public void Scaffold_ActionContainsSkeleton()
    {
        string text = NodePlayTaskWizard.Scaffold(
            NodePlayTaskWizard.BaseKind.Action, "My Sweep Attack", "Custom", "Sweeps the area.");
        Assert.Contains("public sealed class MySweepAttack : NPActionTask", text);
        Assert.Contains("[NPCategory(\"Custom\")]", text);
        Assert.Contains("[NPDescription(\"Sweeps the area.\")]", text);
        Assert.Contains("[NPSlider(0f, 1f)]", text);
        Assert.Contains("protected override void OnExecute()", text);
        Assert.Contains("EndAction(true);", text);
    }

    [Fact]
    public void Scaffold_ConditionAndSanitize()
    {
        string text = NodePlayTaskWizard.Scaffold(
            NodePlayTaskWizard.BaseKind.Condition, "9lives!", "Custom", "");
        Assert.Contains("public sealed class Task9lives : NPConditionTask", text);
        Assert.Contains("protected override bool OnCheck()", text);
        Assert.DoesNotContain("[NPDescription", text);
    }
}

public sealed class NodePlayTypePrefsTests
{
    [Fact]
    public void Preferred_ToggleRoundTrip()
    {
        string fullName = typeof(BTStepIterator).FullName!;
        NodePlayBrowser.TogglePreferred(fullName);
        Assert.True(NodePlayBrowser.IsPreferred(fullName));
        NodePlayBrowser.TogglePreferred(fullName);
        Assert.False(NodePlayBrowser.IsPreferred(fullName));
    }
}
