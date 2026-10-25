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
/// M4 rigging acceptance: the exported skeleton must RECONSTRUCT the illustration — every region
/// attachment's world quad (sampled via the vendored runtime after setup pose) must land on its
/// source crop rect in image space. This is what makes AI animations later trustworthy: rotate any
/// bone and the art swings around the inferred joint without teleporting.
/// </summary>
public sealed class SpineGenRigTests
{
    [Fact]
    public async Task Rig_ReconstructsBearPartsAroundInferredJoints()
    {
        string root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            byte[] rgba = SpineGenSliceTests.BuildBearRgba(out _);
            File.WriteAllBytes(Path.Combine(root, "Assets", "bear.png"),
                PngRgba.Encode(rgba, SpineGenSliceTests.W, SpineGenSliceTests.H));

            var project = new ProjectDescriptor("test", root, null, null, null, "fp");
            var slice = new SpineGenSliceService(project);
            SpineGenSliceResult sliced = slice.Slice("Assets/bear.png", null, null, null, null, null, null, null, dryRun: false);

            var rigService = new SpineGenRigService(project);
            SpineGenRigResult rig = rigService.Rig(sliced.OutputDirectory);

            // Roles: torso bound to hips; head classified; two arms.
            Assert.Equal(4, rig.SlotCount);
            Assert.True(rig.Roles.ContainsKey("Torso"));
            Assert.True(rig.Roles.ContainsKey("Head"), $"roles: {string.Join(',', rig.Roles.Keys)}");
            Assert.Equal(2, rig.Bones.Count(b => b.Name.StartsWith("arm_", StringComparison.Ordinal)));

            string sliceDir = Path.Combine(root, sliced.OutputDirectory);
            var sliceManifest = JsonSerializer.Deserialize<SliceManifest>(
                File.ReadAllText(Path.Combine(sliceDir, "slice_manifest.json")))!;
            string skeletonJson = File.ReadAllText(Path.Combine(sliceDir, "rig", "skeleton.json"));

            SpineRT.SkeletonData data =
                new SpineRT.SkeletonJson(new StubLoader())
                    .ReadSkeletonData(new StringReader(skeletonJson));

            float imgH = sliceManifest.ImageHeight;
            var skel = new SpineRT.Skeleton(data);
            skel.UpdateWorldTransform(SpineRT.Physics.Update);

            foreach (SpineRT.Slot slot in skel.Slots.Items.Take(skel.Slots.Count))
            {
                SlicePartEntry? entry = sliceManifest.Parts.FirstOrDefault(
                    p => $"{p.Name}_slot" == slot.Data.Name);
                Assert.NotNull(entry);
                var region = Assert.IsType<SpineRT.RegionAttachment>(slot.Pose.Attachment);

                var corners = new float[8];
                region.ComputeWorldVertices(slot, region.GetOffsets(slot.Pose), corners, 0);
                float minX = MathF.Min(MathF.Min(corners[0], corners[2]), MathF.Min(corners[4], corners[6]));
                float maxX = MathF.Max(MathF.Max(corners[0], corners[2]), MathF.Max(corners[4], corners[6]));
                float minY = MathF.Min(MathF.Min(corners[1], corners[3]), MathF.Min(corners[5], corners[7]));
                float maxY = MathF.Max(MathF.Max(corners[1], corners[3]), MathF.Max(corners[5], corners[7]));

                // World y-up → image y-down flip must reproduce the crop rect.
                Assert.Equal(entry.X, minX, 1.5f);
                Assert.Equal(entry.Y + entry.Height, imgH - minY, 1.5f);
                Assert.Equal(entry.Width, maxX - minX, 1.5f);
                Assert.Equal(entry.Height, (imgH - minY) - (imgH - maxY), 1.5f);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Rig_ClassifiesDetachedHeadAndArms()
    {
        byte[] rgba = SpineGenSliceTests.BuildBearRgba(out _);
        SliceRunResult run = Slicer.Slice(rgba, SpineGenSliceTests.W, SpineGenSliceTests.H, new SliceOptions());

        var slice = new SliceManifest
        {
            Source = "bear",
            ImageWidth = SpineGenSliceTests.W,
            ImageHeight = SpineGenSliceTests.H,
        };
        foreach (SlicePartImage p in run.Parts)
        {
            slice.Parts.Add(new SlicePartEntry
            {
                Name = p.Name,
                X = p.X,
                Y = p.Y,
                Width = p.Width,
                Height = p.Height,
                PixelCount = p.PixelCount,
                Layer = p.LayerIndex,
            });
        }

        (_, RigManifest manifest) = Rigger.Build(slice);

        Assert.Contains(manifest.Parts, b => b.Role == RigRole.Torso && b.Bone.StartsWith("b_", StringComparison.Ordinal));
        Assert.Contains(manifest.Parts, b => b.Role == RigRole.Head);
        Assert.Equal(2, manifest.Parts.Count(b =>
            b.Role is RigRole.ArmLeft or RigRole.ArmRight));

        // Role bones exist with template parents; every part hangs on its own child bone
        // parented to its role bone.
        Assert.Contains(manifest.Bones, b => b.Name == "head" && b.Parent == "spine");
        Assert.Equal(2, manifest.Bones.Count(b => b.Name is "arm_l" or "arm_r"));
        Assert.All(manifest.Parts, b =>
        {
            Assert.StartsWith("b_", b.Bone);
            string expectedParent = b.Role switch
            {
                RigRole.ArmLeft => "arm_l",
                RigRole.ArmRight => "arm_r",
                RigRole.LegLeft => "leg_l",
                RigRole.LegRight => "leg_r",
                RigRole.Head => "head",
                RigRole.Torso or RigRole.Hips => "hips",
                _ => "spine",
            };
            RigBoneInfo bone = manifest.Bones.Single(x => x.Name == b.Bone);
            Assert.Equal(expectedParent, bone.Parent);
        });
        Assert.All(manifest.Bones, b => Assert.True(b.ImageX >= 0 && b.ImageY >= 0));
    }

    [Fact]
    public void Rig_WaistPlatesBindToHipsBone()
    {
        // Mech-like art: a wide slab torso plus a small square riding low on its body — the
        // near-center waist band must map it to the Hips role (this used to throw
        // ArgumentOutOfRangeException when the Prop role had no bone mapping).
        var slice = new SliceManifest { Source = "slabs", ImageWidth = 128, ImageHeight = 128 };
        slice.Parts.Add(new SlicePartEntry { Name = "torso", X = 20, Y = 30, Width = 80, Height = 60, PixelCount = 4800, Layer = 0 });
        slice.Parts.Add(new SlicePartEntry { Name = "plate", X = 50, Y = 60, Width = 12, Height = 10, PixelCount = 120, Layer = 1 });

        (_, RigManifest manifest) = Rigger.Build(slice);

        Assert.Contains(manifest.Parts, b => b.Part == "plate" && b.Role == RigRole.Hips && b.Bone == "b_plate");
        Assert.Contains(manifest.Bones, b => b.Name == "b_plate" && b.Parent == "hips");
    }

    internal sealed class StubLoader : SpineRT.AttachmentLoader
    {
        public SpineRT.RegionAttachment NewRegionAttachment(SpineRT.Skin skin, string placeholder,
            string name, string path, SpineRT.Sequence sequence) => new(name, sequence) { Path = path };

        public SpineRT.MeshAttachment NewMeshAttachment(SpineRT.Skin skin, string placeholder,
            string name, string path, SpineRT.Sequence sequence) => new(name, sequence) { Path = path };

        public SpineRT.BoundingBoxAttachment NewBoundingBoxAttachment(SpineRT.Skin skin,
            string placeholder, string name) => new(name);

        public SpineRT.PathAttachment NewPathAttachment(SpineRT.Skin skin, string placeholder,
            string name) => new(name);

        public SpineRT.PointAttachment NewPointAttachment(SpineRT.Skin skin, string placeholder,
            string name) => new(name);

        public SpineRT.ClippingAttachment NewClippingAttachment(SpineRT.Skin skin, string placeholder,
            string name) => new(name);
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
