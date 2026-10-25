// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Runtime.Backends.Metal;

internal sealed unsafe partial class MetalCommandTranslator
{
    private MTLPixelFormat _passDepthFormat = MTLPixelFormat.Invalid;

    private void BindPendingRenderTarget(GraphicsFrameBuffer? framebuffer)
    {
        MetalPassRecombiner.BindAction action = _pass.Bind(framebuffer, passOpen: _renderEncoder != 0);
        if (action == MetalPassRecombiner.BindAction.KeepPending)
            return;

        if (action == MetalPassRecombiner.BindAction.FlushThenSwitch)
            BeginPassIfNeeded();

        EndPass();
        _pass.ApplyBind(framebuffer, action == MetalPassRecombiner.BindAction.FlushThenSwitch
            ? MetalPassRecombiner.BindAction.SwitchAndReset
            : action);
        if (action == MetalPassRecombiner.BindAction.FlushThenSwitch)
            _pass.PendingTarget = framebuffer;
    }

    private void BeginPassIfNeeded()
    {
        if (_renderEncoder != 0)
            return;
        if (_commandBuffer == 0)
            return;
        if (!_pass.HasPendingLoadOps && _pass.PendingTarget == null &&
            _device.ScratchTexture == 0 && _device.CurrentDrawable == 0)
            return;

        EndBlit();
        ResolveAttachments(
            out nint colorTexture,
            out MTLPixelFormat colorFormat,
            out uint width,
            out uint height,
            out nint depthTexture,
            out MTLPixelFormat depthFormat,
            out uint colorMip,
            out uint depthMip);

        nint descriptor = MetalNative.NewRenderPassDescriptor();
        if (descriptor == 0)
            throw new InvalidOperationException("MTLRenderPassDescriptor renderPassDescriptor returned nil.");

        try
        {
            if (colorTexture != 0)
            {
                nint color = MetalNative.ColorAttachment(descriptor, 0);
                MetalNative.SetAttachmentTexture(color, colorTexture, colorMip);
                MetalNative.SetAttachmentLoadStore(
                    color,
                    _pass.ColorLoadClear ? MTLLoadAction.Clear : MTLLoadAction.Load,
                    MTLStoreAction.Store);
                if (_pass.ColorLoadClear)
                    MetalNative.SetClearColor(color, _pass.ColorR, _pass.ColorG, _pass.ColorB, _pass.ColorA);
            }

            if (depthTexture != 0)
            {
                nint depth = MetalNative.DepthAttachment(descriptor);
                MetalNative.SetAttachmentTexture(depth, depthTexture, depthMip);
                MetalNative.SetAttachmentLoadStore(
                    depth,
                    _pass.DepthLoadClear ? MTLLoadAction.Clear : MTLLoadAction.Load,
                    MTLStoreAction.Store);
                if (_pass.DepthLoadClear)
                    MetalNative.SetClearDepth(depth, _pass.DepthClear);

                // A packed depth-stencil format REQUIRES the stencil attachment to be configured
                // too — renderCommandEncoderWithDescriptor returns nil (validation failure)
                // otherwise. Both halves share the texture.
                if (MetalFormats.HasStencil(depthFormat))
                {
                    nint stencil = MetalNative.StencilAttachment(descriptor);
                    MetalNative.SetAttachmentTexture(stencil, depthTexture, depthMip);
                    MetalNative.SetAttachmentLoadStore(
                        stencil,
                        _pass.DepthLoadClear ? MTLLoadAction.Clear : MTLLoadAction.Load,
                        MTLStoreAction.Store);
                    if (_pass.DepthLoadClear)
                        MetalNative.SetClearDepth(stencil, 0);
                }
            }

            _renderEncoder = MetalNative.RenderCommandEncoder(_commandBuffer, descriptor);
            if (_renderEncoder == 0)
                throw new InvalidOperationException("MTLCommandBuffer renderCommandEncoderWithDescriptor returned nil.");
        }
        finally
        {
            MetalNative.Release(descriptor);
        }

        _passWidth = width;
        _passHeight = height;
        _passDepthFormat = depthFormat;
        if (!_viewportSet)
        {
            _viewportX = 0;
            _viewportY = 0;
            _viewportWidth = width;
            _viewportHeight = height;
        }

        ApplyViewport();
        ApplyScissor();
        ApplyRaster();
        _pass.ResetLoadOps();
        _ = colorFormat;
        _ = depthFormat;
    }

