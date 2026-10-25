// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using SpineRT = global::Spine;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>M9 quality gates: alpha-island provenance, anatomical skeleton inference, held-prop
/// placement and actual multi-bone mesh weights (parsed and deformed by vendored spine-csharp).</summary>
public sealed class SpineGenQualityTests
{
    [Fact]
    public void Rig_UsesWholeBodyInsteadOfLargestShard_AndSnapsPropAssemblyToHand()
    {
        var slice = new SliceManifest { Source = "mech", ImageWidth = 400, ImageHeight = 400 };
        Add("chest", 1, 70, 60, 120, 120, 10_000);
        Add("head", 1, 100, 10, 60, 50, 2_500);
        Add("shoulder_l", 1, 20, 50, 55, 110, 4_500);
        Add("shoulder_r", 1, 185, 50, 55, 110, 4_500);
        Add("upper_leg_guard_r", 1, 170, 125, 45, 105, 3_800);
        Add("fist_r_outer", 1, 230, 205, 35, 45, 2_000);
        Add("hips_plate", 1, 80, 180, 100, 60, 4_000);
        Add("thigh_l", 1, 35, 210, 70, 150, 12_000); // deliberately largest: not the torso.
        Add("thigh_r", 1, 155, 210, 70, 150, 11_000);
        Add("sword_hilt", 2, 315, 80, 10, 60, 500);
        Add("sword_blade", 3, 312, 143, 16, 140, 1_200); // close to hilt, separate alpha island.

        (_, RigManifest rig) = Rigger.Build(slice);

        Assert.Equal([1], rig.BodyIslands);
        RigBoneInfo root = rig.Bones.Single(b => b.Name == "root");
        RigBoneInfo hips = rig.Bones.Single(b => b.Name == "hips");
        RigBoneInfo spine = rig.Bones.Single(b => b.Name == "spine");
        RigBoneInfo handR = rig.Bones.Single(b => b.Name == "hand_r");
        Assert.Equal(360f, root.ImageY, 2);
        Assert.True(spine.ImageY < hips.ImageY, $"spine={spine.ImageY}, hips={hips.ImageY}");
        Assert.Equal(RigRole.Torso, rig.Parts.Single(p => p.Part == "chest").Role);
        Assert.Equal(RigRole.LegLeft, rig.Parts.Single(p => p.Part == "thigh_l").Role);
        Assert.Equal(RigRole.LegRight, rig.Parts.Single(p => p.Part == "upper_leg_guard_r").Role);
        Assert.True(handR.ImageX > 240f, $"right-hand anchor should follow the outer fist, x={handR.ImageX}");

        List<RigPartBinding> props = [.. rig.Parts.Where(p => p.Role == RigRole.Prop)];
        Assert.Equal(2, props.Count);
        Assert.Single(props.Select(p => p.DeformBone).Distinct());
        Assert.All(props, p =>
        {
            Assert.True(p.OffsetX < -10f, $"{p.Part} offsetX={p.OffsetX}");
            Assert.True(p.OffsetY > 0f, $"{p.Part} offsetY={p.OffsetY}");
        });
        RigBoneInfo prop = rig.Bones.Single(b => b.Name == props[0].DeformBone);
        Assert.Equal("hand_r", prop.Parent);

        void Add(string name, int island, int x, int y, int w, int h, int pixels) =>
            slice.Parts.Add(new SlicePartEntry
            {
                Name = name,
                SourceIsland = island,
                X = x,
                Y = y,
                Width = w,
                Height = h,
                PixelCount = pixels,
                Layer = slice.Parts.Count,
            });
    }

