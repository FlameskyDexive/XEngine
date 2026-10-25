// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Runtime.Backends.Metal;

public sealed partial class MetalGraphicsDevice
{
    public int PipelineCacheHitCount => _pipelineCacheHitCount;
    public int PipelineCreateCount => _pipelineCreateCount;

    internal void BindMslFunctions(nint vertexFunction, nint fragmentFunction)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            _translator!.BindMslFunctions(vertexFunction, fragmentFunction);
        }
    }

    internal MetalPassRecombiner PassRecombiner => _translator!.PassRecombiner;

    internal nint NewLibraryFromSource(string source)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            return MetalNative.NewLibraryFromSource(_device, source);
        }
    }

    /// <summary>
    /// Vertex/fragment MTLLibrary functions for a compiled MetalLib <see cref="ShaderVariant"/>.
    /// Slang emits one metallib per stage; each is wrapped with <c>newLibraryWithData:</c> and the
    /// entry-point function is resolved by candidate name, falling back to the library's first
    /// declared function (slang may mangle <c>main</c> to <c>main0</c>). Results are cached per
    /// variant id and owned by the device.
    /// </summary>
    internal (nint Vertex, nint Fragment) GetOrCreateShaderFunctions_NoLock(ShaderVariant? variant)
    {
        if (variant?.Bytecode is not { } bytecode || !bytecode.HasGraphics)
            return (0, 0);
        if (bytecode.Format != ShaderBytecodeFormat.MetalLib)
            throw new InvalidOperationException(
                $"Metal PSO creation requires MetalLib bytecode, got {bytecode.Format}.");

        if (_shaderFunctions.TryGetValue(variant.Id, out (nint Vertex, nint Fragment) cached))
            return cached;

        nint vertexLibrary = MetalNative.NewLibraryWithData(_device, bytecode.VertexBytecode);
        try
        {
            nint fragmentLibrary = MetalNative.NewLibraryWithData(_device, bytecode.FragmentBytecode);
            try
            {
                nint vertexFunction = ResolveEntryPoint(vertexLibrary);
                nint fragmentFunction = ResolveEntryPoint(fragmentLibrary);
                var functions = (vertexFunction, fragmentFunction);
                _shaderFunctions[variant.Id] = functions;
                return functions;
            }
            finally
            {
                MetalNative.Release(fragmentLibrary);
            }
        }
        finally
        {
            MetalNative.Release(vertexLibrary);
        }
    }

    private static nint ResolveEntryPoint(nint library)
    {
        string[] names = MetalNative.GetLibraryFunctionNames(library);
        foreach (string candidate in EntryPointCandidates)
        {
            foreach (string name in names)
            {
                if (name == candidate)
                    return MetalNative.NewFunction(library, name);
            }
        }

        foreach (string name in names)
        {
            if (name.Length > 0)
                return MetalNative.NewFunction(library, name);
        }

        throw new InvalidOperationException("MTLLibrary declares no functions for the compiled shader stage.");
    }

    private static readonly string[] EntryPointCandidates = ["main", "main0", "main_0"];

    internal nint GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        nint vertexFunction,
        nint fragmentFunction)
        => GetOrCreateGraphicsPipeline(key, vertexFunction, fragmentFunction, new MetalRenderTargetFormats(MTLPixelFormat.RGBA8Unorm));

    internal nint GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        nint vertexFunction,
        nint fragmentFunction,
        MTLPixelFormat colorFormat)
        => GetOrCreateGraphicsPipeline(key, vertexFunction, fragmentFunction, new MetalRenderTargetFormats(colorFormat));

    internal nint GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        nint vertexFunction,
        nint fragmentFunction,
        MetalRenderTargetFormats targetFormats)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            return GetOrCreateGraphicsPipeline_NoLock(key, vertexFunction, fragmentFunction, targetFormats);
        }
    }

    internal nint GetOrCreateGraphicsPipeline_NoLock(
        GraphicsPipelineKey key,
        nint vertexFunction,
        nint fragmentFunction,
        MetalRenderTargetFormats targetFormats,
        nint vertexDescriptor = 0)
    {
        var cacheKey = new MetalGraphicsPipelineKey(key, targetFormats);
        if (_graphicsPipelines.TryGetValue(cacheKey, out nint cached))
        {
            _pipelineCacheHitCount++;
            return cached;
        }

        nint pipeline = MetalNative.NewRenderPipelineState(
            _device,
            vertexFunction,
            fragmentFunction,
            key.RasterState,
            targetFormats.ColorCount > 0 ? targetFormats[0] : MTLPixelFormat.Invalid,
            targetFormats.DepthFormat,
            vertexDescriptor);
        _graphicsPipelines.Add(cacheKey, pipeline);
        _pipelineCreateCount++;
        return pipeline;
    }

    internal nint GetOrCreateDepthStencilState_NoLock(in RasterizerState raster)
    {
        MTLCompareFunction compareFunction = raster.DepthTest
            ? MetalFormats.ToCompare(raster.Depth)
            : MTLCompareFunction.Always;
        var key = (compareFunction, raster.DepthWrite);
        if (_depthStencilStates.TryGetValue(key, out nint cached))
            return cached;

        nint state = MetalNative.NewDepthStencilState(_device, compareFunction, raster.DepthWrite);
        _depthStencilStates.Add(key, state);
        return state;
    }

    private void ReleaseDepthStencilStates_NoLock()
    {
        foreach (nint state in _depthStencilStates.Values)
            MetalNative.Release(state);
        _depthStencilStates.Clear();
    }

    /// <summary>
    /// Cached MTLVertexDescriptor for an interned mesh layout, optional instance layout, and
    /// shader variant. Attribute slots follow the shader's reflected vertex input layout
    /// (semantic → location, unconsumed elements skipped); mesh and instance elements use
    /// separate Metal buffers and per-vertex/per-instance stepping.
    /// </summary>
    internal nint GetOrCreateVertexDescriptor_NoLock(
        VertexFormat format,
        VertexFormat? instanceFormat,
        ShaderVertexInputLayout? shaderInputLayout,
        int shaderVariantId)
    {
        ArgumentNullException.ThrowIfNull(format);
        var key = (format.LayoutId, instanceFormat?.LayoutId ?? 0, shaderVariantId);
        if (_vertexDescriptors.TryGetValue(key, out nint cached))
            return cached;

        var attributeEntries = new List<(MTLVertexFormat Format, ushort Offset, uint Location, uint BufferIndex)>(
            format.Elements.Length + (instanceFormat?.Elements.Length ?? 0));
        AddAttributes(
            format,
            expectedDivisor: 0,
            MetalCommandTranslator.MetalVertexDataBufferIndex,
            shaderInputLayout,
            attributeEntries);
        if (instanceFormat != null)
        {
            AddAttributes(
                instanceFormat,
                expectedDivisor: 1,
                MetalCommandTranslator.MetalInstanceDataBufferIndex,
                shaderInputLayout,
                attributeEntries);
        }
        if (attributeEntries.Count == 0)
            throw new InvalidOperationException(
                "The shader consumes none of the vertex-stream elements — vertex descriptor would be empty.");

        var formats = new MTLVertexFormat[attributeEntries.Count];
        var offsets = new ushort[attributeEntries.Count];
        var locations = new uint[attributeEntries.Count];
        var bufferIndices = new uint[attributeEntries.Count];
        for (int i = 0; i < attributeEntries.Count; i++)
        {
            formats[i] = attributeEntries[i].Format;
            offsets[i] = attributeEntries[i].Offset;
            locations[i] = attributeEntries[i].Location;
            bufferIndices[i] = attributeEntries[i].BufferIndex;
        }

        nint descriptor = MetalNative.NewVertexDescriptor(
            formats,
            offsets,
            locations,
            bufferIndices,
            (nuint)format.Size,
            (nuint)(instanceFormat?.Size ?? 0));
        _vertexDescriptors[key] = descriptor;
        return descriptor;

        static void AddAttributes(
            VertexFormat streamFormat,
            short expectedDivisor,
            uint bufferIndex,
            ShaderVertexInputLayout? inputLayout,
            List<(MTLVertexFormat Format, ushort Offset, uint Location, uint BufferIndex)> entries)
        {
            for (int i = 0; i < streamFormat.Elements.Length; i++)
            {
                VertexFormat.Element element = streamFormat.Elements[i];
                if (element.Divisor != expectedDivisor)
                {
                    throw new NotSupportedException(expectedDivisor == 0
                        ? "Metal vertex-stream element cannot have a non-zero divisor."
                        : "Metal instanced input currently supports divisor 1 only.");
                }

                // With no reflection, use engine semantics for manual MSL test shaders.
                uint location = element.Semantic;
                if (inputLayout != null && !inputLayout.TryGetLocation(element.Semantic, out location))
                    continue;

                entries.Add((
                    MetalFormats.ToVertexFormat(element.Type, element.Count, element.Normalized),
                    (ushort)element.Offset,
                    location,
                    bufferIndex));
            }
        }
    }

    private void ReleasePipelines_NoLock()
    {
        foreach (nint pipeline in _graphicsPipelines.Values)
            MetalNative.Release(pipeline);
        _graphicsPipelines.Clear();
    }
}

