// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text;

using XEngine.Runtime.Backends.WebGPU.Native;

namespace XEngine.Runtime.Backends.WebGPU;

/// <summary>
/// Self-drawn mip chain. WebGPU has no <c>generateMipmap</c>; WG2 blits each level with a
/// fullscreen triangle and <c>textureSampleLevel</c>.
/// </summary>
internal sealed unsafe class WebGPUMipGenerator : IDisposable
{
    private static readonly byte[] s_wgsl = Encoding.UTF8.GetBytes(
        """
        struct VsOut {
            @builtin(position) pos: vec4f,
            @location(0) uv: vec2f,
        };

        @vertex
        fn vs_main(@builtin(vertex_index) vid: u32) -> VsOut {
            var positions = array<vec2f, 3>(
                vec2f(-1.0,  1.0),
                vec2f( 3.0,  1.0),
                vec2f(-1.0, -3.0)
            );
            var uvs = array<vec2f, 3>(
                vec2f(0.0, 0.0),
                vec2f(2.0, 0.0),
                vec2f(0.0, 2.0)
            );
            var o: VsOut;
            o.pos = vec4f(positions[vid], 0.0, 1.0);
            o.uv = uvs[vid];
            return o;
        }

        @group(0) @binding(0) var srcTex: texture_2d<f32>;
        @group(0) @binding(1) var srcSamp: sampler;

        @fragment
        fn fs_main(in: VsOut) -> @location(0) vec4f {
            return textureSampleLevel(srcTex, srcSamp, in.uv, 0.0);
        }
        """);

    private readonly WebGPUGraphicsDevice _device;
    private nint _module;
    private nint _bindGroupLayout;
    private nint _pipelineLayout;
    private nint _pipeline;
    private nint _sampler;
    private WGPUTextureFormat _pipelineFormat;
    private bool _disposed;

    public WebGPUMipGenerator(WebGPUGraphicsDevice device)
    {
        _device = device;
    }

