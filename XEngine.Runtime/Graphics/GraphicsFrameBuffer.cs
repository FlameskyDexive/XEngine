// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
using System;

namespace XEngine.Runtime;

public class GraphicsFrameBuffer
{
    private readonly RHI.IGraphicsDevice? _ownerDevice;

    public struct Attachment
    {
        public GraphicsTexture Texture;
        public bool IsDepth;

        /// <summary>When true, the attachment binds a single cubemap face (selected by
        /// <see cref="CubeFace"/>) instead of a 2D texture. Used for rendering into a
        /// reflection-probe cubemap face.</summary>
        public bool IsCubeFace;

        /// <summary>Cubemap face index 0..5 (GL order +X,-X,+Y,-Y,+Z,-Z). Only read when
        /// <see cref="IsCubeFace"/> is true.</summary>
        public int CubeFace;

        /// <summary>Mip level of the texture/face to attach. Defaults to 0 (full 2D RTs).</summary>
        public int MipLevel;
    }

    public uint Handle { get; internal set; }
    public RHI.GpuHandle NativeHandle => new(Handle);
    public uint NumOfAttachments { get; internal set; }
    public uint Width { get; protected set; }
    public uint Height { get; protected set; }

    /// <summary>
    /// Multisample count shared by every attachment (1 = single-sample). Derived from the
    /// attachment textures at construction: D3D12 and Vulkan both require a render pass's color
    /// and depth attachments to agree on sample count, so a mismatch is rejected here rather than
    /// producing a driver error on the render thread.
    /// </summary>
    public uint SampleCount { get; private set; } = 1;

    /// <summary>True when this target is multisampled and must be resolved before being sampled.</summary>
    public bool IsMultisampled => SampleCount > 1;

    /// <summary>Debug name shown by the Frame Debugger and GPU debug tooling (e.g.
    /// "_CameraColorTexture", "Game view"). Null falls back to "FBO WxH".</summary>
    public string? DebugName { get; set; }

    // Construction params retained for deferred creation by each backend.
    private readonly Attachment[] _attachments;

    internal Attachment[] Attachments => _attachments;

    public GraphicsFrameBuffer(Attachment[] attachments, uint width, uint height)
        : this(attachments, width, height, submitCreate: true)
    {
    }

    internal static GraphicsFrameBuffer CreateDeferred(Attachment[] attachments, uint width, uint height) =>
        new(attachments, width, height, submitCreate: false);

    private GraphicsFrameBuffer(Attachment[] attachments, uint width, uint height, bool submitCreate)
    {
        int numTextures = attachments.Length;
        if (numTextures < 0 || numTextures > Graphics.MaxFramebufferColorAttachments)
            throw new Exception("[FrameBuffer] Invalid number of textures! [0-" + Graphics.MaxFramebufferColorAttachments + "]");

        _attachments = attachments;
        _ownerDevice = Graphics.Device;
        SampleCount = ResolveSampleCount(attachments);
        NumOfAttachments = (uint)numTextures;
        Width = width;
        Height = height;
        Handle = 0;

        // SubmitAndWait so the completeness check throws at construction
        // instead of on a later render-thread tick.
        if (submitCreate)
        {
            using var cmd = Graphics.GetCommandBuffer("GraphicsFrameBuffer.Create");
            cmd.EncodeCreateFramebuffer(this);
            Graphics.SubmitAndWait(cmd);
        }
    }

    private static uint ResolveSampleCount(Attachment[] attachments)
    {
        uint samples = 1;
        bool seen = false;
        for (int i = 0; i < attachments.Length; i++)
        {
            GraphicsTexture? texture = attachments[i].Texture;
            if (texture == null)
                continue;
            uint attachmentSamples = texture.SampleCount < 1 ? 1 : texture.SampleCount;
            if (!seen)
            {
                samples = attachmentSamples;
                seen = true;
            }
            else if (attachmentSamples != samples)
            {
                throw new ArgumentException(
                    $"[FrameBuffer] All attachments must share one sample count (found {samples}x and {attachmentSamples}x).",
                    nameof(attachments));
            }
        }
        return samples;
    }

    public bool IsDisposed { get; protected set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        if (!ReferenceEquals(_ownerDevice, Graphics.Device))
            return;

        using var cmd = Graphics.GetCommandBuffer("GraphicsFrameBuffer.Dispose");
        cmd.EncodeDisposeFramebuffer(this);
        Graphics.Submit(cmd);
    }
    public override string ToString()
    {
        return Handle.ToString();
    }
}
