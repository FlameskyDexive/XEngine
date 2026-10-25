// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Humanoid;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M61 — MatchTarget lands the body on its target at the target time, state parameter curves drive
/// their parameters while the state plays, and humanoid (muscle) clips contribute Hips TDoF root
/// motion with the carrier pinned (no double travel).
/// </summary>
public class MatchTargetTests : RuntimeTestBase
{
    private (Scene scene, GameObject root, Animator animator) BuildActor(bool humanoid = false, float legLength = 0.45f)
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Actor");
        scene.Add(root);
        var armature = new GameObject("Armature");
        armature.SetParent(root);

        var hips = new GameObject("Hips");
        hips.SetParent(armature);
        hips.Transform.LocalPosition = new Float3(0f, 1f, 0f);
        var spine = new GameObject("Spine");
        spine.SetParent(hips);
        spine.Transform.LocalPosition = Float3.Zero;
        var chest = new GameObject("Chest");
        chest.SetParent(spine);
        chest.Transform.LocalPosition = Float3.Zero;

        var lUpper = new GameObject("LeftUpperLeg");
        lUpper.SetParent(hips);
        lUpper.Transform.LocalPosition = new Float3(-0.1f, -legLength, 0f);
        var lLower = new GameObject("LeftLowerLeg");
        lLower.SetParent(lUpper);
        lLower.Transform.LocalPosition = new Float3(0f, -legLength, 0f);
        Node(lLower, "LeftFoot");

        var rUpper = new GameObject("RightUpperLeg");
        rUpper.SetParent(hips);
        rUpper.Transform.LocalPosition = new Float3(0.1f, -legLength, 0f);
        var rLower = new GameObject("RightLowerLeg");
        rLower.SetParent(rUpper);
        rLower.Transform.LocalPosition = new Float3(0f, -legLength, 0f);
        Node(rLower, "RightFoot");

        var shoulder = new GameObject("LeftShoulder");
        shoulder.SetParent(chest);
        shoulder.Transform.LocalPosition = Float3.Zero;
        var upperArm = new GameObject("LeftUpperArm");
        upperArm.SetParent(shoulder);
        upperArm.Transform.LocalPosition = new Float3(-0.3f, 0f, 0f);
        var lowerArm = new GameObject("LeftLowerArm");
        lowerArm.SetParent(upperArm);
        lowerArm.Transform.LocalPosition = new Float3(-0.4f, 0f, 0f);
        Node(lowerArm, "LeftHand");

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        if (humanoid)
            animator.Avatar = new AssetRef<Avatar>(Avatar.BuildAuto(root.Transform));
        return (scene, root, animator);
    }

    private static void Node(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.SetParent(parent);
        go.Transform.LocalPosition = Float3.Zero;
    }

    [Fact]
    public void MatchTarget_BodyLandsOnTargetAtTargetTime()
    {
        var (scene, root, animator) = BuildActor();
        var clip = AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 0f, duration: 2f);
        clip.Wrap = AnimationWrapMode.ClampForever;
        animator.Wrap = AnimationWrapMode.ClampForever; // States take the Animator wrap, not the clip's.
        animator.Play(clip);

        Float3 target = new(3f, 0.5f, -2f);
        animator.MatchTarget(target, Quaternion.Identity, mask: null, startNormalized: 0f, targetNormalized: 1f);

        Update(scene, frames: 121); // 2.017s ≥ target time (2s clip).

        Assert.True(Float3.Distance(root.Transform.Position, target) < 0.01f,
            $"body should land on target, at {root.Transform.Position}");
    }

    [Fact]
    public void MatchTarget_BlendsThroughWindow()
    {
        var (scene, root, animator) = BuildActor();
        var clip = AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 0f, duration: 2f);
        clip.Wrap = AnimationWrapMode.ClampForever;
        animator.Wrap = AnimationWrapMode.ClampForever; // States take the Animator wrap, not the clip's.
        animator.Play(clip);

        Float3 target = new(2f, 0f, 0f);
        animator.MatchTarget(target, Quaternion.Identity, mask: null, startNormalized: 0f, targetNormalized: 1f);

        Update(scene, frames: 60); // Halfway through the window: position ≈ half of (2,0,0).

        float x = root.Transform.Position.X;
        Assert.True(MathF.Abs(x - 1f) < 0.05f, $"mid-window X should be ~1, got {x}");
    }

    [Fact]
    public void ParameterCurve_DrivesParameterWhileInState()
    {
        var (scene, _, animator) = BuildActor();
        var controller = new AnimatorController { Name = "c", DefaultStateName = "Run" };
        controller.Parameters.Add(new AnimatorController.Parameter { Name = "Charge", Type = AnimatorParameterType.Float });
        var state = new AnimatorController.State
        {
            Name = "Run",
            Motion = new AssetRef<AnimationClip>(AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 0f, duration: 1f)),
            IsLooping = false,
        };
        state.Curves.Add(new AnimatorController.StateCurve
        {
            ParameterName = "Charge",
            Curve = AnimationTestHelpers.LinearCurve(0f, 1f, 1f),
        });
        controller.States.Add(state);
        animator.SetControllerAsset(controller);

        Update(scene, frames: 30); // Halfway: Charge ≈ 0.5.

        Assert.True(MathF.Abs(animator.GetFloat("Charge") - 0.5f) < 0.05f,
            $"Charge should track the curve (~0.5), got {animator.GetFloat("Charge")}");
    }

    [Fact]
    public void HumanoidClip_TDoFDrivesRootMotionWithCarrierPin()
    {
        var (scene, root, animator) = BuildActor(humanoid: true);

        // Author a humanoid clip: Hips walks forward in Z via TDoF, baked on THIS rig.
        var armature = root.Children[0];
        var clip = new AnimationClip { Name = "walky", Duration = 1f };
        clip.Wrap = AnimationWrapMode.Loop;
        var hips = new AnimationClip.AnimBone { BoneName = "Hips" };
        hips.RotX = AnimationTestHelpers.ConstantCurve(0f);
        hips.RotY = AnimationTestHelpers.ConstantCurve(0f);
        hips.RotZ = AnimationTestHelpers.ConstantCurve(0f);
        hips.RotW = AnimationTestHelpers.ConstantCurve(1f);
        hips.PosX = AnimationTestHelpers.ConstantCurve(0f);
        hips.PosY = AnimationTestHelpers.ConstantCurve(1f);
        hips.PosZ = AnimationTestHelpers.LinearCurve(0f, 1f, 1f); // 1m forward over the clip.
        clip.AddBone(hips);
        var actorAvatar = Avatar.BuildAuto(root.Transform); // Skeleton root = Actor (the Animator s root).
        HumanoidClipBaker.Bake(clip, actorAvatar);
        Assert.True(clip.IsHumanoid);

        animator.ApplyRootMotion = true;
        animator.Play(clip);

        Float3 startPos = root.Transform.Position;
        Update(scene, frames: 60); // One full clip → 1m forward.

        Float3 moved = root.Transform.Position - startPos;
        Assert.True(MathF.Abs(moved.Z - 1f) < 0.05f,
            $"root should travel 1m in Z via TDoF, moved {moved}");
        // Carrier pinned: the hips BONE stays at its authored local Z (no double travel).
        var hipsBone = armature.Children[0];
        Assert.True(MathF.Abs(hipsBone.Transform.LocalPosition.Z) < 0.05f,
            $"hips carrier should be pinned, local Z = {hipsBone.Transform.LocalPosition.Z}");
    }
}
