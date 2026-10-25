// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Backends.WebGPU.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Runtime.Backends.WebGPU;

internal sealed unsafe partial class WebGPUCommandTranslator
{
    internal void ReleaseGpu()
    {
        AbandonEncoder();
        ReleaseScratch();
    }

    private void ResetPendingLoadOps()
    {
        _colorLoadClear = false;
        _depthLoadClear = false;
        _stencilLoadClear = false;
        _colorClear = default;
        _depthClear = 1f;
        _stencilClear = 0;
    }

    private bool HasPendingLoadOps => _colorLoadClear || _depthLoadClear || _stencilLoadClear;

    /// <summary>
    /// Rebinding the same framebuffer must keep pending load-ops. Universal Pipeline does
    /// <c>SetRenderTarget</c> → <c>Clear</c> → <c>SetRenderTarget</c> (same RT) before any draw;
    /// dropping the clear would leave the camera color black and the final blit copies zeros.
    /// Switching targets flushes a clear-only pass first so the previous RT still receives it.
    /// </summary>
    private void BindPendingRenderTarget(GraphicsFrameBuffer? framebuffer)
    {
        bool same = ReferenceEquals(framebuffer, _pendingRenderTarget);
        if (same && _pass == 0)
            return;

        if (!same && _pass == 0 && HasPendingLoadOps)
            BeginPassIfNeeded();

        EndPass();
        if (!same)
        {
            _pendingRenderTarget = framebuffer;
            ResetPendingLoadOps();
        }
    }

    private void RecordClear(ClearFlags flags, float r, float g, float b, float a, float depth, int stencil)
    {
        if (_pass != 0)
            EndPass();

        if ((flags & ClearFlags.Color) != 0)
        {
            _colorLoadClear = true;
            _colorClear = new WGPUColor { r = r, g = g, b = b, a = a };
        }

        if ((flags & ClearFlags.Depth) != 0)
        {
            _depthLoadClear = true;
            _depthClear = depth;
        }

        if ((flags & ClearFlags.Stencil) != 0)
        {
            _stencilLoadClear = true;
            _stencilClear = (uint)stencil;
        }
    }

    private void BeginPassIfNeeded()
    {
        if (_pass != 0)
            return;

        ResolveAttachments(
            out nint colorView,
            out WGPUTextureFormat colorFormat,
            out uint width,
            out uint height,
            out nint depthView,
            out WGPUTextureFormat depthFormat);

        nint encoder = GetEncoder();
        WGPURenderPassColorAttachment color = default;
        bool hasColor = colorView != 0;
        if (hasColor)
        {
            color = new WGPURenderPassColorAttachment
            {
                view = colorView,
                depthSlice = WebGPUNative.DepthSliceUndefined,
                loadOp = _colorLoadClear ? WGPULoadOp.Clear : WGPULoadOp.Load,
                storeOp = WGPUStoreOp.Store,
                clearValue = _colorClear,
            };
        }
        WGPURenderPassDepthStencilAttachment depth = default;
        bool hasDepth = depthView != 0;
        if (hasDepth)
        {
            bool stencil = WebGPUFormats.HasStencil(depthFormat);
            depth = new WGPURenderPassDepthStencilAttachment
            {
                view = depthView,
                depthLoadOp = _depthLoadClear ? WGPULoadOp.Clear : WGPULoadOp.Load,
                depthStoreOp = WGPUStoreOp.Store,
                depthClearValue = _depthClear,
                stencilLoadOp = stencil
                    ? (_stencilLoadClear ? WGPULoadOp.Clear : WGPULoadOp.Load)
                    : WGPULoadOp.Undefined,
                stencilStoreOp = stencil ? WGPUStoreOp.Store : WGPUStoreOp.Undefined,
                stencilClearValue = _stencilClear,
                stencilReadOnly = stencil ? 0u : 1u,
            };
        }

        WGPURenderPassDescriptor descriptor = new()
        {
            colorAttachmentCount = hasColor ? 1u : 0u,
            colorAttachments = hasColor ? &color : null,
            depthStencilAttachment = hasDepth ? &depth : null,
        };
        _pass = WebGPUNative.wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
        if (_pass == 0)
            throw new InvalidOperationException("wgpuCommandEncoderBeginRenderPass returned null.");

        _passWidth = width;
        _passHeight = height;
        if (!_viewportSet)
        {
            _viewportX = 0;
            _viewportY = 0;
            _viewportWidth = width;
            _viewportHeight = height;
        }

        ApplyViewport();
        ApplyScissor();
        ResetPendingLoadOps();
        _ = colorFormat;
    }

