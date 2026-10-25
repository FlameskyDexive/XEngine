// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Humanoid;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M57 — the IK goal API: OnAnimatorIK fires after the pose applies (handlers see the settled pose),
/// hand/foot goals solve through the avatar's two-bone chains on the live Transforms, weights blend,
/// look-at aims the head, and FootIK plants the lower foot via a pluggable ground sampler.
/// </summary>
public class AnimatorIKTests : RuntimeTestBase
{
    private (Scene scene, GameObject root, Animator animator, SkeletonBinding binding) BuildHumanoid()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        scene.Add(root);

        var hips = Node(root, "Hips");
        hips.Transform.LocalPosition = new Float3(0f, 1f, 0f);
        var spine = Node(hips, "Spine");
        var chest = Node(spine, "Chest");
        Node(Node(chest, "Neck"), "Head");

        var lShoulder = Node(chest, "LeftShoulder");
        var lUpperArm = Node(lShoulder, "LeftUpperArm");
        lUpperArm.Transform.LocalPosition = new Float3(-0.3f, 0f, 0f);
        var lLowerArm = Node(lUpperArm, "LeftLowerArm");
        lLowerArm.Transform.LocalPosition = new Float3(-0.4f, 0f, 0f);
        var lHand = Node(lLowerArm, "LeftHand");
        lHand.Transform.LocalPosition = new Float3(-0.35f, 0f, 0f);

        var rShoulder = Node(chest, "RightShoulder");
        var rUpperArm = Node(rShoulder, "RightUpperArm");
        rUpperArm.Transform.LocalPosition = new Float3(0.3f, 0f, 0f);
        var rLowerArm = Node(rUpperArm, "RightLowerArm");
        rLowerArm.Transform.LocalPosition = new Float3(0.4f, 0f, 0f);
        Node(rLowerArm, "RightHand").Transform.LocalPosition = new Float3(0.35f, 0f, 0f);

        var lUpperLeg = Node(hips, "LeftUpperLeg");
        lUpperLeg.Transform.LocalPosition = new Float3(-0.1f, -0.5f, 0f);
        var lLowerLeg = Node(lUpperLeg, "LeftLowerLeg");
        lLowerLeg.Transform.LocalPosition = new Float3(0f, -0.45f, 0f);
        Node(lLowerLeg, "LeftFoot").Transform.LocalPosition = new Float3(0f, -0.45f, 0f);

