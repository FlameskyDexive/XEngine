// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Humanoid;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M58 — full-body IK (puppet): the body (hips) pins toward its target, unreachable limb goals drag
/// the whole body toward them (scaled by the body weight), and the body orientation distributes
/// down the spine chain.
/// </summary>
public class FullBodyIKTests : RuntimeTestBase
{
    private (Scene scene, GameObject root, Animator animator) BuildHumanoid()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        scene.Add(root);

        var hips = Node(root, "Hips");
        hips.Transform.LocalPosition = new Float3(0f, 1f, 0f);
        var spine = Node(hips, "Spine");
        spine.Transform.LocalPosition = Float3.Zero;
        var chest = Node(spine, "Chest");
        chest.Transform.LocalPosition = Float3.Zero;
        Node(Node(chest, "Neck"), "Head");

        var lShoulder = Node(chest, "LeftShoulder");
        lShoulder.Transform.LocalPosition = Float3.Zero;
        var lUpperArm = Node(lShoulder, "LeftUpperArm");
        lUpperArm.Transform.LocalPosition = new Float3(-0.3f, 0f, 0f);
        var lLowerArm = Node(lUpperArm, "LeftLowerArm");
        lLowerArm.Transform.LocalPosition = new Float3(-0.4f, 0f, 0f);
        Node(lLowerArm, "LeftHand").Transform.LocalPosition = new Float3(-0.35f, 0f, 0f);

        var rShoulder = Node(chest, "RightShoulder");
        rShoulder.Transform.LocalPosition = Float3.Zero;
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
        return (scene, root, animator);
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
            current = next ?? throw new Xunit.Sdk.XunitException($"missing {part}");
        }
        return current;
    }

    [Fact]
    public void BodyPosition_PinsHipsAtTarget()
    {
        var (scene, root, animator) = BuildHumanoid();
        GameObject hips = Find(root, "Hips");

        animator.OnAnimatorIK += _ =>
        {
            animator.SetBodyPosition(new Float3(0.2f, 1.4f, -0.1f));
            animator.SetBodyPositionWeight(1f);
        };

        Update(scene, frames: 1);

        Float3 after = hips.Transform.Position;
        Assert.True(Float3.Distance(after, new Float3(0.2f, 1.4f, -0.1f)) < 0.01f,
            $"hips pinned at {after}");
    }

    [Fact]
    public void PuppetPull_UnreachableHandDragsBody()
    {
        var (scene, root, animator) = BuildHumanoid();
        GameObject hips = Find(root, "Hips");
        GameObject hand = Find(root, "Hips/Spine/Chest/LeftShoulder/LeftUpperArm/LeftLowerArm/LeftHand");
        Float3 hipsRest = hips.Transform.Position;
        Float3 handRest = hand.Transform.Position;

        // Far beyond the arm's 0.75 reach: the body must follow the pull.
        Float3 target = handRest + new Float3(-1.5f, 0f, 0f);

        animator.OnAnimatorIK += _ =>
        {
            animator.SetIKPosition(AvatarIKGoal.LeftHand, target);
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
            animator.SetBodyPositionWeight(1f); // Body follows unreachable pulls.
            animator.SetBodyPosition(hipsRest); // ...while pinning it against the direct target.
        };

        Update(scene, frames: 1);

        Float3 handAfter = hand.Transform.Position;
        // The arm straightened fully toward the target and the body moved with it.
        float restDistance = Float3.Distance(handRest, target);
        float afterDistance = Float3.Distance(handAfter, target);
        Assert.True(afterDistance < restDistance - 0.2f,
            $"hand should close on target ({restDistance} → {afterDistance})");
    }

    [Fact]
    public void BodyRotation_DistributesDownSpine()
    {
        var (scene, root, animator) = BuildHumanoid();
        GameObject hips = Find(root, "Hips");
        GameObject chest = Find(root, "Hips/Spine/Chest");

        Quaternion spin = Quaternion.FromEuler(0f, 60f, 0f);
        animator.OnAnimatorIK += _ =>
        {
            animator.SetBodyRotation(spin);
            animator.SetBodyRotationWeight(1f);
        };

        Update(scene, frames: 1);

        // The hips took a share of the rotation and the chest accumulated the rest.
        Quaternion hipsDelta = hips.Transform.Rotation;
        Quaternion chestDelta = chest.Transform.Rotation;
        float hipsAngle = AngleOf(hipsDelta);
        float chestAngle = AngleOf(chestDelta);
        Assert.True(hipsAngle > 10f && hipsAngle < 45f, $"hips share {hipsAngle}°");
        Assert.True(chestAngle > hipsAngle, $"chest should accumulate ({chestAngle}° vs {hipsAngle}°)");
    }

    private static float AngleOf(Quaternion q)
    {
        return MathF.Atan2(
            Float3.Length(new Float3(q.X, q.Y, q.Z)), MathF.Abs(q.W)) * 2f * Maths.Rad2Deg;
    }
}
