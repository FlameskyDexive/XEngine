// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.WebGPU.Native;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Vector;

using CommandBuffer = XEngine.Runtime.CommandBuffer;

namespace XEngine.Runtime.Backends.WebGPU;

/// <summary>
/// Resource opcodes plus WG5 draw/pass translation. Render-pass begin is delayed until the first
/// draw (or a clear-only flush) so <c>SetRenderTarget</c> / <c>ClearRenderTarget</c> / viewport can
/// coalesce into one <c>beginRenderPass</c> descriptor. Rebinding the same target after a clear
/// keeps the pending load-ops; WebGPU has no in-pass clear.
/// </summary>
internal sealed unsafe partial class WebGPUCommandTranslator
{
    private readonly WebGPUGraphicsDevice _device;
    private nint _encoder;
    private nint _pass;
    private readonly List<nint> _retiredTextures = new();
    private readonly List<nint> _transientViews = new();
    private readonly List<nint> _transientBindGroups = new();
    private readonly Dictionary<string, GraphicsBuffer> _uniformBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphicsTexture> _textures = new(StringComparer.Ordinal);
    private GraphicsFrameBuffer? _pendingRenderTarget;
    private GraphicsFrameBuffer? _pendingReadTarget;
    private ShaderVariant? _currentShader;
    private RasterizerState _currentRaster = new() { DepthTest = false, DepthWrite = false, CullFace = RasterizerState.PolyFace.None };
    private bool _colorLoadClear;
    private WGPUColor _colorClear;
    private bool _depthLoadClear;
    private float _depthClear = 1f;
    private bool _stencilLoadClear;
    private uint _stencilClear;
    private bool _viewportSet;
    private int _viewportX;
    private int _viewportY;
    private uint _viewportWidth = 1;
    private uint _viewportHeight = 1;
    private bool _scissorSet;
    private int _scissorX;
    private int _scissorY;
    private uint _scissorWidth;
    private uint _scissorHeight;
    private uint _passWidth = 1;
    private uint _passHeight = 1;
    private nint _scratchColor;
    private nint _scratchView;
    private uint _scratchWidth;
    private uint _scratchHeight;

    public WebGPUCommandTranslator(WebGPUGraphicsDevice device)
    {
        _device = device;
    }