    private void EndPass()
    {
        if (_renderEncoder == 0)
            return;
        MetalNative.EndEncoding(_renderEncoder);
        _renderEncoder = 0;
        _passDepthFormat = MTLPixelFormat.Invalid;
    }

    private void ApplyViewport()
    {
        if (_renderEncoder == 0)
            return;
        MetalNative.SetViewport(
            _renderEncoder,
            _viewportX,
            _viewportY,
            Math.Max(1u, _viewportWidth),
            Math.Max(1u, _viewportHeight));
    }

    private void ApplyScissor()
    {
        if (_renderEncoder == 0)
            return;
        uint x;
        uint y;
        uint width;
        uint height;
        if (_scissorSet)
        {
            x = (uint)Math.Max(0, _scissorX);
            y = (uint)Math.Max(0, _scissorY);
            width = Math.Max(1u, _scissorWidth);
            height = Math.Max(1u, _scissorHeight);
        }
        else
        {
            x = 0;
            y = 0;
            width = Math.Max(1u, _passWidth);
            height = Math.Max(1u, _passHeight);
        }

        MetalNative.SetScissorRect(_renderEncoder, x, y, width, height);
    }

    private void ApplyRaster()
    {
        if (_renderEncoder == 0)
            return;
        MetalNative.SetCullMode(_renderEncoder, MetalFormats.ToCullMode(_currentRaster.CullFace));
        MetalNative.SetFrontFacing(_renderEncoder, MetalFormats.ToWinding(_currentRaster.Winding));
        RasterizerState depthState = _currentRaster;
        if (_passDepthFormat == MTLPixelFormat.Invalid)
        {
            depthState.DepthTest = false;
            depthState.DepthWrite = false;
        }
        nint depthStencil = _device.GetOrCreateDepthStencilState_NoLock(in depthState);
        MetalNative.SetDepthStencilState(_renderEncoder, depthStencil);
    }

    private void ResolveAttachments(
        out nint colorTexture,
        out MTLPixelFormat colorFormat,
        out uint width,
        out uint height,
        out nint depthTexture,
        out MTLPixelFormat depthFormat,
        out uint colorMip,
        out uint depthMip)
    {
        colorTexture = 0;
        colorFormat = MTLPixelFormat.BGRA8Unorm;
        width = (uint)Math.Max(1, _device.ScratchWidth);
        height = (uint)Math.Max(1, _device.ScratchHeight);
        depthTexture = 0;
        depthFormat = MTLPixelFormat.Invalid;
        colorMip = 0;
        depthMip = 0;

        GraphicsFrameBuffer? framebuffer = _pass.PendingTarget;
        if (framebuffer == null)
        {
            // Windowed presentation: with a CAMetalLayer there is no scratch texture — the
            // default render target is the current drawable's texture (presented at EndFrame).
            if (_device.ScratchTexture != 0)
            {
                colorTexture = _device.ScratchTexture;
            }
            else if (_device.CurrentDrawable != 0)
            {
                colorTexture = MetalNative.DrawableTexture(_device.CurrentDrawable);
                colorFormat = MTLPixelFormat.BGRA8Unorm;
            }
            return;
        }

        if (framebuffer.Handle == 0)
            CreateFramebuffer(framebuffer);
        if (!_device.Framebuffers.TryGetValue(framebuffer.Handle, out MetalFramebufferResource? resource))
            throw new InvalidOperationException("Metal framebuffer is not ready.");

        width = resource.Width;
        height = resource.Height;
        if (resource.ColorHandles.Length > 0 &&
            _device.Textures.TryGetValue(resource.ColorHandles[0], out MetalTextureResource? color) &&
            color.Texture != 0)
        {
            colorTexture = color.Texture;
            colorFormat = color.Format;
            colorMip = (uint)Math.Max(0, resource.ColorMips[0]);
        }

        if (resource.DepthHandle != 0 &&
            _device.Textures.TryGetValue(resource.DepthHandle, out MetalTextureResource? depth) &&
            depth.Texture != 0)
        {
            depthTexture = depth.Texture;
            depthFormat = depth.Format;
            depthMip = (uint)Math.Max(0, resource.DepthMip);
        }
    }

