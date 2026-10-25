using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public class AnimationStateMachineOpsTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public AnimationStateMachineOpsTests(TestSceneFixture fx) => _fx = fx;

    static string ControllerPath(TestSceneFixture fx)
    {
        string dir = Path.Combine(fx.ProjectRoot, "Assets");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".controller");
        File.WriteAllText(path, Minimal);
        return path;
    }

    [Fact]
    public void Inspect_IncludesStateMachineTree()
    {
        var r = AnimationAssetOps.InspectController(Minimal, "Assets/Hero.controller");
        Assert.Contains(r.StateMachines, sm => sm.Name == "Base Layer" && sm.States.Count == 2);
    }

    [Fact]
    public async Task CreateMoveDelete_RoundTrip()
    {
        string path = ControllerPath(_fx);
        await _fx.SceneWriter.MutateAssetDataAsync(
            path, "Assets/Hero.controller",
            root => AnimationAssetOps.CreateStateMachine(root, "Combat", null, null, 200, 80),
            "sm", TestSceneFixture.Provenance("test.anim", "sm-1"), false);

        var afterCreate = AnimationAssetOps.InspectController(File.ReadAllText(path), "Assets/Hero.controller");
        AnimatorStateMachineInfo rootSm = Assert.Single(afterCreate.StateMachines);
        Assert.Contains(rootSm.StateMachines, c => c.Name == "Combat");

        await _fx.SceneWriter.MutateAssetDataAsync(
            path, "Assets/Hero.controller",
            root => AnimationAssetOps.MoveState(root, "Walk", "Combat", null, null),
            "mv", TestSceneFixture.Provenance("test.anim", "sm-2"), false);

        var afterMove = AnimationAssetOps.InspectController(File.ReadAllText(path), "Assets/Hero.controller");
        AnimatorStateMachineInfo movedRoot = Assert.Single(afterMove.StateMachines);
        Assert.DoesNotContain(movedRoot.States, s => s.Name == "Walk");
        Assert.Contains(Assert.Single(movedRoot.StateMachines).States, s => s.Name == "Walk");

        await _fx.SceneWriter.MutateAssetDataAsync(
            path, "Assets/Hero.controller",
            root => AnimationAssetOps.DeleteStateMachine(root, "Combat", null, null),
            "del", TestSceneFixture.Provenance("test.anim", "sm-3"), false);
        var afterDel = AnimationAssetOps.InspectController(File.ReadAllText(path), "Assets/Hero.controller");
        Assert.Empty(Assert.Single(afterDel.StateMachines).StateMachines);
    }

    [Fact]
    public void CapabilityCatalog_IncludesStateMachineTools()
    {
        var project = new ProjectDescriptor("p", _fx.ProjectRoot, null, null, null, "fp");
        var snap = new CapabilityCatalog(project).GetSnapshot();
        var create = Assert.Single(snap.Tools, t => t.Name == "animator_statemachine_create");
        Assert.Equal("animation", create.Group);
        Assert.Equal(RiskLevel.SafeWrite, create.RiskLevel);
        var move = Assert.Single(snap.Tools, t => t.Name == "animator_statemachine_move");
        Assert.Equal(RiskLevel.SafeWrite, move.RiskLevel);
    }

    const string Minimal = """
{
  "$type": "XEngine.Animation.AnimatorController, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "DefaultStateName": "Idle",
  "Name": "Hero",
  "Parameters": { "$values": [ { "Name": "Speed", "Type": 0, "DefaultFloat": 0, "DefaultInt": 0, "DefaultBool": false } ] },
  "States": {
    "$values": [
      { "Name": "Idle", "Tag": "", "Speed": 1F, "IsLooping": true, "PosX": 0, "PosY": 0,
        "Motion": { "AssetID": "00000000-0000-0000-0000-000000000000" },
        "TransitionNames": { "$values": [ "IdleToWalk" ] } },
      { "Name": "Walk", "Tag": "", "Speed": 1F, "IsLooping": true, "PosX": 200, "PosY": 0,
        "Motion": { "AssetID": "00000000-0000-0000-0000-000000000000" },
        "TransitionNames": { "$values": [] } }
    ]
  },
  "Transitions": {
    "$values": [
      { "Name": "IdleToWalk", "DestinationStateName": "Walk", "Duration": 0.1F, "Offset": 0F,
        "HasExitTime": false, "ExitTime": 0.9F, "Conditions": { "$values": [] } }
    ]
  }
}
""";
}