    private void EndPass()
    {
        if (_pass == 0)
            return;
        WebGPUNative.wgpuRenderPassEncoderEnd(_pass);
        WebGPUNative.wgpuRenderPassEncoderRelease(_pass);
        _pass = 0;
    }

    private void ApplyViewport()
    {
        if (_pass == 0)
            return;
        float width = Math.Max(1u, _viewportWidth);
        float height = Math.Max(1u, _viewportHeight);
        WebGPUNative.wgpuRenderPassEncoderSetViewport(
            _pass,
            _viewportX,
            _viewportY,
            width,
            height,
            0f,
            1f);
    }

    private void ApplyScissor()
    {
        if (_pass == 0)
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

        WebGPUNative.wgpuRenderPassEncoderSetScissorRect(_pass, x, y, width, height);
    }

    private void CreateVertexArray(GraphicsVertexArray vao)
    {
        uint handle = _device.AllocateHandle();
        vao.Handle = handle;
        _device.VertexArrays[handle] = new WebGPUVertexArrayResource
        {
            VertexBuffer = vao.Vertices.Handle,
            IndexBuffer = vao.Indices?.Handle ?? 0,
            InstanceBuffer = vao.InstanceBuffer?.Handle ?? 0,
            Format = vao.Format,
            InstanceFormat = vao.InstanceFormat,
        };
    }

