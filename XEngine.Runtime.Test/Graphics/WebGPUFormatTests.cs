// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.Backends.WebGPU.Native;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WG4: every <see cref="TextureImageFormat"/> maps to a wgpu format or an explicit
/// unsupported case (3-component textures). Raster / blend / topology helpers are covered too.
/// </summary>
public class WebGPUFormatTests
{
    [Fact]
    public void ToWgpuFormat_CoversEveryEngineTextureFormat()
    {
        foreach (TextureImageFormat format in Enum.GetValues<TextureImageFormat>())
        {
            if (WebGPUFormats.IsThreeComponent(format))
            {
                NotSupportedException ex = Assert.Throws<NotSupportedException>(() => WebGPUFormats.ToWgpuFormat(format));
                Assert.Contains("3-component", ex.Message, StringComparison.Ordinal);
                continue;
            }

            WGPUTextureFormat wgpu = WebGPUFormats.ToWgpuFormat(format);
            Assert.NotEqual(WGPUTextureFormat.Undefined, wgpu);
        }
    }

    [Theory]
    [InlineData(TextureImageFormat.Color4b, (uint)WGPUTextureFormat.RGBA8Unorm, 4)]
    [InlineData(TextureImageFormat.Byte, (uint)WGPUTextureFormat.R8Unorm, 1)]
    [InlineData(TextureImageFormat.Float, (uint)WGPUTextureFormat.R32Float, 4)]
    [InlineData(TextureImageFormat.Float2, (uint)WGPUTextureFormat.RG32Float, 8)]
    [InlineData(TextureImageFormat.Float4, (uint)WGPUTextureFormat.RGBA32Float, 16)]
    [InlineData(TextureImageFormat.Short, (uint)WGPUTextureFormat.R16Float, 2)]
    [InlineData(TextureImageFormat.Short2, (uint)WGPUTextureFormat.RG16Float, 4)]
    [InlineData(TextureImageFormat.Short4, (uint)WGPUTextureFormat.RGBA16Float, 8)]
    [InlineData(TextureImageFormat.Int, (uint)WGPUTextureFormat.R32Sint, 4)]
    [InlineData(TextureImageFormat.Int2, (uint)WGPUTextureFormat.RG32Sint, 8)]
    [InlineData(TextureImageFormat.Int4, (uint)WGPUTextureFormat.RGBA32Sint, 16)]
    [InlineData(TextureImageFormat.UnsignedShort, (uint)WGPUTextureFormat.R16Uint, 2)]
    [InlineData(TextureImageFormat.UnsignedShort2, (uint)WGPUTextureFormat.RG16Uint, 4)]
    [InlineData(TextureImageFormat.UnsignedShort4, (uint)WGPUTextureFormat.RGBA16Uint, 8)]
    [InlineData(TextureImageFormat.UnsignedInt, (uint)WGPUTextureFormat.R32Uint, 4)]
    [InlineData(TextureImageFormat.UnsignedInt2, (uint)WGPUTextureFormat.RG32Uint, 8)]
    [InlineData(TextureImageFormat.UnsignedInt4, (uint)WGPUTextureFormat.RGBA32Uint, 16)]
    [InlineData(TextureImageFormat.Depth16f, (uint)WGPUTextureFormat.Depth16Unorm, 2)]
    [InlineData(TextureImageFormat.Depth24f, (uint)WGPUTextureFormat.Depth24Plus, 4)]
    [InlineData(TextureImageFormat.Depth32f, (uint)WGPUTextureFormat.Depth32Float, 4)]
    [InlineData(TextureImageFormat.Depth24Stencil8, (uint)WGPUTextureFormat.Depth24PlusStencil8, 4)]
    public void UncompressedFormat_MapsToWgpuAndByteSize(
        TextureImageFormat format,
        uint expected,
        int bytesPerPixel)
    {
        Assert.Equal((WGPUTextureFormat)expected, WebGPUFormats.ToWgpuFormat(format));
        Assert.Equal(bytesPerPixel, WebGPUFormats.BytesPerPixel(format));
        Assert.Equal(
            format is TextureImageFormat.Depth16f
                or TextureImageFormat.Depth24f
                or TextureImageFormat.Depth32f
                or TextureImageFormat.Depth24Stencil8,
            WebGPUFormats.IsDepth(format));
    }

