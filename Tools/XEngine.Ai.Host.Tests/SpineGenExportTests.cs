// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Globalization;
using SpineRT = global::Spine;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// Round-trip acceptance for the SpineGen writers: exported skeleton JSON must parse with the
/// vendored spine-csharp reader that ships inside com.xengine.spine, and the atlas text must parse
/// with <c>SpineRT.Atlas</c> (link-compiled into XEngine.Ai.Host for the restyle pipeline). Sampling
/// asserts actually pose bones through the parsed <c>SpineRT.Animation</c>.
///
/// NOTE: spine-csharp types are referenced fully qualified — the test assembly link-compiles most
/// of the runtime while Atlas/TextureRegion resolve to the shared copies inside XEngine.Ai.Host.
/// </summary>
public sealed class SpineGenExportTests
{
    private const float Epsilon = 0.01f;

    [Fact]
    public void SkeletonJson_RoundTrips_BonesSlotsSkinsAndAnimation()
    {
        SpineSkeletonDoc doc = BuildDoc();

        SpineSkinDef skinDef = new();
        skinDef.Add("torso_slot", "torso", SpineAttachment.Region("torso", x: 0, y: 0, rotation: 0, width: 120, height: 240));
        skinDef.Add("head_slot", "head", SpineAttachment.Region("head", x: 0, y: 10, rotation: 0, width: 60, height: 58));
        doc.Skins!.Add(skinDef);

        SpineAnimation idle = new();
        idle.Bone("head").Rotate =
        [
            new SpineRotateKey { Time = 0f, Angle = -5f },
            new SpineRotateKey { Time = 1f, Angle = 5f },
            new SpineRotateKey { Time = 2f, Angle = -5f },
        ];
        doc.Animations!.Add("idle", idle);

        string json = SpineExporter.Write(doc);

        SpineRT.SkeletonData data = ReadSkeleton(json);
        Assert.Equal(3, data.Bones.Count);
        Assert.Equal(2, data.Slots.Count);

        SpineRT.BoneData head = data.FindBone("head")!;
        Assert.Equal("torso", head.Parent!.Name);
        Assert.Equal(80f, data.FindBone("torso")!.setupPose.Y, 5);

        SpineRT.Skin skin = data.FindSkin("default");
        Assert.NotNull(skin);
        SpineRT.RegionAttachment torsoRegion =
            Assert.IsType<SpineRT.RegionAttachment>(skin.GetAttachment(data.FindSlot("torso_slot")!.Index, "torso"));
        Assert.Equal(120f, torsoRegion.Width, 5);

        SpineRT.RegionAttachment headRegion =
            Assert.IsType<SpineRT.RegionAttachment>(skin.GetAttachment(data.FindSlot("head_slot")!.Index, "head"));
        Assert.Equal(10f, headRegion.Y, 5);

        SpineRT.Animation idleParsed = data.FindAnimation("idle")!;
        Assert.NotNull(idleParsed);
        Assert.Equal(2f, idleParsed.Duration, 5);
        Assert.Single(idleParsed.Timelines);

        var skeleton = new SpineRT.Skeleton(data);

        SampleRotation(idleParsed, skeleton, 0f, -5f);
        SampleRotation(idleParsed, skeleton, 1f, 5f);
        SampleRotation(idleParsed, skeleton, 0.5f, 0f);
    }

    [Fact]
    public void SkeletonJson_RoundTrips_UnweightedMeshAttachment()
    {
        SpineSkeletonDoc doc = new()
        {
            Skeleton = new SpineSkeletonMeta { Width = 64, Height = 64 },
            Bones = [new SpineBoneDef { Name = "root" }],
            Slots = [new SpineSlotDef { Name = "blob", Bone = "root", Attachment = "blob" }],
            Skins = [],
        };

        SpineAttachment mesh = SpineAttachment.Mesh(
            path: "blob",
            vertices: SpineMeshVertices.Unweighted([0f, 0f, 32f, 0f, 32f, 32f]),
            uvs: [10f, 10f, 42f, 10f, 42f, 42f],
            triangles: [0, 1, 2],
            hullPointCount: 3,
            width: 32,
            height: 32);
        doc.Skins.Add(new SpineSkinDef());
        doc.Skins[0].Add("blob", "blob", mesh);

        SpineRT.SkeletonData data = ReadSkeleton(SpineExporter.Write(doc));
        SpineRT.MeshAttachment parsed =
            (SpineRT.MeshAttachment)data.FindSkin("default")!.GetAttachment(0, "blob")!;
        Assert.Equal(new[] { 0, 1, 2 }, parsed.triangles);
        Assert.Equal(6, parsed.regionUVs.Length);
        Assert.Equal(6, parsed.HullLength);
    }

    [Fact]
    public void Exporter_Rejects_MissingParentDuplicateBonesAndCycles()
    {
        var orphan = new SpineSkeletonDoc
        {
            Bones = [new SpineBoneDef { Name = "a", Parent = "ghost" }],
        };
        Assert.Throws<InvalidOperationException>(() => SpineExporter.Write(orphan));

        var duplicate = new SpineSkeletonDoc
        {
            Bones = [new SpineBoneDef { Name = "a" }, new SpineBoneDef { Name = "a" }],
        };
        Assert.Throws<InvalidOperationException>(() => SpineExporter.Write(duplicate));

        var cycle = new SpineSkeletonDoc
        {
            Bones =
            [
                new SpineBoneDef { Name = "a", Parent = "b" },
                new SpineBoneDef { Name = "b", Parent = "a" },
            ],
        };
        Assert.Throws<InvalidOperationException>(() => SpineExporter.Write(cycle));
    }

