// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using SpineRT = global::Spine;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// Action-set acceptance: every built-in motion builds deterministically against a full biped
/// rig, loops close, Trusted plans bypass the LLM amplitude limits (combat swings ±150°), and —
/// the part that matters — the exported timelines actually DRIVE the vendored runtime (sampled
/// bone pose changes between keyframes).
/// </summary>
public sealed class SpineGenMotionTests
{
    /// <summary>A rig carrying every role bone the action set animates.</summary>
    private static RigManifest FullBipedRig() => new()
    {
        ImageWidth = 1152,
        ImageHeight = 2048,
        Bones =
        [
            new RigBoneInfo { Name = "root", ImageX = 282, ImageY = 1639 },
            new RigBoneInfo { Name = "hips", Parent = "root", ImageX = 282, ImageY = 1557 },
            new RigBoneInfo { Name = "spine", Parent = "hips", ImageX = 282, ImageY = 1493 },
            new RigBoneInfo { Name = "head", Parent = "spine", ImageX = 570, ImageY = 240 },
            new RigBoneInfo { Name = "arm_l", Parent = "spine", ImageX = 147, ImageY = 540 },
            new RigBoneInfo { Name = "forearm_l", Parent = "arm_l", ImageX = 120, ImageY = 820 },
            new RigBoneInfo { Name = "hand_l", Parent = "forearm_l", ImageX = 105, ImageY = 1030 },
            new RigBoneInfo { Name = "arm_r", Parent = "spine", ImageX = 946, ImageY = 600 },
            new RigBoneInfo { Name = "forearm_r", Parent = "arm_r", ImageX = 995, ImageY = 835 },
            new RigBoneInfo { Name = "hand_r", Parent = "forearm_r", ImageX = 1035, ImageY = 1045 },
            new RigBoneInfo { Name = "leg_l", Parent = "hips", ImageX = 240, ImageY = 1620 },
            new RigBoneInfo { Name = "shin_l", Parent = "leg_l", ImageX = 225, ImageY = 1810 },
            new RigBoneInfo { Name = "foot_l", Parent = "shin_l", ImageX = 205, ImageY = 1990 },
            new RigBoneInfo { Name = "leg_r", Parent = "hips", ImageX = 863, ImageY = 1320 },
            new RigBoneInfo { Name = "shin_r", Parent = "leg_r", ImageX = 875, ImageY = 1690 },
            new RigBoneInfo { Name = "foot_r", Parent = "shin_r", ImageX = 900, ImageY = 1975 },
        ],
    };