    private void CreateVertexArray(GraphicsVertexArray vao)
    {
        uint handle = _device.AllocateHandle();
        vao.Handle = handle;
        _device.VertexArrays[handle] = new MetalVertexArrayResource
        {
            VertexBuffer = vao.Vertices.Handle,
            InstanceBuffer = vao.InstanceBuffer?.Handle ?? 0,
            IndexBuffer = vao.Indices?.Handle ?? 0,
            Format = vao.Format,
            InstanceFormat = vao.InstanceFormat,
        };
    }

    private void CreateFramebuffer(GraphicsFrameBuffer framebuffer)
    {
        GraphicsFrameBuffer.Attachment[] attachments = framebuffer.Attachments;
        if (attachments.Length == 0)
            throw new NotSupportedException("Metal framebuffers require at least one attachment.");

        int colorCount = 0;
        int depthCount = 0;
        for (int i = 0; i < attachments.Length; i++)
        {
            if (attachments[i].IsCubeFace)
                throw new NotSupportedException("Metal cubemap framebuffer attachments are not supported yet.");
            if (attachments[i].IsDepth)
                depthCount++;
            else
                colorCount++;
        }

        if (colorCount > 4 || depthCount > 1)
            throw new NotSupportedException("Metal framebuffers support up to four color attachments and at most one depth attachment.");

        var colorHandles = new uint[colorCount];
        var colorMips = new int[colorCount];
        uint depthHandle = 0;
        int depthMip = 0;
        int colorIndex = 0;
        for (int i = 0; i < attachments.Length; i++)
        {
            GraphicsFrameBuffer.Attachment attachment = attachments[i];
            GraphicsTexture texture = attachment.Texture;
            if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? resource) || resource.Texture == 0)
                throw new InvalidOperationException("Metal framebuffer attachment is not ready.");
            if (resource.Type != TextureType.Texture2D)
                throw new NotSupportedException("Metal framebuffer attachments must be Texture2D resources.");