    private void CreateFramebuffer(GraphicsFrameBuffer framebuffer)
    {
        GraphicsFrameBuffer.Attachment[] attachments = framebuffer.Attachments;
        if (attachments.Length == 0)
            throw new NotSupportedException("WebGPU framebuffers require at least one attachment.");

        int colorCount = 0;
        int depthCount = 0;
        for (int i = 0; i < attachments.Length; i++)
        {
            GraphicsFrameBuffer.Attachment attachment = attachments[i];
            if (attachment.IsCubeFace)
                throw new NotSupportedException("WebGPU cubemap framebuffer attachments are not supported yet.");
            if (attachment.IsDepth)
                depthCount++;
            else
                colorCount++;
        }

        if (colorCount > 4 || depthCount > 1)
            throw new NotSupportedException("WebGPU framebuffers support up to four color attachments and at most one depth attachment.");

        var colorHandles = new uint[colorCount];
        var colorMips = new int[colorCount];
        var colorViews = new nint[colorCount];
        var colorFormats = new WGPUTextureFormat[colorCount];
        uint depthHandle = 0;
        int depthMip = 0;
        nint depthView = 0;
        WGPUTextureFormat depthFormat = WGPUTextureFormat.Undefined;
        int colorIndex = 0;
        try
        {
            for (int i = 0; i < attachments.Length; i++)
            {
                GraphicsFrameBuffer.Attachment attachment = attachments[i];
                GraphicsTexture texture = attachment.Texture;
                if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out WebGPUTextureResource? resource) || resource.Texture == 0)
                    throw new InvalidOperationException("WebGPU framebuffer attachment is not ready.");
                if (resource.Type != TextureType.Texture2D)
                    throw new NotSupportedException("WebGPU framebuffer attachments must be Texture2D resources.");
                if (attachment.IsDepth != WebGPUFormats.IsDepth(resource.EngineFormat))
                    throw new ArgumentException("WebGPU framebuffer attachment depth flag must match the texture format.");
                if (attachment.MipLevel < 0 || (uint)attachment.MipLevel >= resource.MipLevels)
                    throw new ArgumentOutOfRangeException(nameof(attachment.MipLevel));

                uint expectedWidth = Math.Max(1u, resource.Width >> attachment.MipLevel);
                uint expectedHeight = Math.Max(1u, resource.Height >> attachment.MipLevel);
                if (framebuffer.Width != expectedWidth || framebuffer.Height != expectedHeight)
                    throw new ArgumentException($"WebGPU framebuffer extent must match attachment mip extent {expectedWidth}x{expectedHeight}.");

                nint view = CreateTextureView(
                    resource.Texture,
                    resource.Format,
                    (uint)attachment.MipLevel,
                    WGPUTextureUsage.RenderAttachment);
                if (attachment.IsDepth)
                {
                    depthHandle = texture.Handle;
                    depthMip = attachment.MipLevel;
                    depthView = view;
                    depthFormat = resource.Format;
                }
                else
                {
                    colorHandles[colorIndex] = texture.Handle;
                    colorMips[colorIndex] = attachment.MipLevel;
                    colorViews[colorIndex] = view;
                    colorFormats[colorIndex] = resource.Format;
                    colorIndex++;
                }
            }
        }
        catch
        {
            for (int i = 0; i < colorViews.Length; i++)
            {
                if (colorViews[i] != 0)
                    WebGPUNative.wgpuTextureViewRelease(colorViews[i]);
            }
            if (depthView != 0)
                WebGPUNative.wgpuTextureViewRelease(depthView);
            throw;
        }

        uint handle = _device.AllocateHandle();
        framebuffer.Handle = handle;
        _device.Framebuffers[handle] = new WebGPUFramebufferResource
        {
            ColorHandles = colorHandles,
            ColorMips = colorMips,
            ColorViews = colorViews,
            ColorFormats = colorFormats,
            DepthHandle = depthHandle,
            DepthMip = depthMip,
            DepthView = depthView,
            DepthFormat = depthFormat,
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
        ShaderVariant? shader = _currentShader;
        if (shader?.Bytecode?.Format != ShaderBytecodeFormat.Wgsl)
            return;

        uint vaoHandle = vao?.Handle ?? 0;
        WebGPUVertexArrayResource? vertexArray = null;
        if (vaoHandle != 0)
            _device.VertexArrays.TryGetValue(vaoHandle, out vertexArray);

        if (indexed)
        {
            if (vertexArray == null || vertexArray.IndexBuffer == 0)
                return;
            if (instanceCount > 1 && vertexArray.InstanceFormat == null)
                return;
            if (instanceCount == 1 && vertexArray.InstanceFormat != null)
                throw new NotSupportedException("WebGPU DrawIndexed does not consume an instance stream; use DrawIndexedInstanced.");
        }
        else if (vertexArray is { InstanceFormat: not null })
        {
            throw new NotSupportedException("WebGPU DrawArrays does not consume an instance stream; use an instanced draw opcode.");
        }

        BeginPassIfNeeded();
        var key = new GraphicsPipelineKey(shader, vaoHandle, topology, in _currentRaster, index32Bit, vao?.Format, vao?.InstanceFormat);
        nint pipeline = _device.GetOrCreateGraphicsPipeline_NoLock(key, shader, GetCurrentTargetFormats());
        WebGPUShaderLayoutResource layout = _device.GetOrCreateShaderLayout_NoLock(shader);
        EnsurePackedUniforms(shader);
        WebGPUNative.wgpuRenderPassEncoderSetPipeline(_pass, pipeline);
        BindGroups(layout);

        if (vertexArray != null &&
            _device.Buffers.TryGetValue(vertexArray.VertexBuffer, out WebGPUBufferResource? vertexBuffer) &&
            vertexBuffer.Buffer != 0)
        {
            WebGPUNative.wgpuRenderPassEncoderSetVertexBuffer(_pass, 0, vertexBuffer.Buffer, 0, WebGPUNative.WholeSize);
            if (vertexArray.InstanceFormat != null &&
                _device.Buffers.TryGetValue(vertexArray.InstanceBuffer, out WebGPUBufferResource? instanceBuffer) &&
                instanceBuffer.Buffer != 0)
            {
                WebGPUNative.wgpuRenderPassEncoderSetVertexBuffer(_pass, 1, instanceBuffer.Buffer, 0, WebGPUNative.WholeSize);
            }
        }

        if (indexed)
        {
            if (!_device.Buffers.TryGetValue(vertexArray!.IndexBuffer, out WebGPUBufferResource? indexBuffer) || indexBuffer.Buffer == 0)
                return;
            WebGPUNative.wgpuRenderPassEncoderSetIndexBuffer(
                _pass,
                indexBuffer.Buffer,
                index32Bit ? WGPUIndexFormat.Uint32 : WGPUIndexFormat.Uint16,
                0,
                WebGPUNative.WholeSize);
            WebGPUNative.wgpuRenderPassEncoderDrawIndexed(
                _pass,
                count,
                Math.Max(1u, instanceCount),
                firstIndex,
                baseVertex,
                0);
            return;
        }

        WebGPUNative.wgpuRenderPassEncoderDraw(_pass, count, Math.Max(1u, instanceCount), checked((uint)Math.Max(0, firstVertex)), 0);
    }

    private void BindGroups(WebGPUShaderLayoutResource layout)
    {
        ShaderDescriptorLayoutPlan plan = layout.Plan;
        if (plan.BindGroups.Length == 0)
            return;

        for (int groupIndex = 0; groupIndex < plan.BindGroups.Length; groupIndex++)
        {
            ShaderBindGroupPlan group = plan.BindGroups[groupIndex];
            nint bindGroup = CreateBindGroup(layout.BindGroupLayouts[groupIndex], group);
            _transientBindGroups.Add(bindGroup);
            WebGPUNative.wgpuRenderPassEncoderSetBindGroup(_pass, (uint)group.Group, bindGroup, 0, null);
        }
    }

    private nint CreateBindGroup(nint groupLayout, ShaderBindGroupPlan group)
    {
        ShaderDescriptorBinding[] bindings = group.Bindings;
        WGPUBindGroupEntry* entries = stackalloc WGPUBindGroupEntry[Math.Max(1, bindings.Length)];
        for (int i = 0; i < bindings.Length; i++)
        {
            ShaderDescriptorBinding binding = bindings[i];
            string name = binding.Slot.Name;
            var entry = new WGPUBindGroupEntry
            {
                binding = (uint)binding.PhysicalBinding,
                size = WebGPUNative.WholeSize,
            };
            switch (binding.Slot.Kind)
            {
                case ShaderBindingKind.Buffer:
                case ShaderBindingKind.StructuredBuffer:
                case ShaderBindingKind.StorageBuffer:
                    if (_transientUniformBuffers.TryGetValue(name, out nint packed) && packed != 0)
                    {
                        entry.buffer = packed;
                        break;
                    }

                    if (!_uniformBuffers.TryGetValue(name, out GraphicsBuffer? buffer) ||
                        buffer.Handle == 0 ||
                        !_device.Buffers.TryGetValue(buffer.Handle, out WebGPUBufferResource? bufferResource) ||
                        bufferResource.Buffer == 0)
                    {
                        // Degrade instead of terminating the editor over one bad material: bind a
                        // zeroed transient (degenerate constants → invisible output), log once.
                        LogDrawDegradationOnce($"buffer '{name}'");
                        entry.buffer = AllocateZeroedTransientUniform(binding.Slot.ByteSize);
                        break;
                    }

                    entry.buffer = bufferResource.Buffer;
                    break;
                case ShaderBindingKind.Texture:
                case ShaderBindingKind.StorageTexture:
                {
                    WebGPUTextureResource texture = RequireTexture(binding);
                    nint view = CreateBindingView(
                        texture,
                        binding.Slot.ResourceShape,
                        binding.Slot.Kind == ShaderBindingKind.StorageTexture
                            ? WGPUTextureUsage.StorageBinding
                            : WGPUTextureUsage.TextureBinding);
                    _transientViews.Add(view);
                    entry.textureView = view;
                    break;
                }
                case ShaderBindingKind.Sampler:
                {
                    WebGPUTextureResource texture = RequireTexture(binding);
                    EnsureSampler(texture);
                    entry.sampler = texture.Sampler;
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(binding), binding.Slot.Kind, "Unsupported WebGPU bind-group entry kind.");
            }

            entries[i] = entry;
        }

        WGPUBindGroupDescriptor descriptor = new()
        {
            layout = groupLayout,
            entryCount = (nuint)bindings.Length,
            entries = bindings.Length == 0 ? null : entries,
        };
        nint bindGroup = WebGPUNative.wgpuDeviceCreateBindGroup(_device.NativeDevice, &descriptor);
        if (bindGroup == 0)
            throw new InvalidOperationException($"wgpuDeviceCreateBindGroup returned null for @group({group.Group}).");
        return bindGroup;
    }

    private WebGPUTextureResource RequireTexture(ShaderDescriptorBinding binding)
    {
        string name = binding.Slot.Name;
        if (TryResolveGpuTexture(name, out WebGPUTextureResource? resource))
            return resource!;

        if (binding.Slot.Kind == ShaderBindingKind.Sampler)
        {
            if (name.EndsWith("Sampler", StringComparison.Ordinal) &&
                TryResolveGpuTexture(name[..^7], out resource))
            {
                return resource!;
            }

            if (name.EndsWith("_sampler", StringComparison.OrdinalIgnoreCase) &&
                TryResolveGpuTexture(name[..^8], out resource))
            {
                return resource!;
            }

            ShaderBindingLayout? layout = _currentShader?.Bytecode?.BindingLayout;
            if (layout != null)
            {
                string? textureName = FindTextureNameForSampler(layout.Textures, binding.Slot.Slot);
                if (textureName != null && TryResolveGpuTexture(textureName, out resource))
                    return resource!;
            }
        }

        throw new InvalidOperationException($"WebGPU bind group missing texture '{name}'.");
    }

    private bool TryResolveGpuTexture(string name, out WebGPUTextureResource? resource)
    {
        resource = null;
        if (!TryGetBoundTexture(name, out GraphicsTexture texture) ||
            texture.Handle == 0 ||
            !_device.Textures.TryGetValue(texture.Handle, out resource) ||
            resource.Texture == 0)
        {
            return false;
        }

        return true;
    }

    private nint CreateBindingView(WebGPUTextureResource texture, ShaderResourceShape shape, WGPUTextureUsage usage)
    {
        WGPUTextureViewDimension dimension = shape switch
        {
            ShaderResourceShape.TextureCube or ShaderResourceShape.TextureCubeArray => WGPUTextureViewDimension.Cube,
            ShaderResourceShape.Texture2DArray => WGPUTextureViewDimension.D2Array,
            ShaderResourceShape.Texture3D => WGPUTextureViewDimension.D3,
            _ => texture.Type == TextureType.TextureCubeMap ? WGPUTextureViewDimension.Cube : WGPUTextureViewDimension.D2,
        };
        uint arrayLayers = dimension == WGPUTextureViewDimension.Cube ? 6u : 1u;
        uint mipCount = Math.Max(1u, texture.MipLevels);
        WGPUTextureViewDescriptor descriptor = new()
        {
            format = texture.Format,
            dimension = dimension,
            baseMipLevel = 0,
            mipLevelCount = mipCount,
            baseArrayLayer = 0,
            arrayLayerCount = arrayLayers,
            aspect = WGPUTextureAspect.All,
            usage = usage,
        };
        nint view = WebGPUNative.wgpuTextureCreateView(texture.Texture, &descriptor);
        if (view == 0)
            throw new InvalidOperationException("wgpuTextureCreateView returned null.");
        return view;
    }

    private WebGPURenderTargetFormats GetCurrentTargetFormats()
    {
        if (_pendingRenderTarget is { Handle: not 0 } &&
            _device.Framebuffers.TryGetValue(_pendingRenderTarget.Handle, out WebGPUFramebufferResource? framebuffer))
        {
            return new WebGPURenderTargetFormats(framebuffer.ColorFormats, framebuffer.DepthFormat);
        }

        if (_device.HasSurface)
            return new WebGPURenderTargetFormats(_device.SurfaceFormat);

        return new WebGPURenderTargetFormats(WGPUTextureFormat.RGBA8Unorm);
    }

    private void ResolveAttachments(
        out nint colorView,
        out WGPUTextureFormat colorFormat,
        out uint width,
        out uint height,
        out nint depthView,
        out WGPUTextureFormat depthFormat)
    {
        if (_pendingRenderTarget != null)
        {
            if (_pendingRenderTarget.Handle == 0)
                CreateFramebuffer(_pendingRenderTarget);
            if (!_device.Framebuffers.TryGetValue(_pendingRenderTarget.Handle, out WebGPUFramebufferResource? framebuffer) ||
                (framebuffer.ColorViews.Length == 0 && framebuffer.DepthView == 0))
            {
                throw new InvalidOperationException("WebGPU render target has no attachments.");
            }

            if (framebuffer.ColorViews.Length > 0)
            {
                if (framebuffer.ColorViews[0] == 0)
                    throw new InvalidOperationException("WebGPU render target has no color attachment.");
                colorView = framebuffer.ColorViews[0];
                colorFormat = framebuffer.ColorFormats[0];
            }
            else
            {
                colorView = 0;
                colorFormat = WGPUTextureFormat.Undefined;
            }

            width = framebuffer.Width;
            height = framebuffer.Height;
            depthView = framebuffer.DepthView;
            depthFormat = framebuffer.DepthFormat;
            return;
        }

        if (_device.HasSurface && _device.CurrentSurfaceTexture != 0)
        {
            colorFormat = _device.SurfaceFormat;
            width = _device.SurfaceWidth;
            height = _device.SurfaceHeight;
            colorView = CreateTextureView(
                _device.CurrentSurfaceTexture,
                colorFormat,
                0,
                WGPUTextureUsage.RenderAttachment);
            _transientViews.Add(colorView);
            depthView = 0;
            depthFormat = WGPUTextureFormat.Undefined;
            return;
        }

        EnsureScratch(Math.Max(1u, _viewportWidth), Math.Max(1u, _viewportHeight));
        colorView = _scratchView;
        colorFormat = WGPUTextureFormat.RGBA8Unorm;
        width = _scratchWidth;
        height = _scratchHeight;
        depthView = 0;
        depthFormat = WGPUTextureFormat.Undefined;
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
            throw new NotSupportedException("WebGPU framebuffer blit currently requires matching source and destination extents.");
        if (srcWidth <= 0 || srcHeight <= 0)
            return;
        if (_pendingRenderTarget == null || _pendingReadTarget == null)
            throw new InvalidOperationException("WebGPU framebuffer blit requires draw and read targets.");

        if (_pendingRenderTarget.Handle == 0)
            CreateFramebuffer(_pendingRenderTarget);
        if (_pendingReadTarget.Handle == 0)
            CreateFramebuffer(_pendingReadTarget);
        if (!_device.Framebuffers.TryGetValue(_pendingRenderTarget.Handle, out WebGPUFramebufferResource? destination) ||
            !_device.Framebuffers.TryGetValue(_pendingReadTarget.Handle, out WebGPUFramebufferResource? source))
        {
            throw new InvalidOperationException("WebGPU framebuffer blit targets are not ready.");
        }

        if (mask == ClearFlags.Depth)
        {
            CopyFramebufferAttachment(
                source.DepthHandle,
                source.DepthMip,
                destination.DepthHandle,
                destination.DepthMip,
                srcX, srcY, dstX, dstY, srcWidth, srcHeight,
                depth: true);
            return;
        }

        if (mask != ClearFlags.Color)
            throw new NotSupportedException("WebGPU framebuffer blits currently support color-only or depth-only masks.");
        if (source.ColorHandles.Length == 0 || destination.ColorHandles.Length == 0)
            throw new InvalidOperationException("WebGPU color framebuffer blit requires a color attachment on each target.");

        CopyFramebufferAttachment(
            source.ColorHandles[0],
            source.ColorMips[0],
            destination.ColorHandles[0],
            destination.ColorMips[0],
            srcX, srcY, dstX, dstY, srcWidth, srcHeight,
            depth: false);
    }

    private void CopyFramebufferAttachment(
        uint sourceHandle,
        int sourceMip,
        uint destinationHandle,
        int destinationMip,
        int srcX,
        int srcY,
        int dstX,
        int dstY,
        int width,
        int height,
        bool depth)
    {
        if (sourceHandle == 0 || destinationHandle == 0)
            throw new InvalidOperationException($"WebGPU framebuffer blit is missing a {(depth ? "depth" : "color")} attachment.");
        if (sourceHandle == destinationHandle && sourceMip == destinationMip)
            throw new InvalidOperationException("WebGPU framebuffer blit source and destination subresources must differ.");
        if (!_device.Textures.TryGetValue(sourceHandle, out WebGPUTextureResource? source) || source.Texture == 0 ||
            !_device.Textures.TryGetValue(destinationHandle, out WebGPUTextureResource? destination) || destination.Texture == 0)
        {
            throw new InvalidOperationException("WebGPU framebuffer blit attachment is not ready.");
        }

        nint encoder = GetEncoder();
        WGPUTexelCopyTextureInfo src = new()
        {
            texture = source.Texture,
            mipLevel = (uint)Math.Max(0, sourceMip),
            origin = new WGPUOrigin3D { x = (uint)Math.Max(0, srcX), y = (uint)Math.Max(0, srcY) },
            aspect = depth && WebGPUFormats.HasStencil(source.Format)
                ? WGPUTextureAspect.DepthOnly
                : WGPUTextureAspect.All,
        };
        WGPUTexelCopyTextureInfo dst = new()
        {
            texture = destination.Texture,
            mipLevel = (uint)Math.Max(0, destinationMip),
            origin = new WGPUOrigin3D { x = (uint)Math.Max(0, dstX), y = (uint)Math.Max(0, dstY) },
            aspect = depth && WebGPUFormats.HasStencil(destination.Format)
                ? WGPUTextureAspect.DepthOnly
                : WGPUTextureAspect.All,
        };
        WGPUExtent3D extent = new()
        {
            width = (uint)width,
            height = (uint)height,
            depthOrArrayLayers = 1,
        };
        WebGPUNative.wgpuCommandEncoderCopyTextureToTexture(encoder, &src, &dst, &extent);
    }

    private void EnsureScratch(uint width, uint height)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);
        if (_scratchColor != 0 && _scratchWidth == width && _scratchHeight == height)
            return;

        ReleaseScratch();
        _scratchWidth = width;
        _scratchHeight = height;
        _scratchColor = _device.CreateGpuTexture(width, height, 1, TextureImageFormat.Color4b, WGPUTextureFormat.RGBA8Unorm);
        _scratchView = CreateTextureView(_scratchColor, WGPUTextureFormat.RGBA8Unorm, 0, WGPUTextureUsage.RenderAttachment);
    }

    private void ReleaseScratch()
    {
        if (_scratchView != 0)
        {
            WebGPUNative.wgpuTextureViewRelease(_scratchView);
            _scratchView = 0;
        }

        if (_scratchColor != 0)
        {
            WebGPUNative.wgpuTextureRelease(_scratchColor);
            _scratchColor = 0;
        }
    }

    private void ReleaseTransients()
    {
        for (int i = 0; i < _transientBindGroups.Count; i++)
        {
            if (_transientBindGroups[i] != 0)
                WebGPUNative.wgpuBindGroupRelease(_transientBindGroups[i]);
        }

        _transientBindGroups.Clear();
        for (int i = 0; i < _transientViews.Count; i++)
        {
            if (_transientViews[i] != 0)
                WebGPUNative.wgpuTextureViewRelease(_transientViews[i]);
        }

        _transientViews.Clear();
        ReleaseTransientUniforms();
    }

    private static nint CreateTextureView(nint texture, WGPUTextureFormat format, uint mip, WGPUTextureUsage usage)
    {
        WGPUTextureViewDescriptor descriptor = new()
        {
            format = format,
            dimension = WGPUTextureViewDimension.D2,
            baseMipLevel = mip,
            mipLevelCount = 1,
            baseArrayLayer = 0,
            arrayLayerCount = 1,
            aspect = WGPUTextureAspect.All,
            usage = usage,
        };
        nint view = WebGPUNative.wgpuTextureCreateView(texture, &descriptor);
        if (view == 0)
            throw new InvalidOperationException("wgpuTextureCreateView returned null.");
        return view;
    }
}