    public void Translate(CommandBuffer commandBuffer)
    {
        ReadOnlySpan<byte> stream = commandBuffer._stream.AsSpan(0, commandBuffer._streamPos);
        List<object?> objects = commandBuffer._objects;
        TransientStore store = commandBuffer._store;
        int pos = 0;
        _encoder = 0;
        _pass = 0;
        _pendingRenderTarget = null;
        _pendingReadTarget = null;
        _currentShader = null;
        _currentRaster = new RasterizerState();
        _colorLoadClear = false;
        _depthLoadClear = false;
        _stencilLoadClear = false;
        _viewportSet = false;
        _scissorSet = false;
        _uniformBuffers.Clear();
        _textures.Clear();
        ResetUniformState(commandBuffer);
        try
        {
            while (pos < stream.Length)
            {
                CommandOpcode op = ReadOpcode(stream, ref pos);
                switch (op)
                {
                    case CommandOpcode.CreateBuffer:
                    {
                        var buf = (GraphicsBuffer)objects[ReadU16(stream, ref pos)]!;
                        bool dynamic = ReadU8(stream, ref pos) != 0;
                        ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                        CreateBuffer(buf, dynamic, data);
                        break;
                    }
                    case CommandOpcode.DisposeBuffer:
                    {
                        var buf = (GraphicsBuffer)objects[ReadU16(stream, ref pos)]!;
                        DisposeBuffer(buf);
                        break;
                    }
                    case CommandOpcode.UpdateBuffer:
                    {
                        var buf = (GraphicsBuffer?)objects[ReadU16(stream, ref pos)];
                        uint dstOffset = ReadU32(stream, ref pos);
                        ReadOnlySpan<byte> blob = ReadBlob<byte>(stream, ref pos, store);
                        if (buf != null)
                            UpdateBuffer(buf, dstOffset, blob);
                        break;
                    }
                    case CommandOpcode.CreateTexture:
                    {
                        var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        uint handle = _device.AllocateHandle();
                        tex.Handle = handle;
                        _device.Textures[handle] = new WebGPUTextureResource
                        {
                            EngineFormat = tex.ImageFormat,
                            Format = WebGPUFormats.ToWgpuFormat(tex.ImageFormat),
                            Type = tex.Type,
                        };
                        break;
                    }
                    case CommandOpcode.DisposeTexture:
                    {
                        var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        if (tex.Handle != 0 && _device.Textures.Remove(tex.Handle, out WebGPUTextureResource? res))
                        {
                            res.Release();
                            tex.Handle = 0;
                        }
                        break;
                    }
                    case CommandOpcode.AllocateTexture2D:
                    {
                        var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        int mip = ReadI32(stream, ref pos);
                        uint w = ReadU32(stream, ref pos);
                        uint h = ReadU32(stream, ref pos);
                        _ = ReadI32(stream, ref pos);
                        ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                        AllocateTexture2D(tex, mip, w, h, data);
                        break;
                    }
                    case CommandOpcode.UpdateTexture:
                    {
                        var tex = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                        int x = ReadI32(stream, ref pos);
                        int y = ReadI32(stream, ref pos);
                        uint w = ReadU32(stream, ref pos);
                        uint h = ReadU32(stream, ref pos);
                        int mip = ReadI32(stream, ref pos);
                        ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                        if (tex != null)
                            UpdateTexture2D(tex, mip, x, y, w, h, data);
                        break;
                    }
                    case CommandOpcode.GenerateMipmap:
                    {
                        GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                        if (texture != null)
                        {
                            EndPass();
                            GenerateMipmap(texture);
                        }
                        break;
                    }
                    case CommandOpcode.SetTextureWrap:
                    {
                        var texture = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        byte axis = ReadU8(stream, ref pos);
                        var wrap = (TextureWrap)ReadU8(stream, ref pos);
                        SetTextureWrap(texture, axis, wrap);
                        break;
                    }
                    case CommandOpcode.SetTextureFiltersOp:
                    {
                        var texture = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        var min = (TextureMin)ReadU8(stream, ref pos);
                        var mag = (TextureMag)ReadU8(stream, ref pos);
                        SetTextureFilters(texture, min, mag);
                        break;
                    }
                    case CommandOpcode.SetTextureCompareMode:
                    {
                        var texture = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        bool enabled = ReadU8(stream, ref pos) != 0;
                        if (texture.Handle != 0 && _device.Textures.TryGetValue(texture.Handle, out WebGPUTextureResource? res))
                        {
                            res.CompareEnabled = enabled;
                            res.SamplerDirty = true;
                        }
                        break;
                    }
                    case CommandOpcode.GetTextureData:
                    {
                        var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        int mip = ReadI32(stream, ref pos);
                        var destination = (byte[])objects[ReadU16(stream, ref pos)]!;
                        EndPass();
                        ReadTextureInto(tex, (uint)mip, destination);
                        break;
                    }
                    case CommandOpcode.GetTextureDataPtr:
                    {
                        var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        int mip = ReadI32(stream, ref pos);
                        nint destination = (nint)ReadI64(stream, ref pos);
                        EndPass();
                        ReadTextureIntoPtr(tex, (uint)mip, destination);
                        break;
                    }
                    case CommandOpcode.AllocateTextureCubeFace:
                    {
                        var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        int face = ReadI32(stream, ref pos);
                        int mip = ReadI32(stream, ref pos);
                        uint size = ReadU32(stream, ref pos);
                        ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                        EndPass();
                        AllocateTextureCubeFace(tex, face, mip, size, data);
                        break;
                    }
                    case CommandOpcode.AllocateTexture3D:
                    case CommandOpcode.UpdateTexture3D:
                    case CommandOpcode.GetTextureCubeFaceData:
                    {
                        SkipOpcode(op, stream, ref pos, objects, store);
                        throw new NotSupportedException($"WebGPU {op} is not implemented yet (2D resources only).");
                    }
                    case CommandOpcode.ResolveTexture:
                    {
                        // WebGPU reports a single-sample-only capability mask, so the pipeline
                        // clamps MSAA to 1x and never records this opcode.
                        SkipOpcode(op, stream, ref pos, objects, store);
                        throw new NotSupportedException(
                            "WebGPU multisample resolve is not implemented; this device reports single-sample targets only.");
                    }
                    case CommandOpcode.SetRenderTarget:
                    {
                        var target = objects[ReadU16(stream, ref pos)] as GraphicsFrameBuffer;
                        BindPendingRenderTarget(target);
                        _pendingReadTarget = target;
                        break;
                    }
                    case CommandOpcode.SetRenderTargets:
                    {
                        var drawTarget = objects[ReadU16(stream, ref pos)] as GraphicsFrameBuffer;
                        var readTarget = objects[ReadU16(stream, ref pos)] as GraphicsFrameBuffer;
                        BindPendingRenderTarget(drawTarget);
                        _pendingReadTarget = readTarget;
                        break;
                    }
                    case CommandOpcode.SetViewport:
                    {
                        _viewportX = ReadI32(stream, ref pos);
                        _viewportY = ReadI32(stream, ref pos);
                        _viewportWidth = ReadU32(stream, ref pos);
                        _viewportHeight = ReadU32(stream, ref pos);
                        _viewportSet = true;
                        if (_pass != 0)
                            ApplyViewport();
                        break;
                    }
                    case CommandOpcode.SetScissor:
                    {
                        _scissorX = ReadI32(stream, ref pos);
                        _scissorY = ReadI32(stream, ref pos);
                        _scissorWidth = ReadU32(stream, ref pos);
                        _scissorHeight = ReadU32(stream, ref pos);
                        _scissorSet = true;
                        if (_pass != 0)
                            ApplyScissor();
                        break;
                    }
                    case CommandOpcode.DisableScissor:
                        _scissorSet = false;
                        if (_pass != 0)
                            ApplyScissor();
                        break;
                    case CommandOpcode.ClearRenderTarget:
                    {
                        ClearFlags flags = (ClearFlags)ReadU8(stream, ref pos);
                        float r = ReadF32(stream, ref pos);
                        float g = ReadF32(stream, ref pos);
                        float b = ReadF32(stream, ref pos);
                        float a = ReadF32(stream, ref pos);
                        float depth = ReadF32(stream, ref pos);
                        int stencil = ReadI32(stream, ref pos);
                        RecordClear(flags, r, g, b, a, depth, stencil);
                        break;
                    }
                    case CommandOpcode.BlitFramebuffer:
                    {
                        int srcX = ReadI32(stream, ref pos);
                        int srcY = ReadI32(stream, ref pos);
                        int srcWidth = ReadI32(stream, ref pos);
                        int srcHeight = ReadI32(stream, ref pos);
                        int dstX = ReadI32(stream, ref pos);
                        int dstY = ReadI32(stream, ref pos);
                        int dstWidth = ReadI32(stream, ref pos);
                        int dstHeight = ReadI32(stream, ref pos);
                        ClearFlags mask = (ClearFlags)ReadU8(stream, ref pos);
                        _ = ReadU8(stream, ref pos);
                        BlitFramebuffer(
                            srcX, srcY, srcWidth, srcHeight,
                            dstX, dstY, dstWidth, dstHeight,
                            mask);
                        break;
                    }
                    case CommandOpcode.SetRasterState:
                        _currentRaster = ReadStruct<RasterizerState>(stream, ref pos);
                        break;
                    case CommandOpcode.SetShader:
                    {
                        object? shader = objects[ReadU16(stream, ref pos)];
                        _currentShader = shader as ShaderVariant;
                        break;
                    }
                    case CommandOpcode.SetUniformBuffer:
                    case CommandOpcode.SetGlobalBuffer:
                    {
                        string? name = objects[ReadU16(stream, ref pos)] as string;
                        var buffer = objects[ReadU16(stream, ref pos)] as GraphicsBuffer;
                        _ = ReadU32(stream, ref pos);
                        BindNamedBuffer(name, buffer);
                        break;
                    }
                    case CommandOpcode.SetUniformTexture:
                    {
                        string? name = objects[ReadU16(stream, ref pos)] as string;
                        var texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                        BindNamedTexture(name, texture);
                        break;
                    }
                    case CommandOpcode.SetGlobalTexture:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        var texture = objects[ReadU16(stream, ref pos)] as Resources.Texture2D;
                        if (texture != null)
                        {
                            _textures[name] = texture.Handle;
                            Rendering.PropertyState.s_globalTextures[name] = texture;
                        }
                        else
                        {
                            _textures.Remove(name);
                            Rendering.PropertyState.s_globalTextures.Remove(name);
                        }
                        break;
                    }
                    case CommandOpcode.CreateVertexArrayOp:
                    {
                        var vao = (GraphicsVertexArray)objects[ReadU16(stream, ref pos)]!;
                        CreateVertexArray(vao);
                        break;
                    }
                    case CommandOpcode.DisposeVertexArray:
                    {
                        var vao = (GraphicsVertexArray)objects[ReadU16(stream, ref pos)]!;
                        if (vao.Handle != 0)
                            _device.VertexArrays.Remove(vao.Handle);
                        vao.Handle = 0;
                        break;
                    }
                    case CommandOpcode.CreateFramebufferOp:
                    {
                        var framebuffer = (GraphicsFrameBuffer)objects[ReadU16(stream, ref pos)]!;
                        CreateFramebuffer(framebuffer);
                        break;
                    }
                    case CommandOpcode.DisposeFramebuffer:
                    {
                        var framebuffer = (GraphicsFrameBuffer)objects[ReadU16(stream, ref pos)]!;
                        if (framebuffer.Handle != 0 && _device.Framebuffers.Remove(framebuffer.Handle, out WebGPUFramebufferResource? resource))
                            resource.ReleaseViews();
                        framebuffer.Handle = 0;
                        break;
                    }
                    case CommandOpcode.DrawArrays:
                    {
                        var vao = objects[ReadU16(stream, ref pos)] as GraphicsVertexArray;
                        Topology topology = (Topology)ReadU8(stream, ref pos);
                        int first = ReadI32(stream, ref pos);
                        uint count = ReadU32(stream, ref pos);
                        Draw(vao, topology, indexed: false, index32Bit: false, count, instanceCount: 1, firstIndex: 0, baseVertex: 0, firstVertex: first);
                        break;
                    }
                    case CommandOpcode.DrawIndexed:
                    {
                        var vao = objects[ReadU16(stream, ref pos)] as GraphicsVertexArray;
                        Topology topology = (Topology)ReadU8(stream, ref pos);
                        uint indexCount = ReadU32(stream, ref pos);
                        uint startIndex = ReadU32(stream, ref pos);
                        int baseVertex = ReadI32(stream, ref pos);
                        bool index32Bit = ReadU8(stream, ref pos) != 0;
                        Draw(vao, topology, indexed: true, index32Bit, indexCount, instanceCount: 1, startIndex, baseVertex, firstVertex: 0);
                        break;
                    }
                    case CommandOpcode.DrawIndexedInstanced:
                    {
                        var vao = objects[ReadU16(stream, ref pos)] as GraphicsVertexArray;
                        Topology topology = (Topology)ReadU8(stream, ref pos);
                        uint indexCount = ReadU32(stream, ref pos);
                        uint instanceCount = ReadU32(stream, ref pos);
                        uint startIndex = ReadU32(stream, ref pos);
                        int baseVertex = ReadI32(stream, ref pos);
                        bool index32Bit = ReadU8(stream, ref pos) != 0;
                        Draw(vao, topology, indexed: true, index32Bit, indexCount, instanceCount, startIndex, baseVertex, firstVertex: 0);
                        break;
                    }
                    case CommandOpcode.CompileShader:
                    case CommandOpcode.DisposeShader:
                    case CommandOpcode.BeginSample:
                        _ = objects[ReadU16(stream, ref pos)];
                        break;
                    case CommandOpcode.SetProperties:
                    {
                        _materialProperties = objects[ReadU16(stream, ref pos)] as Rendering.PropertyState;
                        _materialShader = null;
                        _instanceMaterialOverrides = null;
                        _materialUniformsDirty = true;
                        ApplyMaterialTextureBindings();
                        break;
                    }
                    case CommandOpcode.SetInstanceProperties:
                    {
                        var properties = objects[ReadU16(stream, ref pos)] as Rendering.PropertyState;
                        ApplyObjectProperties(properties);
                        // Material-block keys in the instance state (per-draw _MainColor, clip
                        // uniforms...) must override the packed material cbuffer. Repack only when
                        // this draw carries such keys or the previous one did - the common 3D batch
                        // case (object-block-only states) keeps the packed buffer untouched.
                        Rendering.PropertyState? overrides =
                            properties != null && Rendering.MaterialUniformPacking.HasMaterialBlockOverrides(properties)
                                ? properties
                                : null;
                        if (overrides != null || _instanceMaterialOverrides != null)
                        {
                            _instanceMaterialOverrides = overrides;
                            _materialUniformsDirty = true;
                        }
                        break;
                    }
                    case CommandOpcode.ClearGlobalTexture:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        _textures.Remove(name);
                        Rendering.PropertyState.s_globalTextures.Remove(name);
                        Rendering.PropertyState.s_globalTexturesCube.Remove(name);
                        break;
                    }
                    case CommandOpcode.SetMaterialProperties:
                    {
                        _materialProperties = objects[ReadU16(stream, ref pos)] as Rendering.PropertyState;
                        _materialShader = objects[ReadU16(stream, ref pos)] as Resources.Shader;
                        _instanceMaterialOverrides = null;
                        _materialUniformsDirty = true;
                        ApplyMaterialTextureBindings();
                        break;
                    }
                    case CommandOpcode.SetGlobalTextureCube:
                    {
                        string cubeName = (string)objects[ReadU16(stream, ref pos)]!;
                        var cube = objects[ReadU16(stream, ref pos)] as Resources.Cubemap;
                        if (cube != null)
                        {
                            _textures[cubeName] = cube.Handle;
                            Rendering.PropertyState.s_globalTexturesCube[cubeName] = cube;
                        }
                        else
                        {
                            _textures.Remove(cubeName);
                            Rendering.PropertyState.s_globalTexturesCube.Remove(cubeName);
                        }
                        break;
                    }
                    case CommandOpcode.SetGlobalTexture3D:
                        _ = objects[ReadU16(stream, ref pos)];
                        _ = objects[ReadU16(stream, ref pos)];
                        break;
                    case CommandOpcode.ClearProperties:
                        _materialProperties = null;
                        _materialShader = null;
                        _instanceMaterialOverrides = null;
                        _materialUniformsDirty = true;
                        ClearObjectTextureBindings();
                        Rendering.MaterialUniformPacking.ClearTextureBindings(_textures);
                        Rendering.MaterialUniformPacking.ClearTextureBindings(_materialTextures);
                        break;
                    case CommandOpcode.ClearInstanceProperties:
                        _objectUniforms = Rendering.ObjectUniformsData.Identity;
                        _objectUniformsDirty = true;
                        if (_instanceMaterialOverrides != null)
                        {
                            _instanceMaterialOverrides = null;
                            _materialUniformsDirty = true;
                        }
                        break;
                    case CommandOpcode.ClearAllGlobals:
                        Rendering.PropertyState.ClearGlobalsInternal();
                        _textures.Clear();
                        _standardLightingDirty = true;
                        break;
                    case CommandOpcode.SetGlobalInt:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.s_globalInts[name] = ReadI32(stream, ref pos);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetGlobalFloat:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.s_globalFloats[name] = ReadF32(stream, ref pos);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetGlobalVec2:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.s_globalVectors2[name] = ReadStruct<Float2>(stream, ref pos);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetGlobalVec3:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.s_globalVectors3[name] = ReadStruct<Float3>(stream, ref pos);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetGlobalVec4:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.s_globalVectors4[name] = ReadStruct<Float4>(stream, ref pos);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetGlobalColor:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.s_globalColors[name] = ReadStruct<Color>(stream, ref pos);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetGlobalMatrix:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.s_globalMatrices[name] = ReadStruct<Float4x4>(stream, ref pos);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetGlobalMatrices:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        var values = (Float4x4[])objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.SetGlobalMatricesInternal(name, values);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetUniformFloat:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        ApplySetUniformFloat(name, ReadF32(stream, ref pos));
                        break;
                    }
                    case CommandOpcode.SetUniformInt:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        ApplySetUniformInt(name, ReadI32(stream, ref pos));
                        break;
                    }
                    case CommandOpcode.SetUniformVec2:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        ApplySetUniformVec2(name, ReadStruct<Float2>(stream, ref pos));
                        break;
                    }
                    case CommandOpcode.SetUniformVec3:
                    {
                        _ = objects[ReadU16(stream, ref pos)];
                        pos += sizeof(float) * 3;
                        break;
                    }
                    case CommandOpcode.SetUniformVec4:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        ApplySetUniformVec4(name, ReadStruct<Float4>(stream, ref pos));
                        break;
                    }
                    case CommandOpcode.SetUniformMatrix:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        ApplySetUniformMatrix(name, ReadStruct<Float4x4>(stream, ref pos));
                        break;
                    }
                    case CommandOpcode.EndSample:
                        break;
                    default:
                        SkipOpcode(op, stream, ref pos, objects, store);
                        break;
                    }
            }

            FlushEncoder(wait: false);
        }
        catch
        {
            AbandonEncoder();
            throw;
        }
    }

    private void CreateBuffer(GraphicsBuffer buf, bool dynamic, ReadOnlySpan<byte> data)
    {
        ulong size = data.Length > 0 ? (ulong)data.Length : Math.Max(1u, buf.SizeInBytes);
        size = buf.OriginalType == BufferType.UniformBuffer
            ? WebGPUFormats.Align(size, 16)
            : WebGPUFormats.Align(size, 4);

        WGPUBufferUsage usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc;
        usage |= buf.OriginalType switch
        {
            BufferType.VertexBuffer => WGPUBufferUsage.Vertex,
            BufferType.ElementsBuffer => WGPUBufferUsage.Index,
            BufferType.UniformBuffer => WGPUBufferUsage.Uniform,
            BufferType.StructuredBuffer => WGPUBufferUsage.Storage | WGPUBufferUsage.Indirect,
            BufferType.IndirectBuffer => WGPUBufferUsage.Indirect | WGPUBufferUsage.Storage,
            _ => WGPUBufferUsage.Vertex,
        };

        nint gpu = CreateGpuBuffer(size, usage, mappedAtCreation: data.Length > 0);
        if (data.Length > 0)
        {
            void* mapped = WebGPUNative.wgpuBufferGetMappedRange(gpu, 0, (nuint)size);
            if (mapped == null)
            {
                WebGPUNative.wgpuBufferRelease(gpu);
                throw new InvalidOperationException("wgpuBufferGetMappedRange returned null for mappedAtCreation.");
            }
            NativeMemory.Clear(mapped, (nuint)size);
            fixed (byte* source = data)
                Buffer.MemoryCopy(source, mapped, (long)size, data.Length);
            WebGPUNative.wgpuBufferUnmap(gpu);
        }

        uint handle = _device.AllocateHandle();
        buf.Handle = handle;
        buf.SizeInBytes = checked((uint)size);
        _device.Buffers[handle] = new WebGPUBufferResource
        {
            Buffer = gpu,
            Size = size,
            Type = buf.OriginalType,
            Dynamic = dynamic,
        };
    }

    private void UpdateBuffer(GraphicsBuffer buf, uint dstOffset, ReadOnlySpan<byte> blob)
    {
        if (buf.Handle == 0 || !_device.Buffers.TryGetValue(buf.Handle, out WebGPUBufferResource? res) || res.Buffer == 0)
            return;
        if (blob.Length == 0)
            return;

        if ((ulong)dstOffset + (ulong)blob.Length > res.Size)
        {
            if (dstOffset != 0)
                throw new ArgumentOutOfRangeException(nameof(dstOffset), "WebGPU partial buffer update exceeds the destination size.");
            ReplaceBufferStorage(buf, res, blob);
            return;
        }

        WriteBuffer(res.Buffer, dstOffset, blob);
    }

    private void ReplaceBufferStorage(GraphicsBuffer buffer, WebGPUBufferResource previous, ReadOnlySpan<byte> data)
    {
        ulong doubled = previous.Size >= uint.MaxValue / 2 ? uint.MaxValue : previous.Size * 2;
        ulong size = Math.Max((ulong)data.Length, doubled);
        size = previous.Type == BufferType.UniformBuffer
            ? WebGPUFormats.Align(size, 16)
            : WebGPUFormats.Align(size, 4);

        WGPUBufferUsage usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc;
        usage |= previous.Type switch
        {
            BufferType.VertexBuffer => WGPUBufferUsage.Vertex,
            BufferType.ElementsBuffer => WGPUBufferUsage.Index,
            BufferType.UniformBuffer => WGPUBufferUsage.Uniform,
            BufferType.StructuredBuffer => WGPUBufferUsage.Storage | WGPUBufferUsage.Indirect,
            BufferType.IndirectBuffer => WGPUBufferUsage.Indirect | WGPUBufferUsage.Storage,
            _ => WGPUBufferUsage.Vertex,
        };

        nint replacement = CreateGpuBuffer(size, usage, mappedAtCreation: false);
        WriteBuffer(replacement, 0, data);
        previous.Release();
        previous.Buffer = replacement;
        previous.Size = size;
        buffer.SizeInBytes = checked((uint)size);
    }

    private void DisposeBuffer(GraphicsBuffer buf)
    {
        if (buf.Handle == 0)
            return;
        if (_device.Buffers.Remove(buf.Handle, out WebGPUBufferResource? res))
            res.Release();
        buf.Handle = 0;
    }

    private void AllocateTexture2D(GraphicsTexture tex, int mip, uint width, uint height, ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out WebGPUTextureResource? res))
            return;
        if (mip != 0)
            return;
        if (tex.Type != TextureType.Texture2D)
            throw new NotSupportedException("WebGPU AllocateTexture2D requires Texture2D.");

        res.ReleaseTexture();
        res.Format = WebGPUFormats.ToWgpuFormat(tex.ImageFormat);
        res.EngineFormat = tex.ImageFormat;
        res.Type = tex.Type;
        res.Width = width;
        res.Height = height;
        res.MipLevels = 1;
        res.Texture = _device.CreateGpuTexture(width, height, 1, tex.ImageFormat, res.Format);

        if (data.Length > 0 && !WebGPUFormats.IsDepth(tex.ImageFormat))
            WriteTexture(res, 0, 0, 0, width, height, data);
    }

    private void AllocateTextureCubeFace(GraphicsTexture tex, int face, int mip, uint size, ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out WebGPUTextureResource? res))
            return;
        if ((uint)face >= 6)
            throw new ArgumentOutOfRangeException(nameof(face), face, "WebGPU cubemap face must be 0 through 5.");
        if (mip < 0)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (tex.Type != TextureType.TextureCubeMap)
            throw new InvalidOperationException("WebGPU AllocateTextureCubeFace requires a cubemap resource.");
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (mip != 0 && res.Texture == 0)
            throw new InvalidOperationException("WebGPU cubemap mip 0 must be allocated before higher mip levels.");

        if (res.Texture == 0 || (mip == 0 && (res.Width != size || res.Height != size || res.Depth != 6)))
        {
            if (res.Texture != 0)
                _retiredTextures.Add(res.Texture);
            res.Format = WebGPUFormats.ToWgpuFormat(tex.ImageFormat);
            res.EngineFormat = tex.ImageFormat;
            res.Type = TextureType.TextureCubeMap;
            res.Width = size;
            res.Height = size;
            res.Depth = 6;
            res.MipLevels = 1;
            res.Texture = _device.CreateGpuTexture(size, size, 1, tex.ImageFormat, res.Format, arrayLayers: 6);
        }

        if ((uint)mip >= res.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip), "WebGPU cubemap mip chain growth is not implemented.");

        if (WebGPUFormats.IsDepth(res.EngineFormat))
            return;

        if (data.Length > 0)
        {
            WriteTexture(res, (uint)mip, 0, 0, size, size, data, (uint)face);
            return;
        }

        int bpp = WebGPUFormats.BytesPerPixel(res.EngineFormat);
        byte[] zeros = new byte[checked((int)(size * size * (uint)bpp))];
        WriteTexture(res, (uint)mip, 0, 0, size, size, zeros, (uint)face);
    }

    private void UpdateTexture2D(GraphicsTexture tex, int mip, int x, int y, uint width, uint height, ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out WebGPUTextureResource? res) || res.Texture == 0)
            return;
        if (tex.Type != TextureType.Texture2D)
            throw new NotSupportedException("WebGPU UpdateTexture requires a 2D texture.");
        if (WebGPUFormats.IsDepth(res.EngineFormat))
            throw new NotSupportedException("WebGPU depth texture updates are not supported.");
        if (mip < 0 || (uint)mip >= res.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (x < 0 || y < 0)
            throw new ArgumentOutOfRangeException(x < 0 ? nameof(x) : nameof(y));
        if (width == 0 || height == 0 || data.Length == 0)
            return;

        WriteTexture(res, (uint)mip, (uint)x, (uint)y, width, height, data);
    }

    private void GenerateMipmap(GraphicsTexture texture)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out WebGPUTextureResource? res) || res.Texture == 0)
            return;
        if (texture.Type != TextureType.Texture2D)
            return;
                        _device.MipGenerator.Generate(GetEncoder(), res, _retiredTextures);
    }

    private void SetTextureWrap(GraphicsTexture texture, byte axis, TextureWrap wrap)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out WebGPUTextureResource? resource))
            return;
        switch (axis)
        {
            case 0: resource.WrapS = wrap; break;
            case 1: resource.WrapT = wrap; break;
            case 2: resource.WrapR = wrap; break;
            default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "WebGPU texture wrap axis must be 0, 1, or 2.");
        }
        resource.SamplerDirty = true;
        EnsureSampler(resource);
    }

    private void SetTextureFilters(GraphicsTexture texture, TextureMin min, TextureMag mag)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out WebGPUTextureResource? resource))
            return;
        resource.MinFilter = min;
        resource.MagFilter = mag;
        resource.SamplerDirty = true;
        EnsureSampler(resource);
    }

    private void EnsureSampler(WebGPUTextureResource resource)
    {
        if (!resource.SamplerDirty && resource.Sampler != 0)
            return;

        resource.ReleaseSampler();
        WGPUSamplerDescriptor desc = new()
        {
            addressModeU = WebGPUFormats.ToAddressMode(resource.WrapS),
            addressModeV = WebGPUFormats.ToAddressMode(resource.WrapT),
            addressModeW = WebGPUFormats.ToAddressMode(resource.WrapR),
            magFilter = WebGPUFormats.ToMagFilter(resource.MagFilter),
            minFilter = WebGPUFormats.ToMinFilter(resource.MinFilter),
            mipmapFilter = WebGPUFormats.ToMipFilter(resource.MinFilter),
            lodMinClamp = 0f,
            lodMaxClamp = 32f,
            maxAnisotropy = 1,
        };
        resource.Sampler = WebGPUNative.wgpuDeviceCreateSampler(_device.NativeDevice, &desc);
        if (resource.Sampler == 0)
            throw new InvalidOperationException("wgpuDeviceCreateSampler returned null.");
        resource.SamplerDirty = false;
    }

    private void ReadTextureInto(GraphicsTexture tex, uint mip, byte[] destination)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out WebGPUTextureResource? res) || res.Texture == 0)
            return;
        FlushEncoder(wait: true);
        byte[] pixels = _device.ReadTexture2D_NoLock(res, mip);
        int copy = Math.Min(destination.Length, pixels.Length);
        Array.Copy(pixels, destination, copy);
    }

    private void ReadTextureIntoPtr(GraphicsTexture tex, uint mip, nint destination)
    {
        if (destination == 0 || tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out WebGPUTextureResource? res) || res.Texture == 0)
            return;
        FlushEncoder(wait: true);
        byte[] pixels = _device.ReadTexture2D_NoLock(res, mip);
        Marshal.Copy(pixels, 0, destination, pixels.Length);
    }

    private nint CreateGpuBuffer(ulong size, WGPUBufferUsage usage, bool mappedAtCreation)
    {
        WGPUBufferDescriptor desc = new()
        {
            usage = usage,
            size = size,
            mappedAtCreation = mappedAtCreation ? 1u : 0u,
        };
        nint buffer = WebGPUNative.wgpuDeviceCreateBuffer(_device.NativeDevice, &desc);
        if (buffer == 0)
            throw new InvalidOperationException("wgpuDeviceCreateBuffer returned null.");
        return buffer;
    }

    private void WriteBuffer(nint buffer, uint dstOffset, ReadOnlySpan<byte> data)
    {
        int padded = (int)WebGPUFormats.Align((uint)data.Length, 4);
        if (padded == data.Length)
        {
            fixed (byte* source = data)
                WebGPUNative.wgpuQueueWriteBuffer(_device.NativeQueue, buffer, dstOffset, source, (nuint)data.Length);
            return;
        }

        byte[] paddedBytes = new byte[padded];
        data.CopyTo(paddedBytes);
        fixed (byte* source = paddedBytes)
            WebGPUNative.wgpuQueueWriteBuffer(_device.NativeQueue, buffer, dstOffset, source, (nuint)padded);
    }

    private void WriteTexture(
        WebGPUTextureResource resource,
        uint mip,
        uint x,
        uint y,
        uint width,
        uint height,
        ReadOnlySpan<byte> data,
        uint originZ = 0)
    {
        int bpp = WebGPUFormats.BytesPerPixel(resource.EngineFormat);
        uint unpaddedRow = width * (uint)bpp;
        uint bytesPerRow = WebGPUFormats.Align(unpaddedRow, WebGPUNative.BytesPerRowAlignment);
        nuint dataSize = (nuint)data.Length;
        byte[]? padded = null;
        if (bytesPerRow != unpaddedRow)
        {
            padded = new byte[checked((int)(bytesPerRow * height))];
            for (uint row = 0; row < height; row++)
            {
                int src = (int)(row * unpaddedRow);
                int dst = (int)(row * bytesPerRow);
                data.Slice(src, (int)unpaddedRow).CopyTo(padded.AsSpan(dst, (int)unpaddedRow));
            }
            dataSize = (nuint)padded.Length;
        }

        WGPUTexelCopyTextureInfo dest = new()
        {
            texture = resource.Texture,
            mipLevel = mip,
            origin = new WGPUOrigin3D { x = x, y = y, z = originZ },
            aspect = WGPUTextureAspect.All,
        };
        WGPUTexelCopyBufferLayout layout = new()
        {
            bytesPerRow = bytesPerRow,
            rowsPerImage = height,
        };
        WGPUExtent3D extent = new()
        {
            width = width,
            height = height,
            depthOrArrayLayers = 1,
        };
        if (padded != null)
        {
            fixed (byte* source = padded)
                WebGPUNative.wgpuQueueWriteTexture(_device.NativeQueue, &dest, source, dataSize, &layout, &extent);
        }
        else
        {
            fixed (byte* source = data)
                WebGPUNative.wgpuQueueWriteTexture(_device.NativeQueue, &dest, source, dataSize, &layout, &extent);
        }
    }

    private nint GetEncoder()
    {
        if (_encoder == 0)
        {
            _encoder = WebGPUNative.wgpuDeviceCreateCommandEncoder(_device.NativeDevice, null);
            if (_encoder == 0)
                throw new InvalidOperationException("wgpuDeviceCreateCommandEncoder returned null.");
        }
        return _encoder;
    }

    private void FlushEncoder(bool wait)
    {
        if (_pass == 0 && HasPendingLoadOps)
            BeginPassIfNeeded();
        EndPass();
        if (_encoder == 0)
            return;
        nint encoder = _encoder;
        _encoder = 0;
        _device.SubmitEncoder(encoder, wait);
        ReleaseRetiredTextures();
        ReleaseTransients();
    }

    private void AbandonEncoder()
    {
        if (_pass != 0)
        {
            WebGPUNative.wgpuRenderPassEncoderRelease(_pass);
            _pass = 0;
        }
        if (_encoder != 0)
        {
            WebGPUNative.wgpuCommandEncoderRelease(_encoder);
            _encoder = 0;
        }
        ReleaseRetiredTextures();
        ReleaseTransients();
    }

    private void ReleaseRetiredTextures()
    {
        for (int i = 0; i < _retiredTextures.Count; i++)
        {
            if (_retiredTextures[i] != 0)
                WebGPUNative.wgpuTextureRelease(_retiredTextures[i]);
        }
        _retiredTextures.Clear();
    }

    private void SkipOpcode(
        CommandOpcode op,
        ReadOnlySpan<byte> stream,
        ref int pos,
        List<object?> objects,
        TransientStore store)
    {
        switch (op)
        {
            case CommandOpcode.BeginOcclusionQuery:
            case CommandOpcode.EndOcclusionQuery:
                // B7b — occlusion pairs are only recorded on devices that report support.
                _ = ReadI64(stream, ref pos);
                break;
            case CommandOpcode.SetRenderTarget:
            case CommandOpcode.SetShader:
            case CommandOpcode.SetProperties:
            case CommandOpcode.SetInstanceProperties:
            case CommandOpcode.ClearGlobalTexture:
            case CommandOpcode.GenerateMipmap:
            case CommandOpcode.CreateTexture:
            case CommandOpcode.DisposeTexture:
            case CommandOpcode.DisposeBuffer:
            case CommandOpcode.CreateVertexArrayOp:
            case CommandOpcode.DisposeVertexArray:
            case CommandOpcode.CreateFramebufferOp:
            case CommandOpcode.DisposeFramebuffer:
            case CommandOpcode.CompileShader:
            case CommandOpcode.DisposeShader:
            case CommandOpcode.BeginSample:
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.SetRenderTargets:
            case CommandOpcode.SetGlobalTexture:
            case CommandOpcode.SetGlobalTexture3D:
            case CommandOpcode.SetGlobalTextureCube:
            case CommandOpcode.SetUniformTexture:
            case CommandOpcode.SetMaterialProperties:
                _ = objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.SetViewport:
            case CommandOpcode.SetScissor:
                pos += sizeof(int) * 2 + sizeof(uint) * 2;
                break;
            case CommandOpcode.DisableScissor:
            case CommandOpcode.ClearProperties:
            case CommandOpcode.ClearInstanceProperties:
            case CommandOpcode.ClearAllGlobals:
            case CommandOpcode.EndSample:
                break;
            case CommandOpcode.ClearRenderTarget:
                pos += 1 + sizeof(float) * 5 + sizeof(int);
                break;
            case CommandOpcode.BlitFramebuffer:
                pos += sizeof(int) * 8 + 2;
                break;
            case CommandOpcode.SetRasterState:
                pos += Unsafe.SizeOf<RasterizerState>();
                break;
            case CommandOpcode.SetGlobalInt:
            case CommandOpcode.SetUniformInt:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                break;
            case CommandOpcode.SetGlobalFloat:
            case CommandOpcode.SetUniformFloat:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadF32(stream, ref pos);
                break;
            case CommandOpcode.SetGlobalVec2:
                _ = objects[ReadU16(stream, ref pos)];
                pos += Unsafe.SizeOf<Float2>();
                break;
            case CommandOpcode.SetUniformVec2:
                _ = objects[ReadU16(stream, ref pos)];
                pos += sizeof(float) * 2;
                break;
            case CommandOpcode.SetGlobalVec3:
                _ = objects[ReadU16(stream, ref pos)];
                pos += Unsafe.SizeOf<Float3>();
                break;
            case CommandOpcode.SetUniformVec3:
                _ = objects[ReadU16(stream, ref pos)];
                pos += sizeof(float) * 3;
                break;
            case CommandOpcode.SetGlobalVec4:
            case CommandOpcode.SetGlobalColor:
                _ = objects[ReadU16(stream, ref pos)];
                pos += Unsafe.SizeOf<Float4>();
                break;
            case CommandOpcode.SetUniformVec4:
                _ = objects[ReadU16(stream, ref pos)];
                pos += sizeof(float) * 4;
                break;
            case CommandOpcode.SetGlobalMatrix:
            case CommandOpcode.SetUniformMatrix:
                _ = objects[ReadU16(stream, ref pos)];
                pos += Unsafe.SizeOf<Float4x4>();
                break;
            case CommandOpcode.SetGlobalMatrices:
                _ = objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.SetGlobalBuffer:
            case CommandOpcode.SetUniformBuffer:
                _ = objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU32(stream, ref pos);
                break;
            case CommandOpcode.SetUniformMatrixArray:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU32(stream, ref pos);
                _ = ReadBlob<Float4x4>(stream, ref pos, store);
                break;
            case CommandOpcode.UpdateBuffer:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU32(stream, ref pos);
                _ = ReadBlob<byte>(stream, ref pos, store);
                break;
            case CommandOpcode.UpdateTexture:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadBlob<byte>(stream, ref pos, store);
                break;
            case CommandOpcode.DrawIndexed:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU8(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                break;
            case CommandOpcode.DrawIndexedInstanced:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU8(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                break;
            case CommandOpcode.DrawArrays:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU8(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                break;
            case CommandOpcode.CreateBuffer:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU8(stream, ref pos);
                _ = ReadBlob<byte>(stream, ref pos, store);
                break;
            case CommandOpcode.AllocateTexture2D:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadBlob<byte>(stream, ref pos, store);
                break;
            case CommandOpcode.AllocateTextureCubeFace:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadBlob<byte>(stream, ref pos, store);
                break;
            case CommandOpcode.AllocateTexture3D:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadBlob<byte>(stream, ref pos, store);
                break;
            case CommandOpcode.UpdateTexture3D:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadBlob<byte>(stream, ref pos, store);
                break;
            case CommandOpcode.SetTextureWrap:
            case CommandOpcode.SetTextureFiltersOp:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU8(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                break;
            case CommandOpcode.SetTextureCompareMode:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU8(stream, ref pos);
                break;
            case CommandOpcode.GetTextureData:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.GetTextureDataPtr:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                pos += sizeof(long);
                break;
            case CommandOpcode.GetTextureCubeFaceData:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.ResolveTexture:
                _ = objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                break;
            default:
                if (ComputeCommandLayout.TrySkip(op, stream, ref pos, objects))
                    break;
                throw new InvalidOperationException($"WebGPUCommandTranslator: cannot skip unknown opcode {op}.");
        }
    }

    private static CommandOpcode ReadOpcode(ReadOnlySpan<byte> s, ref int pos)
    {
        var v = MemoryMarshal.Read<CommandOpcode>(s.Slice(pos));
        pos += sizeof(ushort);
        return v;
    }

    private static byte ReadU8(ReadOnlySpan<byte> s, ref int pos) { byte v = s[pos]; pos += 1; return v; }
    private static ushort ReadU16(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<ushort>(s.Slice(pos)); pos += sizeof(ushort); return v; }
    private static int ReadI32(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<int>(s.Slice(pos)); pos += sizeof(int); return v; }
    private static long ReadI64(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<long>(s.Slice(pos)); pos += sizeof(long); return v; }
    private static uint ReadU32(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<uint>(s.Slice(pos)); pos += sizeof(uint); return v; }
    private static float ReadF32(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<float>(s.Slice(pos)); pos += sizeof(float); return v; }

    private static T ReadStruct<T>(ReadOnlySpan<byte> s, ref int pos) where T : unmanaged
    {
        var v = MemoryMarshal.Read<T>(s.Slice(pos));
        pos += Unsafe.SizeOf<T>();
        return v;
    }

    private static ReadOnlySpan<T> ReadBlob<T>(ReadOnlySpan<byte> s, ref int pos, TransientStore store) where T : unmanaged
    {
        var r = ReadStruct<TransientStore.Ref>(s, ref pos);
        return store.Read<T>(r);
    }
}
