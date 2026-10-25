using Spine;

using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Spine38.Tests;

public sealed class Spine38MeshTests
{
    [Fact]
    public void RegionQuad_VertexCountUvOrderAndPmaMatchComputeWorldVertices()
    {
        Skeleton skeleton = CreateQuadSkeleton();
        Slot slot = skeleton.DrawOrder.Items[0];
        var region = Assert.IsType<RegionAttachment>(slot.Attachment);

        float[] world = new float[8];
        region.ComputeWorldVertices(slot.Bone, world, 0);

        var generator = new SpineMeshGenerator();
        generator.Build(skeleton, Color.White);
        Assert.Equal(1, generator.BatchCount);
        SpineMeshBatch batch = generator.GetBatch(0);
        Assert.Equal(4, batch.Vertices.Length);
        Assert.Equal(4, batch.UVs.Length);
        Assert.Equal(6, batch.Indices.Length);
        Assert.Equal(new uint[] { 0, 1, 2, 2, 3, 0 }, batch.Indices);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(world[i * 2], batch.Vertices[i].X, 4);
            Assert.Equal(world[i * 2 + 1], batch.Vertices[i].Y, 4);
            Assert.Equal(region.UVs[i * 2], batch.UVs[i].X, 4);
            Assert.Equal(region.UVs[i * 2 + 1], batch.UVs[i].Y, 4);
            Assert.Equal(255, batch.Colors[i].R);
            Assert.Equal(255, batch.Colors[i].G);
            Assert.Equal(255, batch.Colors[i].B);
            Assert.Equal(255, batch.Colors[i].A);
        }
    }

    [Fact]
    public void RegionQuad_HalfAlpha_IsPremultiplied()
    {
        Skeleton skeleton = CreateQuadSkeleton();
        skeleton.A = 0.5f;
        var generator = new SpineMeshGenerator();
        generator.Build(skeleton, Color.White);
        Color32 color = generator.GetBatch(0).Colors[0];
        Assert.Equal(127, color.R);
        Assert.Equal(127, color.G);
        Assert.Equal(127, color.B);
        Assert.Equal(127, color.A);
    }

    internal static Skeleton CreateQuadSkeleton()
    {
        var bone = new BoneData(0, "root", null!);
        var slot = new SlotData(0, "body", bone) { AttachmentName = "body" };
        var data = new SkeletonData();
        data.Bones.Add(bone);
        data.Slots.Add(slot);
        var region = new RegionAttachment("body") { Width = 32, Height = 32 };
        region.SetUVs(0f, 0f, 1f, 1f, rotate: false);
        region.UpdateOffset();
        region.RendererObject = new AtlasRegion
        {
            name = "body",
            width = 32,
            height = 32,
            originalWidth = 32,
            originalHeight = 32,
            u = 0f,
            v = 0f,
            u2 = 1f,
            v2 = 1f,
            page = new AtlasPage { name = "body.png", width = 32, height = 32 },
        };
        var skin = new Skin("default");
        skin.SetAttachment(0, "body", region);
        data.Skins.Add(skin);
        data.DefaultSkin = skin;
        var skeleton = new Skeleton(data);
        skeleton.SetSkin(skin);
        skeleton.SetSlotsToSetupPose();
        skeleton.UpdateWorldTransform();
        return skeleton;
    }
}