    public void Generate(nint encoder, WebGPUTextureResource resource, List<nint> retiredTextures)
    {
        if (resource.Texture == 0 || resource.Width == 0 || resource.Height == 0)
            return;
        if (resource.Type != TextureType.Texture2D)
            throw new NotSupportedException("WebGPU mip generation currently supports Texture2D only.");
        if (WebGPUFormats.IsDepth(resource.EngineFormat))
            throw new NotSupportedException("WebGPU mip generation does not support depth textures.");

        if (resource.MipLevels <= 1)
            RebuildMipChain(encoder, resource, retiredTextures);
        if (resource.MipLevels <= 1)
            return;

        EnsurePipeline(resource.Format);

        for (uint mip = 1; mip < resource.MipLevels; mip++)
        {
            uint destWidth = Math.Max(1u, resource.Width >> (int)mip);
            uint destHeight = Math.Max(1u, resource.Height >> (int)mip);
            nint sourceView = CreateView(resource, mip - 1, WGPUTextureUsage.TextureBinding);
            nint destView = CreateView(resource, mip, WGPUTextureUsage.RenderAttachment);
            nint bindGroup = 0;
            nint pass = 0;
            try
            {
                WGPUBindGroupEntry* entries = stackalloc WGPUBindGroupEntry[2];
                entries[0] = new WGPUBindGroupEntry
                {
                    binding = 0,
                    size = WebGPUNative.WholeSize,
                    textureView = sourceView,
                };
                entries[1] = new WGPUBindGroupEntry
                {
                    binding = 1,
                    size = WebGPUNative.WholeSize,
                    sampler = _sampler,
                };
                WGPUBindGroupDescriptor bgDesc = new()
                {
                    layout = _bindGroupLayout,
                    entryCount = 2,
                    entries = entries,
                };
                bindGroup = WebGPUNative.wgpuDeviceCreateBindGroup(_device.NativeDevice, &bgDesc);
                if (bindGroup == 0)
                    throw new InvalidOperationException("wgpuDeviceCreateBindGroup returned null for mip generation.");

                WGPURenderPassColorAttachment color = new()
                {
                    view = destView,
                    depthSlice = WebGPUNative.DepthSliceUndefined,
                    loadOp = WGPULoadOp.Clear,
                    storeOp = WGPUStoreOp.Store,
                };
                WGPURenderPassDescriptor passDesc = new()
                {
                    colorAttachmentCount = 1,
                    colorAttachments = &color,
                };
                pass = WebGPUNative.wgpuCommandEncoderBeginRenderPass(encoder, &passDesc);
                WebGPUNative.wgpuRenderPassEncoderSetPipeline(pass, _pipeline);
                WebGPUNative.wgpuRenderPassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
                WebGPUNative.wgpuRenderPassEncoderSetViewport(pass, 0, 0, destWidth, destHeight, 0f, 1f);
                WebGPUNative.wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
                WebGPUNative.wgpuRenderPassEncoderEnd(pass);
            }
            finally
            {
                if (pass != 0)
                    WebGPUNative.wgpuRenderPassEncoderRelease(pass);
                if (bindGroup != 0)
                    WebGPUNative.wgpuBindGroupRelease(bindGroup);
                if (destView != 0)
                    WebGPUNative.wgpuTextureViewRelease(destView);
                if (sourceView != 0)
                    WebGPUNative.wgpuTextureViewRelease(sourceView);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleasePipeline();
    }

    private void RebuildMipChain(nint encoder, WebGPUTextureResource resource, List<nint> retiredTextures)
    {
        uint mipLevels = CalculateMipLevels(Math.Max(resource.Width, resource.Height));
        if (mipLevels <= 1)
            return;

        nint replacement = _device.CreateGpuTexture(
            resource.Width,
            resource.Height,
            mipLevels,
            resource.EngineFormat,
            resource.Format);
        WGPUTexelCopyTextureInfo src = new()
        {
            texture = resource.Texture,
            aspect = WGPUTextureAspect.All,
        };
        WGPUTexelCopyTextureInfo dst = new()
        {
            texture = replacement,
            aspect = WGPUTextureAspect.All,
        };
        WGPUExtent3D extent = new()
        {
            width = resource.Width,
            height = resource.Height,
            depthOrArrayLayers = 1,
        };
        WebGPUNative.wgpuCommandEncoderCopyTextureToTexture(encoder, &src, &dst, &extent);
        retiredTextures.Add(resource.Texture);
        resource.Texture = replacement;
        resource.MipLevels = mipLevels;
    }

    private void EnsurePipeline(WGPUTextureFormat format)
    {
        if (_pipeline != 0 && _pipelineFormat == format)
            return;
        ReleasePipeline();
        _pipelineFormat = format;

        nint device = _device.NativeDevice;
        fixed (byte* wgsl = s_wgsl)
        {
            WGPUShaderSourceWGSL source = new()
            {
                chain = new WGPUChainedStruct { sType = WGPUSType.ShaderSourceWGSL },
                code = new WGPUStringView { data = wgsl, length = (nuint)s_wgsl.Length },
            };
            WGPUShaderModuleDescriptor moduleDesc = new()
            {
                nextInChain = (WGPUChainedStruct*)&source,
            };
            _module = WebGPUNative.wgpuDeviceCreateShaderModule(device, &moduleDesc);
        }
        if (_module == 0)
            throw new InvalidOperationException("wgpuDeviceCreateShaderModule returned null for mip generation WGSL.");

        WGPUBindGroupLayoutEntry* layoutEntries = stackalloc WGPUBindGroupLayoutEntry[2];
        layoutEntries[0] = new WGPUBindGroupLayoutEntry
        {
            binding = 0,
            visibility = WGPUShaderStage.Fragment,
            texture = new WGPUTextureBindingLayout
            {
                sampleType = WGPUTextureSampleType.Float,
                viewDimension = WGPUTextureViewDimension.D2,
            },
        };
        layoutEntries[1] = new WGPUBindGroupLayoutEntry
        {
            binding = 1,
            visibility = WGPUShaderStage.Fragment,
            sampler = new WGPUSamplerBindingLayout { type = WGPUSamplerBindingType.Filtering },
        };
        WGPUBindGroupLayoutDescriptor bglDesc = new()
        {
            entryCount = 2,
            entries = layoutEntries,
        };
        _bindGroupLayout = WebGPUNative.wgpuDeviceCreateBindGroupLayout(device, &bglDesc);
        if (_bindGroupLayout == 0)
            throw new InvalidOperationException("wgpuDeviceCreateBindGroupLayout returned null for mip generation.");

        nint bgl = _bindGroupLayout;
        WGPUPipelineLayoutDescriptor plDesc = new()
        {
            bindGroupLayoutCount = 1,
            bindGroupLayouts = &bgl,
        };
        _pipelineLayout = WebGPUNative.wgpuDeviceCreatePipelineLayout(device, &plDesc);
        if (_pipelineLayout == 0)
            throw new InvalidOperationException("wgpuDeviceCreatePipelineLayout returned null for mip generation.");

        WGPUSamplerDescriptor samplerDesc = new()
        {
            magFilter = WGPUFilterMode.Linear,
            minFilter = WGPUFilterMode.Linear,
            mipmapFilter = WGPUMipmapFilterMode.Nearest,
            lodMinClamp = 0f,
            lodMaxClamp = 32f,
            maxAnisotropy = 1,
        };
        _sampler = WebGPUNative.wgpuDeviceCreateSampler(device, &samplerDesc);
        if (_sampler == 0)
            throw new InvalidOperationException("wgpuDeviceCreateSampler returned null for mip generation.");

        ReadOnlySpan<byte> vsName = "vs_main"u8;
        ReadOnlySpan<byte> fsName = "fs_main"u8;
        fixed (byte* vs = vsName)
        fixed (byte* fs = fsName)
        {
            WGPUColorTargetState target = new()
            {
                format = format,
                writeMask = WGPUColorWriteMask.All,
            };
            WGPUFragmentState fragment = new()
            {
                module = _module,
                entryPoint = new WGPUStringView { data = fs, length = (nuint)fsName.Length },
                targetCount = 1,
                targets = &target,
            };
            WGPURenderPipelineDescriptor pipelineDesc = new()
            {
                layout = _pipelineLayout,
                vertex = new WGPUVertexState
                {
                    module = _module,
                    entryPoint = new WGPUStringView { data = vs, length = (nuint)vsName.Length },
                },
                primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList },
                multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFFu },
                fragment = &fragment,
            };
            _pipeline = WebGPUNative.wgpuDeviceCreateRenderPipeline(device, &pipelineDesc);
        }
        if (_pipeline == 0)
            throw new InvalidOperationException("wgpuDeviceCreateRenderPipeline returned null for mip generation.");
    }

