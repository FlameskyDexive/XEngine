// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Vector;

using CommandBuffer = XEngine.Runtime.CommandBuffer;

namespace XEngine.Runtime.Backends.Metal;

/// <summary>
/// M2 resource opcodes plus M4 draw/pass translation. Render-pass begin is delayed until the
/// first draw (or a clear-only flush). Rebinding the same target after a clear keeps pending
/// load-ops; Metal has no in-pass clear.
/// </summary>
internal sealed unsafe partial class MetalCommandTranslator
{
    private readonly MetalGraphicsDevice _device;
    private readonly MetalPassRecombiner _pass = new();
    private nint _commandBuffer;
    private nint _blitEncoder;
    private nint _renderEncoder;
    private nint _mslVertex;
    private nint _mslFragment;
    private ShaderVariant? _currentShader;
    private RasterizerState _currentRaster = new()
    {
        DepthTest = false,
        DepthWrite = false,
        CullFace = RasterizerState.PolyFace.None,
    };
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

    internal MetalPassRecombiner PassRecombiner => _pass;

    internal void BindMslFunctions(nint vertexFunction, nint fragmentFunction)
    {
        if (_mslVertex != 0)
            MetalNative.Release(_mslVertex);
        if (_mslFragment != 0)
            MetalNative.Release(_mslFragment);
        _mslVertex = vertexFunction == 0 ? 0 : MetalNative.Retain(vertexFunction);
        _mslFragment = fragmentFunction == 0 ? 0 : MetalNative.Retain(fragmentFunction);
    }

    internal void ReleaseMslFunctions()
    {
        BindMslFunctions(0, 0);
    }

    public MetalCommandTranslator(MetalGraphicsDevice device)
    {
        _device = device;
    }

