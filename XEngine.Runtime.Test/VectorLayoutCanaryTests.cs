// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;

using XEngine.Echo;
using XEngine.Runtime.Backends.D3D12;
using XEngine.Runtime.Backends.Vulkan;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public class VectorLayoutCanaryTests : RuntimeTestBase
{
    [Fact]
    public void PublicVectors_StayTightlyPackedForAbiAndRhi()
    {
        Assert.Equal(8, Marshal.SizeOf<Float2>());
        Assert.Equal(12, Marshal.SizeOf<Float3>());
        Assert.Equal(16, Marshal.SizeOf<Float4>());
        Assert.Equal(1, Marshal.SizeOf<Bool3>());

        var format = new VertexFormat(
        [
            new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3),
            new(VertexFormat.VertexSemantic.Normal, VertexFormat.VertexType.Float, 3),
            new(VertexFormat.VertexSemantic.TexCoord0, VertexFormat.VertexType.Float, 2),
        ]);
        Assert.Equal(0, format.Elements[0].Offset);
        Assert.Equal(12, format.Elements[0].Stride);
        Assert.Equal(12, format.Elements[1].Offset);
        Assert.Equal(24, format.Elements[2].Offset);
        Assert.Equal(32, format.Size);

        Assert.Equal(12, D3D12Formats.BytesPerPixel(TextureImageFormat.Float3));
        Assert.Equal(12, VulkanFormats.BytesPerPixel(TextureImageFormat.Float3));
        Assert.Equal(Vortice.DXGI.Format.R32G32B32_Float, D3D12Formats.ToDxgiFormat(TextureImageFormat.Float3));
        Assert.Equal(Silk.NET.Vulkan.Format.R32G32B32Sfloat, VulkanFormats.ToVkFormat(TextureImageFormat.Float3));
        Assert.Equal(
            Vortice.DXGI.Format.R32G32B32_Float,
            D3D12Formats.ToVertexFormat(VertexFormat.VertexType.Float, 3, normalized: false));
        Assert.Equal(
            Silk.NET.Vulkan.Format.R32G32B32Sfloat,
            VulkanFormats.ToVertexFormat(VertexFormat.VertexType.Float, 3, normalized: false));
    }

    [Fact]
    public void Echo_RoundTripsUnpaddedFloat3OnTransforms()
    {
        GameObject go = CreateGameObject("LayoutCanary");
        go.Transform.LocalPosition = new Float3(1.5f, -2.25f, 3.125f);
        go.Transform.LocalScale = new Float3(0.5f, 1f, 1.5f);

        EchoObject echo = Serializer.Serialize(go);
        GameObject clone = Serializer.Deserialize<GameObject>(echo);

        Assert.Equal(new Float3(1.5f, -2.25f, 3.125f), clone.Transform.LocalPosition);
        Assert.Equal(new Float3(0.5f, 1f, 1.5f), clone.Transform.LocalScale);
    }
}
