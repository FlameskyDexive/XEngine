// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// CPU contract tests for the editor grid/gizmo depth-copy lifecycle in
/// <see cref="UniversalRenderPipeline"/>. The copy target is a depth-only FBO (zero color
/// attachments), so the recorded command stream must restore the color RT as the draw
/// target before the color pass submits (the gizmos-only path records no SetRenderTarget
/// of its own), and releasing the copy back to the temporary pool must drop its
/// _CameraDepthTexture global binding in the same command buffer so later passes and
/// cameras cannot sample the recycled texture.
/// </summary>
public sealed class EditorDepthCopyContractTests
{
    [Fact]
    public void BindEditorDepthCopy_RestoresColorDrawTarget_AfterDepthBlit()
    {
        var colorRT = new RenderTexture(64, 32, true, [TextureImageFormat.Color4b]);
        CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("editor-depth-bind");
        RenderTexture depthRT = UniversalRenderPipeline.BindEditorDepthCopy(cmd, colorRT);

        try
        {
            // The copy target is depth-only: zero color attachments, same size as the color RT.
            Assert.Empty(depthRT.InternalTextures);
            Assert.NotNull(depthRT.InternalDepth);
            Assert.Equal(colorRT.Width, depthRT.Width);
            Assert.Equal(colorRT.Height, depthRT.Height);

            ReadOnlySpan<byte> stream = cmd._stream.AsSpan(0, cmd._streamPos);
            int pos = 0;
            Assert.Equal(CommandOpcode.BeginSample, ReadOpcode(stream, ref pos));
            Assert.Equal("editor-depth-bind", cmd._objects[ReadU16(stream, ref pos)]);

            Assert.Equal(CommandOpcode.SetRenderTargets, ReadOpcode(stream, ref pos));
            Assert.Same(depthRT.frameBuffer, cmd._objects[ReadU16(stream, ref pos)]);
            Assert.Same(colorRT.frameBuffer, cmd._objects[ReadU16(stream, ref pos)]);

            Assert.Equal(CommandOpcode.BlitFramebuffer, ReadOpcode(stream, ref pos));
            Assert.Equal(0, ReadI32(stream, ref pos)); Assert.Equal(0, ReadI32(stream, ref pos));
            Assert.Equal(colorRT.Width, ReadI32(stream, ref pos));
            Assert.Equal(colorRT.Height, ReadI32(stream, ref pos));
            Assert.Equal(0, ReadI32(stream, ref pos)); Assert.Equal(0, ReadI32(stream, ref pos));
            Assert.Equal(depthRT.Width, ReadI32(stream, ref pos));
            Assert.Equal(depthRT.Height, ReadI32(stream, ref pos));
            Assert.Equal((byte)ClearFlags.Depth, stream[pos]); pos += 1;
            Assert.Equal((byte)BlitFilter.Nearest, stream[pos]); pos += 1;

            // Regression: the blit leaves the zero-color-attachment FBO bound as the draw
            // target; the color RT must be restored before anything else is recorded.
            Assert.Equal(CommandOpcode.SetRenderTarget, ReadOpcode(stream, ref pos));
            Assert.Same(colorRT.frameBuffer, cmd._objects[ReadU16(stream, ref pos)]);

            Assert.Equal(CommandOpcode.SetGlobalTexture, ReadOpcode(stream, ref pos));
            Assert.Equal("_CameraDepthTexture", cmd._objects[ReadU16(stream, ref pos)]);
            Assert.Same(depthRT.InternalDepth, cmd._objects[ReadU16(stream, ref pos)]);

            Assert.Equal(stream.Length, pos);
        }
        finally
        {
            cmd._ownerReleased = true;
            CommandBufferPool.Return(cmd);
            RenderTexture.ReleaseTemporaryRT(depthRT);
            colorRT.Dispose();
        }
    }

    [Fact]
    public void ReleaseEditorDepthCopy_ClearsCameraDepthGlobal_AndReturnsToPool()
    {
        // Distinctive size so no parallel test shares the temporary-pool key.
        RenderTexture depthRT = RenderTexture.GetTemporaryRT(67, 31, true, []);
        CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("editor-depth-release");
        UniversalRenderPipeline.ReleaseEditorDepthCopy(cmd, depthRT);

        RenderTexture reacquired = RenderTexture.GetTemporaryRT(67, 31, true, []);
        try
        {
            // Regression: the global unbind must ride the same command buffer as the pool
            // return, so it executes before any later submission that reuses the texture.
            ReadOnlySpan<byte> stream = cmd._stream.AsSpan(0, cmd._streamPos);
            int pos = 0;
            Assert.Equal(CommandOpcode.BeginSample, ReadOpcode(stream, ref pos));
            Assert.Equal("editor-depth-release", cmd._objects[ReadU16(stream, ref pos)]);
            Assert.Equal(CommandOpcode.ClearGlobalTexture, ReadOpcode(stream, ref pos));
            Assert.Equal("_CameraDepthTexture", cmd._objects[ReadU16(stream, ref pos)]);
            Assert.Equal(stream.Length, pos);

            // The release must have returned the RT to the temporary pool, not leaked it.
            Assert.Same(depthRT, reacquired);
        }
        finally
        {
            cmd._ownerReleased = true;
            CommandBufferPool.Return(cmd);
            RenderTexture.ReleaseTemporaryRT(reacquired);
        }
    }

    private static CommandOpcode ReadOpcode(ReadOnlySpan<byte> s, ref int pos)
    {
        ushort v = (ushort)(s[pos] | (s[pos + 1] << 8));
        pos += 2;
        return (CommandOpcode)v;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, ref int pos)
    {
        ushort v = BitConverter.ToUInt16(s.Slice(pos, 2));
        pos += 2;
        return v;
    }

    private static int ReadI32(ReadOnlySpan<byte> s, ref int pos)
    {
        int v = BitConverter.ToInt32(s.Slice(pos, 4));
        pos += 4;
        return v;
    }
}
