// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;
using XEngine.Runtime.Backends.Metal;
using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// M2: Metal buffer / texture / sampler / GenerateMipmap.
/// Non-macOS hosts are reported as skipped; device failures on macOS fail the test.
/// </summary>
public class MetalResourceTests
{
    [MacOSMetalFact]
    public unsafe void NativeCubeFacesAndMipLevels_UploadAndReadbackPreserveDistinctPixels()
    {
        using var pool = MetalNative.CreateAutoreleasePool();
        using var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });
        device.Initialize(null);
        nint texture = MetalNative.NewTextureCube(device.NativeDevice,
            MetalFormats.ToPixelFormat(TextureImageFormat.Color4b), 4, mipmapped: true,
            MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget,
            MetalFormats.ToStorageMode(TextureImageFormat.Color4b));
        Assert.NotEqual(nint.Zero, texture);
        byte[][] expected = new byte[12][];
        try
        {
            for (int face = 0; face < 6; face++)
            {
                for (int mip = 0; mip < 2; mip++)
                {
                    int size = 4 >> mip;
                    byte[] pixels = new byte[size * size * 4];
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        pixels[i] = (byte)(20 + face * 30);
                        pixels[i + 1] = (byte)(40 + mip * 80);
                        pixels[i + 2] = (byte)(i / 4);
                        pixels[i + 3] = 255;
                    }
                    expected[face * 2 + mip] = pixels;
                    fixed (byte* bytes = pixels)
                        MetalNative.ReplaceRegionCube(texture, (uint)face, (uint)mip,
                            (uint)size, (uint)size, (nint)bytes, (nuint)(size * 4));
                }
            }
            for (int face = 0; face < 6; face++)
            {
                for (int mip = 0; mip < 2; mip++)
                {
                    int size = 4 >> mip;
                    byte[] actual = new byte[size * size * 4];
                    fixed (byte* bytes = actual)
                        MetalNative.GetBytesCube(texture, (uint)face, (uint)mip,
                            (uint)size, (uint)size, (nint)bytes, (nuint)(size * 4));
                    Assert.Equal(expected[face * 2 + mip], actual);
                }
            }
        }
        finally
        {
            MetalNative.Release(texture);
        }
    }

    [MacOSMetalFact]
    public void NativeTextureCopy_SubregionPreservesOffsetsAndOutsidePixels()
    {
        using var pool = MetalNative.CreateAutoreleasePool();
        using var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });
        device.Initialize(null);
        using var source = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
        using var destination = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
        byte[] pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(i + 1);
        using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-copy-abi"))
        {
            create.EncodeCreateTexture(source);
            create.EncodeAllocateTexture2D(source, 0, 4, 4, 0, pixels);
            create.EncodeCreateTexture(destination);
            create.EncodeAllocateTexture2D(destination, 0, 4, 4, 0, new byte[pixels.Length]);
            device.Execute(create, wait: true);
        }
        nint commands = MetalNative.NewCommandBuffer(device.NativeQueue);
        nint blit = MetalNative.BlitCommandEncoder(commands);
        Assert.NotEqual(nint.Zero, blit);
        MetalNative.CopyTextureToTexture(blit, device.Textures[source.Handle].Texture,
            device.Textures[destination.Handle].Texture, 1, 1, 2, 0, 2, 2, 0, 0);
        MetalNative.EndEncoding(blit);
        MetalNative.Commit(commands);
        MetalNative.WaitUntilCompleted(commands);
        byte[] expected = new byte[pixels.Length];
        Buffer.BlockCopy(pixels, (1 * 4 + 1) * 4, expected, 2 * 4, 2 * 4);
        Buffer.BlockCopy(pixels, (2 * 4 + 1) * 4, expected, (1 * 4 + 2) * 4, 2 * 4);
        Assert.Equal(expected, device.ReadTexture2D(device.Textures[destination.Handle], 0));
    }

    [MacOSMetalFact]
    public void Buffer_CreateUploadGrowAndReadback()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            byte[] initial = [1, 2, 3, 4, 5, 6, 7, 8];
            using var buffer = new GraphicsBuffer(BufferType.VertexBuffer, initial, dynamic: true);

            using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m2-buffer-create"))
            {
                create.EncodeCreateBuffer(buffer, dynamic: true, initial);
                device.Execute(create, wait: true);
            }

            Assert.NotEqual(0u, buffer.Handle);
            MetalBufferResource resource = device.Buffers[buffer.Handle];
            Assert.True(resource.Size >= (ulong)initial.Length);
            Assert.Equal(initial, device.ReadBuffer(resource, 0, initial.Length));

            byte[] grown = new byte[32];
            for (int i = 0; i < grown.Length; i++)
                grown[i] = (byte)(i + 10);

            using (CommandBuffer update = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m2-buffer-grow"))
            {
                update.UpdateBuffer<byte>(buffer, grown);
                device.Execute(update, wait: true);
            }

            resource = device.Buffers[buffer.Handle];
            Assert.True(resource.Size >= (ulong)grown.Length);
            Assert.Equal(grown, device.ReadBuffer(resource, 0, grown.Length));
        }
    }

    [MacOSMetalFact]
    public void Texture2D_AllocateUpdateReadback()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using var texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
            byte[] initial = new byte[4 * 4 * 4];
            byte[] update =
            [
                255, 0, 0, 255,
                0, 255, 0, 255,
                0, 0, 255, 255,
                255, 255, 255, 255,
            ];

            using (CommandBuffer upload = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m2-texture-upload"))
            {
                upload.EncodeCreateTexture(texture);
                upload.EncodeAllocateTexture2D(texture, 0, 4, 4, 0, initial);
                upload.EncodeUpdateTexture2D(texture, 0, 1, 1, 2, 2, update);
                device.Execute(upload, wait: true);
            }

            MetalTextureResource resource = device.Textures[texture.Handle];
            Assert.Equal(4u, resource.Width);
            Assert.Equal(4u, resource.Height);
            byte[] expected = new byte[initial.Length];
            Buffer.BlockCopy(update, 0, expected, (1 * 4 + 1) * 4, 2 * 4);
            Buffer.BlockCopy(update, 2 * 4, expected, (2 * 4 + 1) * 4, 2 * 4);
            Assert.Equal(expected, device.ReadTexture2D(resource, 0));
        }
    }

    [MacOSMetalFact]
    public void Texture_WrapAndFilters_AreStored()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using var texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);

            using (CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m2-sampler"))
            {
                cmd.EncodeCreateTexture(texture);
                cmd.EncodeAllocateTexture2D(texture, 0, 1, 1, 0, new byte[] { 16, 32, 48, 64 });
                cmd.EncodeSetTextureWrap(texture, 0, TextureWrap.ClampToEdge);
                cmd.EncodeSetTextureWrap(texture, 1, TextureWrap.MirroredRepeat);
                cmd.EncodeSetTextureFilters(texture, TextureMin.Nearest, TextureMag.Nearest);
                device.Execute(cmd, wait: true);
            }

            MetalTextureResource resource = device.Textures[texture.Handle];
            Assert.Equal(TextureWrap.ClampToEdge, resource.WrapS);
            Assert.Equal(TextureWrap.MirroredRepeat, resource.WrapT);
            Assert.Equal(TextureMin.Nearest, resource.MinFilter);
            Assert.Equal(TextureMag.Nearest, resource.MagFilter);
            Assert.NotEqual(nint.Zero, resource.Sampler);
        }
    }

    [MacOSMetalFact]
    public void GenerateMipmap_4x4Uniform_ProducesMip1()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using var texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
            byte[] initial = new byte[4 * 4 * 4];
            for (int i = 0; i < initial.Length; i += 4)
            {
                initial[i] = 255;
                initial[i + 1] = 128;
                initial[i + 2] = 64;
                initial[i + 3] = 255;
            }

            using (CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m2-mipgen"))
            {
                cmd.EncodeCreateTexture(texture);
                cmd.EncodeAllocateTexture2D(texture, 0, 4, 4, 0, initial);
                cmd.GenerateMipmap(texture);
                device.Execute(cmd, wait: true);
            }

            MetalTextureResource resource = device.Textures[texture.Handle];
            Assert.Equal(3u, resource.MipLevels);
            byte[] mip1 = device.ReadTexture2D(resource, 1);
            Assert.Equal(2 * 2 * 4, mip1.Length);
            for (int i = 0; i < mip1.Length; i += 4)
            {
                Assert.InRange(mip1[i], 250, 255);
                Assert.InRange(mip1[i + 1], 123, 133);
                Assert.InRange(mip1[i + 2], 59, 69);
                Assert.InRange(mip1[i + 3], 250, 255);
            }
        }
    }

    [MacOSMetalFact]
    public void Execute_SetRenderTargetNull_DoesNotThrow()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using (CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m2-set-rt-null"))
            {
                cmd.SetRenderTarget(null);
                device.Execute(cmd, wait: true);
            }
            Assert.NotEqual(0ul, device.GetFenceValue());
        }
    }

    [MacOSMetalTheory]
    [InlineData(4)]
    [InlineData(64)]
    public void GenerateMipmap_ThenUploadAndRegenerate_PreservesPixels(int size)
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using var texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
            byte[] pixels = new byte[size * size * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
                pixels[i + 1] = 128;
                pixels[i + 2] = 64;
                pixels[i + 3] = 255;
            }

            // Texture2D deserialization constructs a mipmapped blank image, then uploads
            // its serialized pixels and regenerates the chain. GPU blits must stay ordered
            // with that upload even when all operations share one submission.
            using (CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-mipgen-upload-order"))
            {
                cmd.EncodeCreateTexture(texture);
                cmd.EncodeAllocateTexture2D(texture, 0, (uint)size, (uint)size, 0, new byte[pixels.Length]);
                cmd.GenerateMipmap(texture);
                cmd.EncodeUpdateTexture2D(texture, 0, 0, 0, (uint)size, (uint)size, pixels);
                cmd.GenerateMipmap(texture);
                device.Execute(cmd, wait: true);
            }

            MetalTextureResource resource = device.Textures[texture.Handle];
            Assert.Equal(pixels, device.ReadTexture2D(resource, 0));
            byte[] mip1 = device.ReadTexture2D(resource, 1);
            for (int i = 0; i < mip1.Length; i += 4)
            {
                Assert.InRange(mip1[i], 250, 255);
                Assert.InRange(mip1[i + 1], 123, 133);
                Assert.InRange(mip1[i + 2], 59, 69);
                Assert.InRange(mip1[i + 3], 250, 255);
            }
        }
    }


}
