// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text;

using XEngine.Runtime.Backends.WebGPU.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Runtime.Backends.WebGPU;

public sealed unsafe partial class WebGPUGraphicsDevice
{
    public int PipelineCacheHitCount => _pipelineCacheHitCount;
    public int PipelineCreateCount => _pipelineCreateCount;

    internal nint GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        ShaderVariant variant)
        => GetOrCreateGraphicsPipeline(key, variant, WGPUTextureFormat.RGBA8Unorm);

    internal nint GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        ShaderVariant variant,
        WGPUTextureFormat colorFormat)
        => GetOrCreateGraphicsPipeline(key, variant, new WebGPURenderTargetFormats(colorFormat));

    internal nint GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        ShaderVariant variant,
        WebGPURenderTargetFormats targetFormats)
    {
        ArgumentNullException.ThrowIfNull(variant);
        lock (_gate)
        {
            EnsureReady_NoLock();
            return GetOrCreateGraphicsPipeline_NoLock(key, variant, targetFormats);
        }
    }

    internal nint GetOrCreateGraphicsPipeline_NoLock(
        GraphicsPipelineKey key,
        ShaderVariant variant,
        WebGPURenderTargetFormats targetFormats)
    {
        var cacheKey = new WebGPUGraphicsPipelineKey(key, targetFormats);
        if (_graphicsPipelines.TryGetValue(cacheKey, out nint cached))
        {
            _pipelineCacheHitCount++;
            if (_pipelineCacheHitCount == 1)
                Debug.Log("[WebGPU] pipeline-cache first in-process hit");
            return cached;
        }

        nint pipeline = CreateGraphicsPipeline_NoLock(key, variant, targetFormats);
        _graphicsPipelines.Add(cacheKey, pipeline);
        _pipelineCreateCount++;
        return pipeline;
    }

    private nint CreateGraphicsPipeline_NoLock(
        GraphicsPipelineKey key,
        ShaderVariant variant,
        WebGPURenderTargetFormats targetFormats)
    {
        RasterizerState raster = key.RasterState;
        if ((raster.DepthTest || raster.DepthWrite) && targetFormats.DepthFormat == WGPUTextureFormat.Undefined)
        {
            raster.DepthTest = false;
            raster.DepthWrite = false;
        }
        if (raster.StencilEnabled && !WebGPUFormats.HasStencil(targetFormats.DepthFormat))
            raster.StencilEnabled = false;

        WebGPUShaderModuleResource modules = GetOrCreateShaderModules_NoLock(variant);
        WebGPUShaderLayoutResource layout = GetOrCreateShaderLayout_NoLock(variant);
        WebGPUVertexArrayResource? vertexArray = null;
        if (key.VertexArrayHandle != 0 && !VertexArrays.TryGetValue(key.VertexArrayHandle, out vertexArray))
            throw new InvalidOperationException($"WebGPU PSO references unknown vertex array {key.VertexArrayHandle}.");

        byte[] vertexNameBytes = Encoding.UTF8.GetBytes(modules.VertexEntryPoint);
        byte[] fragmentNameBytes = Encoding.UTF8.GetBytes(modules.FragmentEntryPoint);
        WGPUBlendState blend = CreateBlendState(in raster);
        WGPUDepthStencilState depthStencilState = default;
        bool hasDepth = targetFormats.DepthFormat != WGPUTextureFormat.Undefined;
        if (hasDepth)
            depthStencilState = CreateDepthStencilState(in raster, targetFormats.DepthFormat);

        WGPUVertexAttribute* attributes = stackalloc WGPUVertexAttribute[32];
        WGPUVertexBufferLayout* buffers = stackalloc WGPUVertexBufferLayout[2];
        int bufferCount = FillVertexLayouts(vertexArray, variant.Bytecode?.VertexInputLayout, attributes, buffers);

        fixed (byte* vertexName = vertexNameBytes)
        fixed (byte* fragmentName = fragmentNameBytes)
        {
            WGPUColorTargetState* targets = stackalloc WGPUColorTargetState[Math.Max(1, targetFormats.ColorCount)];
            for (int i = 0; i < targetFormats.ColorCount; i++)
            {
                targets[i] = new WGPUColorTargetState
                {
                    format = targetFormats[i],
                    blend = raster.DoBlend ? &blend : null,
                    writeMask = (WGPUColorWriteMask)raster.ColorMask,
                };
            }

            WGPUFragmentState fragment = new()
            {
                module = modules.FragmentModule,
                entryPoint = new WGPUStringView
                {
                    data = fragmentName,
                    length = (nuint)fragmentNameBytes.Length,
                },
                targetCount = (nuint)targetFormats.ColorCount,
                targets = targetFormats.ColorCount == 0 ? null : targets,
            };

            WGPURenderPipelineDescriptor descriptor = new()
            {
                layout = layout.PipelineLayout,
                vertex = new WGPUVertexState
                {
                    module = modules.VertexModule,
                    entryPoint = new WGPUStringView
                    {
                        data = vertexName,
                        length = (nuint)vertexNameBytes.Length,
                    },
                    bufferCount = (nuint)bufferCount,
                    buffers = bufferCount == 0 ? null : buffers,
                },
                primitive = new WGPUPrimitiveState
                {
                    topology = WebGPUFormats.ToTopology(key.Topology),
                    stripIndexFormat = WebGPUFormats.IsStripTopology(key.Topology)
                        ? (key.Index32Bit ? WGPUIndexFormat.Uint32 : WGPUIndexFormat.Uint16)
                        : WGPUIndexFormat.Undefined,
                    frontFace = WebGPUFormats.ToFrontFace(raster.Winding),
                    cullMode = WebGPUFormats.ToCullMode(raster.CullFace),
                },
                depthStencil = hasDepth ? (void*)&depthStencilState : null,
                multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFFu },
                fragment = &fragment,
            };

            nint pipeline = WebGPUNative.wgpuDeviceCreateRenderPipeline(_device, &descriptor);
            if (pipeline == 0)
                throw new InvalidOperationException("wgpuDeviceCreateRenderPipeline returned null.");
            return pipeline;
        }
    }

    private static int FillVertexLayouts(
        WebGPUVertexArrayResource? vertexArray,
        ShaderVertexInputLayout? shaderInputs,
        WGPUVertexAttribute* attributes,
        WGPUVertexBufferLayout* buffers)
    {
        if (vertexArray == null)
            return 0;

        int attributeCount = FillVertexAttributes(vertexArray.Format, shaderInputs, attributes);
        buffers[0] = new WGPUVertexBufferLayout
        {
            stepMode = WGPUVertexStepMode.Vertex,
            arrayStride = (ulong)Math.Max(1, vertexArray.Format.Size),
            attributeCount = (nuint)attributeCount,
            attributes = attributes,
        };
        int bufferCount = 1;
        if (vertexArray.InstanceFormat != null)
        {
            int instanceCount = FillVertexAttributes(
                vertexArray.InstanceFormat,
                shaderInputs,
                attributes + attributeCount);
            buffers[1] = new WGPUVertexBufferLayout
            {
                stepMode = WGPUVertexStepMode.Instance,
                arrayStride = (ulong)Math.Max(1, vertexArray.InstanceFormat.Size),
                attributeCount = (nuint)instanceCount,
                attributes = attributes + attributeCount,
            };
            bufferCount = 2;
        }

        return bufferCount;
    }

    private static int FillVertexAttributes(
        VertexFormat format,
        ShaderVertexInputLayout? shaderInputs,
        WGPUVertexAttribute* attributes)
    {
        VertexFormat.Element[] elements = format.Elements;
        for (int i = 0; i < elements.Length; i++)
        {
            VertexFormat.Element element = elements[i];
            uint location = shaderInputs != null && shaderInputs.TryGetLocation(element.Semantic, out uint shaderLocation)
                ? shaderLocation
                : element.Semantic;
            attributes[i] = new WGPUVertexAttribute
            {
                format = WebGPUFormats.ToVertexFormat(element.Type, element.Count, element.Normalized),
                offset = (ulong)(ushort)element.Offset,
                shaderLocation = location,
            };
        }

        return elements.Length;
    }

    internal WebGPUShaderLayoutResource GetOrCreateShaderLayout(ShaderVariant variant)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            return GetOrCreateShaderLayout_NoLock(variant);
        }
    }

    private WebGPUShaderModuleResource GetOrCreateShaderModules_NoLock(ShaderVariant variant)
    {
        if (_shaderModules.TryGetValue(variant.Id, out WebGPUShaderModuleResource? cached))
            return cached;

        CompiledShaderBytecode bytecode = variant.Bytecode
            ?? throw new InvalidOperationException("WebGPU PSO creation requires WGSL bytecode.");
        if (bytecode.Format != ShaderBytecodeFormat.Wgsl)
            throw new InvalidOperationException($"WebGPU PSO creation requires WGSL, got {bytecode.Format}.");

        string vertexWgsl = Encoding.UTF8.GetString(bytecode.VertexBytecode);
        string fragmentWgsl = Encoding.UTF8.GetString(bytecode.FragmentBytecode);
        var resource = new WebGPUShaderModuleResource
        {
            VertexModule = CreateWgslModule_NoLock(bytecode.VertexBytecode),
            FragmentModule = CreateWgslModule_NoLock(bytecode.FragmentBytecode),
            VertexEntryPoint = WebGPUFormats.FindWgslEntryPoint(vertexWgsl, "vertex"),
            FragmentEntryPoint = WebGPUFormats.FindWgslEntryPoint(fragmentWgsl, "fragment"),
        };
        _shaderModules.Add(variant.Id, resource);
        return resource;
    }

    private nint CreateWgslModule_NoLock(byte[] wgsl)
    {
        fixed (byte* ptr = wgsl)
        {
            WGPUShaderSourceWGSL source = new()
            {
                chain = new WGPUChainedStruct { sType = WGPUSType.ShaderSourceWGSL },
                code = new WGPUStringView { data = ptr, length = (nuint)wgsl.Length },
            };
            WGPUShaderModuleDescriptor descriptor = new()
            {
                nextInChain = (WGPUChainedStruct*)&source,
            };
            nint module = WebGPUNative.wgpuDeviceCreateShaderModule(_device, &descriptor);
            if (module == 0)
                throw new InvalidOperationException("wgpuDeviceCreateShaderModule returned null.");
            return module;
        }
    }

    internal WebGPUShaderLayoutResource GetOrCreateShaderLayout_NoLock(ShaderVariant variant)
    {
        if (_shaderLayouts.TryGetValue(variant.Id, out WebGPUShaderLayoutResource? cached))
            return cached;

        ShaderBindingLayout bindingLayout = variant.Bytecode?.BindingLayout ?? new ShaderBindingLayout();
        ShaderDescriptorLayoutPlan plan = ShaderDescriptorLayoutPlan.Create(bindingLayout);
        nint[] bindGroupLayouts = WebGPUBindGroupLayouts.CreateNativeLayouts(_device, plan);
        nint pipelineLayout;
        fixed (nint* layouts = bindGroupLayouts)
        {
            WGPUPipelineLayoutDescriptor descriptor = new()
            {
                bindGroupLayoutCount = (nuint)bindGroupLayouts.Length,
                bindGroupLayouts = bindGroupLayouts.Length == 0 ? null : layouts,
            };
            pipelineLayout = WebGPUNative.wgpuDeviceCreatePipelineLayout(_device, &descriptor);
        }

        if (pipelineLayout == 0)
        {
            WebGPUBindGroupLayouts.Release(bindGroupLayouts);
            throw new InvalidOperationException("wgpuDeviceCreatePipelineLayout returned null.");
        }

        var resource = new WebGPUShaderLayoutResource
        {
            BindGroupLayouts = bindGroupLayouts,
            PipelineLayout = pipelineLayout,
            Plan = plan,
        };
        _shaderLayouts.Add(variant.Id, resource);
        return resource;
    }

    private static WGPUBlendState CreateBlendState(in RasterizerState raster)
    {
        WGPUBlendComponent color = new()
        {
            operation = WebGPUFormats.ToBlendOp(raster.Blend),
            srcFactor = WebGPUFormats.ToBlendFactor(raster.BlendSrc),
            dstFactor = WebGPUFormats.ToBlendFactor(raster.BlendDst),
        };
        return new WGPUBlendState { color = color, alpha = color };
    }

    private static WGPUDepthStencilState CreateDepthStencilState(in RasterizerState raster, WGPUTextureFormat format)
    {
        WGPUStencilFaceState stencil = new()
        {
            compare = raster.StencilEnabled
                ? WebGPUFormats.ToCompare(raster.StencilFunc)
                : WGPUCompareFunction.Always,
            failOp = WebGPUFormats.ToStencilOp(raster.StencilFailOp),
            depthFailOp = WebGPUFormats.ToStencilOp(raster.StencilZFailOp),
            passOp = WebGPUFormats.ToStencilOp(raster.StencilPassOp),
        };
        return new WGPUDepthStencilState
        {
            format = format,
            depthWriteEnabled = raster.DepthWrite ? WGPUOptionalBool.True : WGPUOptionalBool.False,
            depthCompare = raster.DepthTest ? WebGPUFormats.ToCompare(raster.Depth) : WGPUCompareFunction.Always,
            stencilFront = stencil,
            stencilBack = stencil,
            stencilReadMask = (uint)raster.StencilReadMask,
            stencilWriteMask = (uint)raster.StencilWriteMask,
        };
    }

    private void ReleasePipelines_NoLock()
    {
        foreach (nint pipeline in _graphicsPipelines.Values)
        {
            if (pipeline != 0)
                WebGPUNative.wgpuRenderPipelineRelease(pipeline);
        }
        _graphicsPipelines.Clear();

        foreach (WebGPUShaderModuleResource modules in _shaderModules.Values)
        {
            if (modules.VertexModule != 0)
                WebGPUNative.wgpuShaderModuleRelease(modules.VertexModule);
            if (modules.FragmentModule != 0)
                WebGPUNative.wgpuShaderModuleRelease(modules.FragmentModule);
        }
        _shaderModules.Clear();

        foreach (WebGPUShaderLayoutResource layout in _shaderLayouts.Values)
        {
            if (layout.PipelineLayout != 0)
                WebGPUNative.wgpuPipelineLayoutRelease(layout.PipelineLayout);
            if (layout.BindGroupLayouts != null)
                WebGPUBindGroupLayouts.Release(layout.BindGroupLayouts);
        }
        _shaderLayouts.Clear();
    }
}

