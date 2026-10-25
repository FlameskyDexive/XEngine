using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public class AnimationAssetOpsTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public AnimationAssetOpsTests(TestSceneFixture fx) => _fx = fx;

    static string ControllerPath(TestSceneFixture fx)
    {
        string dir = Path.Combine(fx.ProjectRoot, "Assets");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".controller");
        File.WriteAllText(path, MinimalController);
        return path;
    }

    static string ClipPath(TestSceneFixture fx)
    {
        string dir = Path.Combine(fx.ProjectRoot, "Assets");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".anim");
        File.WriteAllText(path, MinimalClip);
        return path;
    }

    [Fact]
    public void InspectController_ReadsStatesTransitionsParametersAndBlendTree()
    {
        var r = AnimationAssetOps.InspectController(MinimalController, "Assets/Hero.controller");
        Assert.Equal("Hero", r.Name);
        Assert.Equal("Idle", r.DefaultStateName);
        Assert.Contains(r.States, s => s.Name == "Idle" && s.WriteDefaults
            && s.Behaviours != null && s.Behaviours.Contains("XEngine.Animation.EmptyStateMachineBehaviour"));
        Assert.Contains(r.States, s => s.Name == "Walk");
        Assert.Contains(r.Parameters, p => p.Name == "Speed" && p.Type == "Float");
        Assert.Contains(r.Transitions, t => t.Name == "IdleToWalk" && t.DestinationStateName == "Walk");
        Assert.Contains(r.BlendTrees, b => b.OwnerState == "Walk" && b.ParameterName == "Speed");
        Assert.False(string.IsNullOrEmpty(r.ContentHash));
    }

    [Fact]
    public void InspectClip_ReportsBindingsAndRange()
    {
        var r = AnimationAssetOps.InspectClip(MinimalClip, "Assets/Walk.anim");
        Assert.Equal(1f, r.Duration);
        Assert.Equal(30f, r.TicksPerSecond);
        Assert.Equal("Loop", r.Wrap);
        ClipBindingInfo hipsX = Assert.Single(r.Bindings, b => b.Path == "Hips" && b.Channel == "PosX");
        Assert.Equal(2, hipsX.KeyCount);
        Assert.Equal(0f, hipsX.MinValue);
        Assert.Equal(10f, hipsX.MaxValue);
        Assert.Empty(r.Events);
    }

    [Fact]
    public async Task StateCreatePatchDelete_IsJournaledAndIdempotent()
    {
        string path = ControllerPath(_fx);
        var created = await _fx.SceneWriter.MutateAssetDataAsync(
            path, "Assets/Hero.controller",
            root => AnimationAssetOps.CreateState(root, "Jump", null, null),
            "create Jump", TestSceneFixture.Provenance("test.anim", "st-create-1"), dryRun: false);
        Assert.False(created.DryRun);
        Assert.NotEqual(created.BeforeHash, created.AfterHash);

        var replay = await _fx.SceneWriter.MutateAssetDataAsync(
            path, "Assets/Hero.controller",
            root => AnimationAssetOps.CreateState(root, "Jump", null, null),
            "create Jump", TestSceneFixture.Provenance("test.anim", "st-create-1"), dryRun: false);
        Assert.True(
            replay.Summary.Contains("Idempotent", StringComparison.Ordinal)
            || replay.Summary.Contains("No change", StringComparison.Ordinal));

        var patched = await _fx.SceneWriter.MutateAssetDataAsync(
            path, "Assets/Hero.controller",
            root => AnimationAssetOps.PatchState(root, "Jump",
                new[] { new SceneFieldPatch("Speed", "1.5") }, null),
            "patch Jump", TestSceneFixture.Provenance("test.anim", "st-patch-1"), dryRun: false);
        var afterPatch = AnimationAssetOps.InspectController(File.ReadAllText(path), "Assets/Hero.controller");
        Assert.Equal(1.5f, Assert.Single(afterPatch.States, s => s.Name == "Jump").Speed);
        Assert.Equal(patched.AfterHash, afterPatch.ContentHash);

        await _fx.SceneWriter.MutateAssetDataAsync(
            path, "Assets/Hero.controller",
            root => AnimationAssetOps.DeleteState(root, "Jump", null),
            "delete Jump", TestSceneFixture.Provenance("test.anim", "st-del-1"), dryRun: false);
        var afterDel = AnimationAssetOps.InspectController(File.ReadAllText(path), "Assets/Hero.controller");
        Assert.DoesNotContain(afterDel.States, s => s.Name == "Jump");
    }

    [Fact]
    public async Task TransitionAndParameterAndLayer_RoundTrip()
    {
        string path = ControllerPath(_fx);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.controller",
            root => AnimationAssetOps.CreateTransition(root, "Idle", "Walk", "IdleToWalk2", null),
            "t", TestSceneFixture.Provenance("test.anim", "tr-1"), false);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.controller",
            root => AnimationAssetOps.AddParameter(root, "Jump", "Trigger"),
            "p", TestSceneFixture.Provenance("test.anim", "p-1"), false);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.controller",
            root => AnimationAssetOps.AddLayer(root, "Upper"),
            "l", TestSceneFixture.Provenance("test.anim", "l-1"), false);

        var inspect = AnimationAssetOps.InspectController(File.ReadAllText(path), "Assets/Hero.controller");
        Assert.Contains(inspect.Transitions, t => t.Name == "IdleToWalk2");
        Assert.Contains(inspect.Parameters, p => p.Name == "Jump" && p.Type == "Trigger");
        Assert.Contains(inspect.Layers, l => l.Name == "Upper");
    }

    [Fact]
    public async Task TransitionSoloMuteAndReorder()
    {
        string path = ControllerPath(_fx);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.controller",
            root => AnimationAssetOps.CreateTransition(root, "Idle", "Walk", "IdleToWalk2", null),
            "t2", TestSceneFixture.Provenance("test.anim", "tr-solo-1"), false);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.controller",
            root => AnimationAssetOps.PatchTransition(root, "IdleToWalk2",
                new[]
                {
                    new SceneFieldPatch("Solo", "true"),
                    new SceneFieldPatch("Mute", "false"),
                    new SceneFieldPatch("HasFixedDuration", "true"),
                    new SceneFieldPatch("InterruptionSource", "1"),
                    new SceneFieldPatch("OrderedInterruption", "false"),
                    new SceneFieldPatch("CanTransitionToSelf", "true"),
                }, null),
            "solo", TestSceneFixture.Provenance("test.anim", "tr-solo-2"), false);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.controller",
            root => AnimationAssetOps.ReorderStateTransitions(root, "Idle", new[] { "IdleToWalk2", "IdleToWalk" }, null),
            "ord", TestSceneFixture.Provenance("test.anim", "tr-solo-3"), false);

        var inspect = AnimationAssetOps.InspectController(File.ReadAllText(path), "Assets/Hero.controller");
        AnimatorTransitionInfo t2 = Assert.Single(inspect.Transitions, t => t.Name == "IdleToWalk2");
        Assert.True(t2.Solo);
        Assert.False(t2.Mute);
        Assert.True(t2.HasFixedDuration);
        Assert.Equal("CurrentState", t2.InterruptionSource);
        Assert.False(t2.OrderedInterruption);
        Assert.True(t2.CanTransitionToSelf);
        Assert.Equal(new[] { "IdleToWalk2", "IdleToWalk" },
            Assert.Single(inspect.States, s => s.Name == "Idle").TransitionNames);
    }

    [Fact]
    public async Task ClipCurvePatch_AddSetRemove()
    {
        string path = ClipPath(_fx);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchCurve(root, "Hips", "PosX", "add", 0.5f, 5f),
            "add", TestSceneFixture.Provenance("test.anim", "c-add"), false);
        var added = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Equal(3, Assert.Single(added.Bindings, b => b.Channel == "PosX").KeyCount);

        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchCurve(root, "Hips", "PosX", "set", 1f, 7f),
            "set", TestSceneFixture.Provenance("test.anim", "c-set"), false);
        var set = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Equal(7f, Assert.Single(set.Bindings, b => b.Channel == "PosX").MaxValue);

        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchCurve(root, "Hips", "PosX", "remove", 0.5f, null),
            "rm", TestSceneFixture.Provenance("test.anim", "c-rm"), false);
        var rm = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Equal(2, Assert.Single(rm.Bindings, b => b.Channel == "PosX").KeyCount);
    }

    [Fact]
    public void InspectClip_ReportsGenericBinding()
    {
        var r = AnimationAssetOps.InspectClip(GenericClip, "Assets/Lit.anim");
        ClipBindingInfo intensity = Assert.Single(r.Bindings, b => b.Property == "Intensity");
        Assert.Equal("", intensity.Path);
        Assert.Equal("XEngine.Runtime.DirectionalLight", intensity.TypeName);
        Assert.Equal("Intensity", intensity.Channel);
        Assert.Equal(2, intensity.KeyCount);
        Assert.Equal(1f, intensity.MinValue);
        Assert.Equal(5f, intensity.MaxValue);
    }

    [Fact]
    public async Task ClipCurvePatch_GenericTypeName_AddSetRemove()
    {
        string path = ClipPath(_fx);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchCurve(root, "", "Intensity", "add", 0f, 1f, "XEngine.Runtime.DirectionalLight"),
            "g-add", TestSceneFixture.Provenance("test.anim", "g-add"), false);
        var added = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        ClipBindingInfo g = Assert.Single(added.Bindings, b => b.TypeName == "XEngine.Runtime.DirectionalLight");
        Assert.Equal("Intensity", g.Property);
        Assert.Equal(1, g.KeyCount);

        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchCurve(root, "", "Intensity", "add", 1f, 5f, "XEngine.Runtime.DirectionalLight"),
            "g-add2", TestSceneFixture.Provenance("test.anim", "g-add2"), false);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchCurve(root, "", "Intensity", "set", 1f, 7f, "XEngine.Runtime.DirectionalLight"),
            "g-set", TestSceneFixture.Provenance("test.anim", "g-set"), false);
        var set = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Equal(7f, Assert.Single(set.Bindings, b => b.Property == "Intensity").MaxValue);

        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchCurve(root, "", "Intensity", "remove", 0f, null, "XEngine.Runtime.DirectionalLight"),
            "g-rm", TestSceneFixture.Provenance("test.anim", "g-rm"), false);
        var rm = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Equal(1, Assert.Single(rm.Bindings, b => b.Property == "Intensity").KeyCount);
    }

    [Fact]
    public async Task ClipPatch_TicksPerSecondAndSampleRateAlias()
    {
        string path = ClipPath(_fx);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchClip(root, new[] { new SceneFieldPatch("TicksPerSecond", "60") }),
            "sr", TestSceneFixture.Provenance("test.anim", "clip-sr"), false);
        var after = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Equal(60f, after.TicksPerSecond);

        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchClip(root, new[] { new SceneFieldPatch("SampleRate", "24") }),
            "sr2", TestSceneFixture.Provenance("test.anim", "clip-sr2"), false);
        var aliased = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Equal(24f, aliased.TicksPerSecond);
    }

    [Fact]
    public async Task ClipEvent_AddPatchRemove()
    {
        string path = ClipPath(_fx);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.AddClipEvent(root, 0.5f, "Footstep", 1.2f, 3, "heel", null),
            "ev-add", TestSceneFixture.Provenance("test.anim", "ev-add"), false);
        var added = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Contains(added.Events, s => s.StartsWith("Footstep@"));

        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.PatchClipEvent(root, 0,
                new[] { new SceneFieldPatch("FunctionName", "OnLand") }),
            "ev-patch", TestSceneFixture.Provenance("test.anim", "ev-patch"), false);
        var patched = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Contains(patched.Events, s => s.StartsWith("OnLand@"));

        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Walk.anim",
            root => AnimationAssetOps.RemoveClipEvent(root, 0),
            "ev-rm", TestSceneFixture.Provenance("test.anim", "ev-rm"), false);
        var rm = AnimationAssetOps.InspectClip(File.ReadAllText(path), "Assets/Walk.anim");
        Assert.Empty(rm.Events);
    }

    [Fact]
    public async Task BlendTreeInspectAndPatch_EmbeddedInstance()
    {
        string path = ControllerPath(_fx);
        var before = AnimationAssetOps.InspectBlendTree(File.ReadAllText(path), "Assets/Hero.controller", "Walk");
        Assert.Equal("OneD", before.Type);
        Assert.Equal(2, before.Children.Count);

        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.controller",
            root => AnimationAssetOps.PatchBlendTree(root, "Walk",
                new[] { new SceneFieldPatch("ParameterName", "Move") }),
            "bt", TestSceneFixture.Provenance("test.anim", "bt-1"), false);
        var after = AnimationAssetOps.InspectBlendTree(File.ReadAllText(path), "Assets/Hero.controller", "Walk");
        Assert.Equal("Move", after.ParameterName);
    }

    [Fact]
    public async Task Mutate_DryRun_DoesNotWrite()
    {
        string path = ControllerPath(_fx);
        string before = File.ReadAllText(path);
        var result = await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.controller",
            root => AnimationAssetOps.CreateState(root, "Dry", null, null),
            "dry", TestSceneFixture.Provenance("test.anim", "dry-1"), dryRun: true);
        Assert.True(result.DryRun);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void CapabilityCatalog_GroupsAnimationTools()
    {
        var project = new ProjectDescriptor("p", _fx.ProjectRoot, null, null, null, "fp");
        var snap = new CapabilityCatalog(project).GetSnapshot();
        var inspect = Assert.Single(snap.Tools, t => t.Name == "animator_inspect");
        Assert.Equal("animation", inspect.Group);
        Assert.Equal(RiskLevel.ReadOnly, inspect.RiskLevel);
        var create = Assert.Single(snap.Tools, t => t.Name == "animator_state_create");
        Assert.Equal("animation", create.Group);
        Assert.Equal(RiskLevel.SafeWrite, create.RiskLevel);
        Assert.True(create.SupportsDryRun);
        var add = Assert.Single(snap.Tools, t => t.Name == "animator_component_add");
        Assert.Equal("scene-write", add.Group);
        var validate = Assert.Single(snap.Tools, t => t.Name == "animator_validate");
        Assert.Equal("animation", validate.Group);
        Assert.Equal(RiskLevel.ReadOnly, validate.RiskLevel);
    }

    [Fact]
    public void ValidateController_Minimal_ReportsMissingMotionOnIdleNotWalk()
    {
        var r = AnimationAssetOps.ValidateController(MinimalController, "Assets/Hero.controller");
        Assert.Contains(r.Diagnostics, d => d.Code == "missing-motion" && d.State == "Idle");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "missing-motion" && d.State == "Walk");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "dead-transition");
        Assert.False(string.IsNullOrEmpty(r.ContentHash));
    }

    [Fact]
    public void ValidateController_ReportsUnreachableDeadMuteSoloMissingParamAndLayerWeight()
    {
        var r = AnimationAssetOps.ValidateController(DiagnosticController, "Assets/Diag.controller");
        Assert.Contains(r.Diagnostics, d => d.Code == "unreachable-state" && d.State == "Orphan");
        Assert.Contains(r.Diagnostics, d => d.Code == "dead-transition" && d.Transition == "Dead");
        Assert.Contains(r.Diagnostics, d => d.Code == "muted-transition" && d.Transition == "Muted");
        Assert.Contains(r.Diagnostics, d => d.Code == "solo-shadowed-transition" && d.Transition == "Shadowed");
        Assert.Contains(r.Diagnostics, d => d.Code == "missing-parameter" && d.Parameter == "Nope");
        Assert.Contains(r.Diagnostics, d => d.Code == "missing-parameter" && d.Parameter == "MissingSpeed");
        Assert.Contains(r.Diagnostics, d => d.Code == "layer-weight-zero" && d.Layer == "Upper");
        Assert.Contains(r.Diagnostics, d => d.Code == "missing-motion" && d.Layer == "Upper");
    }

    const string MinimalController = """
{
  "$type": "XEngine.Animation.AnimatorController, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "DefaultStateName": "Idle",
  "Name": "Hero",
  "Parameters": {
    "$values": [
      { "Name": "Speed", "Type": 0, "DefaultFloat": 0, "DefaultInt": 0, "DefaultBool": false }
    ]
  },
  "States": {
    "$values": [
      {
        "Name": "Idle",
        "Tag": "",
        "Speed": 1F,
        "IsLooping": true,
        "PosX": 0,
        "PosY": 0,
        "Motion": { "AssetID": "00000000-0000-0000-0000-000000000000" },
        "TransitionNames": { "$values": [ "IdleToWalk" ] },
        "Behaviours": { "$values": [ "XEngine.Animation.EmptyStateMachineBehaviour" ] }
      },
      {
        "Name": "Walk",
        "Tag": "",
        "Speed": 1F,
        "IsLooping": true,
        "PosX": 200,
        "PosY": 0,
        "Motion": { "AssetID": "00000000-0000-0000-0000-000000000000" },
        "TransitionNames": { "$values": [] },
        "BlendTree": {
          "AssetID": "00000000-0000-0000-0000-000000000000",
          "Instance": {
            "Type": 0,
            "ParameterName": "Speed",
            "Children": {
              "$values": [
                { "Clip": { "AssetID": "11111111-0000-0000-0000-000000000001" }, "Threshold": 0F, "Position": { "X": 0F, "Y": 0F } },
                { "Clip": { "AssetID": "22222222-0000-0000-0000-000000000002" }, "Threshold": 0.5F, "Position": { "X": 0F, "Y": 0F } }
              ]
            }
          }
        }
      }
    ]
  },
  "Transitions": {
    "$values": [
      {
        "Name": "IdleToWalk",
        "DestinationStateName": "Walk",
        "Duration": 0.1F,
        "Offset": 0F,
        "HasExitTime": false,
        "ExitTime": 0.9F,
        "Conditions": { "$values": [ { "Mode": 2, "ParamName": "Speed", "Threshold": 0.1F } ] }
      }
    ]
  },
  "AnyStateTransitions": { "$values": [] }
}
""";

    const string DiagnosticController = """
{
  "$type": "XEngine.Animation.AnimatorController, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "DefaultStateName": "Idle",
  "Name": "Diag",
  "Parameters": {
    "$values": [
      { "Name": "Speed", "Type": 0, "DefaultFloat": 0, "DefaultInt": 0, "DefaultBool": false }
    ]
  },
  "Layers": {
    "$values": [
      {
        "Name": "Upper",
        "Weight": 0,
        "DefaultStateName": "Idle",
        "Root": {
          "DefaultStateName": "Idle",
          "States": {
            "$values": [
              {
                "Name": "Idle",
                "Motion": { "AssetID": "00000000-0000-0000-0000-000000000000" },
                "TransitionNames": { "$values": [] }
              }
            ]
          }
        }
      }
    ]
  },
  "States": {
    "$values": [
      {
        "Name": "Idle",
        "SpeedMultiplierParameter": "MissingSpeed",
        "Motion": { "AssetID": "11111111-0000-0000-0000-000000000001" },
        "TransitionNames": { "$values": [ "Dead", "Muted", "Soloed", "Shadowed", "BadParam" ] }
      },
      {
        "Name": "Orphan",
        "Motion": { "AssetID": "11111111-0000-0000-0000-000000000001" },
        "TransitionNames": { "$values": [] }
      }
    ]
  },
  "Transitions": {
    "$values": [
      { "Name": "Dead", "DestinationStateName": "Idle", "HasExitTime": false, "Conditions": { "$values": [] } },
      { "Name": "Muted", "DestinationStateName": "Idle", "HasExitTime": true, "Mute": true, "Conditions": { "$values": [] } },
      { "Name": "Soloed", "DestinationStateName": "Idle", "HasExitTime": true, "Solo": true, "Conditions": { "$values": [] } },
      { "Name": "Shadowed", "DestinationStateName": "Idle", "HasExitTime": true, "Solo": false, "Conditions": { "$values": [] } },
      { "Name": "BadParam", "DestinationStateName": "Idle", "HasExitTime": true,
        "Conditions": { "$values": [ { "Mode": 2, "ParamName": "Nope", "Threshold": 0 } ] } }
    ]
  }
}
""";

    const string MinimalClip = """
{
  "$type": "XEngine.Runtime.AnimationClip, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "Name": "Walk",
  "Duration": 1,
  "TicksPerSecond": 30,
  "Wrap": 1,
  "Bones": [
    {
      "BoneName": "Hips",
      "PosX": {
        "Keys": [
          { "Position": 0, "Value": 0, "TangentIn": 0, "TangentOut": 0, "Continuity": 0 },
          { "Position": 1, "Value": 10, "TangentIn": 0, "TangentOut": 0, "Continuity": 0 }
        ]
      }
    }
  ]
}
""";

    const string GenericClip = """
{
  "$type": "XEngine.Runtime.AnimationClip, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "Name": "Lit",
  "Duration": 1,
  "TicksPerSecond": 30,
  "Wrap": 1,
  "Bones": [],
  "GenericCurves": [
    {
      "Path": "",
      "TypeName": "XEngine.Runtime.DirectionalLight",
      "Property": "Intensity",
      "Curve": {
        "Keys": [
          { "Position": 0, "Value": 1, "TangentIn": 0, "TangentOut": 0, "Continuity": 0 },
          { "Position": 1, "Value": 5, "TangentIn": 0, "TangentOut": 0, "Continuity": 0 }
        ]
      }
    }
  ]
}
""";

    const string MinimalOverride = """
{
  "$type": "XEngine.Animation.AnimatorOverrideController, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "Name": "HeroOverride",
  "Base": { "AssetID": "aaaaaaaa-0000-0000-0000-000000000001" },
  "Overrides": {
    "$values": [
      {
        "From": { "AssetID": "11111111-0000-0000-0000-000000000001" },
        "To": { "AssetID": "22222222-0000-0000-0000-000000000002" }
      }
    ]
  }
}
""";

    const string MinimalMask = """
{
  "$type": "XEngine.Animation.AvatarMask, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "Name": "Upper",
  "Entries": {
    "$values": [
      { "Path": "Hips", "Weight": 1 },
      { "Path": "Hips/Spine/Head", "Weight": 0 }
    ]
  },
  "HumanoidParts": { "$values": [ true, true, false, true, true, true, true, true, true ] }
}
""";

    [Fact]
    public void InspectOverrideController_ReadsBaseAndMappings()
    {
        var r = AnimationAssetOps.InspectOverrideController(MinimalOverride, "Assets/Hero.overridecontroller");
        Assert.Equal("HeroOverride", r.Name);
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", r.BaseGuid);
        Assert.Single(r.Overrides);
        Assert.Equal("11111111-0000-0000-0000-000000000001", r.Overrides[0].FromGuid);
        Assert.Equal("22222222-0000-0000-0000-000000000002", r.Overrides[0].ToGuid);
        Assert.False(string.IsNullOrEmpty(r.ContentHash));
    }

    [Fact]
    public async Task PatchOverrideController_UpdatesBaseGuid()
    {
        string dir = Path.Combine(_fx.ProjectRoot, "Assets");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".overridecontroller");
        File.WriteAllText(path, MinimalOverride);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Hero.overridecontroller",
            root => AnimationAssetOps.PatchOverrideController(root,
                new[] { new SceneFieldPatch("Base/AssetID", "bbbbbbbb-0000-0000-0000-00000000000b") }),
            "ov", TestSceneFixture.Provenance("test.anim", "ov-1"), false);
        var after = AnimationAssetOps.InspectOverrideController(File.ReadAllText(path), "Assets/Hero.overridecontroller");
        Assert.Equal("bbbbbbbb-0000-0000-0000-00000000000b", after.BaseGuid);
    }

    [Fact]
    public void InspectAvatarMask_ReadsBonesAndHumanoid()
    {
        var r = AnimationAssetOps.InspectAvatarMask(MinimalMask, "Assets/Upper.mask");
        Assert.Equal("Upper", r.Name);
        Assert.Equal(2, r.Entries.Count);
        Assert.Equal("Hips", r.Entries[0].Path);
        Assert.Equal(0f, r.Entries[1].Weight);
        Assert.Contains(r.HumanoidParts, p => p.Name == "Head" && !p.Included);
        Assert.Contains(r.HumanoidParts, p => p.Name == "Body" && p.Included);
    }

    [Fact]
    public async Task PatchAvatarMask_UpdatesBoneWeight()
    {
        string dir = Path.Combine(_fx.ProjectRoot, "Assets");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mask");
        File.WriteAllText(path, MinimalMask);
        await _fx.SceneWriter.MutateAssetDataAsync(path, "Assets/Upper.mask",
            root => AnimationAssetOps.PatchAvatarMask(root,
                new[] { new SceneFieldPatch("Entries/$values/1/Weight", "1") }),
            "mask", TestSceneFixture.Provenance("test.anim", "mask-1"), false);
        var after = AnimationAssetOps.InspectAvatarMask(File.ReadAllText(path), "Assets/Upper.mask");
        Assert.Equal(1f, after.Entries[1].Weight);
    }
}
