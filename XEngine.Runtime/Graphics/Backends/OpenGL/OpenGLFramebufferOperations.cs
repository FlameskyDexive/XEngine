// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.OpenGL;

namespace XEngine.Runtime.Backends.OpenGL;

/// <summary>OpenGL framebuffer creation and attachment binding kept behind the backend boundary.</summary>
internal static class OpenGLFramebufferOperations
{
    private static readonly GLEnum[] s_drawBuffers =
    [
        GLEnum.ColorAttachment0,  GLEnum.ColorAttachment1,  GLEnum.ColorAttachment2,
        GLEnum.ColorAttachment3,  GLEnum.ColorAttachment4,  GLEnum.ColorAttachment5,
        GLEnum.ColorAttachment6,  GLEnum.ColorAttachment7,  GLEnum.ColorAttachment8,
        GLEnum.ColorAttachment9,  GLEnum.ColorAttachment10, GLEnum.ColorAttachment11,
        GLEnum.ColorAttachment12, GLEnum.ColorAttachment13, GLEnum.ColorAttachment14,
        GLEnum.ColorAttachment15, GLEnum.ColorAttachment16, GLEnum.ColorAttachment16,
        GLEnum.ColorAttachment17, GLEnum.ColorAttachment18, GLEnum.ColorAttachment19,
        GLEnum.ColorAttachment20, GLEnum.ColorAttachment21, GLEnum.ColorAttachment22,
        GLEnum.ColorAttachment23, GLEnum.ColorAttachment24, GLEnum.ColorAttachment25,
        GLEnum.ColorAttachment26, GLEnum.ColorAttachment27, GLEnum.ColorAttachment28,
        GLEnum.ColorAttachment29, GLEnum.ColorAttachment30, GLEnum.ColorAttachment31,
    ];

    public static void Create(GL gl, GraphicsFrameBuffer framebuffer)
    {
        framebuffer.Handle = gl.GenFramebuffer();
        if (framebuffer.Handle == 0)
            throw new Exception("[FrameBuffer] Failed to generate new FrameBuffer.");

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.Handle);

        GraphicsFrameBuffer.Attachment[] attachments = framebuffer.Attachments;
        for (int i = 0; i < attachments.Length; i++)
        {
            ref readonly GraphicsFrameBuffer.Attachment attachment = ref attachments[i];
            if (attachment.IsDepth)
            {
                gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.DepthAttachment,
                    TextureTarget.Texture2D,
                    attachment.Texture.Handle,
                    attachment.MipLevel);
                continue;
            }

            TextureTarget textureTarget = attachment.IsCubeFace
                ? TextureTarget.TextureCubeMapPositiveX + attachment.CubeFace
                : OpenGLTextureOperations.GetTarget(attachment.Texture.Type);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0 + i,
                textureTarget,
                attachment.Texture.Handle,
                attachment.MipLevel);
        }

        if (attachments.Length > 0)
            gl.DrawBuffers((uint)attachments.Length, s_drawBuffers);

        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new Exception("RenderTexture: [ID {fboId}] RenderTexture object creation failed.");

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public static void Delete(GL gl, GraphicsFrameBuffer framebuffer)
    {
        if (framebuffer.Handle == 0)
            return;

        gl.DeleteFramebuffer(framebuffer.Handle);
        framebuffer.Handle = 0;
    }
}
