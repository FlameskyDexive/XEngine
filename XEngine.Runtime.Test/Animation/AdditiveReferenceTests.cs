// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M51 — additive reference poses (layer delta = pose − reference, Mecanim semantics) and controller
/// layer weights driven by a parameter.
/// </summary>
public class AdditiveReferenceTests : RuntimeTestBase
{
    private (Scene scene, GameObject root, Animator animator) BuildAnimator()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        return (scene, root, animator);
    }

    private static AnimationClip ClipAt(string name, float posX)
        => AnimationTestHelpers.MakeConstantPosClip(name, "Hips", posX);

    [Fact]
    public void AdditiveLayer_WithoutReference_AddsAroundZero()
    {
        var (scene, root, animator) = BuildAnimator();
        animator.Play(ClipAt("base", 2f));                      // Base at x=2.

        var overlay = animator.AddLayer(null, AnimatorLayerBlendingMode.Additive);
        var additiveState = new AnimationClipPlayable(ClipAt("wave", 5f));
        additiveState.Bind(animator.Skeleton!);
        additiveState.Play();
        additiveState.Weight = 1f;
        overlay.AddInput(additiveState);

        Update(scene, frames: 2);                                // Historical: 2 + 5 = 7.

        Assert.Equal(7f, root.Children[0].Transform.LocalPosition.X, precision: 2);
    }

    [Fact]
    public void AdditiveLayer_WithReferencePose_ContributesDeltaOnly()
    {
        var (scene, root, animator) = BuildAnimator();
        animator.Play(ClipAt("base", 2f));                      // Base at x=2.

        // Reference clip holds x=4 at t0; the additive clip holds x=5 → delta = +1.
        var overlay = animator.AddLayer(
            null, AnimatorLayerBlendingMode.Additive,
            weight: 1f, additiveReferenceClip: ClipAt("stance", 4f));
        var additiveState = new AnimationClipPlayable(ClipAt("wave", 5f));
        additiveState.Bind(animator.Skeleton!);
        additiveState.Play();
        additiveState.Weight = 1f;
        overlay.AddInput(additiveState);

        Update(scene, frames: 2);                                // 2 + (5 − 4) = 3.

        Assert.Equal(3f, root.Children[0].Transform.LocalPosition.X, precision: 2);
    }

    [Fact]
    public void AdditiveReference_PartialWeight_ScalesDelta()
    {
        var (scene, root, animator) = BuildAnimator();
        animator.Play(ClipAt("base", 2f));

        var overlay = animator.AddLayer(
            null, AnimatorLayerBlendingMode.Additive,
            weight: 0.5f, additiveReferenceClip: ClipAt("stance", 4f));
        var additiveState = new AnimationClipPlayable(ClipAt("wave", 8f));
        additiveState.Bind(animator.Skeleton!);
        additiveState.Play();
        additiveState.Weight = 1f;
        overlay.AddInput(additiveState);

        Update(scene, frames: 2);                                // 2 + 0.5×(8 − 4) = 4.

        Assert.Equal(4f, root.Children[0].Transform.LocalPosition.X, precision: 2);
    }

    [Fact]
    public void LayerWeightParameter_DrivesCompiledLayerWeight()
    {
        var (scene, root, animator) = BuildAnimator();
        var controller = new AnimatorController { Name = "c", DefaultStateName = "Idle" };
        controller.Parameters.Add(new AnimatorController.Parameter { Name = "UpperBody", Type = AnimatorParameterType.Float });
        var layer0 = new AnimatorController.Layer { Name = "Base Layer" };
        layer0.States.Add(new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(ClipAt("idle", 1f)) });
        var layer1 = new AnimatorController.Layer
        {
            Name = "Overlay",
            Weight = 1f,
            WeightParameterName = "UpperBody",
            Blending = AnimatorLayerBlendingMode.Additive,
        };
        layer1.States.Add(new AnimatorController.State
        {
            Name = "Wave",
            Motion = new AssetRef<AnimationClip>(ClipAt("wave", 3f)),
            IsLooping = true,
        });
        controller.Layers.Add(layer0);
        controller.Layers.Add(layer1);

        animator.SetControllerAsset(controller);
        animator.SetFloat("UpperBody", 1f);                      // Full additive: 1 + 3 = 4.
        Update(scene, frames: 3);
        Assert.Equal(4f, root.Children[0].Transform.LocalPosition.X, precision: 2);

        animator.SetFloat("UpperBody", 0.25f);                   // 1 + 0.25×3 = 1.75.
        Update(scene, frames: 2);
        Assert.Equal(1.75f, root.Children[0].Transform.LocalPosition.X, precision: 2);
    }
}