            if (attachment.IsDepth)
            {
                depthHandle = texture.Handle;
                depthMip = attachment.MipLevel;
            }
            else
            {
                colorHandles[colorIndex] = texture.Handle;
                colorMips[colorIndex] = attachment.MipLevel;
                colorIndex++;
            }
        }

        uint handle = _device.AllocateHandle();
        framebuffer.Handle = handle;
        _device.Framebuffers[handle] = new MetalFramebufferResource
        {
            ColorHandles = colorHandles,
            ColorMips = colorMips,
            DepthHandle = depthHandle,
            DepthMip = depthMip,
            Width = framebuffer.Width,
            Height = framebuffer.Height,
        };
    }

    private void Draw(
        GraphicsVertexArray? vao,
        Topology topology,
        bool indexed,
        bool index32Bit,
        uint count,
        uint instanceCount,
        uint firstIndex,
        int baseVertex,
        int firstVertex)
    {
        BeginPassIfNeeded();
        nint vertexFunction = _mslVertex;
        nint fragmentFunction = _mslFragment;
        if (vertexFunction == 0 || fragmentFunction == 0)
        {
            // Production draws resolve MTLLibrary functions from the compiled MetalLib variant;
            // manually bound test functions (BindMslFunctions) keep precedence.
            (vertexFunction, fragmentFunction) = _device.GetOrCreateShaderFunctions_NoLock(_currentShader);
        }
        if (vertexFunction == 0 || fragmentFunction == 0)
            return;

        ShaderVariant shader = _currentShader ?? throw new InvalidOperationException("Metal draw requires SetShader.");
        uint vaoHandle = vao?.Handle ?? 0;
        // The window/scratch target is BGRA8 on Metal. An explicit framebuffer starts with no
        // color attachment until one is resolved below; depth-only shadow passes must keep
        // color attachment 0 invalid in the pipeline descriptor.
        MTLPixelFormat colorFormat = _pass.PendingTarget == null
            ? MTLPixelFormat.BGRA8Unorm
            : MTLPixelFormat.Invalid;
        MTLPixelFormat depthFormat = MTLPixelFormat.Invalid;
        if (_pass.PendingTarget != null &&
            _device.Framebuffers.TryGetValue(_pass.PendingTarget.Handle, out MetalFramebufferResource? fb))
        {
            if (fb.ColorHandles.Length > 0 &&
                _device.Textures.TryGetValue(fb.ColorHandles[0], out MetalTextureResource? color))
            {
                colorFormat = color.Format;
            }

            if (fb.DepthHandle != 0 &&
                _device.Textures.TryGetValue(fb.DepthHandle, out MetalTextureResource? depth))
            {
                depthFormat = depth.Format;
            }
        }
        else if (_device.ScratchTexture != 0)
            colorFormat = MTLPixelFormat.BGRA8Unorm;

        var key = new GraphicsPipelineKey(shader, vaoHandle, topology, in _currentRaster, index32Bit, vao?.Format, vao?.InstanceFormat);
        nint vertexDescriptor = vao?.Format != null
            ? _device.GetOrCreateVertexDescriptor_NoLock(vao.Format, vao.InstanceFormat, shader.Bytecode?.VertexInputLayout, shader.Id)
            : 0;
        nint pipeline = _device.GetOrCreateGraphicsPipeline_NoLock(
            key,
            vertexFunction,
            fragmentFunction,
            new MetalRenderTargetFormats(colorFormat, depthFormat),
            vertexDescriptor);
        MetalNative.SetRenderPipelineState(_renderEncoder, pipeline);
        ApplyRaster();
        BindShaderResources(shader);

        MetalVertexArrayResource? vertexArray = null;
        if (vaoHandle != 0)
            _device.VertexArrays.TryGetValue(vaoHandle, out vertexArray);

        if (vertexArray != null &&
            _device.Buffers.TryGetValue(vertexArray.VertexBuffer, out MetalBufferResource? vertexBuffer) &&
            vertexBuffer.Buffer != 0)
        {
            // Vertex data owns the dedicated high buffer index (MetalBufferSlotVertexData) —
            // cbuffers occupy the low per-kind indices below it.
            MetalNative.SetVertexBuffer(_renderEncoder, vertexBuffer.Buffer, 0, MetalCommandTranslator.MetalVertexDataBufferIndex);
        }

        if (vertexArray?.InstanceFormat != null)
        {
            if (vertexArray.InstanceBuffer == 0 ||
                !_device.Buffers.TryGetValue(vertexArray.InstanceBuffer, out MetalBufferResource? instanceBuffer) ||
                instanceBuffer.Buffer == 0)
            {
                WarnOnce("DrawIndexedInstanced",
                    "Metal instanced draw skipped: vertex-array instance stream is incomplete.");
                return;
            }

            MetalNative.SetVertexBuffer(
                _renderEncoder,
                instanceBuffer.Buffer,
                0,
                MetalCommandTranslator.MetalInstanceDataBufferIndex);
        }

        MTLPrimitiveType primitive = MetalFormats.ToPrimitiveType(topology);
        if (indexed)
        {
            if (vertexArray == null ||
                !_device.Buffers.TryGetValue(vertexArray.IndexBuffer, out MetalBufferResource? indexBuffer) ||
                indexBuffer.Buffer == 0)
            {
                return;
            }

            nuint stride = index32Bit ? 4u : 2u;
            MetalNative.DrawIndexed(
                _renderEncoder,
                primitive,
                count,
                index32Bit ? MTLIndexType.UInt32 : MTLIndexType.UInt16,
                indexBuffer.Buffer,
                firstIndex * stride,
                instanceCount);
            return;
        }

        MetalNative.DrawPrimitives(_renderEncoder, primitive, checked((uint)Math.Max(0, firstVertex)), count, instanceCount);
        _ = baseVertex;
    }

    private void BlitFramebuffer(
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        int dstX,
        int dstY,
        int dstWidth,
        int dstHeight,
        ClearFlags mask)
    {
        EndPass();
        if (mask == 0)
            return;
        if (srcWidth != dstWidth || srcHeight != dstHeight)
            throw new NotSupportedException("Metal framebuffer blit currently requires matching source and destination extents.");
        if (srcWidth <= 0 || srcHeight <= 0)
            return;
        if (_pass.PendingTarget == null || _pass.PendingReadTarget == null)
            throw new InvalidOperationException("Metal framebuffer blit requires draw and read targets.");

        if (_pass.PendingTarget.Handle == 0)
            CreateFramebuffer(_pass.PendingTarget);
        if (_pass.PendingReadTarget.Handle == 0)
            CreateFramebuffer(_pass.PendingReadTarget);
        if (!_device.Framebuffers.TryGetValue(_pass.PendingTarget.Handle, out MetalFramebufferResource? destination) ||
            !_device.Framebuffers.TryGetValue(_pass.PendingReadTarget.Handle, out MetalFramebufferResource? source))
        {
            throw new InvalidOperationException("Metal framebuffer blit targets are not ready.");
        }

        uint srcHandle;
        uint dstHandle;
        int srcMip;
        int dstMip;
        if (mask == ClearFlags.Depth)
        {
            srcHandle = source.DepthHandle;
            dstHandle = destination.DepthHandle;
            srcMip = source.DepthMip;
            dstMip = destination.DepthMip;
        }
        else if (mask == ClearFlags.Color)
        {
            if (source.ColorHandles.Length == 0 || destination.ColorHandles.Length == 0)
                throw new InvalidOperationException("Metal color framebuffer blit requires a color attachment on each target.");
            srcHandle = source.ColorHandles[0];
            dstHandle = destination.ColorHandles[0];
            srcMip = source.ColorMips[0];
            dstMip = destination.ColorMips[0];
        }
        else
        {
            throw new NotSupportedException("Metal framebuffer blits currently support color-only or depth-only masks.");
        }

        if (srcHandle == 0 || dstHandle == 0)
            throw new InvalidOperationException("Metal framebuffer blit is missing an attachment.");
        if (!_device.Textures.TryGetValue(srcHandle, out MetalTextureResource? srcTex) || srcTex.Texture == 0 ||
            !_device.Textures.TryGetValue(dstHandle, out MetalTextureResource? dstTex) || dstTex.Texture == 0)
        {
            throw new InvalidOperationException("Metal framebuffer blit attachment is not ready.");
        }

        nint blit = EnsureBlit();
        MetalNative.CopyTextureToTexture(
            blit,
            srcTex.Texture,
            dstTex.Texture,
            (uint)srcX,
            (uint)srcY,
            (uint)dstX,
            (uint)dstY,
            (uint)srcWidth,
            (uint)srcHeight,
            (uint)Math.Max(0, srcMip),
            (uint)Math.Max(0, dstMip));
        EndBlit();
    }
}