    public void Translate(nint commandBuffer, CommandBuffer recorded)
    {
        _commandBuffer = commandBuffer;
        _blitEncoder = 0;
        _renderEncoder = 0;
        _currentShader = null;
        _viewportSet = false;
        _scissorSet = false;
        _pass.Reset();
        ResetFrameBindings(recorded);
        ReadOnlySpan<byte> stream = recorded._stream.AsSpan(0, recorded._streamPos);
        var objects = recorded._objects;
        TransientStore store = recorded._store;
        int pos = 0;
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
                        MetalAstcSupport.EnsureTextureFormatSupported(tex.ImageFormat, _device.SupportsAstc);
                        uint handle = _device.AllocateHandle();
                        tex.Handle = handle;
                        _device.Textures[handle] = new MetalTextureResource
                        {
                            EngineFormat = tex.ImageFormat,
                            Format = MetalFormats.ToPixelFormat(tex.ImageFormat),
                            Type = tex.Type,
                        };
                        break;
                    }
                    case CommandOpcode.DisposeTexture:
                    {
                        var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        if (tex.Handle != 0 && _device.Textures.Remove(tex.Handle, out MetalTextureResource? res))
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
                            GenerateMipmap(texture);
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
                        if (texture.Handle != 0 && _device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? res))
                        {
                            res.CompareEnabled = enabled;
                            res.SamplerDirty = true;
                        }
                        break;
                    }
                    case CommandOpcode.SetRenderTarget:
                    {
                        var target = objects[ReadU16(stream, ref pos)] as GraphicsFrameBuffer;
                        BindPendingRenderTarget(target);
                        _pass.PendingReadTarget = target;
                        break;
                    }
                    case CommandOpcode.SetRenderTargets:
                    {
                        var drawTarget = objects[ReadU16(stream, ref pos)] as GraphicsFrameBuffer;
                        var readTarget = objects[ReadU16(stream, ref pos)] as GraphicsFrameBuffer;
                        BindPendingRenderTarget(drawTarget);
                        _pass.PendingReadTarget = readTarget;
                        break;
                    }
                    case CommandOpcode.SetViewport:
                    {
                        _viewportX = ReadI32(stream, ref pos);
                        _viewportY = ReadI32(stream, ref pos);
                        _viewportWidth = ReadU32(stream, ref pos);
                        _viewportHeight = ReadU32(stream, ref pos);
                        _viewportSet = true;
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
                        ApplyScissor();
                        break;
                    }
                    case CommandOpcode.DisableScissor:
                        _scissorSet = false;
                        ApplyScissor();
                        break;
                    case CommandOpcode.ClearRenderTarget:
                    {
                        var flags = (ClearFlags)ReadU8(stream, ref pos);
                        float r = ReadF32(stream, ref pos);
                        float g = ReadF32(stream, ref pos);
                        float b = ReadF32(stream, ref pos);
                        float a = ReadF32(stream, ref pos);
                        float depth = ReadF32(stream, ref pos);
                        int stencil = ReadI32(stream, ref pos);
                        if (_renderEncoder != 0)
                            EndPass();
                        _pass.RecordClear(flags, r, g, b, a, depth, stencil);
                        break;
                    }
                    case CommandOpcode.BlitFramebuffer:
                    {
                        int srcX = ReadI32(stream, ref pos);
                        int srcY = ReadI32(stream, ref pos);
                        int srcW = ReadI32(stream, ref pos);
                        int srcH = ReadI32(stream, ref pos);
                        int dstX = ReadI32(stream, ref pos);
                        int dstY = ReadI32(stream, ref pos);
                        int dstW = ReadI32(stream, ref pos);
                        int dstH = ReadI32(stream, ref pos);
                        var mask = (ClearFlags)ReadU8(stream, ref pos);
                        _ = ReadU8(stream, ref pos);
                        BlitFramebuffer(srcX, srcY, srcW, srcH, dstX, dstY, dstW, dstH, mask);
                        break;
                    }
                    case CommandOpcode.SetRasterState:
                        _currentRaster = ReadStruct<RasterizerState>(stream, ref pos);
                        ApplyRaster();
                        break;
                    case CommandOpcode.SetShader:
                    {
                        object? shader = objects[ReadU16(stream, ref pos)];
                        _currentShader = shader as ShaderVariant;
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
                        if (framebuffer.Handle != 0)
                            _device.Framebuffers.Remove(framebuffer.Handle);
                        framebuffer.Handle = 0;
                        break;
                    }
                    case CommandOpcode.DrawArrays:
                    {
                        var vao = objects[ReadU16(stream, ref pos)] as GraphicsVertexArray;
                        Topology topology = (Topology)ReadU8(stream, ref pos);
                        int first = ReadI32(stream, ref pos);
                        uint count = ReadU32(stream, ref pos);
                        Draw(vao, topology, indexed: false, index32Bit: false, count, 1, 0, 0, first);
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
                        Draw(vao, topology, indexed: true, index32Bit, indexCount, 1, startIndex, baseVertex, 0);
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
                        Draw(vao, topology, indexed: true, index32Bit, indexCount, instanceCount, startIndex, baseVertex, 0);
                        break;
                    }
                    case CommandOpcode.AllocateTextureCubeFace:
                    {
                        var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                        int face = ReadI32(stream, ref pos);
                        int mip = ReadI32(stream, ref pos);
                        uint size = ReadU32(stream, ref pos);
                        ReadOnlySpan<byte> faceData = ReadBlob<byte>(stream, ref pos, store);
                        AllocateTextureCubeFace(tex, face, mip, size, faceData);
                        break;
                    }
                    case CommandOpcode.AllocateTexture3D:
                    case CommandOpcode.UpdateTexture3D:
                        throw new NotSupportedException($"Metal {op} is 2D/cube resources only.");
                    case CommandOpcode.ResolveTexture:
                        // Metal reports a single-sample-only capability mask, so the pipeline
                        // clamps MSAA to 1x and never records this opcode.
                        _ = ReadU16(stream, ref pos);
                        _ = ReadU16(stream, ref pos);
                        throw new NotSupportedException(
                            "Metal multisample resolve is not implemented; this device reports single-sample targets only.");
                    case CommandOpcode.BeginSample:
                    case CommandOpcode.CompileShader:
                    case CommandOpcode.DisposeShader:
                        _ = ReadU16(stream, ref pos);
                        break;
                    case CommandOpcode.EndSample:
                        break;
                    case CommandOpcode.SetGlobalTexture:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Resources.Texture2D? texture = objects[ReadU16(stream, ref pos)] as Resources.Texture2D;
                        if (texture != null && texture.IsValid())
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
                    case CommandOpcode.ClearGlobalTexture:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        _textures.Remove(name);
                        Rendering.PropertyState.s_globalTextures.Remove(name);
                        Rendering.PropertyState.s_globalTexturesCube.Remove(name);
                        break;
                    }
                    case CommandOpcode.SetGlobalTexture3D:
                    case CommandOpcode.SetGlobalTextureCube:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                        if (texture != null)
                            _textures[name] = texture;
                        else
                            _textures.Remove(name);
                        break;
                    }
                    case CommandOpcode.SetUniformTexture:
                    {
                        string? name = (string?)objects[ReadU16(stream, ref pos)];
                        GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                        if (name != null && texture != null)
                            _textures[name] = texture;
                        break;
                    }
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
                        Float4x4[] values = (Float4x4[])objects[ReadU16(stream, ref pos)]!;
                        Rendering.PropertyState.SetGlobalMatricesInternal(name, values);
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.SetUniformInt:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        int value = ReadI32(stream, ref pos);
                        if (name == "_ObjectID")
                        {
                            _objectUniforms._ObjectID = value;
                        }
                        else if (Rendering.UIUniformPacking.TrySetInt(ref _uiFragmentUniforms, name, value))
                            _uiFragmentUniformsDirty = true;
                        else
                            WarnOnce("SetUniformInt", $"uniform '{name}' is not packable yet.");
                        break;
                    }
                    case CommandOpcode.SetUniformFloat:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        float value = ReadF32(stream, ref pos);
                        if (Rendering.UIUniformPacking.TrySetFloat(ref _uiFragmentUniforms, name, value))
                            _uiFragmentUniformsDirty = true;
                        else
                            WarnOnce("SetUniformFloat", $"uniform '{name}' is not packable yet.");
                        break;
                    }
                    case CommandOpcode.SetUniformVec2:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Float2 value = ReadStruct<Float2>(stream, ref pos);
                        if (Rendering.UIUniformPacking.TrySetVector2(ref _uiFragmentUniforms, name, value))
                            _uiFragmentUniformsDirty = true;
                        else
                            WarnOnce("SetUniformVec2", $"uniform '{name}' is not packable yet.");
                        break;
                    }
                    case CommandOpcode.SetUniformVec4:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Float4 value = ReadStruct<Float4>(stream, ref pos);
                        if (Rendering.UIUniformPacking.TrySetVector4(ref _uiFragmentUniforms, name, value))
                            _uiFragmentUniformsDirty = true;
                        else
                            WarnOnce("SetUniformVec4", $"uniform '{name}' is not packable yet.");
                        break;
                    }
                    case CommandOpcode.SetUniformMatrix:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        Float4x4 value = ReadStruct<Float4x4>(stream, ref pos);
                        if (name == "projection")
                        {
                            _uiVertexUniforms.projection = value;
                            _uiVertexUniformsDirty = true;
                        }
                        else if (name == "xengine_ObjectToWorld")
                            _objectUniforms.xengine_ObjectToWorld = value;
                        else if (name == "xengine_WorldToObject")
                            _objectUniforms.xengine_WorldToObject = value;
                        else if (name == "xengine_PrevObjectToWorld")
                            _objectUniforms.xengine_PrevObjectToWorld = value;
                        else if (Rendering.UIUniformPacking.TrySetMatrix(ref _uiFragmentUniforms, name, value))
                            _uiFragmentUniformsDirty = true;
                        else
                        {
                            WarnOnce("SetUniformMatrix", $"uniform '{name}' is not packable yet.");
                            break;
                        }
                        break;
                    }
                    case CommandOpcode.SetUniformMatrixArray:
                    {
                        string name = (string)objects[ReadU16(stream, ref pos)]!;
                        _ = ReadU32(stream, ref pos);
                        _ = ReadBlob<Float4x4>(stream, ref pos, store);
                        WarnOnce("SetUniformMatrixArray", $"matrix array '{name}' is not packable on Metal yet.");
                        break;
                    }
                    case CommandOpcode.SetGlobalBuffer:
                    case CommandOpcode.SetUniformBuffer:
                    {
                        string? name = (string?)objects[ReadU16(stream, ref pos)];
                        GraphicsBuffer? buffer = objects[ReadU16(stream, ref pos)] as GraphicsBuffer;
                        _ = ReadU32(stream, ref pos);
                        if (name != null && buffer != null)
                        {
                            _uniformBuffers[name] = buffer;
                            if (name == "GlobalUniforms")
                                _globalUniformsExplicitlyBound = true;
                        }
                        break;
                    }
                    case CommandOpcode.SetProperties:
                    {
                        _materialProperties = objects[ReadU16(stream, ref pos)] as Rendering.PropertyState;
                        _materialShader = null;
                        _instanceMaterialOverrides = null;
                        _instanceTextureState = null;
                        _materialUniformsDirty = true;
                        ApplyMaterialTextureBindings();
                        break;
                    }
                    case CommandOpcode.SetMaterialProperties:
                    {
                        _materialProperties = objects[ReadU16(stream, ref pos)] as Rendering.PropertyState;
                        _materialShader = objects[ReadU16(stream, ref pos)] as Resources.Shader;
                        _instanceMaterialOverrides = null;
                        _instanceTextureState = null;
                        _materialUniformsDirty = true;
                        ApplyMaterialTextureBindings();
                        break;
                    }
                    case CommandOpcode.SetInstanceProperties:
                    {
                        var properties = objects[ReadU16(stream, ref pos)] as Rendering.PropertyState;
                        ApplyObjectProperties(properties);
                        _instanceTextureState = properties;
                        Rendering.PropertyState? overrides =
                            properties != null && Rendering.MaterialUniformPacking.HasMaterialBlockOverrides(properties)
                                ? properties
                                : null;
                        if (overrides != null || _instanceMaterialOverrides != null)
                        {
                            _instanceMaterialOverrides = overrides;
                            _materialUniformsDirty = true;
                        }
                        ApplyMaterialTextureBindings();
                        break;
                    }
                    case CommandOpcode.ClearProperties:
                    {
                        _materialProperties = null;
                        _materialShader = null;
                        _instanceMaterialOverrides = null;
                        _instanceTextureState = null;
                        _materialUniformsDirty = true;
                        ClearObjectTextureBindings();
                        Rendering.MaterialUniformPacking.ClearTextureBindings(_textures);
                        Rendering.MaterialUniformPacking.ClearTextureBindings(_materialTextures);
                        break;
                    }
                    case CommandOpcode.ClearAllGlobals:
                    {
                        Rendering.PropertyState.ClearGlobalsInternal();
                        _textures.Clear();
                        _standardLightingDirty = true;
                        break;
                    }
                    case CommandOpcode.ClearInstanceProperties:
                    {
                        _objectUniforms = Rendering.ObjectUniformsData.Identity;
                        if (_instanceMaterialOverrides != null)
                        {
                            _instanceMaterialOverrides = null;
                            _materialUniformsDirty = true;
                        }
                        break;
                    }
                    case CommandOpcode.GetTextureData:
                    {
                        GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                        int mip = ReadI32(stream, ref pos);
                        byte[]? destination = objects[ReadU16(stream, ref pos)] as byte[];
                        if (texture != null && destination != null &&
                            _device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? readResource))
                        {
                            // Deferred: copied out after this command buffer completes (getBytes
                            // has no implicit sync with in-flight encoders).
                            _pendingReadbacks.Add((readResource, mip, -1, destination, 0));
                        }
                        break;
                    }
                    case CommandOpcode.GetTextureDataPtr:
                    {
                        GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                        int mip = ReadI32(stream, ref pos);
                        nint destination = (nint)ReadI64(stream, ref pos);
                        if (texture != null && destination != 0 &&
                            _device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? ptrResource))
                        {
                            _pendingReadbacks.Add((ptrResource, mip, -1, null, destination));
                        }
                        break;
                    }
                    case CommandOpcode.GetTextureCubeFaceData:
                    {
                        GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                        int face = ReadI32(stream, ref pos);
                        int mip = ReadI32(stream, ref pos);
                        byte[]? destination = objects[ReadU16(stream, ref pos)] as byte[];
                        if (texture != null && destination != null &&
                            _device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? cubeResource))
                        {
                            // Cube faces share the cube texture — after completion the face is
                            // read directly at the right slice.
                            _pendingReadbacks.Add((cubeResource, mip, face, destination, 0));
                        }
                        break;
                    }
                    default:
                        SkipOpcode(op, stream, ref pos, objects, store);
                        break;
                }
            }
        }
        finally
        {
            if (_pass.HasPendingLoadOps)
                BeginPassIfNeeded();
            EndPass();
            EndBlit();
            _commandBuffer = 0;
        }
    }

    private void CreateBuffer(GraphicsBuffer buf, bool dynamic, ReadOnlySpan<byte> data)
    {
        ulong size = data.Length > 0 ? (ulong)data.Length : Math.Max(1u, buf.SizeInBytes);
        size = buf.OriginalType == BufferType.UniformBuffer
            ? MetalFormats.Align((uint)size, 16)
            : MetalFormats.Align((uint)size, 4);

        nint gpu = MetalNative.NewBuffer(_device.NativeDevice, (nuint)size, 0);
        if (gpu == 0)
            throw new InvalidOperationException("MTLDevice newBufferWithLength returned nil.");
        if (data.Length > 0)
            WriteBuffer(gpu, 0, data);

        uint handle = _device.AllocateHandle();
        buf.Handle = handle;
        buf.SizeInBytes = checked((uint)size);
        _device.Buffers[handle] = new MetalBufferResource
        {
            Buffer = gpu,
            Size = size,
            Type = buf.OriginalType,
            Dynamic = dynamic,
        };
    }

    private void UpdateBuffer(GraphicsBuffer buf, uint dstOffset, ReadOnlySpan<byte> blob)
    {
        if (buf.Handle == 0 || !_device.Buffers.TryGetValue(buf.Handle, out MetalBufferResource? res) || res.Buffer == 0)
            return;
        if (blob.Length == 0)
            return;

        if ((ulong)dstOffset + (ulong)blob.Length > res.Size)
        {
            if (dstOffset != 0)
                throw new ArgumentOutOfRangeException(nameof(dstOffset), "Metal partial buffer update exceeds the destination size.");
            ReplaceBufferStorage(buf, res, blob);
            return;
        }

        WriteBuffer(res.Buffer, dstOffset, blob);
    }

    private void ReplaceBufferStorage(GraphicsBuffer buffer, MetalBufferResource previous, ReadOnlySpan<byte> data)
    {
        ulong doubled = previous.Size >= uint.MaxValue / 2 ? uint.MaxValue : previous.Size * 2;
        ulong size = Math.Max((ulong)data.Length, doubled);
        size = previous.Type == BufferType.UniformBuffer
            ? MetalFormats.Align((uint)size, 16)
            : MetalFormats.Align((uint)size, 4);

        nint replacement = MetalNative.NewBuffer(_device.NativeDevice, (nuint)size, 0);
        if (replacement == 0)
            throw new InvalidOperationException("MTLDevice newBufferWithLength returned nil while growing a buffer.");
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
        if (_device.Buffers.Remove(buf.Handle, out MetalBufferResource? res))
            res.Release();
        buf.Handle = 0;
    }

    private void AllocateTexture2D(GraphicsTexture tex, int mip, uint width, uint height, ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out MetalTextureResource? res))
            return;
        if (mip != 0)
            return;
        if (tex.Type != TextureType.Texture2D)
            throw new NotSupportedException("Metal AllocateTexture2D requires Texture2D.");
        MetalAstcSupport.EnsureTextureFormatSupported(tex.ImageFormat, _device.SupportsAstc);

        res.ReleaseTexture();
        res.Format = MetalFormats.ToPixelFormat(tex.ImageFormat);
        res.EngineFormat = tex.ImageFormat;
        res.Type = tex.Type;
        res.Width = width;
        res.Height = height;
        res.MipLevels = 1;
        res.Texture = MetalNative.NewTexture2D(
            _device.NativeDevice,
            res.Format,
            width,
            height,
            mipmapped: false,
            MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget,
            MetalFormats.ToStorageMode(tex.ImageFormat));
        if (res.Texture == 0)
            throw new InvalidOperationException("MTLDevice newTextureWithDescriptor returned nil.");

        if (data.Length > 0 && !MetalFormats.IsDepth(tex.ImageFormat))
            WriteTexture(res, 0, 0, 0, width, height, data);
    }

    /// <summary>
    /// Creates (first face) or reuses the cube MTLTexture and uploads one face's mip level.
    /// GraphicsTexture.Type == TextureCubeMap carries no per-face storage; Metal models the six
    /// faces as slices 0..5 of a textureCube resource.
    /// </summary>
    private void AllocateTextureCubeFace(GraphicsTexture tex, int face, int mip, uint size, ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out MetalTextureResource? res))
            return;
        if (tex.Type != TextureType.TextureCubeMap)
            throw new NotSupportedException("Metal AllocateTextureCubeFace requires TextureCubeMap.");
        if (face < 0 || face > 5)
            throw new ArgumentOutOfRangeException(nameof(face));
        MetalAstcSupport.EnsureTextureFormatSupported(tex.ImageFormat, _device.SupportsAstc);

        if (res.Texture == 0 || res.Type != TextureType.TextureCubeMap)
        {
            res.ReleaseTexture();
            res.Format = MetalFormats.ToPixelFormat(tex.ImageFormat);
            res.EngineFormat = tex.ImageFormat;
            res.Type = tex.Type;
            res.Width = size;
            res.Height = size;
            res.MipLevels = 1;
            res.Texture = MetalNative.NewTextureCube(
                _device.NativeDevice,
                res.Format,
                size,
                mipmapped: false,
                MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget,
                MetalFormats.ToStorageMode(tex.ImageFormat));
            if (res.Texture == 0)
                throw new InvalidOperationException("MTLDevice newTextureWithDescriptor (cube) returned nil.");
        }

        if (mip != 0)
            return;
        if (data.Length == 0 || MetalFormats.IsDepth(tex.ImageFormat))
            return;

        int bpp = MetalFormats.BytesPerPixel(tex.ImageFormat);
        nuint bytesPerRow = (nuint)(size * (uint)bpp);
        unsafe
        {
            fixed (byte* source = data)
            {
                MetalNative.ReplaceRegionCube(res.Texture, (uint)face, (uint)mip, size, size, (nint)source, bytesPerRow);
            }
        }
    }

    private void UpdateTexture2D(GraphicsTexture tex, int mip, int x, int y, uint width, uint height, ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out MetalTextureResource? res) || res.Texture == 0)
            return;
        if (tex.Type != TextureType.Texture2D)
            throw new NotSupportedException("Metal UpdateTexture requires a 2D texture.");
        if (MetalFormats.IsDepth(res.EngineFormat))
            throw new NotSupportedException("Metal depth texture updates are not supported.");
        if (mip < 0 || (uint)mip >= res.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (x < 0 || y < 0)
            throw new ArgumentOutOfRangeException(x < 0 ? nameof(x) : nameof(y));
        if (width == 0 || height == 0 || data.Length == 0)
            return;

        // replaceRegion is a CPU write, so it can race an earlier encoded mip-generation
        // blit that has not executed yet. Upload into a fresh texture and copy on the GPU
        // to preserve command order, including constructor mipgen -> upload -> mipgen.
        nint upload = MetalNative.NewTexture2D(
            _device.NativeDevice, res.Format, width, height, mipmapped: false,
            MTLTextureUsage.ShaderRead, MetalFormats.ToStorageMode(res.EngineFormat));
        if (upload == 0)
            throw new InvalidOperationException("Failed to create a Metal texture upload resource.");
        try
        {
            WriteTexture(upload, res.EngineFormat, 0, 0, 0, width, height, data);
            EndPass();
            nint blit = EnsureBlit();
            MetalNative.CopyTextureToTexture(blit, upload, res.Texture,
                0, 0, (uint)x, (uint)y, width, height, 0, (uint)mip);
            EndBlit();
        }
        finally
        {
            // The command buffer retains resources referenced by encoded copies until completion.
            MetalNative.Release(upload);
        }
    }

    private void GenerateMipmap(GraphicsTexture texture)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? res) || res.Texture == 0)
            return;
        if (texture.Type != TextureType.Texture2D)
            return;
        // A 1×1 Metal texture has no lower mip levels even when the caller asks to generate
        // them. Avoid allocating a replacement texture and issuing an invalid blit command.
        if (res.Width == 1 && res.Height == 1)
            return;

        EndPass();

        nint source = res.Texture;
        nint destination = MetalNative.NewTexture2D(
            _device.NativeDevice,
            res.Format,
            res.Width,
            res.Height,
            mipmapped: true,
            MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget,
            MetalFormats.ToStorageMode(res.EngineFormat));
        if (destination == 0)
            throw new InvalidOperationException("Failed to recreate a mipmapped Metal texture.");

        nint blit = EnsureBlit();
        MetalNative.CopyTextureToTexture(blit, source, destination, res.Width, res.Height);
        MetalNative.GenerateMipmaps(blit, destination);
        EndBlit();

        res.ReleaseTexture();
        res.Texture = destination;
        nuint levels = MetalNative.GetMipmapLevelCount(destination);
        res.MipLevels = levels == 0 ? 1u : (uint)levels;
    }

    private void SetTextureWrap(GraphicsTexture texture, byte axis, TextureWrap wrap)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? resource))
            return;
        switch (axis)
        {
            case 0: resource.WrapS = wrap; break;
            case 1: resource.WrapT = wrap; break;
            case 2: resource.WrapR = wrap; break;
            default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "Metal texture wrap axis must be 0, 1, or 2.");
        }
        resource.SamplerDirty = true;
        EnsureSampler(resource);
    }

    private void SetTextureFilters(GraphicsTexture texture, TextureMin min, TextureMag mag)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? resource))
            return;
        resource.MinFilter = min;
        resource.MagFilter = mag;
        resource.SamplerDirty = true;
        EnsureSampler(resource);
    }

    private void EnsureSampler(MetalTextureResource resource)
    {
        if (!resource.SamplerDirty && resource.Sampler != 0)
            return;

        resource.ReleaseSampler();
        resource.Sampler = MetalNative.NewSamplerState(
            _device.NativeDevice,
            MetalFormats.ToAddressMode(resource.WrapS),
            MetalFormats.ToAddressMode(resource.WrapT),
            MetalFormats.ToAddressMode(resource.WrapR),
            MetalFormats.ToMinMag(resource.MinFilter),
            MetalFormats.ToMinMag(resource.MagFilter),
            MetalFormats.ToMipFilter(resource.MinFilter));
        if (resource.Sampler == 0)
            throw new InvalidOperationException("MTLDevice newSamplerStateWithDescriptor returned nil.");
        resource.SamplerDirty = false;
    }

    private static void WriteBuffer(nint buffer, uint offset, ReadOnlySpan<byte> data)
    {
        nint contents = MetalNative.BufferContents(buffer);
        if (contents == 0)
            throw new InvalidOperationException("MTLBuffer contents returned null.");
        unsafe
        {
            fixed (byte* source = data)
                Buffer.MemoryCopy(source, (void*)(contents + (nint)offset), data.Length, data.Length);
        }
    }

    private static void WriteTexture(
        MetalTextureResource resource,
        uint mip,
        uint x,
        uint y,
        uint width,
        uint height,
        ReadOnlySpan<byte> data)
        => WriteTexture(resource.Texture, resource.EngineFormat, mip, x, y, width, height, data);

    private static void WriteTexture(
        nint texture, TextureImageFormat format, uint mip, uint x, uint y,
        uint width, uint height, ReadOnlySpan<byte> data)
    {
        int bpp = MetalFormats.BytesPerPixel(format);
        nuint bpr = (nuint)(width * (uint)bpp);
        unsafe
        {
            fixed (byte* source = data)
            {
                MetalNative.ReplaceRegion(texture, mip, x, y, width, height, (nint)source, bpr);
            }
        }
    }

    private nint EnsureBlit()
    {
        if (_blitEncoder != 0)
            return _blitEncoder;
        if (_commandBuffer == 0)
            throw new InvalidOperationException("Metal blit encoder requires an MTLCommandBuffer.");
        _blitEncoder = MetalNative.BlitCommandEncoder(_commandBuffer);
        if (_blitEncoder == 0)
            throw new InvalidOperationException("MTLCommandBuffer blitCommandEncoder returned nil.");
        return _blitEncoder;
    }

    private void EndBlit()
    {
        if (_blitEncoder == 0)
            return;
        MetalNative.EndEncoding(_blitEncoder);
        _blitEncoder = 0;
    }

    private static float ReadF32(ReadOnlySpan<byte> s, ref int pos)
    {
        var v = MemoryMarshal.Read<float>(s.Slice(pos));
        pos += sizeof(float);
        return v;
    }

    private static void SkipOpcode(
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
                pos += sizeof(long);
                break;
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
            case CommandOpcode.SetShader:
            case CommandOpcode.SetRenderTarget:
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
            case CommandOpcode.ResolveTexture:
                _ = objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                break;
            default:
                if (ComputeCommandLayout.TrySkip(op, stream, ref pos, objects))
                    break;
                throw new NotSupportedException($"Metal {op} is not implemented yet.");
        }
    }

    private static CommandOpcode ReadOpcode(ReadOnlySpan<byte> s, ref int pos)
    {
        var v = MemoryMarshal.Read<CommandOpcode>(s.Slice(pos));
        pos += sizeof(ushort);
        return v;
    }

    private static byte ReadU8(ReadOnlySpan<byte> s, ref int pos)
    {
        byte v = s[pos];
        pos += 1;
        return v;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, ref int pos)
    {
        var v = MemoryMarshal.Read<ushort>(s.Slice(pos));
        pos += sizeof(ushort);
        return v;
    }

    private static int ReadI32(ReadOnlySpan<byte> s, ref int pos)
    {
        var v = MemoryMarshal.Read<int>(s.Slice(pos));
        pos += sizeof(int);
        return v;
    }

    private static uint ReadU32(ReadOnlySpan<byte> s, ref int pos)
    {
        var v = MemoryMarshal.Read<uint>(s.Slice(pos));
        pos += sizeof(uint);
        return v;
    }

    private static long ReadI64(ReadOnlySpan<byte> s, ref int pos)
    {
        var v = MemoryMarshal.Read<long>(s.Slice(pos));
        pos += sizeof(long);
        return v;
    }

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