    public static IEnumerable<object[]> AllMotionNames() =>
        MotionLibrary.Names.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(AllMotionNames))]
    public void EveryMotion_Builds_AndIsValidates(string name)
    {
        RigManifest rig = FullBipedRig();

        MotionPlan plan = MotionLibrary.Build(name, rig);

        Assert.Equal(name, plan.Animation);
        Assert.True(plan.Trusted);
        Assert.NotEmpty(plan.Bones);
        Assert.All(plan.Bones, t =>
            Assert.True(rig.Bones.Any(b => b.Name == t.Bone), $"{name} animated unknown bone {t.Bone}"));
        Assert.Empty(MotionValidator.Validate(plan, rig));

        MotionPlan again = MotionLibrary.Build(name, rig);
        Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(again));
    }

    [Fact]
    public void ArticulatedMotions_DriveChildJoints()
    {
        RigManifest rig = FullBipedRig();

        MotionPlan walk = MotionLibrary.Build("walk", rig);
        Assert.Contains(walk.Bones, t => t.Bone == "forearm_l");
        Assert.Contains(walk.Bones, t => t.Bone == "forearm_r");
        Assert.Contains(walk.Bones, t => t.Bone == "shin_l");
        Assert.Contains(walk.Bones, t => t.Bone == "shin_r");

        MotionPlan slash = MotionLibrary.Build("atk_1", rig);
        Assert.Contains(slash.Bones, t => t.Bone == "forearm_r");
        Assert.Contains(slash.Bones, t => t.Bone == "hand_r");
    }

    [Fact]
    public void LoopMotions_CloseFirstToLast()
    {
        RigManifest rig = FullBipedRig();
        foreach (string name in new[] { "idle_breath", "walk", "run", "sway", "bob", "wave_arm" })
        {
            MotionPlan plan = MotionLibrary.Build(name, rig);
            Assert.True(plan.Loop, name);
            foreach (MotionBoneTrack track in plan.Bones)
                AssertClose(track.Rotate, name, track.Bone, plan.Duration);
        }
    }

    private static void AssertClose(List<MotionKey>? keys, string anim, string bone, float duration)
    {
        if (keys == null || keys.Count == 0)
            return;
        Assert.True(MathF.Abs(keys[0].Time) < 1e-3f, $"{anim}/{bone}: first key not at t=0");
        Assert.True(MathF.Abs(keys[^1].Time - duration) < 1e-2f, $"{anim}/{bone}: last key not at t=duration");
        Assert.True(MathF.Abs(keys[0].Value - keys[^1].Value) < 1e-3f,
            $"{anim}/{bone}: loop open ({keys[0].Value} vs {keys[^1].Value})");
    }

    [Fact]
    public void TrustedBypass_AmplitudeLimits_ButKeepsStructureChecks()
    {
        var rig = FullBipedRig();
        var trusted = new MotionPlan
        {
            Animation = "big",
            Trusted = true,
            Loop = false,
            Duration = 1f,
            Bones =
            {
                new MotionBoneTrack { Bone = "arm_r", Rotate = [new MotionKey { Time = 0, Value = 0 }, new MotionKey { Time = 1, Value = -150 }] },
            },
        };
        Assert.Empty(MotionValidator.Validate(trusted, rig));

        var untrusted = JsonSerializer.Deserialize<MotionPlan>(JsonSerializer.Serialize(trusted));
        Assert.NotNull(untrusted);
        Assert.False(untrusted!.Trusted); // JsonIgnore → LLM round-trips are never trusted
        var violations = MotionValidator.Validate(untrusted, rig);
        Assert.Contains(violations, v => v.Code == "value-range");
    }

    [Fact]
    public async Task AnimateService_AppendsMultipleActions_RuntimeSamplesThem()
    {
        string root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            File.WriteAllBytes(Path.Combine(root, "Assets", "bear.png"),
                PngRgba.Encode(SpineGenSliceTests.BuildBearRgba(out _),
                    SpineGenSliceTests.W, SpineGenSliceTests.H));
            var project = new ProjectDescriptor("t", root, null, null, null, "fp");
            string sliceDir = new SpineGenSliceService(project)
                .Slice("Assets/bear.png", null, null, null, null, null, null, null, false).OutputDirectory;
            new SpineGenRigService(project).Rig(sliceDir);
            new SpineGenSkinService(project).Skin(sliceDir, null, null, null);
            var animate = new SpineGenAnimateService(project);

            await animate.AnimateAsync(sliceDir, "walk", null, null, null, CancellationToken.None);
            await animate.AnimateAsync(sliceDir, "atk_1", null, null, null, CancellationToken.None);
            await animate.AnimateAsync(sliceDir, "death", null, null, null, CancellationToken.None);

            string absDir = Path.Combine(root, sliceDir.Replace('/', Path.DirectorySeparatorChar));
            string skeletonJson = File.ReadAllText(Path.Combine(absDir, "rig", "skeleton.json"));

            // The vendored runtime parses the export and the timelines actually drive bones.
            SpineRT.SkeletonData data = new SpineRT.SkeletonJson(new SpineGenRigTests.StubLoader())
                .ReadSkeletonData(new StringReader(skeletonJson));
            Assert.NotNull(data.FindAnimation("walk"));
            Assert.NotNull(data.FindAnimation("atk_1"));
            Assert.NotNull(data.FindAnimation("death"));

            var skel = new SpineRT.Skeleton(data);
            SpineRT.Bone? armR = skel.FindBone("arm_r");
            Assert.NotNull(armR);

            var atk = data.FindAnimation("atk_1")!;
            atk.Apply(skel, 0f, 0f, false, null, 1f, default, false, false, true);
            skel.UpdateWorldTransform(SpineRT.Physics.Update);
            float windupAngle = armR.AppliedPose.Rotation;

            atk.Apply(skel, 0f, 0.36f, false, null, 1f, default, false, false, true);
            skel.UpdateWorldTransform(SpineRT.Physics.Update);
            float strikeAngle = armR.AppliedPose.Rotation;

            Assert.True(MathF.Abs(strikeAngle - windupAngle) > 1f,
                $"slash must swing the right arm (windup {windupAngle:F1}° → strike {strikeAngle:F1}°)");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewTempProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "xengine-spinegen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
