// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using SpineRT = global::Spine;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class SpineGenAnimateTests
{
    private static (string Root, string SliceDir, ProjectDescriptor Project) PrepareBear()
    {
        string root = Path.Combine(Path.GetTempPath(), "xengine-spinegen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        File.WriteAllBytes(Path.Combine(root, "Assets", "bear.png"),
            PngRgba.Encode(SpineGenSliceTests.BuildBearRgba(out _),
                SpineGenSliceTests.W, SpineGenSliceTests.H));
        var project = new ProjectDescriptor("t", root, null, null, null, "fp");
        string sliceDir = new SpineGenSliceService(project)
            .Slice("Assets/bear.png", null, null, null, null, null, null, null, false).OutputDirectory;
        new SpineGenRigService(project).Rig(sliceDir);
        new SpineGenSkinService(project).Skin(sliceDir, null, null, null);
        return (root, sliceDir, project);
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuiltinBreath_PlaysThroughVendoredRuntime_LoopSafe()
    {
        (string root, string sliceDir, ProjectDescriptor project) = PrepareBear();
        try
        {
            SpineGenAnimateResult result = await new SpineGenAnimateService(project)
                .AnimateAsync(sliceDir, motion: "idle_breath", prompt: null, provider: null, model: null,
                    CancellationToken.None);
            Assert.Equal("idle_breath", result.Animation);
            Assert.Equal(0, result.RepairRounds);

            string skeletonJson = File.ReadAllText(Path.Combine(root,
                result.SkeletonJson.Replace('/', Path.DirectorySeparatorChar)));
            SpineRT.SkeletonData data = new SpineRT.SkeletonJson(new SpineGenRigTests.StubLoader())
                .ReadSkeletonData(new StringReader(skeletonJson));
            SpineRT.Animation anim = data.FindAnimation("idle_breath")!;
            Assert.Equal(2f, anim.Duration, 5);
            Assert.Equal(2, anim.Timelines.Count); // spine + head

            var skel = new SpineRT.Skeleton(data);
            float startRotation = SampleHead(anim, skel, 0f);
            float midRotation = SampleHead(anim, skel, 1f);
            float endRotation = SampleHead(anim, skel, 2f);

            Assert.Equal(startRotation, endRotation, 3); // loop closes
            Assert.True(MathF.Abs(midRotation - startRotation) > 0.5f, "head must actually move");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static float SampleHead(SpineRT.Animation anim, SpineRT.Skeleton skel, float time)
    {
        skel.SetupPose();
        anim.Apply(skel, 0f, time, loop: false, events: null, alpha: 1f,
            from: SpineRT.MixFrom.Setup, add: false, mixOut: false, appliedPose: false);
        skel.UpdateWorldTransform(SpineRT.Physics.Update);
        return skel.FindBone("head")!.Pose.Rotation;
    }

    [Fact]
    public void Validator_RejectsUnknownBonesRangesAndBrokenLoops()
    {
        var rig = new RigManifest { ImageWidth = 96, ImageHeight = 96 };
        rig.Bones.Add(new RigBoneInfo { Name = "root", ImageX = 48, ImageY = 80 });
        rig.Bones.Add(new RigBoneInfo { Name = "head", Parent = "root", ImageX = 48, ImageY = 30 });

        var bad = new MotionPlan
        {
            Animation = "x",
            Loop = true,
            Duration = 2f,
            Bones =
            [
                new MotionBoneTrack
                {
                    Bone = "ghost",
                    Rotate = [K(0, 0), K(1f, 500)],
                },
                new MotionBoneTrack
                {
                    Bone = "head",
                    Rotate = [K(1f, 0), K(0.5f, 10), K(2f, 5)],
                },
            ],
        };

        List<string> codes = MotionValidator.Validate(bad, rig).Select(v => v.Code).ToHashSet().ToList();
        Assert.Contains("unknown-bone", codes);
        Assert.Contains("value-range", codes);
        Assert.Contains("non-monotonic", codes);
        Assert.Contains("loop-mismatch", codes);

        MotionPlan good = FixToValid();
        Assert.Empty(MotionValidator.Validate(good, rig));
        SpineAnimation converted = MotionValidator.ToAnimation(good);
        Assert.NotNull(converted.Bone("head").Rotate);
        Assert.Equal(3, converted.Bone("head").Rotate!.Count);
    }

    private static MotionPlan FixToValid() => new()
    {
        Animation = "ok",
        Loop = true,
        Duration = 2f,
        Bones =
        [
            new MotionBoneTrack
            {
                Bone = "head",
                Rotate = [K(0, -2f), K(1f, 2f), K(2f, -2f)],
            },
        ],
    };

    private static MotionKey K(float t, float v) => new() { Time = t, Value = v };

    [Fact]
    public async Task MockLlmRoute_GoesThroughSchemaValidationAndAppends()
    {
        (string root, string sliceDir, ProjectDescriptor project) = PrepareBear();
        try
        {
            SpineGenAnimateResult result = await new SpineGenAnimateService(project)
                .AnimateAsync(sliceDir, motion: null, prompt: "gentle idle, keep it subtle",
                    provider: "mock", model: null, CancellationToken.None);

            Assert.StartsWith("llm:mock", result.Source);
            Assert.Equal(2, result.Bones.Count); // spine + head from the deterministic mock planner

            // Skeleton still carries the M5 meshes after the JSON round-trip append.
            SpineSkeletonDoc doc = SpineExporter.Read(File.ReadAllText(Path.Combine(root,
                result.SkeletonJson.Replace('/', Path.DirectorySeparatorChar))));
            Assert.NotNull(doc.Animations![result.Animation]);
            int meshCount = doc.Skins![0].Attachments.Values
                .SelectMany(v => v.Values).Count(a => a.Type == "mesh");
            Assert.Equal(4, meshCount);

            // Prompt builder embeds bones + schema hints (drives the real LLM round).
            var rig = JsonSerializer.Deserialize<RigManifest>(File.ReadAllText(Path.Combine(root,
                result.MotionManifest.Replace('/', Path.DirectorySeparatorChar)).Replace(
                    "motion_manifest.json", "rig_manifest.json")))!;
            string built = SpineGenAnimateService.BuildUserPrompt("test", rig, null);
            Assert.Contains("head", built);
            Assert.Contains("loop", built);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
