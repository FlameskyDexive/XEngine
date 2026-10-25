// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;
using XEngine.Runtime.Backends.Metal;
using XEngine.Runtime.Backends.Metal.Native;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// M3: every <see cref="TextureImageFormat"/> maps to an MTLPixelFormat or an explicit
/// unsupported case (3-component textures). Raster / blend / topology helpers are covered too.
/// </summary>
public class MetalFormatTests
{
    [Fact]
    public void ToPixelFormat_CoversEveryEngineTextureFormat()
    {
        foreach (TextureImageFormat format in Enum.GetValues<TextureImageFormat>())
        {
            if (MetalFormats.IsThreeComponent(format))
            {
                NotSupportedException ex = Assert.Throws<NotSupportedException>(() => MetalFormats.ToPixelFormat(format));
                Assert.Contains("3-component", ex.Message, StringComparison.Ordinal);
                continue;
            }

            MTLPixelFormat metal = MetalFormats.ToPixelFormat(format);
            Assert.NotEqual(MTLPixelFormat.Invalid, metal);
        }
    }

    [Theory]
    [InlineData(TextureImageFormat.Color4b, (uint)MTLPixelFormat.RGBA8Unorm, 4)]
    [InlineData(TextureImageFormat.Byte, (uint)MTLPixelFormat.R8Unorm, 1)]
    [InlineData(TextureImageFormat.Float, (uint)MTLPixelFormat.R32Float, 4)]
    [InlineData(TextureImageFormat.Float2, (uint)MTLPixelFormat.RG32Float, 8)]
    [InlineData(TextureImageFormat.Float4, (uint)MTLPixelFormat.RGBA32Float, 16)]
    [InlineData(TextureImageFormat.Short, (uint)MTLPixelFormat.R16Float, 2)]
    [InlineData(TextureImageFormat.Short2, (uint)MTLPixelFormat.RG16Float, 4)]
    [InlineData(TextureImageFormat.Short4, (uint)MTLPixelFormat.RGBA16Float, 8)]
    [InlineData(TextureImageFormat.Int, (uint)MTLPixelFormat.R32Sint, 4)]
    [InlineData(TextureImageFormat.Int2, (uint)MTLPixelFormat.RG32Sint, 8)]
    [InlineData(TextureImageFormat.Int4, (uint)MTLPixelFormat.RGBA32Sint, 16)]
    [InlineData(TextureImageFormat.UnsignedShort, (uint)MTLPixelFormat.R16Uint, 2)]
    [InlineData(TextureImageFormat.UnsignedShort2, (uint)MTLPixelFormat.RG16Uint, 4)]
    [InlineData(TextureImageFormat.UnsignedShort4, (uint)MTLPixelFormat.RGBA16Uint, 8)]
    [InlineData(TextureImageFormat.UnsignedInt, (uint)MTLPixelFormat.R32Uint, 4)]
    [InlineData(TextureImageFormat.UnsignedInt2, (uint)MTLPixelFormat.RG32Uint, 8)]
    [InlineData(TextureImageFormat.UnsignedInt4, (uint)MTLPixelFormat.RGBA32Uint, 16)]
    [InlineData(TextureImageFormat.Depth16f, (uint)MTLPixelFormat.Depth16Unorm, 2)]
    [InlineData(TextureImageFormat.Depth24f, (uint)MTLPixelFormat.Depth32Float, 4)]
    [InlineData(TextureImageFormat.Depth32f, (uint)MTLPixelFormat.Depth32Float, 4)]
    [InlineData(TextureImageFormat.Depth24Stencil8, (uint)MTLPixelFormat.Depth32Float_Stencil8, 4)]
    public void UncompressedFormat_MapsToMetalAndByteSize(
        TextureImageFormat format,
        uint expected,
        int bytesPerPixel)
    {
        Assert.Equal((MTLPixelFormat)expected, MetalFormats.ToPixelFormat(format));
        Assert.Equal(bytesPerPixel, MetalFormats.BytesPerPixel(format));
        Assert.Equal(
            format is TextureImageFormat.Depth16f
                or TextureImageFormat.Depth24f
                or TextureImageFormat.Depth32f
                or TextureImageFormat.Depth24Stencil8,
            MetalFormats.IsDepth(format));
    }