internal sealed class WebGPUShaderModuleResource
{
    public nint VertexModule;
    public nint FragmentModule;
    public string VertexEntryPoint = "";
    public string FragmentEntryPoint = "";
}

internal sealed class WebGPUShaderLayoutResource
{
    public nint[] BindGroupLayouts = [];
    public nint PipelineLayout;
    public ShaderDescriptorLayoutPlan Plan = null!;
}

internal readonly struct WebGPUGraphicsPipelineKey : IEquatable<WebGPUGraphicsPipelineKey>
{
    private readonly GraphicsPipelineKey _pipeline;
    private readonly WebGPURenderTargetFormats _targetFormats;

    public WebGPUGraphicsPipelineKey(GraphicsPipelineKey pipeline, WebGPURenderTargetFormats targetFormats)
    {
        _pipeline = pipeline;
        _targetFormats = targetFormats;
    }

    public bool Equals(WebGPUGraphicsPipelineKey other) =>
        _pipeline.Equals(other._pipeline) && _targetFormats.Equals(other._targetFormats);

    public override bool Equals(object? obj) => obj is WebGPUGraphicsPipelineKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_pipeline, _targetFormats);
}

internal readonly struct WebGPURenderTargetFormats : IEquatable<WebGPURenderTargetFormats>
{
    private readonly WGPUTextureFormat _format0;
    private readonly WGPUTextureFormat _format1;
    private readonly WGPUTextureFormat _format2;
    private readonly WGPUTextureFormat _format3;

    public WebGPURenderTargetFormats(WGPUTextureFormat colorFormat)
        : this(colorFormat, WGPUTextureFormat.Undefined)
    {
    }

    public WebGPURenderTargetFormats(WGPUTextureFormat colorFormat, WGPUTextureFormat depthFormat)
        : this([colorFormat], depthFormat)
    {
    }

    public WebGPURenderTargetFormats(WGPUTextureFormat[] colorFormats, WGPUTextureFormat depthFormat)
    {
        ArgumentNullException.ThrowIfNull(colorFormats);
        if (colorFormats.Length > 4)
            throw new NotSupportedException("WebGPU render targets currently support at most four color attachments.");

        ColorCount = colorFormats.Length;
        _format0 = colorFormats.Length > 0 ? colorFormats[0] : default;
        _format1 = colorFormats.Length > 1 ? colorFormats[1] : default;
        _format2 = colorFormats.Length > 2 ? colorFormats[2] : default;
        _format3 = colorFormats.Length > 3 ? colorFormats[3] : default;
        DepthFormat = depthFormat;
    }

    public int ColorCount { get; }
    public WGPUTextureFormat DepthFormat { get; }

    public WGPUTextureFormat this[int index] => index switch
    {
        0 => _format0,
        1 => _format1,
        2 => _format2,
        3 => _format3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public bool Equals(WebGPURenderTargetFormats other) =>
        ColorCount == other.ColorCount &&
        DepthFormat == other.DepthFormat &&
        _format0 == other._format0 &&
        _format1 == other._format1 &&
        _format2 == other._format2 &&
        _format3 == other._format3;

    public override bool Equals(object? obj) => obj is WebGPURenderTargetFormats other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ColorCount, DepthFormat, _format0, _format1, _format2, _format3);
}