    [Fact]
    public void Exporter_OrdersBones_ParentsBeforeChildren()
    {
        var doc = new SpineSkeletonDoc
        {
            Bones =
            [
                new SpineBoneDef { Name = "hand", Parent = "arm" },
                new SpineBoneDef { Name = "root" },
                new SpineBoneDef { Name = "arm", Parent = "root" },
            ],
        };
        string json = SpineExporter.Write(doc);

        int rootAt = json.IndexOf("\"name\": \"root\"", StringComparison.Ordinal);
        int armAt = json.IndexOf("\"name\": \"arm\"", StringComparison.Ordinal);
        int handAt = json.IndexOf("\"name\": \"hand\"", StringComparison.Ordinal);
        Assert.True(rootAt >= 0 && armAt > rootAt && handAt > armAt,
            $"bones must serialize parent-first:\n{json}");
    }

    [Fact]
    public void AtlasWriter_ParsesBack_WithVendoredAtlasReader()
    {
        using var writer = new StringWriter();
        SpineAtlasWriter.Write(writer, "bear.png", 256, 256,
        [
            new SpineAtlasRegion { Name = "torso", X = 2, Y = 3, Width = 120, Height = 240 },
            new SpineAtlasRegion { Name = "head", X = 130, Y = 3, Width = 60, Height = 58 },
        ]);
        string text = writer.ToString();

        var atlas = new SpineRT.Atlas(new StringReader(text), "", new XEngine.Spine.SpineNoopTextureLoader());
        Assert.Equal(2, atlas.Regions.Count);
        Assert.Equal("bear.png", atlas.Pages[0].name);
        Assert.Equal(256, atlas.Pages[0].width);
        Assert.Equal(256, atlas.Pages[0].height);

        SpineRT.AtlasRegion torso = atlas.FindRegion("torso")!;
        Assert.Equal(2, torso.x);
        Assert.Equal(3, torso.y);
        Assert.Equal(120, torso.width);
        Assert.Equal(240, torso.height);
        Assert.NotEqual(90, torso.degrees);

        SpineRT.AtlasRegion head = atlas.FindRegion("head")!;
        Assert.Equal(60, head.originalWidth);
        Assert.Equal(58, head.originalHeight);
        Assert.Equal(-1, head.index);
    }

    private static void SampleRotation(SpineRT.Animation animation, SpineRT.Skeleton skeleton, float time,
        float expected)
    {
        // Apply onto a fresh setup-pose skeleton each sample so alpha=1 lands exactly on the key.
        skeleton.SetupPose();
        skeleton.UpdateWorldTransform(SpineRT.Physics.Update);
        animation.Apply(skeleton, 0f, time, loop: false, events: null, alpha: 1f,
            from: SpineRT.MixFrom.Setup, add: false, mixOut: false, appliedPose: false);
        skeleton.UpdateWorldTransform(SpineRT.Physics.Update);
        SpineRT.Bone head = skeleton.FindBone("head")!;
        float angle = Normalize(head.Pose.Rotation);
        Assert.True(MathF.Abs(angle - expected) <= Epsilon,
            $"t={time.ToString(CultureInfo.InvariantCulture)} rotation={head.Pose.Rotation} expected={expected}");
    }

    private static float Normalize(float degrees)
    {
        while (degrees > 180f) degrees -= 360f;
        while (degrees < -180f) degrees += 360f;
        return degrees;
    }

    private static SpineRT.SkeletonData ReadSkeleton(string json)
    {
        var loader = new StubAttachmentLoader();
        return new SpineRT.SkeletonJson(loader).ReadSkeletonData(new StringReader(json));
    }

    internal static SpineSkeletonDoc BuildDoc() => new()
    {
        Skeleton = new SpineSkeletonMeta { X = 0, Y = 0, Width = 256, Height = 320, Fps = 30 },
        Bones =
        [
            new SpineBoneDef { Name = "root" },
            new SpineBoneDef { Name = "torso", Parent = "root", X = 0f, Y = 80f },
            new SpineBoneDef { Name = "head", Parent = "torso", X = 0f, Y = 70f },
        ],
        Slots =
        [
            new SpineSlotDef { Name = "torso_slot", Bone = "torso", Attachment = "torso" },
            new SpineSlotDef { Name = "head_slot", Bone = "head", Attachment = "head" },
        ],
        Skins = [],
        Animations = [],
    };

    private sealed class StubAttachmentLoader : SpineRT.AttachmentLoader
    {
        public SpineRT.RegionAttachment NewRegionAttachment(SpineRT.Skin skin, string placeholder, string name,
            string path, SpineRT.Sequence sequence) => new(name, sequence) { Path = path };

        public SpineRT.MeshAttachment NewMeshAttachment(SpineRT.Skin skin, string placeholder, string name,
            string path, SpineRT.Sequence sequence) => new(name, sequence) { Path = path };

        public SpineRT.BoundingBoxAttachment NewBoundingBoxAttachment(SpineRT.Skin skin, string placeholder,
            string name) => new(name);

        public SpineRT.PathAttachment NewPathAttachment(SpineRT.Skin skin, string placeholder, string name) =>
            new(name);

        public SpineRT.PointAttachment NewPointAttachment(SpineRT.Skin skin, string placeholder, string name) =>
            new(name);

        public SpineRT.ClippingAttachment NewClippingAttachment(SpineRT.Skin skin, string placeholder, string name) =>
            new(name);
    }
}
