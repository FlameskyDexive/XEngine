// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Transitions;
using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M48 — code-driven mixer transitions (Linear / Cartesian / Directional / Manual / authored-tree)
/// driven by <see cref="global::XEngine.Animation.AnimatorParameterStore"/>, including damped
/// targets and Echo round-trips of the inline child lists.
/// </summary>
public class MixerTransitionTests : RuntimeTestBase
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

    private static MixerChild Child(float posX, float threshold, float posX2d = 0f, string? direct = null)
        => new()
        {
            Clip = new AssetRef<AnimationClip>(AnimationTestHelpers.MakeConstantPosClip(
                $"c{posX}_{threshold}_{posX2d}_{direct}", "Hips", posX)),
            Threshold = threshold,
            Position = new Float2(threshold, posX2d),
            DirectParameterName = direct ?? "",
        };

    [Fact]
    public void LinearMixer_ParameterSelectsChild()
    {
        var (scene, root, animator) = BuildAnimator();
        var t = new LinearMixerTransition { ParameterName = "Speed", FadeDuration = 0f };
        t.Children.Add(Child(0f, 0f));
        t.Children.Add(Child(5f, 0.5f));
        t.Children.Add(Child(10f, 1f));

        animator.Play(t);
        animator.Parameters.SetFloat("Speed", 0.5f);
        Update(scene, frames: 2);

        Assert.Equal(5f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.Parameters.SetFloat("Speed", 0.25f); // Halfway between children 0 and 1.
        Update(scene, frames: 2);
        Assert.Equal(2.5f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.Parameters.SetFloat("Speed", 5f);    // Above the top threshold clamps to child 2.
        Update(scene, frames: 2);
        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void CartesianMixer_ParameterHitsChildPositions()
    {
        var (scene, root, animator) = BuildAnimator();
        var t = new CartesianMixerTransition { ParameterName = "X", ParameterNameY = "Y", FadeDuration = 0f };
        t.Children.Add(Child(0f, 0f, 0f));   // at (0, 0)
        t.Children.Add(Child(10f, 1f, 1f));  // at (1, 1)

        animator.Play(t);

        animator.Parameters.SetFloat("X", 0f);
        animator.Parameters.SetFloat("Y", 0f);
        Update(scene, frames: 2);
        Assert.Equal(0f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.Parameters.SetFloat("X", 1f);
        animator.Parameters.SetFloat("Y", 1f);
        Update(scene, frames: 2);
        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void DirectionalMixer_SelectsNearestDirection()
    {
        var (scene, root, animator) = BuildAnimator();
        var t = new DirectionalMixerTransition { ParameterName = "MX", ParameterNameY = "MY", FadeDuration = 0f };
        t.Children.Add(Child(0f, 1f, 0f));   // pointing +X
        t.Children.Add(Child(10f, 0f, 1f));  // pointing +Y

        animator.Play(t);
        animator.Parameters.SetFloat("MX", 1f);
        animator.Parameters.SetFloat("MY", 0f);
        Update(scene, frames: 2);

        Assert.Equal(0f, root.Children[0].Transform.LocalPosition.X, precision: 2);
    }

    [Fact]
    public void ManualMixer_DirectWeightsAreRawSum()
    {
        var (scene, root, animator) = BuildAnimator();
        var t = new ManualMixerTransition { FadeDuration = 0f };
        t.Children.Add(Child(2f, 0f, 0f, direct: "A"));
        t.Children.Add(Child(3f, 0f, 0f, direct: "B"));

        animator.Play(t);
        animator.Parameters.SetFloat("A", 1f);
        animator.Parameters.SetFloat("B", 0.5f);
        Update(scene, frames: 2);

        // Direct blend = raw weighted sum (not renormalized): 1*2 + 0.5*3 = 3.5.
        Assert.Equal(3.5f, root.Children[0].Transform.LocalPosition.X, precision: 2);
    }

    [Fact]
    public void SmoothedParameter_ConvergesOverTime()
    {
        var (scene, _, animator) = BuildAnimator();
        var t = new LinearMixerTransition { ParameterName = "Speed", FadeDuration = 0f };
        t.Children.Add(Child(0f, 0f));
        t.Children.Add(Child(10f, 1f));

        animator.Play(t);
        animator.Parameters.SetFloat("Speed", 0f);
        Update(scene, frames: 2);

        animator.Parameters.SetFloatTarget("Speed", 1f, dampSeconds: 0.25f);
        Update(scene, frames: 5);            // ~0.083s of damping.
        float first = animator.Parameters.GetFloat("Speed");
        Update(scene, frames: 5);            // ~0.083s more.

        float after = animator.Parameters.GetFloat("Speed");
        Assert.True(first > 0f && first < 0.5f, $"first sample should be mid-ramp, got {first}");
        Assert.True(after > first, $"damped value should increase ({first} → {after})");
        Assert.True(after < 1f, "should not snap to target instantly");

        Update(scene, frames: 210);          // 3.5s >> 9× time constant → within the 1e-4 settle epsilon.
        Assert.Equal(1f, animator.Parameters.GetFloat("Speed"), precision: 3);
        Assert.False(animator.Parameters.IsSmoothing(AnimationNameHash.Hash("Speed")));
    }

    [Fact]
    public void BlendTreeTransition_PlaysAuthoredTreeWithParameters()
    {
        var (scene, root, animator) = BuildAnimator();
        var tree = new BlendTree { Type = BlendType.OneD, ParameterName = "Speed" };
        tree.Children.Add(new BlendTree.Child
        { Clip = new AssetRef<AnimationClip>(AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f)), Threshold = 0f });
        tree.Children.Add(new BlendTree.Child
        { Clip = new AssetRef<AnimationClip>(AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 10f)), Threshold = 1f });

        var t = new BlendTreeTransition { Tree = new AssetRef<BlendTree>(tree), FadeDuration = 0f };
        animator.Play(t);

        animator.Parameters.SetFloat("Speed", 1f);
        Update(scene, frames: 2);
        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void MixerTransition_ReplayReusesStateAndRebindsParameters()
    {
        var (scene, _, animator) = BuildAnimator();
        var t = new LinearMixerTransition { ParameterName = "Speed", FadeDuration = 0f };
        t.Children.Add(Child(0f, 0f));
        t.Children.Add(Child(10f, 1f));

        var s1 = animator.Play(t);
        var s2 = animator.Play(t);

        Assert.Same(s1, s2);
        animator.Parameters.SetFloat("Speed", 1f);
        animator.Play(t);                    // Rebind must not duplicate the registration.
        Update(scene, frames: 1);            // Push happens during the frame's LateUpdate.
        Assert.Equal(1f, ((BlendTreePlayable)s2).ParameterValue, precision: 3);
    }

    [Fact]
    public void Echo_RoundTrip_LinearMixerKeepsChildren()
    {
        var t = new LinearMixerTransition
        {
            ParameterName = "Velocity",
            FadeDuration = 0.35f,
            FadeMode = global::XEngine.Playables.FadeMode.FixedSpeed,
        };
        t.Children.Add(Child(0f, 0f));
        t.Children.Add(Child(10f, 1f));

        EchoObject echo = Serializer.Serialize(typeof(object), t);
        var back = Serializer.Deserialize<AnimationTransition>(echo) as LinearMixerTransition;

        Assert.NotNull(back);
        Assert.Equal("Velocity", back!.ParameterName);
        Assert.Equal(0.35f, back.FadeDuration);
        Assert.Equal(2, back.Children.Count);
        Assert.Equal(1f, back.Children[1].Threshold);
        Assert.Equal(10f,
            back.Children[1].Clip.Res!.Bones[0].PosX!.Evaluate(0f), precision: 3);
    }
}

