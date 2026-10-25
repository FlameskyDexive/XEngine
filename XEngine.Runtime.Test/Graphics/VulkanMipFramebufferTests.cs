using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.RHI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class VulkanMipFramebufferTests
{
    [Fact]
    public void MipPromotion_RebuildsEveryFramebuffer_AndSurvivesRetirement()
    {
        try
        {
            using var device = new VulkanGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Vulkan, EnableValidation = true });
        device.Initialize(null);
        using var texture = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
        var a = GraphicsFrameBuffer.CreateDeferred([new GraphicsFrameBuffer.Attachment { Texture = texture }], 4, 4);
        var b = GraphicsFrameBuffer.CreateDeferred([new GraphicsFrameBuffer.Attachment { Texture = texture }], 4, 4);
        using (var create = Graphics.GetCommandBuffer("mip-framebuffer-create"))
        {
            create.EncodeCreateTexture(texture);
            create.EncodeAllocateTexture2D(texture, 0, 4, 4, 0, new byte[64]);
            create.EncodeCreateFramebuffer(a);
            create.EncodeCreateFramebuffer(b);
            device.Execute(create, wait: true);
        }
        ulong oldImage = device.Images[texture.Handle].Image.Handle;
        uint oldA = a.Handle, oldB = b.Handle;
        using (var mip = Graphics.GetCommandBuffer("mip-promote"))
        {
            mip.GenerateMipmap(texture);
            device.Execute(mip, wait: true); // retires/destroys the OLD image now
        }
        ulong replacement = device.Images[texture.Handle].Image.Handle;
        Assert.NotEqual(oldImage, replacement);
        Assert.False(device.Framebuffers.ContainsKey(oldA));
        Assert.False(device.Framebuffers.ContainsKey(oldB));
        Assert.Equal(replacement, Assert.Single(device.Framebuffers[a.Handle].AttachmentImages));
        Assert.Equal(replacement, Assert.Single(device.Framebuffers[b.Handle].AttachmentImages));
        for (int i = 0; i < 4; i++)
        {
            using var clear = Graphics.GetCommandBuffer("render-after-mip-retirement");
            clear.SetRenderTarget(i % 2 == 0 ? a : b);
            clear.ClearRenderTarget(ClearFlags.Color, new Color(1f, 0f, 0f, 1f));
            device.Execute(clear, wait: true);
            byte[] pixels = device.ReadTexture2D(device.Images[texture.Handle], 4);
            Assert.Equal(255, pixels[0]);
            Assert.Equal(0, pixels[1]);
            Assert.Equal(0, pixels[2]);
            Assert.Equal(255, pixels[3]);
        }
        using var dispose = Graphics.GetCommandBuffer("mip-framebuffer-dispose");
        dispose.EncodeDisposeFramebuffer(a);
        dispose.EncodeDisposeFramebuffer(b);
        dispose.EncodeDisposeTexture(texture);
        device.Execute(dispose, wait: true);
        }
        catch (Exception ex) when (GraphicsTestAvailability.IsBackendUnavailable(ex))
        {
            // Host without a Vulkan loader (e.g. macOS) — skip.
            return;
        }
    }
}
