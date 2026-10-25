// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WG2: WebGPU buffer / texture / sampler / GenerateMipmap against wgpu-native v29.
/// GPU-less machines skip with the same availability exceptions as WG1.
/// </summary>
public class WebGPUResourceTests
{
    [Fact]
    public void Buffer_CreateUploadGrowAndReadback_Or_Skip()
    {
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            byte[] initial = [1, 2, 3, 4, 5, 6, 7, 8];
            using var buffer = new GraphicsBuffer(BufferType.VertexBuffer, initial, dynamic: true);

            using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg2-buffer-create"))
            {
                create.EncodeCreateBuffer(buffer, dynamic: true, initial);
                device.Execute(create, wait: true);
            }

            Assert.NotEqual(0u, buffer.Handle);
            WebGPUBufferResource resource = device.Buffers[buffer.Handle];
            Assert.True(resource.Size >= (ulong)initial.Length);
            Assert.Equal(initial, device.ReadBuffer(resource, 0, initial.Length));

            byte[] grown = new byte[32];
            for (int i = 0; i < grown.Length; i++)
                grown[i] = (byte)(i + 10);

            using (CommandBuffer update = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg2-buffer-grow"))
            {
                update.UpdateBuffer<byte>(buffer, grown);
                device.Execute(update, wait: true);
            }

            resource = device.Buffers[buffer.Handle];
            Assert.True(resource.Size >= (ulong)grown.Length);
            Assert.Equal(grown, device.ReadBuffer(resource, 0, grown.Length));
        }
    }

    [Fact]
    public void Texture2D_AllocateUpdateReadback_Or_Skip()
    {
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

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

            using (CommandBuffer upload = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg2-texture-upload"))
            {
                upload.EncodeCreateTexture(texture);
                upload.EncodeAllocateTexture2D(texture, 0, 4, 4, 0, initial);
                upload.EncodeUpdateTexture2D(texture, 0, 1, 1, 2, 2, update);
                device.Execute(upload, wait: true);
            }

            WebGPUTextureResource resource = device.Textures[texture.Handle];
            Assert.Equal(4u, resource.Width);
            Assert.Equal(4u, resource.Height);
            byte[] expected = new byte[initial.Length];
            Buffer.BlockCopy(update, 0, expected, (1 * 4 + 1) * 4, 2 * 4);
            Buffer.BlockCopy(update, 2 * 4, expected, (2 * 4 + 1) * 4, 2 * 4);
            Assert.Equal(expected, device.ReadTexture2D(resource, 0));
        }
    }

    [Fact]
    public void Texture_WrapAndFilters_AreStored_Or_Skip()
    {
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using var texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);

            using (CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg2-sampler"))
            {
                cmd.EncodeCreateTexture(texture);
                cmd.EncodeAllocateTexture2D(texture, 0, 1, 1, 0, new byte[] { 16, 32, 48, 64 });
                cmd.EncodeSetTextureWrap(texture, 0, TextureWrap.ClampToEdge);
                cmd.EncodeSetTextureWrap(texture, 1, TextureWrap.MirroredRepeat);
                cmd.EncodeSetTextureFilters(texture, TextureMin.Nearest, TextureMag.Nearest);
                device.Execute(cmd, wait: true);
            }

            WebGPUTextureResource resource = device.Textures[texture.Handle];
            Assert.Equal(TextureWrap.ClampToEdge, resource.WrapS);
            Assert.Equal(TextureWrap.MirroredRepeat, resource.WrapT);
            Assert.Equal(TextureMin.Nearest, resource.MinFilter);
            Assert.Equal(TextureMag.Nearest, resource.MagFilter);
            Assert.NotEqual(nint.Zero, resource.Sampler);
        }
    }

    [Fact]
    public void GenerateMipmap_4x4Uniform_ProducesMip1_Or_Skip()
    {
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

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

            using (CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg2-mipgen"))
            {
                cmd.EncodeCreateTexture(texture);
                cmd.EncodeAllocateTexture2D(texture, 0, 4, 4, 0, initial);
                cmd.GenerateMipmap(texture);
                device.Execute(cmd, wait: true);
            }

            WebGPUTextureResource resource = device.Textures[texture.Handle];
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

    [Fact]
    public void Execute_SetRenderTargetNull_DoesNotThrow_Or_Skip()
    {
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using (CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg5-set-rt-null"))
            {
                cmd.SetRenderTarget(null);
                device.Execute(cmd, wait: true);
            }
            Assert.NotEqual(0ul, device.GetFenceValue());
        }
    }

    private static bool TryCreateDevice(out WebGPUGraphicsDevice device)
    {
        try
        {
            device = new WebGPUGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.WebGPU });
            return true;
        }
        catch (Exception ex)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected WebGPU create failure: {ex.GetType().FullName}: {ex.Message}");
            device = null!;
            return false;
        }
    }

    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                return true;
            }

            if (current is InvalidOperationException && IsExplicitGpuAvailabilityMessage(current.Message))
                return true;
        }

        return false;
    }

    private static bool IsExplicitGpuAvailabilityMessage(string message) =>
        message.StartsWith("Failed to create WebGPU device. wgpu-native is unavailable", StringComparison.Ordinal)
        || message.StartsWith("wgpuCreateInstance returned null", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU adapter:", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU device:", StringComparison.Ordinal)
        || message.Equals("wgpuDeviceGetQueue returned null.", StringComparison.Ordinal);
}
