// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Humanoid;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M54 — muscle space math (T-pose-relative normalized angles, exact round-trip within human ranges)
/// and the humanoid clip baker (mapped bone curves → role-keyed muscle curves, unmapped bones kept).
/// </summary>
public class HumanoidMuscleSpaceTests : RuntimeTestBase
{
    private static readonly System.Random Rng = new(20260928);

    private static Quaternion RandomDelta(float maxDegrees)
    {
        Float3 euler = new(
            (float)(Rng.NextDouble() * 2 - 1) * maxDegrees,
            (float)(Rng.NextDouble() * 2 - 1) * maxDegrees,
            (float)(Rng.NextDouble() * 2 - 1) * maxDegrees);
        return Quaternion.FromEuler(euler);
    }

    [Fact]
    public void RotationMuscles_RoundTripRandomDeltas_IsExact()
    {
        HumanoidBone[] roles =
        {
            HumanoidBone.Hips, HumanoidBone.Spine, HumanoidBone.Head,
            HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm,
            HumanoidBone.RightUpperLeg, HumanoidBone.LeftFoot,
        };

        for (int i = 0; i < 500; i++)
        {
            HumanoidBone role = roles[i % roles.Length];
            Quaternion reference = RandomDelta(60f); // Arbitrary T-pose orientations.
            Quaternion animated = RandomDelta(80f) * reference;

            Float3 degrees = HumanoidMuscleSpace.RotationMuscles(reference, animated);
            Float3 muscles = HumanoidMuscleSpace.NormalizeAngles(role, degrees);
            Quaternion back = HumanoidMuscleSpace.ApplyRotationMuscles(role, reference, muscles);

            float angleDeg;
            Quaternion diff = back * Quaternion.Inverse(animated);
            angleDeg = MathF.Atan2(Float3.Length(new Float3(diff.X, diff.Y, diff.Z)), MathF.Abs(diff.W)) * 2f * Maths.Rad2Deg;
            Assert.True(angleDeg < 0.05f, $"round-trip drift {angleDeg}° on {role} (iter {i})");
        }
    }

    [Fact]
    public void TPose_YieldsZeroMuscles()
    {
        Quaternion reference = RandomDelta(75f);
        Float3 degrees = HumanoidMuscleSpace.RotationMuscles(reference, reference);

        Assert.Equal(0f, degrees.X, precision: 4);
        Assert.Equal(0f, degrees.Y, precision: 4);
        Assert.Equal(0f, degrees.Z, precision: 4);
    }

    [Fact]
    public void Limits_AllRolesPositive()
    {
        foreach (HumanoidBone bone in System.Enum.GetValues<HumanoidBone>())
        {
            HumanoidMuscleSpace.GetLimits(bone, out float x, out float y, out float z);
            Assert.True(x > 0f && y > 0f && z > 0f, $"non-positive limit on {bone}");
        }
    }

    // ---- baker ----