        var rUpperLeg = Node(hips, "RightUpperLeg");
        rUpperLeg.Transform.LocalPosition = new Float3(0.1f, -0.5f, 0f);
        var rLowerLeg = Node(rUpperLeg, "RightLowerLeg");
        rLowerLeg.Transform.LocalPosition = new Float3(0f, -0.45f, 0f);
        Node(rLowerLeg, "RightFoot").Transform.LocalPosition = new Float3(0f, -0.45f, 0f);

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.Avatar = new AssetRef<Avatar>(Avatar.BuildAuto(root.Transform));
        return (scene, root, animator, animator.Skeleton!);
    }

    private static GameObject Node(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.SetParent(parent);
        return go;
    }

    private static GameObject Find(GameObject root, string path)
    {
        GameObject current = root;
        foreach (string part in path.Split('/'))
        {
            int count = current.Transform.ChildCount;
            GameObject? next = null;
            for (int i = 0; i < count; i++)
            {
                GameObject child = current.Transform.GetChild(i).GameObject;
                if (child.Name == part)
                {
                    next = child;
                    break;
                }
            }
            current = next ?? throw new Xunit.Sdk.XunitException($"missing {part} under {current.Name}");
        }
        return current;
    }

    [Fact]
    public void IKGoal_HandReachesTarget()
    {
        var (scene, root, animator, _) = BuildHumanoid();

        Float3 target = new(-0.5f, 0.15f, 0.1f); // Within the 0.75 arm reach.
        animator.OnAnimatorIK += _ =>
        {
            animator.SetIKPosition(AvatarIKGoal.LeftHand, target);
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
        };

        Update(scene, frames: 2);

        Float3 hand = Find(root, "Hips/Spine/Chest/LeftShoulder/LeftUpperArm/LeftLowerArm/LeftHand").Transform.Position;
        float drift = Float3.Distance(hand, target);
        Assert.True(drift < 0.02f, $"hand drifted {drift}");
    }

    [Fact]
    public void OnAnimatorIK_FiresAfterPoseApplied()
    {
        var (scene, root, animator, _) = BuildHumanoid();
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("sway", "Hips/Spine", endX: 1f);
        animator.Play(clip);

        Float3? spineWhenFired = null;
        animator.OnAnimatorIK += _ => spineWhenFired = Find(root, "Hips/Spine").Transform.LocalPosition;

        Update(scene, frames: 31); // 0.517s → spine X ≈ 0.517 applied before the callback.

        Assert.NotNull(spineWhenFired);
        Assert.True(MathF.Abs(spineWhenFired!.Value.X - 0.517f) < 0.02f,
            $"handler should see the applied pose (~0.517), got {spineWhenFired}");
    }

    [Fact]
    public void IKGoal_PartialWeight_Blends()
    {
        var (scene, root, animator, _) = BuildHumanoid();

        GameObject hand = Find(root, "Hips/Spine/Chest/LeftShoulder/LeftUpperArm/LeftLowerArm/LeftHand");
        Float3 rest = hand.Transform.Position;
        Float3 target = rest + new Float3(0f, 0.3f, 0f);

        animator.OnAnimatorIK += _ =>
        {
            animator.SetIKPosition(AvatarIKGoal.LeftHand, target);
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0.5f);
        };

        Update(scene, frames: 1); // One frame: the blend lerps from the pre-IK (rest) pose.

        Float3 after = hand.Transform.Position;
        Float3 halfway = rest + (target - rest) * 0.5f;
        Assert.True(Float3.Distance(after, halfway) < 0.03f,
            $"halfway blend: {after} vs {halfway}");
    }

    [Fact]
    public void IKGoal_Rotation_BlendsEndBone()
    {
        var (scene, root, animator, _) = BuildHumanoid();

        GameObject hand = Find(root, "Hips/Spine/Chest/LeftShoulder/LeftUpperArm/LeftLowerArm/LeftHand");
        Quaternion spin = Quaternion.FromEuler(0f, 90f, 0f);
        animator.OnAnimatorIK += _ =>
        {
            animator.SetIKRotation(AvatarIKGoal.LeftHand, spin);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1f);
        };

        Update(scene, frames: 2);

        Quaternion diff = hand.Transform.Rotation * Quaternion.Inverse(spin);
        float angle = MathF.Atan2(
            Float3.Length(new Float3(diff.X, diff.Y, diff.Z)), MathF.Abs(diff.W)) * 2f * Maths.Rad2Deg;
        Assert.True(angle < 5f, $"hand rotation drifted {angle}°");
    }

    [Fact]
    public void LookAt_AimsHeadAtTarget()
    {
        var (scene, root, animator, _) = BuildHumanoid();

        GameObject head = Find(root, "Hips/Spine/Chest/Neck/Head");
        Float3 target = head.Transform.Position + new Float3(3f, 0f, 0f); // Look +X.

        animator.OnAnimatorIK += _ =>
        {
            animator.SetLookAtPosition(target);
            animator.SetLookAtWeight(1f);
        };

        Update(scene, frames: 2);

        Float3 forward = head.Transform.Rotation * Float3.UnitZ;
        Float3 desired = Float3.Normalize(target - head.Transform.Position);
        float angle = MathF.Acos(Maths.Clamp(Float3.Dot(forward, desired), -1f, 1f)) * Maths.Rad2Deg;
        Assert.True(angle < 12f, $"look-at residual {angle}°");
    }

    [Fact]
    public void FootIK_PlantsLowerFoot()
    {
        var (scene, root, animator, _) = BuildHumanoid();

        // Ground at y = 0.05 while the rest rig's feet sit near y = -0.4 (below the hips at y = 1):
        // the sampler reports ground ABOVE a probing foot only when probed below it, so model a
        // ledge under the left foot only.
        animator.GroundSample = probe => probe.X < 0f ? new Float3(probe.X, 0.05f, probe.Z) : null;
        animator.FootIK = true;

        GameObject hips = Find(root, "Hips");
        GameObject leftFoot = Find(root, "Hips/LeftUpperLeg/LeftLowerLeg/LeftFoot");
        float footRestY = leftFoot.Transform.Position.Y;
        float hipsRestY = hips.Transform.Position.Y;

        Update(scene, frames: 2);

        // The left foot ends planted at the ground; the hips ROSE by the penetration amount.
        Assert.True(MathF.Abs(leftFoot.Transform.Position.Y - 0.05f) < 0.01f,
            $"left foot should plant at 0.05, got {leftFoot.Transform.Position.Y}");
        float rise = hips.Transform.Position.Y - hipsRestY;
        float expectedRise = 0.05f - footRestY;
        Assert.True(MathF.Abs(rise - expectedRise) < 0.01f,
            $"hips should rise {expectedRise}, rose {rise}");
    }
}
