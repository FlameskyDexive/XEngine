// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>M35: WriteDefaults, Mirror, CycleOffset, synced layers, IKPass.</summary>
public class AnimatorControllerStateLayerFieldTests : RuntimeTestBase
{
    [Fact]
    public void MirrorNames_SwapsLeftRightTokens()
    {
        Assert.Equal("RightArm", AnimationMirrorNames.Mirror("LeftArm"));
        Assert.Equal("LeftArm", AnimationMirrorNames.Mirror("RightArm"));
        Assert.Equal("Hand_R", AnimationMirrorNames.Mirror("Hand_L"));
        Assert.Equal("Hips", AnimationMirrorNames.Mirror("Hips"));
    }

    [Fact]
    public void Compile_CopiesStateAndLayerFields()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f, duration: 1f);
        var author = new AnimatorController { DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Mul", Type = AnimatorParameterType.Float, DefaultFloat = 2f });
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "Base Layer", DefaultStateName = "Idle",
            States = { new AnimatorController.State
            {
                Name = "Idle", Motion = new AssetRef<AnimationClip>(idle),
                WriteDefaults = false, Mirror = true, CycleOffset = 0.25f, IKOnFeet = true,
                SpeedMultiplierParameter = "Mul",
            }},
        });
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "Upper", SyncedLayerIndex = 0, SyncedLayerAffectsTiming = true, IKPass = true,
            DefaultStateName = "Idle",
            States = { new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(idle) } },
        });
        var c = AnimatorControllerCompiler.Compile(author);
        Assert.False(c.States[0].WriteDefaults);
        Assert.True(c.States[0].Mirror);
        Assert.Equal(0.25f, c.States[0].CycleOffset);
        Assert.True(c.States[0].IKOnFeet);
        Assert.Equal(0, c.States[0].SpeedMultiplierParameterIndex);
        Assert.Equal(0, c.Layers[1].SyncedLayerIndex);
        Assert.True(c.Layers[1].SyncedLayerAffectsTiming);
        Assert.True(c.Layers[1].IKPass);
    }

    [Fact]
    public void CycleOffset_StartsNormalizedTimeAtOffset()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f, duration: 1f);
        var author = new AnimatorController { DefaultStateName = "Idle" };
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle", Motion = new AssetRef<AnimationClip>(idle), CycleOffset = 0.5f,
        });
        var c = AnimatorControllerCompiler.Compile(author);
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var runtime = animator.SetController(c);
        Update(scene, frames: 1);
        Assert.InRange(runtime.CurrentNormalizedTime, 0.45f, 0.7f);
    }
}