    private GameObject BuildRig()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        hips.Transform.LocalPosition = new Float3(0f, 1f, 0f);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);
        var chest = CreateGameObject("Chest");
        chest.SetParent(spine);
        var sword = CreateGameObject("SwordHolder");
        sword.SetParent(chest);
        Chain(chest, "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand");
        Chain(hips, "LeftUpperLeg", "LeftLowerLeg", "LeftFoot");
        Chain(hips, "RightUpperLeg", "RightLowerLeg", "RightFoot");
        return root;
    }

    private GameObject Chain(GameObject parent, params string[] names)
    {
        GameObject current = parent;
        for (int i = 0; i < names.Length; i++)
        {
            var next = CreateGameObject(names[i]);
            next.SetParent(current);
            current = next;
        }
        return current;
    }

    private static AnimationClip MakeHumanoidClip()
    {
        var clip = new AnimationClip { Name = "wave", Duration = 1f };
        clip.Wrap = AnimationWrapMode.Loop;

        // Quaternion components for the end euler angles: pure-axis rotations interpolate
        // component-wise exactly and stay inside the euler-safe range.
        Quaternion hipsEnd = Quaternion.FromEuler(30f, 0f, 0f);
        Quaternion armEnd = Quaternion.FromEuler(-45f, 0f, 0f);

        // Hips: rotation + translation curves (2 keys each).
        var hips = new AnimationClip.AnimBone { BoneName = "Hips" };
        hips.RotX = AnimationTestHelpers.LinearCurve(0f, hipsEnd.X, 1f);
        hips.RotY = AnimationTestHelpers.ConstantCurve(0f);
        hips.RotZ = AnimationTestHelpers.ConstantCurve(0f);
        hips.RotW = AnimationTestHelpers.LinearCurve(1f, hipsEnd.W, 1f);
        hips.PosX = AnimationTestHelpers.LinearCurve(0f, 0.5f, 1f);
        hips.PosY = AnimationTestHelpers.ConstantCurve(1f);
        hips.PosZ = AnimationTestHelpers.ConstantCurve(0f);
        clip.AddBone(hips);

        // LeftUpperArm: rotation only.
        var arm = new AnimationClip.AnimBone { BoneName = "LeftUpperArm" };
        arm.RotX = AnimationTestHelpers.LinearCurve(0f, armEnd.X, 1f);
        arm.RotY = AnimationTestHelpers.ConstantCurve(0f);
        arm.RotZ = AnimationTestHelpers.ConstantCurve(0f);
        arm.RotW = AnimationTestHelpers.LinearCurve(1f, armEnd.W, 1f);
        clip.AddBone(arm);

        // Unmapped accessory bone must survive the bake.
        var sword = new AnimationClip.AnimBone { BoneName = "SwordHolder" };
        sword.PosX = AnimationTestHelpers.LinearCurve(0f, 2f, 1f);
        sword.PosY = AnimationTestHelpers.ConstantCurve(0f);
        sword.PosZ = AnimationTestHelpers.ConstantCurve(0f);
        clip.AddBone(sword);
        return clip;
    }

    [Fact]
    public void Bake_ConvertsRoles_AndKeepsUnmappedBones()
    {
        var root = BuildRig();
        var avatar = Avatar.BuildAuto(root.Transform);
        var clip = MakeHumanoidClip();

        int channels = HumanoidClipBaker.Bake(clip, avatar);

        string mapped = "";
        for (int i = 0; i < avatar.Mappings.Count; i++)
            mapped += avatar.Mappings[i].Bone + "@" + avatar.Mappings[i].Path + "; ";
        string curves = "";
        for (int i = 0; i < clip.MuscleCurves.Count; i++)
            curves += clip.MuscleCurves[i].Bone + "#" + clip.MuscleCurves[i].Channel + "; ";
        Assert.True(channels >= 9, $"expected ≥9 channels (2 bones ×3 + hips TDoF ×3), got {channels}. mapped=[{mapped}] curves=[{curves}]");
        Assert.True(clip.IsHumanoid);
        Assert.Null(clip.GetBone("Hips"));
        Assert.Null(clip.GetBone("LeftUpperArm"));
        Assert.NotNull(clip.GetBone("SwordHolder")); // Accessory untouched.
        Assert.NotNull(clip.FindMuscleCurve(HumanoidBone.Hips, 0));
        Assert.NotNull(clip.FindMuscleCurve(HumanoidBone.Hips, 3)); // TDoF X.
        Assert.NotNull(clip.FindMuscleCurve(HumanoidBone.LeftUpperArm, 0));
        Assert.Null(clip.RootBonePath);
    }

    [Fact]
    public void Bake_ReplayReconstructsOriginalRotations()
    {
        var root = BuildRig();
        var avatar = Avatar.BuildAuto(root.Transform);
        var original = MakeHumanoidClip();

        // Snapshot the original poses before baking mutates the clip.
        var hipsBone = original.GetBone("Hips")!;
        var armBone = original.GetBone("LeftUpperArm")!;
        var sampleTimes = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
        var hipsPose = new Quaternion[sampleTimes.Length];
        var armPose = new Quaternion[sampleTimes.Length];
        var hipsPos = new Float3[sampleTimes.Length];
        for (int i = 0; i < sampleTimes.Length; i++)
        {
            hipsPose[i] = hipsBone.EvaluateRotationAt(sampleTimes[i]);
            armPose[i] = armBone.EvaluateRotationAt(sampleTimes[i]);
            hipsPos[i] = hipsBone.EvaluatePositionAt(sampleTimes[i]);
        }

        var armMapping = avatar.Mappings[avatar.IndexOf(HumanoidBone.LeftUpperArm)];
        var hipsMapping = avatar.Mappings[avatar.IndexOf(HumanoidBone.Hips)];
        HumanoidClipBaker.Bake(original, avatar);

        for (int i = 0; i < sampleTimes.Length; i++)
        {
            float t = sampleTimes[i];

            // Reconstruct arm rotation from muscles.
            Float3 muscles = new(
                original.FindMuscleCurve(HumanoidBone.LeftUpperArm, 0)!.Evaluate(t),
                original.FindMuscleCurve(HumanoidBone.LeftUpperArm, 1)!.Evaluate(t),
                original.FindMuscleCurve(HumanoidBone.LeftUpperArm, 2)!.Evaluate(t));
            Quaternion rebuilt = HumanoidMuscleSpace.ApplyRotationMuscles(
                HumanoidBone.LeftUpperArm, armMapping.LocalRotation, muscles);
            float drift = QuaternionAngle(rebuilt, armPose[i]);
            Assert.True(drift < 0.15f, $"arm drift {drift}° at t={t}");

            // Reconstruct hips rotation.
            Float3 hipsMuscles = new(
                original.FindMuscleCurve(HumanoidBone.Hips, 0)!.Evaluate(t),
                original.FindMuscleCurve(HumanoidBone.Hips, 1)!.Evaluate(t),
                original.FindMuscleCurve(HumanoidBone.Hips, 2)!.Evaluate(t));
            Quaternion hipsRebuilt = HumanoidMuscleSpace.ApplyRotationMuscles(
                HumanoidBone.Hips, hipsMapping.LocalRotation, hipsMuscles);
            Assert.True(QuaternionAngle(hipsRebuilt, hipsPose[i]) < 0.05f, $"hips rot drift at t={t}");

            // Reconstruct hips TDoF.
            Float3 tdof = new(
                original.FindMuscleCurve(HumanoidBone.Hips, 3)!.Evaluate(t),
                original.FindMuscleCurve(HumanoidBone.Hips, 4)!.Evaluate(t),
                original.FindMuscleCurve(HumanoidBone.Hips, 5)!.Evaluate(t));
            Float3 rebuiltPos = hipsMapping.LocalPosition + tdof;
            Assert.True(Float3.Length(rebuiltPos - hipsPos[i]) < 0.001f, $"hips TDoF drift at t={t}");
        }
    }

    private static float QuaternionAngle(Quaternion a, Quaternion b)
    {
        Quaternion diff = a * Quaternion.Inverse(b);
        return MathF.Atan2(Float3.Length(new Float3(diff.X, diff.Y, diff.Z)), MathF.Abs(diff.W)) * 2f * Maths.Rad2Deg;
    }
}