internal readonly struct MetalGraphicsPipelineKey : IEquatable<MetalGraphicsPipelineKey>
{
    private readonly GraphicsPipelineKey _pipeline;
    private readonly MetalRenderTargetFormats _targetFormats;

    public MetalGraphicsPipelineKey(GraphicsPipelineKey pipeline, MetalRenderTargetFormats targetFormats)
    {
        _pipeline = pipeline;
        _targetFormats = targetFormats;
    }

    public bool Equals(MetalGraphicsPipelineKey other) =>
        _pipeline.Equals(other._pipeline) && _targetFormats.Equals(other._targetFormats);

    public override bool Equals(object? obj) => obj is MetalGraphicsPipelineKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_pipeline, _targetFormats);
}

internal readonly struct MetalRenderTargetFormats : IEquatable<MetalRenderTargetFormats>
{
    private readonly MTLPixelFormat _format0;
    private readonly MTLPixelFormat _format1;
    private readonly MTLPixelFormat _format2;
    private readonly MTLPixelFormat _format3;

    public MetalRenderTargetFormats(MTLPixelFormat colorFormat)
        : this(colorFormat, MTLPixelFormat.Invalid)
    {
    }

    public MetalRenderTargetFormats(MTLPixelFormat colorFormat, MTLPixelFormat depthFormat)
        : this([colorFormat], depthFormat)
    {
    }

    public MetalRenderTargetFormats(MTLPixelFormat[] colorFormats, MTLPixelFormat depthFormat)
    {
        ArgumentNullException.ThrowIfNull(colorFormats);
        if (colorFormats.Length > 4)
            throw new NotSupportedException("Metal render targets currently support at most four color attachments.");

        ColorCount = colorFormats.Length;
        _format0 = colorFormats.Length > 0 ? colorFormats[0] : default;
        _format1 = colorFormats.Length > 1 ? colorFormats[1] : default;
        _format2 = colorFormats.Length > 2 ? colorFormats[2] : default;
        _format3 = colorFormats.Length > 3 ? colorFormats[3] : default;
        DepthFormat = depthFormat;
    }

    public int ColorCount { get; }
    public MTLPixelFormat DepthFormat { get; }

    public MTLPixelFormat this[int index] => index switch
    {
        0 => _format0,
        1 => _format1,
        2 => _format2,
        3 => _format3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public bool Equals(MetalRenderTargetFormats other) =>
        ColorCount == other.ColorCount &&
        DepthFormat == other.DepthFormat &&
        _format0 == other._format0 &&
        _format1 == other._format1 &&
        _format2 == other._format2 &&
        _format3 == other._format3;

    public override bool Equals(object? obj) => obj is MetalRenderTargetFormats other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ColorCount, DepthFormat, _format0, _format1, _format2, _format3);
}