    [Fact]
    public void WatershedParts_RetainTheirOriginalAlphaIsland()
    {
        const int size = 128;
        var rgba = new byte[size * size * 4];
        Fill(10, 10, 50, 50);
        Fill(40, 40, 50, 50); // one touching body island, split by watershed.
        Fill(110, 20, 8, 35); // detached prop island.

        SliceRunResult run = Slicer.Slice(rgba, size, size, new SliceOptions
        {
            Split = SliceSplitMode.DistanceWatershed,
            MinAreaPixels = 24,
        });

        IGrouping<int, SlicePartImage> body = run.Parts.GroupBy(p => p.SourceIsland)
            .OrderByDescending(g => g.Sum(p => p.PixelCount)).First();
        Assert.True(body.Count() >= 2, "the touching slabs should be watershed sub-parts");
        Assert.Equal(2, run.Parts.Select(p => p.SourceIsland).Distinct().Count());

        void Fill(int x0, int y0, int w, int h)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                {
                    int i = (y * size + x) * 4;
                    rgba[i] = 100;
                    rgba[i + 1] = 140;
                    rgba[i + 2] = 180;
                    rgba[i + 3] = 255;
                }
        }
    }

    [Fact]
    public void SkinService_WritesStandardWeightedVertices_AndRuntimeDeformsAcrossJoint()
    {
        string root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            File.WriteAllBytes(Path.Combine(root, "Assets", "bear.png"),
                PngRgba.Encode(SpineGenSliceTests.BuildBearRgba(out _),
                    SpineGenSliceTests.W, SpineGenSliceTests.H));
            var project = new ProjectDescriptor("m9", root, null, null, null, "fp");
            string sliceDir = new SpineGenSliceService(project)
                .Slice("Assets/bear.png", null, null, null, null, null, null, null, false)
                .OutputDirectory;
            new SpineGenRigService(project).Rig(sliceDir);
            new SpineGenSkinService(project).Skin(sliceDir, null, null, null);

            string absDir = Path.Combine(root, sliceDir.Replace('/', Path.DirectorySeparatorChar));
            RigManifest rig = JsonSerializer.Deserialize<RigManifest>(
                File.ReadAllText(Path.Combine(absDir, "rig", "rig_manifest.json")))!;
            string skeletonJson = File.ReadAllText(Path.Combine(absDir, "rig", "skeleton.json"));
            using (JsonDocument json = JsonDocument.Parse(skeletonJson))
            {
                JsonElement firstMesh = json.RootElement.GetProperty("skins")[0]
                    .GetProperty("attachments").EnumerateObject().First().Value
                    .EnumerateObject().First().Value;
                Assert.Equal(JsonValueKind.Array, firstMesh.GetProperty("vertices").ValueKind);
            }

            SpineRT.SkeletonData data = new SpineRT.SkeletonJson(new SpineGenRigTests.StubLoader())
                .ReadSkeletonData(new StringReader(skeletonJson));
            var skeleton = new SpineRT.Skeleton(data);
            skeleton.UpdateWorldTransform(SpineRT.Physics.Update);

            RigPartBinding armBinding = rig.Parts.First(p => p.Role == RigRole.ArmRight);
            SpineRT.Slot slot = skeleton.Slots.Items.Take(skeleton.Slots.Count)
                .Single(s => s.Data.Name == $"{armBinding.Part}_slot");
            var mesh = Assert.IsType<SpineRT.MeshAttachment>(slot.Pose.Attachment);
            Assert.NotNull(mesh.Bones);
            float[] before = World(mesh, skeleton, slot);

            SpineRT.Bone joint = skeleton.FindBone(armBinding.DeformBone)!;
            joint.Pose.Rotation += 28f;
            skeleton.UpdateWorldTransform(SpineRT.Physics.Update);
            float[] after = World(mesh, skeleton, slot);

            double maxEdgeChange = 0;
            for (int a = 0; a < before.Length; a += 2)
                for (int b = a + 2; b < before.Length; b += 2)
                    maxEdgeChange = Math.Max(maxEdgeChange,
                        Math.Abs(Distance(before, a, b) - Distance(after, a, b)));
            Assert.True(maxEdgeChange > 0.05,
                $"weighted joint should deform the mesh; max pair-distance change={maxEdgeChange:F4}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static float[] World(SpineRT.MeshAttachment mesh, SpineRT.Skeleton skeleton, SpineRT.Slot slot)
    {
        var world = new float[mesh.WorldVerticesLength];
        mesh.ComputeWorldVertices(skeleton, slot, world);
        return world;
    }

    private static double Distance(float[] points, int a, int b)
    {
        double dx = points[a] - points[b];
        double dy = points[a + 1] - points[b + 1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string NewTempProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "xengine-spinegen-m9-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