    private static nint CreateView(WebGPUTextureResource resource, uint mip, WGPUTextureUsage usage)
    {
        WGPUTextureViewDescriptor desc = new()
        {
            format = resource.Format,
            dimension = WGPUTextureViewDimension.D2,
            baseMipLevel = mip,
            mipLevelCount = 1,
            baseArrayLayer = 0,
            arrayLayerCount = 1,
            aspect = WGPUTextureAspect.All,
            usage = usage,
        };
        nint view = WebGPUNative.wgpuTextureCreateView(resource.Texture, &desc);
        if (view == 0)
            throw new InvalidOperationException($"wgpuTextureCreateView returned null for mip {mip}.");
        return view;
    }

    private static uint CalculateMipLevels(uint size)
    {
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        uint levels = 1;
        while (size > 1)
        {
            size >>= 1;
            levels++;
        }
        return levels;
    }

    private void ReleasePipeline()
    {
        if (_pipeline != 0)
        {
            WebGPUNative.wgpuRenderPipelineRelease(_pipeline);
            _pipeline = 0;
        }
        if (_sampler != 0)
        {
            WebGPUNative.wgpuSamplerRelease(_sampler);
            _sampler = 0;
        }
        if (_pipelineLayout != 0)
        {
            WebGPUNative.wgpuPipelineLayoutRelease(_pipelineLayout);
            _pipelineLayout = 0;
        }
        if (_bindGroupLayout != 0)
        {
            WebGPUNative.wgpuBindGroupLayoutRelease(_bindGroupLayout);
            _bindGroupLayout = 0;
        }
        if (_module != 0)
        {
            WebGPUNative.wgpuShaderModuleRelease(_module);
            _module = 0;
        }
    }
}