    [Theory]
    [InlineData(TextureImageFormat.Etc2Rgb8, (uint)WGPUTextureFormat.ETC2RGB8Unorm, 8)]
    [InlineData(TextureImageFormat.Etc2Rgba8, (uint)WGPUTextureFormat.ETC2RGBA8Unorm, 16)]
    [InlineData(TextureImageFormat.Astc4x4, (uint)WGPUTextureFormat.ASTC4x4Unorm, 16)]
    [InlineData(TextureImageFormat.Etc2Rgb8Srgb, (uint)WGPUTextureFormat.ETC2RGB8UnormSrgb, 8)]
    [InlineData(TextureImageFormat.Etc2Rgba8Srgb, (uint)WGPUTextureFormat.ETC2RGBA8UnormSrgb, 16)]
    [InlineData(TextureImageFormat.Astc4x4Srgb, (uint)WGPUTextureFormat.ASTC4x4UnormSrgb, 16)]
    public void CompressedFormat_MapsToWgpuBlock(
        TextureImageFormat format,
        uint expected,
        int blockBytes)
    {
        Assert.True(WebGPUFormats.IsCompressed(format));
        Assert.Equal((WGPUTextureFormat)expected, WebGPUFormats.ToWgpuFormat(format));
        Assert.Equal(blockBytes, WebGPUFormats.CompressedBlockBytes(format));
        Assert.Equal(blockBytes, WebGPUFormats.CompressedDataSize(format, 4, 4));
        Assert.Equal(blockBytes * 4, WebGPUFormats.CompressedDataSize(format, 5, 5));
    }

    [Fact]
    public void TopologyBlendAndCompare_MatchUnifiedHeaderValues()
    {
        Assert.Equal(WGPUPrimitiveTopology.TriangleList, WebGPUFormats.ToTopology(Topology.Triangles));
        Assert.Equal(WGPUPrimitiveTopology.LineStrip, WebGPUFormats.ToTopology(Topology.LineLoop));
        Assert.Equal(WGPUCullMode.None, WebGPUFormats.ToCullMode(RasterizerState.PolyFace.None));
        Assert.Equal(WGPUFrontFace.CW, WebGPUFormats.ToFrontFace(RasterizerState.WindingOrder.CW));
        Assert.Equal(WGPUCompareFunction.LessEqual, WebGPUFormats.ToCompare(RasterizerState.DepthMode.Lequal));
        Assert.Equal(WGPUBlendFactor.SrcAlpha, WebGPUFormats.ToBlendFactor(RasterizerState.Blending.SrcAlpha));
        Assert.Equal(WGPUBlendFactor.Src, WebGPUFormats.ToBlendFactor(RasterizerState.Blending.SrcColor));
        Assert.Equal(WGPUBlendOperation.Add, WebGPUFormats.ToBlendOp(RasterizerState.BlendMode.Add));
        Assert.Equal(WGPUVertexFormat.Float32x3, WebGPUFormats.ToVertexFormat(VertexFormat.VertexType.Float, 3, false));
        Assert.Equal(WGPUVertexFormat.Unorm8x4, WebGPUFormats.ToVertexFormat(VertexFormat.VertexType.UnsignedByte, 4, true));
        Assert.True(WebGPUFormats.HasStencil(WGPUTextureFormat.Depth24PlusStencil8));
        Assert.False(WebGPUFormats.HasStencil(WGPUTextureFormat.Depth32Float));
    }

    [Fact]
    public void FindWgslEntryPoint_ReadsStageFunctionName()
    {
        const string wgsl = """
@binding(0) @group(0) var<uniform> Globals_0 : f32;
@vertex
fn vs_main(@builtin(vertex_index) id : u32) -> @builtin(position) vec4<f32> { return vec4<f32>(0.0); }
@fragment
fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(1.0); }
""";
        Assert.Equal("vs_main", WebGPUFormats.FindWgslEntryPoint(wgsl, "vertex"));
        Assert.Equal("fs_main", WebGPUFormats.FindWgslEntryPoint(wgsl, "fragment"));
    }
}
