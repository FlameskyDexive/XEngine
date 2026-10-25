// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root.

using Silk.NET.Vulkan;

using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test.CompressedTextures;

public sealed class CompressedTextureFormatTests
{
    [Theory]
    [InlineData(TextureImageFormat.Etc2Rgb8, 1u, 1u, 8)]
    [InlineData(TextureImageFormat.Etc2Rgb8, 4u, 4u, 8)]
    [InlineData(TextureImageFormat.Etc2Rgb8, 5u, 4u, 16)]
    [InlineData(TextureImageFormat.Etc2Rgba8, 5u, 5u, 64)]
    [InlineData(TextureImageFormat.Astc4x4, 7u, 5u, 64)]
    [InlineData(TextureImageFormat.Etc2Rgb8Srgb, 5u, 4u, 16)]
    [InlineData(TextureImageFormat.Etc2Rgba8Srgb, 5u, 5u, 64)]
    [InlineData(TextureImageFormat.Astc4x4Srgb, 7u, 5u, 64)]
    public void CompressedDataSize_UsesTightFourByFourBlocks(
        TextureImageFormat format,
        uint width,
        uint height,
        int expectedBytes)
    {
        Assert.True(Texture.IsCompressedFormat(format));
        Assert.Equal(expectedBytes, Texture.GetDataSize(format, width, height));
        Assert.Equal(expectedBytes, VulkanFormats.CompressedDataSize(format, width, height));
    }

    [Fact]
    public void CompressedFormat_MapsToExpectedVulkanFormat()
    {
        Assert.Equal(Format.Etc2R8G8B8UnormBlock, VulkanFormats.ToVkFormat(TextureImageFormat.Etc2Rgb8));
        Assert.Equal(Format.Etc2R8G8B8A8UnormBlock, VulkanFormats.ToVkFormat(TextureImageFormat.Etc2Rgba8));
        Assert.Equal(Format.Astc4x4UnormBlock, VulkanFormats.ToVkFormat(TextureImageFormat.Astc4x4));
        Assert.Equal(Format.Etc2R8G8B8SrgbBlock, VulkanFormats.ToVkFormat(TextureImageFormat.Etc2Rgb8Srgb));
        Assert.Equal(Format.Etc2R8G8B8A8SrgbBlock, VulkanFormats.ToVkFormat(TextureImageFormat.Etc2Rgba8Srgb));
        Assert.Equal(Format.Astc4x4SrgbBlock, VulkanFormats.ToVkFormat(TextureImageFormat.Astc4x4Srgb));
    }

    [Fact]
    public void CompressedBlockByteCount_IsNotDerivedFromTexelBytes()
    {
        Assert.Equal(8, VulkanFormats.CompressedBlockBytes(TextureImageFormat.Etc2Rgb8));
        Assert.Equal(16, VulkanFormats.CompressedBlockBytes(TextureImageFormat.Etc2Rgba8));
        Assert.Equal(16, VulkanFormats.CompressedBlockBytes(TextureImageFormat.Astc4x4));
        Assert.Equal(8, VulkanFormats.CompressedBlockBytes(TextureImageFormat.Etc2Rgb8Srgb));
        Assert.Equal(16, VulkanFormats.CompressedBlockBytes(TextureImageFormat.Etc2Rgba8Srgb));
        Assert.Equal(16, VulkanFormats.CompressedBlockBytes(TextureImageFormat.Astc4x4Srgb));
        Assert.False(Texture.IsCompressedFormat(TextureImageFormat.Color4b));
    }
}
