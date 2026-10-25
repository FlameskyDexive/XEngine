using System;
using System.Buffers.Binary;
using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public class MeshVertexCompressionTests
{
    private static Mesh Sample() => new()
    {
        Vertices = [new Float3(123.25f, -7.5f, .125f)],
        UV = [new Float2(1.2345f, -2.125f)],
        Normals = [new Float3(.25f, -.75f, 1f)],
        Tangents = [new Float4(1f, 0, 0, -1f)],
        Colors = [new Color(.25f, .5f, .75f, 1f)],
        BoneIndices = [new Float4(1, 2, 3, 4)],
        BoneWeights = [new Float4(.1f, .2f, .3f, .4f)],
    };

    [Fact]
    public void CompactUpload_ReducesStrideAndPreservesSkinningPositionAndUV()
    {
        using var mesh = Sample();
        var original = mesh.MakeVertexDataBlob(Mesh.GetVertexLayout(mesh));
        Assert.Equal(96, original.Length);
        mesh.CompressVertexData = true;
        var packed = mesh.MakeVertexDataBlob(Mesh.GetVertexLayout(mesh));
        Assert.Equal(72, packed.Length);
        Assert.True(original.AsSpan(0, 20).SequenceEqual(packed.AsSpan(0, 20)));
        Assert.True(original.AsSpan(64, 32).SequenceEqual(packed.AsSpan(40, 32)));
        Assert.InRange(MathF.Abs(BinaryPrimitives.ReadInt16LittleEndian(packed.AsSpan(20)) / 32767f - .25f), 0, 1f / 32767f);
        Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(packed.AsSpan(38)));
        Assert.Equal(64, packed[28]);
    }

    [Fact]
    public void OutOfRangeAttributesRetainFloatPrecision()
    {
        using var mesh = Sample();
        mesh.Normals = [new Float3(2, 0, 0)];
        mesh.Colors = [new Color(4, .5f, .75f, 1)];
        mesh.CompressVertexData = true;
        var packed = mesh.MakeVertexDataBlob(Mesh.GetVertexLayout(mesh));
        Assert.Equal(88, packed.Length);
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(packed.AsSpan(20)));
        Assert.Equal(4f, BinaryPrimitives.ReadSingleLittleEndian(packed.AsSpan(32)));
    }

    [Fact]
    public void SerializationPreservesCompressionAndOriginalCpuStreams()
    {
        using var mesh = Sample();
        mesh.CompressVertexData = true;
        var tag = Serializer.Serialize(mesh);
        using var restored = Serializer.Deserialize<Mesh>(tag);
        Assert.True(restored.CompressVertexData);
        Assert.Equal(mesh.Vertices, restored.Vertices);
        Assert.Equal(mesh.Normals, restored.Normals);
        Assert.Equal(mesh.BoneWeights, restored.BoneWeights);
    }

    [Fact]
    public void VulkanUsesNormalizedShortFormat()
        => Assert.Equal(Silk.NET.Vulkan.Format.R16G16B16A16SNorm,
            Backends.Vulkan.VulkanFormats.ToVertexFormat(VertexFormat.VertexType.Short, 4, true));
}