    [Theory]
    [InlineData(TextureImageFormat.Color4b, 0)]
    [InlineData(TextureImageFormat.Color4bSrgb, 0)]
    [InlineData(TextureImageFormat.Depth16f, 2)]
    [InlineData(TextureImageFormat.Depth24f, 2)]
    [InlineData(TextureImageFormat.Depth32f, 2)]
    [InlineData(TextureImageFormat.Depth24Stencil8, 2)]
    public void TextureStorageMode_IsPrivateForDepthAndStencil(
        TextureImageFormat format,
        int expected)
    {
        Assert.Equal((MTLStorageMode)expected, MetalFormats.ToStorageMode(format));
    }

    [Theory]
    [InlineData(TextureImageFormat.Etc2Rgb8, (uint)MTLPixelFormat.ETC2_RGB8, 8)]
    [InlineData(TextureImageFormat.Etc2Rgba8, (uint)MTLPixelFormat.EAC_RGBA8, 16)]
    [InlineData(TextureImageFormat.Astc4x4, (uint)MTLPixelFormat.ASTC_4x4_LDR, 16)]
    [InlineData(TextureImageFormat.Etc2Rgb8Srgb, (uint)MTLPixelFormat.ETC2_RGB8_sRGB, 8)]
    [InlineData(TextureImageFormat.Etc2Rgba8Srgb, (uint)MTLPixelFormat.EAC_RGBA8_sRGB, 16)]
    [InlineData(TextureImageFormat.Astc4x4Srgb, (uint)MTLPixelFormat.ASTC_4x4_sRGB, 16)]
    public void CompressedFormat_MapsToMetalBlock(
        TextureImageFormat format,
        uint expected,
        int blockBytes)
    {
        Assert.True(MetalFormats.IsCompressed(format));
        Assert.Equal((MTLPixelFormat)expected, MetalFormats.ToPixelFormat(format));
        Assert.Equal(blockBytes, MetalFormats.CompressedBlockBytes(format));
        Assert.Equal(blockBytes, MetalFormats.CompressedDataSize(format, 4, 4));
        Assert.Equal(blockBytes * 4, MetalFormats.CompressedDataSize(format, 5, 5));
    }

    [Fact]
    public void TopologyBlendAndCompare_MatchMetalHeaderValues()
    {
        Assert.Equal(MTLPrimitiveType.Triangle, MetalFormats.ToPrimitiveType(Topology.Triangles));
        Assert.Equal(MTLPrimitiveType.LineStrip, MetalFormats.ToPrimitiveType(Topology.LineLoop));
        Assert.Equal(MTLCullMode.None, MetalFormats.ToCullMode(RasterizerState.PolyFace.None));
        Assert.Equal(MTLWinding.Clockwise, MetalFormats.ToWinding(RasterizerState.WindingOrder.CW));
        Assert.Equal(MTLCompareFunction.LessEqual, MetalFormats.ToCompare(RasterizerState.DepthMode.Lequal));
        Assert.Equal(MTLBlendFactor.SourceAlpha, MetalFormats.ToBlendFactor(RasterizerState.Blending.SrcAlpha));
        Assert.Equal(MTLBlendFactor.SourceColor, MetalFormats.ToBlendFactor(RasterizerState.Blending.SrcColor));
        Assert.Equal(MTLBlendOperation.Add, MetalFormats.ToBlendOp(RasterizerState.BlendMode.Add));
        Assert.Equal(MTLVertexFormat.Float3, MetalFormats.ToVertexFormat(VertexFormat.VertexType.Float, 3, false));
        Assert.Equal(MTLVertexFormat.UChar4Normalized, MetalFormats.ToVertexFormat(VertexFormat.VertexType.UnsignedByte, 4, true));
        Assert.True(MetalFormats.HasStencil(MTLPixelFormat.Depth32Float_Stencil8));
        Assert.False(MetalFormats.HasStencil(MTLPixelFormat.Depth32Float));
        Assert.Equal(MTLSamplerAddressMode.ClampToEdge, MetalFormats.ToAddressMode(TextureWrap.ClampToBorder));
    }

    [Fact]
    public void Astc_HardFailsWhenGpuFamilyIsMissing()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => MetalAstcSupport.EnsureTextureFormatSupported(TextureImageFormat.Astc4x4, deviceSupportsAstc: false));
        Assert.Equal(MetalAstcSupport.UnsupportedMessage, ex.Message);
        Assert.Throws<InvalidOperationException>(
            () => MetalAstcSupport.EnsureTextureFormatSupported(TextureImageFormat.Astc4x4Srgb, deviceSupportsAstc: false));
        MetalAstcSupport.EnsureTextureFormatSupported(TextureImageFormat.Astc4x4, deviceSupportsAstc: true);
        MetalAstcSupport.EnsureTextureFormatSupported(TextureImageFormat.Color4b, deviceSupportsAstc: false);
    }
}
