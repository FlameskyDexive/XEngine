// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Vortice;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Resources;

using CommandBuffer = XEngine.Runtime.CommandBuffer;

namespace XEngine.Runtime.Backends.D3D12;

/// <summary>
/// Translates engine <see cref="CommandBuffer"/> recordings into D3D12 commands.
/// Resource create/dispose and clears are implemented; draws no-op until PSOs exist.
/// </summary>
internal sealed unsafe class D3D12CommandTranslator
{
    private const ulong TransientUniformArenaSize = 64 * 1024;
    private const ulong TransientBufferUploadArenaSize = 4 * 1024 * 1024;

    private readonly D3D12GraphicsDevice _device;
    private readonly ID3D12DescriptorHeap[] _descriptorHeaps;
    private readonly HashSet<CommandOpcode> _warnedOpcodes = new();
    private bool _warnedDrawNoPso;

    private GraphicsFrameBuffer? _pendingRenderTarget;
    private GraphicsFrameBuffer? _pendingReadTarget;
    private bool _scissorEnabled;
    private RectI _viewportRect = new(0, 0, 16384, 16384);
    private RectI _scissorRect = new(0, 0, 16384, 16384);
    private ShaderVariant? _currentShader;
    private ShaderVariant? _currentComputeShader;
    private RasterizerState _currentRaster = new();
    private readonly Dictionary<string, GraphicsBuffer> _uniformBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, D3D12TransientUniformBinding> _transientUniformBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphicsTexture> _textures = new(StringComparer.Ordinal);
    // Material-owned texture bindings plus the per-draw instance texture names layered on top of
    // them; ClearObjectTextureBindings restores an instance-bound name (a text's font atlas via
    // _MainTex, a renderable's _Lightmap) to its material/global binding before the next draw.
    // Mirrors the Vulkan translator's mechanism.
    private readonly Dictionary<string, GraphicsTexture> _materialTextures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _objectTextureNames = new(StringComparer.Ordinal);
    private Rendering.ObjectUniformsData _objectUniforms;
    private bool _objectUniformsDirty;
    private Rendering.UIVertexUniformsData _uiVertexUniforms;
    private bool _uiVertexUniformsDirty;
    private Rendering.UIFragmentUniformsData _uiFragmentUniforms;
    private bool _uiFragmentUniformsDirty;
    private Rendering.PropertyState? _materialProperties;
    private Resources.Shader? _materialShader;
    // Instance PropertyState carrying material-block overrides for the current draw (null when
    // the instance state only holds per-object data). Merged over _materialProperties by
    // EffectiveMaterialProperties; the scratch below backs that merge without allocating.
    private Rendering.PropertyState? _instanceMaterialOverrides;
    private readonly Rendering.PropertyState _mergedMaterialProperties = new();
    private bool _materialUniformsDirty;
    private bool _standardLightingDirty;
    private List<ID3D12Resource>? _submissionTransientResources;
    private ID3D12Resource? _transientUniformArena;
    private ulong _transientUniformArenaOffset;
    private byte* _transientUniformArenaPtr; // persistent upload-heap mapping (mapped once at creation)
    // Pooled scratch for generic cbuffer packing (fallback path). Grows on demand.
    private byte[]? _genericUniformScratch;

    // White texture fallback for missing texture bindings. D3D12 requires all descriptor
    // set bindings to be valid; when a shader declares a sampler but no texture is provided
    // (e.g., material override missing and no shader default), bind a 1x1 white texture
    // instead of crashing the draw with "D3D12 draw requires texture '<name>'".
    // Per-device instance, created by InitializeMissingBindingFallbacks during device init.
    private Texture2D? _whiteTextureFallbackRef;

    /// <summary>Drops both transient arenas. Called at every non-frame Translate and by the
    /// device whenever the frame's transient resources are disposed (BeginFrame / mid-frame
    /// flush) so the translator cannot write into a released resource.</summary>
    internal void ResetTransientArenas()
    {
        _transientUniformArena = null;
        _transientUniformArenaOffset = 0;
        _transientUniformArenaPtr = null;
        _transientBufferUploadArena = null;
        _transientBufferUploadArenaOffset = 0;
        // Both call sites coincide with a command-list Reset, so tracked list state resets too.
        ResetTrackedListState();
    }

    // Per-draw state dedup: SetPipelineState / SetGraphicsRootSignature / IASetPrimitiveTopology /
    // OMSetRenderTargets were re-issued on every draw even when unchanged (re-setting the root
    // signature also invalidates all root bindings in the driver). The cached objects come from
    // the device's PSO/layout caches, so reference equality identifies them. Every path that
    // writes these list states outside the tracked setters either records into or invalidates
    // the tracking.
    private ID3D12PipelineState? _lastPipelineState;
    private ID3D12RootSignature? _lastRootSignature;
    private Vortice.Direct3D.PrimitiveTopology _lastTopology = unchecked((Vortice.Direct3D.PrimitiveTopology)(-1));
    private D3D12FramebufferResource? _lastBoundFramebuffer;
    private bool _lastBoundBackbuffer;
    private int _lastStencilRef = -1;

    private void ResetTrackedListState()
    {
        _lastPipelineState = null;
        _lastRootSignature = null;
        _lastTopology = unchecked((Vortice.Direct3D.PrimitiveTopology)(-1));
        InvalidateTrackedRenderTargets();
    }

    private void InvalidateTrackedRenderTargets()
    {
        _lastBoundFramebuffer = null;
        _lastBoundBackbuffer = false;
        _lastStencilRef = -1;
    }

    private void SetPipelineTracked(ID3D12GraphicsCommandList list, ID3D12PipelineState pipeline)
    {
        if (ReferenceEquals(_lastPipelineState, pipeline))
            return;
        list.SetPipelineState(pipeline);
        _lastPipelineState = pipeline;
    }

    private void SetGraphicsRootSignatureTracked(ID3D12GraphicsCommandList list, ID3D12RootSignature rootSignature)
    {
        if (ReferenceEquals(_lastRootSignature, rootSignature))
            return;
        list.SetGraphicsRootSignature(rootSignature);
        _lastRootSignature = rootSignature;
    }

    private void SetTopologyTracked(ID3D12GraphicsCommandList list, Vortice.Direct3D.PrimitiveTopology topology)
    {
        if (_lastTopology == topology)
            return;
        list.IASetPrimitiveTopology(topology);
        _lastTopology = topology;
    }
    private ID3D12Resource? _transientBufferUploadArena;
    private ulong _transientBufferUploadArenaOffset;

    public D3D12CommandTranslator(D3D12GraphicsDevice device)
    {
        _device = device;
        _descriptorHeaps = [device.CbvSrvUavHeap, device.SamplerHeap];
    }

    public void Translate(
        CommandBuffer commandBuffer,
        ID3D12GraphicsCommandList list,
        List<ID3D12Resource> transientResources,
        bool preserveUniformArena = false)
    {
        var stream = commandBuffer._stream.AsSpan(0, commandBuffer._streamPos);
        var objects = commandBuffer._objects;
        var store = commandBuffer._store;
        var fdScope = commandBuffer._fdScopeKind;
        int pos = 0;
        _uniformBuffers.Clear();
        _transientUniformBuffers.Clear();
        _textures.Clear();
        _materialTextures.Clear();
        _objectTextureNames.Clear();
        _objectUniforms = Rendering.ObjectUniformsData.Identity;
        _objectUniformsDirty = true;
        _uiVertexUniforms = default;
        _uiVertexUniformsDirty = true;
        _uiFragmentUniforms = default;
        _uiFragmentUniformsDirty = true;
        _materialProperties = null;
        _materialShader = null;
        _instanceMaterialOverrides = null;
        _materialUniformsDirty = true;
        _standardLightingDirty = true;
        _submissionTransientResources = transientResources;
        if (!preserveUniformArena)
            ResetTransientArenas();
        _currentComputeShader = null;
        GraphicsBuffer? globalUniforms = Rendering.GlobalUniforms.GetBuffer();
        if (globalUniforms is { Handle: not 0 })
            _uniformBuffers["GlobalUniforms"] = globalUniforms;

        while (pos < stream.Length)
        {
            CommandOpcode op = ReadOpcode(stream, ref pos);
            switch (op)
            {
                case CommandOpcode.SetRenderTarget:
                {
                    GraphicsFrameBuffer? framebuffer = (GraphicsFrameBuffer?)objects[ReadU16(stream, ref pos)];
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    SetRenderTarget(list, framebuffer);
                    _pendingReadTarget = framebuffer;
                    break;
                }
                case CommandOpcode.SetRenderTargets:
                {
                    var draw = (GraphicsFrameBuffer?)objects[ReadU16(stream, ref pos)];
                    var read = (GraphicsFrameBuffer?)objects[ReadU16(stream, ref pos)];
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    SetRenderTarget(list, draw);
                    _pendingReadTarget = read;
                    break;
                }
                case CommandOpcode.SetViewport:
                {
                    int x = ReadI32(stream, ref pos);
                    int y = ReadI32(stream, ref pos);
                    uint w = ReadU32(stream, ref pos);
                    uint h = ReadU32(stream, ref pos);
                    list.RSSetViewport(new Viewport(x, y, w, h, 0f, 1f));
                    _viewportRect = new RectI(x, y, checked((int)w), checked((int)h));
                    break;
                }
                case CommandOpcode.SetScissor:
                {
                    int x = ReadI32(stream, ref pos);
                    int y = ReadI32(stream, ref pos);
                    uint w = ReadU32(stream, ref pos);
                    uint h = ReadU32(stream, ref pos);
                    _scissorEnabled = true;
                    // RectI takes x/y/width/height, not native RECT left/top/right/bottom.
                    _scissorRect = new RectI(x, y, checked((int)w), checked((int)h));
                    list.RSSetScissorRect(_scissorRect);
                    break;
                }
                case CommandOpcode.DisableScissor:
                {
                    _scissorEnabled = false;
                    list.RSSetScissorRect(_viewportRect);
                    break;
                }
                case CommandOpcode.ClearRenderTarget:
                {
                    ClearFlags flags = (ClearFlags)ReadU8(stream, ref pos);
                    float r = ReadF32(stream, ref pos);
                    float g = ReadF32(stream, ref pos);
                    float b = ReadF32(stream, ref pos);
                    float a = ReadF32(stream, ref pos);
                    float depth = ReadF32(stream, ref pos);
                    int stencil = ReadI32(stream, ref pos);
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DoClear(list, flags, r, g, b, a, depth, stencil);
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
                    BlitFilter filter = (BlitFilter)ReadU8(stream, ref pos);
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DoBlit(list, srcX, srcY, srcWidth, srcHeight, dstX, dstY, dstWidth, dstHeight, mask, filter);
                    break;
                }
                case CommandOpcode.ResolveTexture:
                {
                    var source = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    var destination = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DoResolveTexture(list, source, destination);
                    break;
                }
                case CommandOpcode.GetTextureData:
                {
                    GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    int mip = ReadI32(stream, ref pos);
                    byte[]? destination = objects[ReadU16(stream, ref pos)] as byte[];
                    ReadTextureData(texture, mip, destination);
                    break;
                }
                case CommandOpcode.GetTextureDataPtr:
                {
                    GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    int mip = ReadI32(stream, ref pos);
                    nint destination = (nint)ReadI64(stream, ref pos);
                    ReadTextureData(texture, mip, destination);
                    break;
                }
                case CommandOpcode.GetTextureCubeFaceData:
                {
                    GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    int face = ReadI32(stream, ref pos);
                    int mip = ReadI32(stream, ref pos);
                    byte[]? destination = objects[ReadU16(stream, ref pos)] as byte[];
                    ReadTextureCubeFaceData(texture, face, mip, destination);
                    break;
                }
                case CommandOpcode.SetShader:
                {
                    _currentShader = objects[ReadU16(stream, ref pos)] as ShaderVariant;
                    if (_currentShader?.Bytecode?.Format != ShaderBytecodeFormat.Dxil)
                        WarnOnce(CommandOpcode.SetShader, "D3D12 shader bind skipped: expected a DXIL ShaderVariant.");
                    else
                        _device.GetOrCreateShaderLayout(_currentShader);
                    break;
                }
                case CommandOpcode.SetInstanceProperties:
                {
                    var properties = (Rendering.PropertyState?)objects[ReadU16(stream, ref pos)];
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
                case CommandOpcode.SetProperties:
                {
                    _materialProperties = (Rendering.PropertyState?)objects[ReadU16(stream, ref pos)];
                    _materialShader = null;
                    _instanceMaterialOverrides = null;
                    _materialUniformsDirty = true;
                    ApplyMaterialTextureBindings();
                    break;
                }
                case CommandOpcode.SetMaterialProperties:
                {
                    _materialProperties = (Rendering.PropertyState?)objects[ReadU16(stream, ref pos)];
                    _materialShader = (Resources.Shader?)objects[ReadU16(stream, ref pos)];
                    _instanceMaterialOverrides = null;
                    _materialUniformsDirty = true;
                    ApplyMaterialTextureBindings();
                    break;
                }
                case CommandOpcode.ClearProperties:
                {
                    _materialProperties = null;
                    _materialShader = null;
                    _instanceMaterialOverrides = null;
                    _materialUniformsDirty = true;
                    ClearObjectTextureBindings();
                    Rendering.MaterialUniformPacking.ClearTextureBindings(_textures);
                    Rendering.MaterialUniformPacking.ClearTextureBindings(_materialTextures);
                    break;
                }
                case CommandOpcode.SetGlobalTexture:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Resources.Texture2D? texture = (Resources.Texture2D?)objects[ReadU16(stream, ref pos)];
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
                case CommandOpcode.SetGlobalTextureCube:
                {
                    string cubeName = (string)objects[ReadU16(stream, ref pos)]!;
                    Resources.Cubemap? cube = (Resources.Cubemap?)objects[ReadU16(stream, ref pos)];
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
                case CommandOpcode.ClearGlobalTexture:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    _textures.Remove(name);
                    Rendering.PropertyState.s_globalTextures.Remove(name);
                    Rendering.PropertyState.s_globalTexturesCube.Remove(name);
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
                    Rendering.PropertyState.s_globalVectors2[name] = ReadStruct<Vector.Float2>(stream, ref pos);
                    _standardLightingDirty = true;
                    break;
                }
                case CommandOpcode.SetGlobalVec3:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Rendering.PropertyState.s_globalVectors3[name] = ReadStruct<Vector.Float3>(stream, ref pos);
                    _standardLightingDirty = true;
                    break;
                }
                case CommandOpcode.SetGlobalVec4:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Rendering.PropertyState.s_globalVectors4[name] = ReadStruct<Vector.Float4>(stream, ref pos);
                    _standardLightingDirty = true;
                    break;
                }
                case CommandOpcode.SetGlobalColor:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Rendering.PropertyState.s_globalColors[name] = ReadStruct<XEngine.Vector.Color>(stream, ref pos);
                    _standardLightingDirty = true;
                    break;
                }
                case CommandOpcode.SetGlobalMatrix:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Rendering.PropertyState.s_globalMatrices[name] = ReadStruct<Vector.Float4x4>(stream, ref pos);
                    _standardLightingDirty = true;
                    break;
                }
                case CommandOpcode.SetGlobalMatrices:
                {
                    // Array-form global matrix upload (e.g. _PointShadowMatrices / _SpotShadowMatrices).
                    // Mirrors the OpenGL command executor path by populating s_globalMatrixArr so the
                    // Standard shadow packer can read it back, and dirties the lighting/shadow block.
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Vector.Float4x4[] values = (Vector.Float4x4[])objects[ReadU16(stream, ref pos)]!;
                    Rendering.PropertyState.SetGlobalMatricesInternal(name, values);
                    _standardLightingDirty = true;
                    break;
                }
                case CommandOpcode.ClearAllGlobals:
                    Rendering.PropertyState.ClearGlobalsInternal();
                    _textures.Clear();
                    _standardLightingDirty = true;
                    break;
                case CommandOpcode.ClearInstanceProperties:
                {
                    _objectUniforms = Rendering.ObjectUniformsData.Identity;
                    _objectUniformsDirty = true;
                    if (_instanceMaterialOverrides != null)
                    {
                        _instanceMaterialOverrides = null;
                        _materialUniformsDirty = true;
                    }
                    break;
                }
                case CommandOpcode.SetUniformBuffer:
                {
                    string? name = (string?)objects[ReadU16(stream, ref pos)];
                    GraphicsBuffer? buffer = (GraphicsBuffer?)objects[ReadU16(stream, ref pos)];
                    _ = ReadU32(stream, ref pos);
                    if (name != null && buffer != null)
                        _uniformBuffers[name] = buffer;
                    break;
                }
                case CommandOpcode.SetUniformFloat:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    float value = ReadF32(stream, ref pos);
                    if (Rendering.UIUniformPacking.TrySetFloat(ref _uiFragmentUniforms, name, value))
                        _uiFragmentUniformsDirty = true;
                    else
                        WarnOnce(CommandOpcode.SetUniformFloat, $"D3D12CommandTranslator: uniform '{name}' is not packable yet.");
                    break;
                }
                case CommandOpcode.SetUniformInt:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    int value = ReadI32(stream, ref pos);
                    if (name == "_ObjectID")
                    {
                        _objectUniforms._ObjectID = value;
                        _objectUniformsDirty = true;
                    }
                    else if (Rendering.UIUniformPacking.TrySetInt(ref _uiFragmentUniforms, name, value))
                    {
                        _uiFragmentUniformsDirty = true;
                    }
                    else
                    {
                        WarnOnce(CommandOpcode.SetUniformInt, $"D3D12CommandTranslator: uniform '{name}' is not packable yet.");
                    }
                    break;
                }
                case CommandOpcode.SetUniformVec2:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Vector.Float2 value = ReadStruct<Vector.Float2>(stream, ref pos);
                    if (Rendering.UIUniformPacking.TrySetVector2(ref _uiFragmentUniforms, name, value))
                        _uiFragmentUniformsDirty = true;
                    else
                        WarnOnce(CommandOpcode.SetUniformVec2, $"D3D12CommandTranslator: uniform '{name}' is not packable yet.");
                    break;
                }
                case CommandOpcode.SetUniformVec4:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Vector.Float4 value = ReadStruct<Vector.Float4>(stream, ref pos);
                    if (Rendering.UIUniformPacking.TrySetVector4(ref _uiFragmentUniforms, name, value))
                        _uiFragmentUniformsDirty = true;
                    else
                        WarnOnce(CommandOpcode.SetUniformVec4, $"D3D12CommandTranslator: uniform '{name}' is not packable yet.");
                    break;
                }
                case CommandOpcode.SetUniformMatrix:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Vector.Float4x4 value = ReadStruct<Vector.Float4x4>(stream, ref pos);
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
                    {
                        _uiFragmentUniformsDirty = true;
                    }
                    else
                    {
                        WarnOnce(CommandOpcode.SetUniformMatrix, $"D3D12CommandTranslator: uniform '{name}' is not packable yet.");
                        break;
                    }
                    _objectUniformsDirty = true;
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
                case CommandOpcode.SetRasterState:
                {
                    _currentRaster = ReadStruct<RasterizerState>(stream, ref pos);
                    break;
                }
                case CommandOpcode.DrawIndexed:
                {
                    var vao = (GraphicsVertexArray?)objects[ReadU16(stream, ref pos)];
                    Topology topology = (Topology)ReadU8(stream, ref pos);
                    uint indexCount = ReadU32(stream, ref pos);
                    uint startIndex = ReadU32(stream, ref pos);
                    int baseVertex = ReadI32(stream, ref pos);
                    bool index32Bit = ReadU8(stream, ref pos) != 0;
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DrawIndexed(list, vao, topology, indexCount, startIndex, baseVertex, index32Bit);
                    break;
                }
                case CommandOpcode.DrawIndexedInstanced:
                {
                    var vao = (GraphicsVertexArray?)objects[ReadU16(stream, ref pos)];
                    Topology topology = (Topology)ReadU8(stream, ref pos);
                    uint indexCount = ReadU32(stream, ref pos);
                    uint instanceCount = ReadU32(stream, ref pos);
                    uint startIndex = ReadU32(stream, ref pos);
                    int baseVertex = ReadI32(stream, ref pos);
                    bool index32Bit = ReadU8(stream, ref pos) != 0;
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DrawIndexedInstanced(list, vao, topology, indexCount, instanceCount, startIndex, baseVertex, index32Bit);
                    break;
                }
                case CommandOpcode.DrawArrays:
                {
                    var vao = (GraphicsVertexArray?)objects[ReadU16(stream, ref pos)];
                    Topology topology = (Topology)ReadU8(stream, ref pos);
                    int first = ReadI32(stream, ref pos);
                    uint count = ReadU32(stream, ref pos);
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DrawArrays(list, vao, topology, first, count);
                    break;
                }
                case CommandOpcode.SetComputeShader:
                {
                    _currentComputeShader = objects[ReadU16(stream, ref pos)] as ShaderVariant;
                    if (_currentComputeShader?.Bytecode?.Format == ShaderBytecodeFormat.Dxil
                        && _currentComputeShader.Bytecode.HasCompute)
                    {
                        _device.GetOrCreateShaderLayout(_currentComputeShader);
                    }
                    break;
                }
                case CommandOpcode.DispatchCompute:
                {
                    uint groupCountX = ReadU32(stream, ref pos);
                    uint groupCountY = ReadU32(stream, ref pos);
                    uint groupCountZ = ReadU32(stream, ref pos);
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DispatchCompute(list, groupCountX, groupCountY, groupCountZ);
                    break;
                }
                case CommandOpcode.DispatchIndirect:
                {
                    var args = objects[ReadU16(stream, ref pos)] as GraphicsBuffer;
                    uint offset = ReadU32(stream, ref pos);
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DispatchIndirect(list, args, offset);
                    break;
                }
                case CommandOpcode.DrawIndexedIndirect:
                {
                    var vao = objects[ReadU16(stream, ref pos)] as GraphicsVertexArray;
                    Topology topology = (Topology)ReadU8(stream, ref pos);
                    var args = objects[ReadU16(stream, ref pos)] as GraphicsBuffer;
                    uint offset = ReadU32(stream, ref pos);
                    bool index32Bit = ReadU8(stream, ref pos) != 0;
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DrawIndexedIndirect(list, vao, topology, args, offset, index32Bit);
                    break;
                }
                case CommandOpcode.ResourceBarrier:
                {
                    GpuBarrierKind kind = (GpuBarrierKind)ReadU8(stream, ref pos);
                    object? resource = objects[ReadU16(stream, ref pos)];
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    RecordResourceBarrier(list, kind, resource);
                    break;
                }
                case CommandOpcode.CreateBuffer:
                {
                    var buf = (GraphicsBuffer)objects[ReadU16(stream, ref pos)]!;
                    bool dynamic = ReadU8(stream, ref pos) != 0;
                    ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                    CreateBuffer(list, buf, dynamic, data);
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
                    if (buf != null && buf.Handle != 0 &&
                        _device.Buffers.TryGetValue(buf.Handle, out D3D12BufferResource? res) &&
                        res.Resource != null)
                    {
                        if ((ulong)dstOffset + (ulong)blob.Length > res.Size)
                        {
                            if (dstOffset != 0)
                                throw new ArgumentOutOfRangeException(nameof(dstOffset), "D3D12 partial buffer update exceeds the destination size.");
                            ReplaceBufferStorage(list, buf, res, blob);
                        }
                        else
                        {
                            RecordBufferUpload(list, res, blob, dstOffset);
                        }
                    }
                    break;
                }
                case CommandOpcode.CreateTexture:
                {
                    var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                    uint handle = _device.AllocateHandle();
                    tex.Handle = handle;
                    _device.Textures[handle] = new D3D12TextureResource
                    {
                        EngineFormat = tex.ImageFormat,
                        Format = D3D12Formats.ToDxgiFormat(tex.ImageFormat),
                        Type = tex.Type,
                    };
                    break;
                }
                case CommandOpcode.DisposeTexture:
                {
                    var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                    if (tex.Handle != 0 && _device.Textures.Remove(tex.Handle, out D3D12TextureResource? res))
                    {
                        _device.ReleaseMipGenerationRtvs(res);
                        RetireResource(res.Resource);
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
                    _ = ReadI32(stream, ref pos); // border
                    ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                    AllocateTexture2D(tex, mip, w, h, data);
                    break;
                }
                case CommandOpcode.UpdateTexture:
                {
                    var tex = (GraphicsTexture?)objects[ReadU16(stream, ref pos)];
                    int x = ReadI32(stream, ref pos);
                    int y = ReadI32(stream, ref pos);
                    uint w = ReadU32(stream, ref pos);
                    uint h = ReadU32(stream, ref pos);
                    int mip = ReadI32(stream, ref pos);
                    ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                    if (tex != null)
                        RecordTexture2DUpdate(list, tex, mip, x, y, w, h, data);
                    break;
                }
                case CommandOpcode.AllocateTexture3D:
                {
                    var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                    int mip = ReadI32(stream, ref pos);
                    uint w = ReadU32(stream, ref pos);
                    uint h = ReadU32(stream, ref pos);
                    uint d = ReadU32(stream, ref pos);
                    ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                    AllocateTexture3D(tex, mip, w, h, d, data);
                    break;
                }
                case CommandOpcode.AllocateTextureCubeFace:
                {
                    var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                    int face = ReadI32(stream, ref pos);
                    int mip = ReadI32(stream, ref pos);
                    uint size = ReadU32(stream, ref pos);
                    ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                    AllocateTextureCubeFace(tex, face, mip, size, data);
                    break;
                }
                case CommandOpcode.GenerateMipmap:
                {
                    GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    if (texture != null)
                        GenerateMipmaps(list, texture);
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
                    DisposeVertexArray(vao);
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
                    DisposeFramebuffer(list, framebuffer);
                    break;
                }
                case CommandOpcode.BeginSample:
                {
                    _ = objects[ReadU16(stream, ref pos)];
                    break;
                }
                case CommandOpcode.EndSample:
                    break;

                // Remaining opcodes: consume payloads so the stream stays aligned.
                default:
                    SkipOpcode(op, stream, ref pos, objects, store);
                    break;
            }
        }

        RestorePendingRenderTarget(list);
    }

    private void DoClear(
        ID3D12GraphicsCommandList list,
        ClearFlags flags,
        float r, float g, float b, float a,
        float depth,
        int stencil)
    {
        if ((flags & ClearFlags.Color) != 0)
        {
            var color = new Color4(r, g, b, a);
            D3D12FramebufferResource? framebuffer = GetPendingFramebuffer();
            if (framebuffer == null)
            {
                list.ClearRenderTargetView(_device.CurrentRtv, color);
            }
            else
            {
                TransitionFramebuffer(list, framebuffer, ResourceStates.RenderTarget);
                for (int i = 0; i < framebuffer.Rtvs.Length; i++)
                    list.ClearRenderTargetView(framebuffer.Rtvs[i], color);
            }
        }

        D3D12FramebufferResource? depthFramebuffer = GetPendingFramebuffer();
        if (depthFramebuffer != null && depthFramebuffer.DepthFormat != Format.Unknown &&
            (flags & (ClearFlags.Depth | ClearFlags.Stencil)) != 0)
        {
            Vortice.Direct3D12.ClearFlags clearFlags = Vortice.Direct3D12.ClearFlags.None;
            if ((flags & ClearFlags.Depth) != 0)
                clearFlags |= Vortice.Direct3D12.ClearFlags.Depth;
            if ((flags & ClearFlags.Stencil) != 0)
                clearFlags |= Vortice.Direct3D12.ClearFlags.Stencil;
            list.ClearDepthStencilView(depthFramebuffer.Dsv, clearFlags, depth, checked((byte)stencil), 0, null);
        }
    }

    private void DoBlit(
        ID3D12GraphicsCommandList list,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        int dstX,
        int dstY,
        int dstWidth,
        int dstHeight,
        ClearFlags mask,
        BlitFilter filter)
    {
        if (mask == 0)
            return;

        D3D12FramebufferResource destination = GetFramebuffer(_pendingRenderTarget, "draw");
        if (_pendingReadTarget == null)
        {
            if (mask != ClearFlags.Color)
                throw new NotSupportedException("D3D12 default framebuffer blits support color-only masks.");
            DoDefaultColorBlit(list, destination, srcX, srcY, srcWidth, srcHeight, dstX, dstY, dstWidth, dstHeight, filter);
            return;
        }
        D3D12FramebufferResource source = GetFramebuffer(_pendingReadTarget, "read");
        if (mask == ClearFlags.Depth)
        {
            DoDepthBlit(list, source, destination, srcX, srcY, srcWidth, srcHeight, dstX, dstY, dstWidth, dstHeight, filter);
            return;
        }
        if (mask != ClearFlags.Color)
            throw new NotSupportedException("D3D12 framebuffer blits currently support color-only or depth-only masks.");

        if (source.ColorHandles.Length != 1 || destination.ColorHandles.Length != 1)
            throw new NotSupportedException("D3D12 framebuffer blits currently require one color attachment on each framebuffer.");
        if (source.SubresourceOnly || destination.SubresourceOnly || source.ColorSubresource != 0 || destination.ColorSubresource != 0)
            throw new NotSupportedException("D3D12 framebuffer blits currently support mip-0 Texture2D attachments only.");
        ValidateBlitRect(srcX, srcY, srcWidth, srcHeight, source.Width, source.Height, "source");
        ValidateBlitRect(dstX, dstY, dstWidth, dstHeight, destination.Width, destination.Height, "destination");
        if (source.ColorHandle == destination.ColorHandle)
            throw new InvalidOperationException("D3D12 framebuffer blit source and destination textures must differ.");

        if (!_device.Textures.TryGetValue(source.ColorHandle, out D3D12TextureResource? sourceTexture) ||
            sourceTexture.Resource == null || !sourceTexture.HasSrvDescriptor)
            throw new InvalidOperationException("D3D12 framebuffer blit source texture is not sample-ready.");
        if (!_device.Textures.TryGetValue(destination.ColorHandle, out D3D12TextureResource? destinationTexture) ||
            destinationTexture.Resource == null)
            throw new InvalidOperationException("D3D12 framebuffer blit destination texture is not available.");
        if (sourceTexture.Type != TextureType.Texture2D || destinationTexture.Type != TextureType.Texture2D)
            throw new NotSupportedException("D3D12 framebuffer blits currently support Texture2D resources only.");
        if (sourceTexture.State != ResourceStates.PixelShaderResource)
            throw new InvalidOperationException("D3D12 framebuffer blit source must be in pixel-shader resource state.");

        RestorePendingRenderTarget(list);
        if (destinationTexture.State != ResourceStates.PixelShaderResource)
            throw new InvalidOperationException("D3D12 framebuffer blit destination must be in pixel-shader resource state before transfer.");
        TransitionFramebuffer(list, destination, ResourceStates.RenderTarget);

        ID3D12PipelineState pipeline = _device.GetOrCreateFramebufferBlitPipeline(
            destination.ColorFormat,
            out ID3D12RootSignature rootSignature,
            out GpuDescriptorHandle pointSampler,
            out GpuDescriptorHandle linearSampler);
        list.SetDescriptorHeaps(_descriptorHeaps);
        list.SetPipelineState(pipeline);
        list.SetGraphicsRootSignature(rootSignature);
        list.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _lastPipelineState = pipeline;
        _lastRootSignature = rootSignature;
        _lastTopology = Vortice.Direct3D.PrimitiveTopology.TriangleList;
        list.RSSetViewport(new Viewport(
            dstX,
            dstY,
            checked((uint)(dstWidth - dstX)),
            checked((uint)(dstHeight - dstY)),
            0f,
            1f));
        list.RSSetScissorRect(new RectI(dstX, dstY, dstWidth, dstHeight));
        list.OMSetRenderTargets(destination.Rtv, null);
        InvalidateTrackedRenderTargets();
        list.SetGraphicsRootDescriptorTable(0, sourceTexture.SrvDescriptor.Gpu);
        list.SetGraphicsRootDescriptorTable(1, filter == BlitFilter.Linear ? linearSampler : pointSampler);
        list.SetGraphicsRoot32BitConstant(2, BitConverter.SingleToUInt32Bits(srcX / (float)source.Width), 0);
        list.SetGraphicsRoot32BitConstant(2, BitConverter.SingleToUInt32Bits(srcY / (float)source.Height), 1);
        list.SetGraphicsRoot32BitConstant(2, BitConverter.SingleToUInt32Bits(srcWidth / (float)source.Width), 2);
        list.SetGraphicsRoot32BitConstant(2, BitConverter.SingleToUInt32Bits(srcHeight / (float)source.Height), 3);
        list.DrawInstanced(3, 1, 0, 0);
        TransitionFramebuffer(list, destination, ResourceStates.PixelShaderResource);
    }

    private void DoDefaultColorBlit(ID3D12GraphicsCommandList list, D3D12FramebufferResource destination,
        int srcX, int srcY, int srcWidth, int srcHeight, int dstX, int dstY, int dstWidth, int dstHeight, BlitFilter filter)
    {
        if (destination.ColorHandles.Length != 1 || destination.SubresourceOnly || destination.ColorSubresource != 0)
            throw new NotSupportedException("D3D12 default framebuffer blits require one mip-0 Texture2D destination.");
        ID3D12Resource source = _device.CurrentRenderTarget;
        ResourceDescription description = source.Description;
        uint sourceWidth = checked((uint)description.Width);
        uint sourceHeight = description.Height;
        ValidateBlitRect(srcX, srcY, srcWidth, srcHeight, sourceWidth, sourceHeight, "source");
        ValidateBlitRect(dstX, dstY, dstWidth, dstHeight, destination.Width, destination.Height, "destination");
        D3D12DescriptorAllocation sourceSrv = _device.GetCurrentRenderTargetSrv();
        _device.TransitionCurrentRenderTarget(list, ResourceStates.PixelShaderResource);
        TransitionFramebuffer(list, destination, ResourceStates.RenderTarget);
        ID3D12PipelineState pipeline = _device.GetOrCreateFramebufferBlitPipeline(destination.ColorFormat, out ID3D12RootSignature rootSignature, out GpuDescriptorHandle pointSampler, out GpuDescriptorHandle linearSampler);
        list.SetDescriptorHeaps(_descriptorHeaps);
        list.SetPipelineState(pipeline);
        list.SetGraphicsRootSignature(rootSignature);
        list.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _lastPipelineState = pipeline;
        _lastRootSignature = rootSignature;
        _lastTopology = Vortice.Direct3D.PrimitiveTopology.TriangleList;
        list.RSSetViewport(new Viewport(dstX, dstY, checked((uint)(dstWidth - dstX)), checked((uint)(dstHeight - dstY)), 0f, 1f));
        list.RSSetScissorRect(new RectI(dstX, dstY, dstWidth, dstHeight));
        list.OMSetRenderTargets(destination.Rtv, null);
        InvalidateTrackedRenderTargets();
        list.SetGraphicsRootDescriptorTable(0, sourceSrv.Gpu);
        list.SetGraphicsRootDescriptorTable(1, filter == BlitFilter.Linear ? linearSampler : pointSampler);
        list.SetGraphicsRoot32BitConstant(2, BitConverter.SingleToUInt32Bits(srcX / (float)sourceWidth), 0);
        list.SetGraphicsRoot32BitConstant(2, BitConverter.SingleToUInt32Bits(srcY / (float)sourceHeight), 1);
        list.SetGraphicsRoot32BitConstant(2, BitConverter.SingleToUInt32Bits(srcWidth / (float)sourceWidth), 2);
        list.SetGraphicsRoot32BitConstant(2, BitConverter.SingleToUInt32Bits(srcHeight / (float)sourceHeight), 3);
        list.DrawInstanced(3, 1, 0, 0);
        TransitionFramebuffer(list, destination, ResourceStates.PixelShaderResource);
        _device.TransitionCurrentRenderTarget(list, ResourceStates.RenderTarget);
    }

    private void DoDepthBlit(
        ID3D12GraphicsCommandList list,
        D3D12FramebufferResource source,
        D3D12FramebufferResource destination,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        int dstX,
        int dstY,
        int dstWidth,
        int dstHeight,
        BlitFilter filter)
    {
        if (filter != BlitFilter.Nearest)
            throw new NotSupportedException("D3D12 depth framebuffer blits require nearest filtering.");
        if (source.DepthHandle == 0 || destination.DepthHandle == 0)
            throw new InvalidOperationException("D3D12 depth framebuffer blits require depth attachments on both framebuffers.");
        if (source.DepthFormat != destination.DepthFormat)
            throw new NotSupportedException("D3D12 depth framebuffer blits require matching depth formats.");
        if (source.Width != destination.Width || source.Height != destination.Height ||
            srcX != 0 || srcY != 0 || dstX != 0 || dstY != 0 ||
            srcWidth != checked((int)source.Width) || srcHeight != checked((int)source.Height) ||
            dstWidth != checked((int)destination.Width) || dstHeight != checked((int)destination.Height))
        {
            throw new NotSupportedException("D3D12 depth framebuffer blits currently require equal extents and full-frame rectangles.");
        }
        if (source.DepthHandle == destination.DepthHandle)
            throw new InvalidOperationException("D3D12 depth framebuffer blit source and destination textures must differ.");
        if (!_device.Textures.TryGetValue(source.DepthHandle, out D3D12TextureResource? sourceTexture) ||
            sourceTexture.Resource == null)
            throw new InvalidOperationException("D3D12 depth framebuffer blit source texture is not available.");
        if (!_device.Textures.TryGetValue(destination.DepthHandle, out D3D12TextureResource? destinationTexture) ||
            destinationTexture.Resource == null)
            throw new InvalidOperationException("D3D12 depth framebuffer blit destination texture is not available.");
        ResourceStates sourceState = sourceTexture.State;
        ResourceStates destinationState = destinationTexture.State;
        list.ResourceBarrierTransition(sourceTexture.Resource, sourceState, ResourceStates.CopySource);
        list.ResourceBarrierTransition(destinationTexture.Resource, destinationState, ResourceStates.CopyDest);
        list.CopyTextureRegion(
            new TextureCopyLocation(destinationTexture.Resource, 0),
            0,
            0,
            0,
            new TextureCopyLocation(sourceTexture.Resource, 0),
            null);
        list.ResourceBarrierTransition(sourceTexture.Resource, ResourceStates.CopySource, sourceState);
        list.ResourceBarrierTransition(destinationTexture.Resource, ResourceStates.CopyDest, destinationState);
    }

    private D3D12FramebufferResource GetFramebuffer(GraphicsFrameBuffer? framebuffer, string role)
    {
        if (framebuffer == null || framebuffer.Handle == 0 ||
            !_device.Framebuffers.TryGetValue(framebuffer.Handle, out D3D12FramebufferResource? resource))
            throw new NotSupportedException($"D3D12 framebuffer blits currently require a custom {role} framebuffer.");
        return resource;
    }

    private static void ValidateBlitRect(
        int x0,
        int y0,
        int x1,
        int y1,
        uint width,
        uint height,
        string role)
    {
        if (x0 < 0 || y0 < 0 || x1 < 0 || y1 < 0 ||
            x0 > width || x1 > width || y0 > height || y1 > height ||
            x0 >= x1 || y0 >= y1)
            throw new ArgumentOutOfRangeException(role, $"D3D12 {role} blit rectangle must be non-empty and within the framebuffer extent.");
    }

    private void CreateBuffer(ID3D12GraphicsCommandList list, GraphicsBuffer buf, bool dynamic, ReadOnlySpan<byte> data)
    {
        uint handle = _device.AllocateHandle();
        ulong size = data.Length > 0 ? (ulong)data.Length : Math.Max(1u, buf.SizeInBytes);
        bool unordered = buf.OriginalType is BufferType.StructuredBuffer or BufferType.IndirectBuffer;
        ResourceFlags flags = unordered ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None;
        ResourceStates initialState = unordered ? ResourceStates.Common : ResourceStates.GenericRead;
        ID3D12Resource resource = _device.CreateCommittedBuffer(size, HeapType.Default, initialState, flags);

        if (data.Length > 0)
            RecordBufferUpload(list, resource, data, 0, initialState, initialState);

        buf.Handle = handle;
        buf.SizeInBytes = (uint)size;
        _device.Buffers[handle] = new D3D12BufferResource
        {
            Resource = resource,
            Size = size,
            Type = buf.OriginalType,
            Dynamic = dynamic,
            HeapType = HeapType.Default,
            State = initialState,
        };
    }

    private void RecordBufferUpload(
        ID3D12GraphicsCommandList list,
        ID3D12Resource destination,
        ReadOnlySpan<byte> data,
        uint destinationOffset,
        ResourceStates before = ResourceStates.GenericRead,
        ResourceStates after = ResourceStates.GenericRead)
    {
        if (data.Length == 0)
            return;

        const ulong alignment = 16;
        ulong uploadSize = (ulong)data.Length;
        ulong alignedSize = (uploadSize + alignment - 1) / alignment * alignment;
        ID3D12Resource upload;
        ulong uploadOffset;
        if (alignedSize > TransientBufferUploadArenaSize)
        {
            upload = _device.CreateCommittedBuffer(uploadSize, HeapType.Upload, ResourceStates.GenericRead);
            _submissionTransientResources!.Add(upload);
            uploadOffset = 0;
        }
        else
        {
            if (_transientBufferUploadArena == null ||
                _transientBufferUploadArenaOffset + alignedSize > TransientBufferUploadArenaSize)
            {
                _transientBufferUploadArena = _device.CreateCommittedBuffer(
                    TransientBufferUploadArenaSize,
                    HeapType.Upload,
                    ResourceStates.GenericRead);
                _submissionTransientResources!.Add(_transientBufferUploadArena);
                _transientBufferUploadArenaOffset = 0;
            }
            upload = _transientBufferUploadArena;
            uploadOffset = _transientBufferUploadArenaOffset;
            _transientBufferUploadArenaOffset += alignedSize;
        }

        byte* mapped = upload.Map<byte>(0);
        try
        {
            fixed (byte* source = data)
                System.Buffer.MemoryCopy(source, mapped + uploadOffset, checked((long)uploadSize), data.Length);
        }
        finally
        {
            upload.Unmap(0);
        }

        list.ResourceBarrierTransition(destination, before, ResourceStates.CopyDest);
        list.CopyBufferRegion(destination, destinationOffset, upload, uploadOffset, uploadSize);
        list.ResourceBarrierTransition(destination, ResourceStates.CopyDest, after);
    }

    private void RecordBufferUpload(
        ID3D12GraphicsCommandList list,
        D3D12BufferResource destination,
        ReadOnlySpan<byte> data,
        uint destinationOffset)
    {
        if (destination.Resource == null)
            return;
        ResourceStates restore = destination.State;
        RecordBufferUpload(list, destination.Resource, data, destinationOffset, restore, restore);
    }

    private void ReplaceBufferStorage(
        ID3D12GraphicsCommandList list,
        GraphicsBuffer buffer,
        D3D12BufferResource previous,
        ReadOnlySpan<byte> data)
    {
        ulong maxBufferSize = uint.MaxValue;
        ulong doubledCapacity = previous.Size >= maxBufferSize / 2
            ? maxBufferSize
            : previous.Size * 2;
        ulong size = Math.Max((ulong)data.Length, doubledCapacity);
        bool unordered = buffer.OriginalType is BufferType.StructuredBuffer or BufferType.IndirectBuffer;
        ResourceFlags flags = unordered ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None;
        ResourceStates initialState = unordered ? ResourceStates.Common : ResourceStates.GenericRead;
        ID3D12Resource replacement = _device.CreateCommittedBuffer(
            size, HeapType.Default, initialState, flags);
        try
        {
            RecordBufferUpload(list, replacement, data, 0, initialState, initialState);
            ID3D12Resource oldResource = previous.Resource!;
            previous.Resource = replacement;
            previous.Size = size;
            previous.State = initialState;
            buffer.SizeInBytes = checked((uint)size);
            RetireResource(oldResource);
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
    }

    private void DisposeBuffer(GraphicsBuffer buf)
    {
        if (buf.Handle == 0)
            return;
        if (_device.Buffers.Remove(buf.Handle, out D3D12BufferResource? res))
            RetireResource(res.Resource);
        buf.Handle = 0;
    }

    private void RetireResource(ID3D12Resource? resource)
    {
        if (resource != null)
            _submissionTransientResources!.Add(resource);
    }

    private void CreateFramebuffer(GraphicsFrameBuffer framebuffer)
    {
        GraphicsFrameBuffer.Attachment[] attachments = framebuffer.Attachments;
        if (attachments.Length is < 1 or > 9)
            throw new NotSupportedException("D3D12 custom framebuffers support one through eight color attachments and one depth attachment.");
        int colorCount = 0;
        int depthCount = 0;
        for (int i = 0; i < attachments.Length; i++)
        {
            if (attachments[i].IsDepth)
                depthCount++;
            else
                colorCount++;
        }
        if (colorCount > 8 || depthCount > 1 || colorCount + depthCount == 0)
            throw new NotSupportedException("D3D12 custom framebuffers require one through eight color attachments, a depth attachment, or both.");

        var rtvs = new CpuDescriptorHandle[colorCount];
        var colorFormats = new Format[colorCount];
        var colorHandles = new uint[colorCount];
        var colorSubresources = new uint[colorCount];
        var subresourceOnly = new bool[colorCount];
        CpuDescriptorHandle dsv = default;
        Format depthFormat = Format.Unknown;
        uint depthHandle = 0;
        int colorIndex = 0;
        for (int i = 0; i < attachments.Length; i++)
        {
            GraphicsFrameBuffer.Attachment attachment = attachments[i];
            GraphicsTexture texture = attachment.Texture;
            if (texture.Handle == 0 ||
                !_device.Textures.TryGetValue(texture.Handle, out D3D12TextureResource? image) || image.Resource == null)
            {
                throw new InvalidOperationException("D3D12 framebuffer color attachment is not ready.");
            }
            if (attachment.MipLevel < 0 || (uint)attachment.MipLevel >= image.MipLevels)
                throw new ArgumentOutOfRangeException(nameof(attachment.MipLevel));
            bool isDepth = attachment.IsDepth;
            if (isDepth != D3D12Formats.IsDepth(image.EngineFormat))
                throw new ArgumentException("D3D12 framebuffer attachment depth flag must match the texture format.");
            if (isDepth && attachment.IsCubeFace)
                throw new NotSupportedException("D3D12 depth cubemap framebuffer attachments are not supported yet.");

            uint arraySlice = 0;
            if (!isDepth && attachment.IsCubeFace)
            {
                if (image.Type != TextureType.TextureCubeMap || (uint)attachment.CubeFace >= 6)
                    throw new ArgumentException("D3D12 cubemap framebuffer attachment requires a valid face 0 through 5.");
                arraySlice = checked((uint)attachment.CubeFace);
                subresourceOnly[i] = true;
            }
            else if (image.Type != TextureType.Texture2D)
            {
                throw new NotSupportedException("D3D12 non-cubemap framebuffer attachments must be Texture2D resources.");
            }

            uint expectedWidth = Math.Max(1u, image.Width >> attachment.MipLevel);
            uint expectedHeight = Math.Max(1u, image.Height >> attachment.MipLevel);
            if (framebuffer.Width != expectedWidth || framebuffer.Height != expectedHeight)
                throw new ArgumentException($"D3D12 framebuffer extent must match attachment mip extent {expectedWidth}x{expectedHeight}.");

            if (isDepth)
            {
                if (image.State != ResourceStates.DepthWrite)
                    throw new InvalidOperationException("D3D12 framebuffer depth attachment is not in depth-write state.");
                dsv = _device.AllocateDsvDescriptor();
                // A multisample DSV has no mip slice — the resource is single-level by construction.
                var dsvDescription = image.IsMultisampled
                    ? new DepthStencilViewDescription
                    {
                        Format = image.Format,
                        ViewDimension = DepthStencilViewDimension.Texture2DMultisampled,
                    }
                    : new DepthStencilViewDescription
                    {
                        Format = image.Format,
                        ViewDimension = DepthStencilViewDimension.Texture2D,
                        Texture2D = new Texture2DDepthStencilView
                        {
                            MipSlice = checked((uint)attachment.MipLevel),
                        },
                    };
                _device.Device.CreateDepthStencilView(image.Resource, dsvDescription, dsv);
                depthFormat = image.Format;
                depthHandle = texture.Handle;
                continue;
            }

            CpuDescriptorHandle rtv = _device.AllocateRtvDescriptor();
            uint colorSubresource = checked((uint)attachment.MipLevel + arraySlice * image.MipLevels);
            var rtvDescription = new RenderTargetViewDescription
            {
                Format = image.Format,
                ViewDimension = attachment.IsCubeFace
                    ? RenderTargetViewDimension.Texture2DArray
                    : image.IsMultisampled
                        ? RenderTargetViewDimension.Texture2DMultisampled
                        : RenderTargetViewDimension.Texture2D,
            };
            if (image.IsMultisampled && !attachment.IsCubeFace)
            {
                // Texture2DMS carries no mip/plane slice; the resource has exactly one subresource.
            }
            else if (attachment.IsCubeFace)
            {
                rtvDescription.Texture2DArray = new Texture2DArrayRenderTargetView
                {
                    MipSlice = checked((uint)attachment.MipLevel),
                    FirstArraySlice = arraySlice,
                    ArraySize = 1,
                    PlaneSlice = 0,
                };
            }
            else
            {
                rtvDescription.Texture2D = new Texture2DRenderTargetView
                {
                    MipSlice = checked((uint)attachment.MipLevel),
                    PlaneSlice = 0,
                };
            }
            _device.Device.CreateRenderTargetView(image.Resource, rtvDescription, rtv);
            rtvs[colorIndex] = rtv;
            colorFormats[colorIndex] = image.Format;
            colorHandles[colorIndex] = texture.Handle;
            colorSubresources[colorIndex] = colorSubresource;
            subresourceOnly[colorIndex] = attachment.IsCubeFace;
            colorIndex++;
        }

        uint handle = _device.AllocateHandle();
        framebuffer.Handle = handle;
        _device.Framebuffers[handle] = new D3D12FramebufferResource
        {
            SampleCount = framebuffer.SampleCount,
            Rtv = colorCount > 0 ? rtvs[0] : default,
            Rtvs = rtvs,
            Width = framebuffer.Width,
            Height = framebuffer.Height,
            ColorFormat = colorCount > 0 ? colorFormats[0] : Format.Unknown,
            ColorFormats = new D3D12ColorAttachmentFormats(colorFormats),
            ColorHandle = colorCount > 0 ? colorHandles[0] : 0,
            ColorHandles = colorHandles,
            ColorSubresource = colorCount > 0 ? colorSubresources[0] : 0,
            ColorSubresources = colorSubresources,
            SubresourceOnly = colorCount > 0 && subresourceOnly[0],
            SubresourceOnlyByAttachment = subresourceOnly,
            Dsv = dsv,
            DepthFormat = depthFormat,
            DepthHandle = depthHandle,
        };
    }

    private void DisposeFramebuffer(ID3D12GraphicsCommandList list, GraphicsFrameBuffer framebuffer)
    {
        if (framebuffer.Handle == 0)
            return;
        if (ReferenceEquals(_pendingRenderTarget, framebuffer))
        {
            RestorePendingRenderTarget(list);
            _pendingRenderTarget = null;
        }
        if (_device.Framebuffers.Remove(framebuffer.Handle, out D3D12FramebufferResource? resource))
        {
            for (int i = 0; i < resource.Rtvs.Length; i++)
                _device.ReleaseRtvDescriptor(resource.Rtvs[i]);
            if (resource.DepthFormat != Format.Unknown)
                _device.ReleaseDsvDescriptor(resource.Dsv);
        }
        framebuffer.Handle = 0;
    }

    private void AllocateTexture2D(GraphicsTexture tex, int mip, uint width, uint height, ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out D3D12TextureResource? res))
            return;

        // Only allocate storage at mip 0 for MVP; higher mips ignored.
        if (mip != 0)
            return;

        RetireResource(res.Resource);
        Format format = D3D12Formats.ToDxgiFormat(tex.ImageFormat);
        bool isDepth = D3D12Formats.IsDepth(tex.ImageFormat);
        uint sampleCount = tex.SampleCount < 1 ? 1 : tex.SampleCount;
        ResourceFlags flags = isDepth
            ? ResourceFlags.AllowDepthStencil
            : ResourceFlags.AllowRenderTarget;
        ResourceStates initial = isDepth
            ? ResourceStates.DepthWrite
            : ResourceStates.Common;

        res.Resource = _device.CreateCommittedTexture2D(width, height, format, flags, initial, 1, sampleCount);
        res.Width = width;
        res.Height = height;
        res.Format = format;
        res.State = initial;
        res.SampleCount = sampleCount;

        if (sampleCount > 1)
        {
            // A multisample resource is a render target only. Its SRV must be Texture2DMS (a
            // Texture2D view over an MSAA resource is invalid), and the sampler is meaningless
            // because MSAA targets are resolved rather than filtered. Data upload is rejected for
            // the same reason: CopyTextureRegion cannot write into a multisample resource.
            if (!res.HasSrvDescriptor)
            {
                res.SrvDescriptor = _device.AllocateSrvDescriptor();
                res.HasSrvDescriptor = true;
            }
            var msSrv = new ShaderResourceViewDescription
            {
                Format = isDepth ? D3D12Formats.ToDxgiShaderResourceFormat(tex.ImageFormat) : format,
                ViewDimension = ShaderResourceViewDimension.Texture2DMultisampled,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
            };
            _device.Device.CreateShaderResourceView(res.Resource, msSrv, res.SrvDescriptor.Cpu);
            WriteSamplerDescriptor(res);
            if (data.Length > 0)
                throw new NotSupportedException("D3D12 multisample textures cannot be uploaded to; resolve into a single-sample texture instead.");
            res.AvailableMipLevels = 1;
            return;
        }

        if (!isDepth)
        {
            if (!res.HasSrvDescriptor)
            {
                res.SrvDescriptor = _device.AllocateSrvDescriptor();
                res.HasSrvDescriptor = true;
            }

            var srv = new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    PlaneSlice = 0,
                    ResourceMinLODClamp = 0,
                },
            };
            _device.Device.CreateShaderResourceView(res.Resource, srv, res.SrvDescriptor.Cpu);

            WriteSamplerDescriptor(res);
        }
        else
        {
            // Depth-as-SRV: the shadow atlas and other depth attachments must be sampleable as the
            // _ShadowAtlas texture. SRVs cannot use the typed depth format, so create the view with
            // the matching typeless/readable format (R24_UNorm_X8_Typeless etc.) and a sampler so the
            // shadow path can read the stored depth through .r. The resource itself stays typed-depth
            // for DSV compatibility; only the view is typeless.
            if (!res.HasSrvDescriptor)
            {
                res.SrvDescriptor = _device.AllocateSrvDescriptor();
                res.HasSrvDescriptor = true;
            }
            Format srvFormat = D3D12Formats.ToDxgiShaderResourceFormat(tex.ImageFormat);
            var depthSrv = new ShaderResourceViewDescription
            {
                Format = srvFormat,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    PlaneSlice = 0,
                    ResourceMinLODClamp = 0,
                },
            };
            _device.Device.CreateShaderResourceView(res.Resource, depthSrv, res.SrvDescriptor.Cpu);
            WriteSamplerDescriptor(res);
        }

        if (data.Length > 0 && !D3D12Formats.IsDepth(tex.ImageFormat))
        {
            _device.UploadTexture2D(
                res.Resource,
                data,
                width,
                height,
                D3D12Formats.BytesPerPixel(tex.ImageFormat),
                res.State,
                out ResourceStates uploadedState);
            res.State = uploadedState;
        }
    }

    private void RecordTexture2DUpdate(
        ID3D12GraphicsCommandList list,
        GraphicsTexture texture,
        int mip,
        int x,
        int y,
        uint width,
        uint height,
        ReadOnlySpan<byte> data)
    {
        if (texture.Handle == 0 ||
            !_device.Textures.TryGetValue(texture.Handle, out D3D12TextureResource? resource) ||
            resource.Resource == null)
        {
            return;
        }
        if (texture.Type != TextureType.Texture2D)
            throw new NotSupportedException("D3D12 UpdateTexture requires a 2D texture.");
        if (D3D12Formats.IsDepth(resource.EngineFormat))
            throw new NotSupportedException("D3D12 depth texture updates are not supported.");
        if (mip < 0 || (uint)mip >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (x < 0 || y < 0)
            throw new ArgumentOutOfRangeException(x < 0 ? nameof(x) : nameof(y));
        if (width == 0 || height == 0)
            return;

        uint mipWidth = Math.Max(1u, resource.Width >> mip);
        uint mipHeight = Math.Max(1u, resource.Height >> mip);
        if ((ulong)(uint)x + width > mipWidth || (ulong)(uint)y + height > mipHeight)
            throw new ArgumentOutOfRangeException(nameof(width), "D3D12 texture update exceeds the destination mip extent.");

        int bytesPerPixel = D3D12Formats.BytesPerPixel(resource.EngineFormat);
        uint rowSize = checked(width * (uint)bytesPerPixel);
        int expectedSize = checked((int)(rowSize * height));
        if (data.Length != expectedSize)
            throw new ArgumentException($"D3D12 texture update expected {expectedSize} bytes, got {data.Length}.", nameof(data));

        uint rowPitch = (rowSize + 255u) & ~255u;
        ulong uploadSize = checked((ulong)rowPitch * height);
        ID3D12Resource upload = _device.CreateCommittedBuffer(uploadSize, HeapType.Upload, ResourceStates.GenericRead);
        _submissionTransientResources!.Add(upload);

        byte* mapped = upload.Map<byte>(0);
        try
        {
            fixed (byte* source = data)
            {
                for (uint row = 0; row < height; row++)
                {
                    byte* sourceRow = source + row * rowSize;
                    byte* destinationRow = mapped + row * rowPitch;
                    System.Buffer.MemoryCopy(sourceRow, destinationRow, rowPitch, rowSize);
                }
            }
        }
        finally
        {
            upload.Unmap(0);
        }

        var footprint = new PlacedSubresourceFootPrint
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(resource.Format, width, height, 1, rowPitch),
        };
        ResourceStates previousState = resource.State;
        list.ResourceBarrierTransition(resource.Resource, previousState, ResourceStates.CopyDest);
        list.CopyTextureRegion(
            new TextureCopyLocation(resource.Resource, checked((uint)mip)),
            checked((uint)x),
            checked((uint)y),
            0,
            new TextureCopyLocation(upload, footprint),
            null);
        list.ResourceBarrierTransition(resource.Resource, ResourceStates.CopyDest, previousState);
    }

    private void CreateVertexArray(GraphicsVertexArray vao)
    {
        uint handle = _device.AllocateHandle();
        vao.Handle = handle;
        _device.VertexArrays[handle] = new D3D12VertexArrayResource
        {
            VertexBuffer = vao.Vertices.Handle,
            IndexBuffer = vao.Indices?.Handle ?? 0,
            InstanceBuffer = vao.InstanceBuffer?.Handle ?? 0,
            Format = vao.Format,
            InstanceFormat = vao.InstanceFormat,
        };
    }

    private void AllocateTexture3D(
        GraphicsTexture tex,
        int mip,
        uint width,
        uint height,
        uint depth,
        ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out D3D12TextureResource? resource))
            return;
        if (mip != 0)
            return;
        if (tex.Type != TextureType.Texture3D && tex.Type != TextureType.Texture2DArray)
            throw new InvalidOperationException("D3D12 AllocateTexture3D requires a Texture3D or Texture2DArray resource.");
        if (D3D12Formats.IsDepth(tex.ImageFormat))
            throw new NotSupportedException("D3D12 depth volume or array textures are not supported.");

        bool isArray = tex.Type == TextureType.Texture2DArray;
        RetireResource(resource.Resource);
        Format format = D3D12Formats.ToDxgiFormat(tex.ImageFormat);
        resource.Resource = isArray
            ? _device.CreateCommittedTexture2D(width, height, format, ResourceFlags.None, ResourceStates.Common, 1, 1, depth)
            : _device.CreateCommittedTexture3D(width, height, depth, format, ResourceStates.Common);
        resource.Width = width;
        resource.Height = height;
        resource.Depth = depth;
        resource.Format = format;
        resource.State = ResourceStates.Common;

        if (!resource.HasSrvDescriptor)
        {
            resource.SrvDescriptor = _device.AllocateSrvDescriptor();
            resource.HasSrvDescriptor = true;
        }
        var srv = isArray
            ? new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2DArray = new Texture2DArrayShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    FirstArraySlice = 0,
                    ArraySize = depth,
                    PlaneSlice = 0,
                    ResourceMinLODClamp = 0,
                },
            }
            : new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture3D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture3D = new Texture3DShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    ResourceMinLODClamp = 0,
                },
            };
        _device.Device.CreateShaderResourceView(resource.Resource, srv, resource.SrvDescriptor.Cpu);
        WriteSamplerDescriptor(resource);

        if (data.Length > 0)
        {
            if (isArray)
            {
                _device.UploadTexture2DArray(
                    resource.Resource,
                    data,
                    width,
                    height,
                    depth,
                    D3D12Formats.BytesPerPixel(tex.ImageFormat),
                    resource.State,
                    out ResourceStates uploadedState);
                resource.State = uploadedState;
            }
            else
            {
                _device.UploadTexture3D(
                    resource.Resource,
                    data,
                    width,
                    height,
                    depth,
                    D3D12Formats.BytesPerPixel(tex.ImageFormat),
                    resource.State,
                    out ResourceStates uploadedState);
                resource.State = uploadedState;
            }
        }
    }

    private void AllocateTextureCubeFace(
        GraphicsTexture tex,
        int face,
        int mip,
        uint size,
        ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Textures.TryGetValue(tex.Handle, out D3D12TextureResource? resource))
            return;
        if ((uint)face >= 6)
            throw new ArgumentOutOfRangeException(nameof(face), face, "D3D12 cubemap face must be 0 through 5.");
        if (mip < 0)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (tex.Type != TextureType.TextureCubeMap)
            throw new InvalidOperationException("D3D12 AllocateTextureCubeFace requires a cubemap resource.");
        if (D3D12Formats.IsDepth(tex.ImageFormat))
            throw new NotSupportedException("D3D12 depth cubemaps are not supported.");

        if (resource.Resource == null && mip != 0)
            throw new InvalidOperationException("D3D12 cubemap mip 0 must be allocated before higher mip levels.");

        if (resource.Resource == null || (mip == 0 && (resource.Width != size || resource.Height != size)))
        {
            RetireResource(resource.Resource);
            Format format = D3D12Formats.ToDxgiFormat(tex.ImageFormat);
            uint mipLevels = CalculateMipLevels(size);
            resource.Resource = _device.CreateCommittedTextureCube(size, mipLevels, format, ResourceStates.Common);
            resource.Width = size;
            resource.Height = size;
            resource.Depth = 1;
            resource.MipLevels = mipLevels;
            resource.Format = format;
            resource.State = ResourceStates.Common;
            resource.CubeInitializedFaces = 0;
            resource.CubeInitializedFacesByMip = new byte[mipLevels];
            resource.AvailableMipLevels = 0;

            if (!resource.HasSrvDescriptor)
            {
                resource.SrvDescriptor = _device.AllocateSrvDescriptor();
                resource.HasSrvDescriptor = true;
            }
            var srv = new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.TextureCube,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                TextureCube = new TextureCubeShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = mipLevels,
                    ResourceMinLODClamp = 0,
                },
            };
            _device.Device.CreateShaderResourceView(resource.Resource, srv, resource.SrvDescriptor.Cpu);
            WriteSamplerDescriptor(resource);
            CreateMipGenerationViews(resource, 6);
        }

        if ((uint)mip >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip), mip, "D3D12 cubemap mip exceeds the allocated mip chain.");
        uint expectedSize = Math.Max(1u, resource.Width >> mip);
        if (size != expectedSize)
            throw new ArgumentException($"D3D12 cubemap mip {mip} expects size {expectedSize}, got {size}.", nameof(size));

        if (data.Length > 0)
        {
            uint destinationSubresource = checked((uint)mip + checked((uint)face) * resource.MipLevels);
            _device.UploadTexture2D(
                resource.Resource,
                data,
                size,
                size,
                D3D12Formats.BytesPerPixel(tex.ImageFormat),
                resource.State,
                out ResourceStates uploadedState,
                destinationSubresource);
            resource.State = uploadedState;
        }

        resource.CubeInitializedFacesByMip[mip] |= checked((byte)(1 << face));
        resource.CubeInitializedFaces = resource.CubeInitializedFacesByMip[0];
        uint availableMipLevels = 0;
        while (availableMipLevels < resource.MipLevels &&
            resource.CubeInitializedFacesByMip[availableMipLevels] == 0b0011_1111)
        {
            availableMipLevels++;
        }
        resource.AvailableMipLevels = availableMipLevels;
    }

    private void SetTextureWrap(GraphicsTexture texture, byte axis, TextureWrap wrap)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out D3D12TextureResource? resource))
            return;

        switch (axis)
        {
            case 0: resource.WrapS = wrap; break;
            case 1: resource.WrapT = wrap; break;
            case 2: resource.WrapR = wrap; break;
            default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "D3D12 texture wrap axis must be 0, 1, or 2.");
        }

        ReplaceSamplerDescriptorIfAllocated(resource);
    }

    private void CreateMipGenerationViews(D3D12TextureResource resource, uint arraySize)
    {
        _device.ReleaseMipGenerationRtvs(resource);
        int generatedMipCount = checked((int)resource.MipLevels - 1);
        resource.MipGenerationSrvs = new D3D12DescriptorAllocation[generatedMipCount];
        resource.MipGenerationRtvs = new CpuDescriptorHandle[checked(generatedMipCount * (int)arraySize)];
        for (int generatedMip = 0; generatedMip < generatedMipCount; generatedMip++)
        {
            uint sourceMip = checked((uint)generatedMip);
            D3D12DescriptorAllocation srvDescriptor = _device.AllocateSrvDescriptor();
            resource.MipGenerationSrvs[generatedMip] = srvDescriptor;
            var srv = new ShaderResourceViewDescription
            {
                Format = resource.Format,
                ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2DArray = new Texture2DArrayShaderResourceView
                {
                    MostDetailedMip = sourceMip,
                    MipLevels = 1,
                    FirstArraySlice = 0,
                    ArraySize = arraySize,
                    PlaneSlice = 0,
                    ResourceMinLODClamp = 0,
                },
            };
            _device.Device.CreateShaderResourceView(resource.Resource, srv, srvDescriptor.Cpu);

            uint targetMip = sourceMip + 1;
            for (uint face = 0; face < arraySize; face++)
            {
                CpuDescriptorHandle rtv = _device.AllocateRtvDescriptor();
                resource.MipGenerationRtvs[generatedMip * arraySize + face] = rtv;
                var rtvDescription = new RenderTargetViewDescription
                {
                    Format = resource.Format,
                    ViewDimension = RenderTargetViewDimension.Texture2DArray,
                    Texture2DArray = new Texture2DArrayRenderTargetView
                    {
                        MipSlice = targetMip,
                        FirstArraySlice = face,
                        ArraySize = 1,
                        PlaneSlice = 0,
                    },
                };
                _device.Device.CreateRenderTargetView(resource.Resource, rtvDescription, rtv);
            }
        }
    }

    private void GenerateMipmaps(ID3D12GraphicsCommandList list, GraphicsTexture texture)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out D3D12TextureResource? resource) || resource.Resource == null)
            return;
        if (resource.Type is not TextureType.Texture2D and not TextureType.TextureCubeMap)
            throw new NotSupportedException("D3D12 mip generation supports 2D textures and cubemaps.");
        bool isCube = resource.Type == TextureType.TextureCubeMap;
        if (isCube && resource.CubeInitializedFaces != 0b0011_1111)
            throw new InvalidOperationException("D3D12 cubemap mip generation requires all base-level faces.");
        if (!isCube && resource.MipLevels == 1)
            RebuildTexture2DMipChain(list, resource);
        if (resource.MipLevels <= 1)
            return;
        if (resource.State != ResourceStates.PixelShaderResource)
            throw new InvalidOperationException("D3D12 mip generation requires pixel-shader resource state.");

        ID3D12PipelineState pipeline = _device.GetOrCreateCubemapMipPipeline(resource.Format, out ID3D12RootSignature rootSignature);
        list.SetDescriptorHeaps(_descriptorHeaps);
        list.SetPipelineState(pipeline);
        list.SetGraphicsRootSignature(rootSignature);
        list.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _lastPipelineState = pipeline;
        _lastRootSignature = rootSignature;
        _lastTopology = Vortice.Direct3D.PrimitiveTopology.TriangleList;

        for (uint mip = 1; mip < resource.MipLevels; mip++)
        {
            uint sourceWidth = Math.Max(1u, resource.Width >> checked((int)(mip - 1)));
            uint sourceHeight = Math.Max(1u, resource.Height >> checked((int)(mip - 1)));
            uint destinationWidth = Math.Max(1u, resource.Width >> checked((int)mip));
            uint destinationHeight = Math.Max(1u, resource.Height >> checked((int)mip));
            list.RSSetViewport(new Viewport(0, 0, destinationWidth, destinationHeight, 0f, 1f));
            list.RSSetScissorRect(new RectI(0, 0, checked((int)destinationWidth), checked((int)destinationHeight)));
            list.SetGraphicsRootDescriptorTable(0, resource.MipGenerationSrvs[mip - 1].Gpu);
            list.SetGraphicsRoot32BitConstant(1, sourceWidth, 1);
            list.SetGraphicsRoot32BitConstant(1, sourceHeight, 2);

            uint arraySize = isCube ? 6u : 1u;
            for (uint face = 0; face < arraySize; face++)
            {
                uint targetSubresource = mip + face * resource.MipLevels;
                list.ResourceBarrierTransition(
                    resource.Resource,
                    ResourceStates.PixelShaderResource,
                    ResourceStates.RenderTarget,
                    targetSubresource);
                CpuDescriptorHandle rtv = resource.MipGenerationRtvs[(mip - 1) * arraySize + face];
                list.OMSetRenderTargets(rtv, null);
                InvalidateTrackedRenderTargets();
                list.SetGraphicsRoot32BitConstant(1, face, 0);
                list.DrawInstanced(3, 1, 0, 0);
                list.ResourceBarrierTransition(
                    resource.Resource,
                    ResourceStates.RenderTarget,
                    ResourceStates.PixelShaderResource,
                    targetSubresource);
            }
            if (isCube)
                resource.CubeInitializedFacesByMip[mip] = 0b0011_1111;
        }

        resource.AvailableMipLevels = resource.MipLevels;
    }

    private void RebuildTexture2DMipChain(ID3D12GraphicsCommandList list, D3D12TextureResource resource)
    {
        uint mipLevels = CalculateMipLevels(Math.Max(resource.Width, resource.Height));
        if (mipLevels <= 1)
            return;

        ID3D12Resource previous = resource.Resource!;
        ID3D12Resource replacement = _device.CreateCommittedTexture2D(
            resource.Width,
            resource.Height,
            resource.Format,
            ResourceFlags.AllowRenderTarget,
            ResourceStates.Common,
            mipLevels);

        list.ResourceBarrierTransition(previous, resource.State, ResourceStates.CopySource);
        list.ResourceBarrierTransition(replacement, ResourceStates.Common, ResourceStates.CopyDest);
        list.CopyTextureRegion(
            new TextureCopyLocation(replacement, 0),
            0,
            0,
            0,
            new TextureCopyLocation(previous, 0),
            null);
        list.ResourceBarrierTransition(replacement, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        _submissionTransientResources!.Add(previous);

        resource.Resource = replacement;
        resource.MipLevels = mipLevels;
        resource.AvailableMipLevels = 1;
        resource.State = ResourceStates.PixelShaderResource;
        var srv = new ShaderResourceViewDescription
        {
            Format = resource.Format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = mipLevels,
                PlaneSlice = 0,
                ResourceMinLODClamp = 0,
            },
        };
        _device.Device.CreateShaderResourceView(replacement, srv, resource.SrvDescriptor.Cpu);
        CreateMipGenerationViews(resource, 1);
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

    private void SetTextureFilters(GraphicsTexture texture, TextureMin min, TextureMag mag)
    {
        if (texture.Handle == 0 || !_device.Textures.TryGetValue(texture.Handle, out D3D12TextureResource? resource))
            return;

        resource.MinFilter = min;
        resource.MagFilter = mag;
        ReplaceSamplerDescriptorIfAllocated(resource);
    }

    private void ReplaceSamplerDescriptorIfAllocated(D3D12TextureResource resource)
    {
        if (!resource.HasSamplerDescriptor)
            return;

        WriteSamplerDescriptor(resource);
    }

    private void WriteSamplerDescriptor(D3D12TextureResource resource)
    {
        resource.SamplerDescriptor = _device.GetOrCreateSamplerDescriptor(
            resource.MinFilter,
            resource.MagFilter,
            resource.WrapS,
            resource.WrapT,
            resource.WrapR);
        resource.HasSamplerDescriptor = true;
    }

    private void DisposeVertexArray(GraphicsVertexArray vao)
    {
        if (vao.Handle == 0)
            return;

        _device.VertexArrays.Remove(vao.Handle);
        vao.Handle = 0;
    }

    private void DrawArrays(
        ID3D12GraphicsCommandList list,
        GraphicsVertexArray? vao,
        Topology topology,
        int first,
        uint count)
    {
        if (_currentShader?.Bytecode?.Format != ShaderBytecodeFormat.Dxil || vao == null || vao.Handle == 0)
        {
            WarnDrawNoPso();
            return;
        }
        if (!_device.VertexArrays.TryGetValue(vao.Handle, out D3D12VertexArrayResource? vertexArray) ||
            !_device.Buffers.TryGetValue(vertexArray.VertexBuffer, out D3D12BufferResource? vertexBuffer) ||
            vertexBuffer.Resource == null)
        {
            WarnOnce(CommandOpcode.DrawArrays, "D3D12 DrawArrays skipped: vertex-array resources are incomplete.");
            return;
        }
        if (vertexArray.InstanceFormat != null)
            throw new NotSupportedException("D3D12 DrawArrays does not consume an instance stream; use an instanced draw opcode.");

        var key = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit: false, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
        ID3D12PipelineState pipeline = _device.GetOrCreateGraphicsPipeline(key, _currentShader, GetCurrentTargetFormats());
        D3D12ShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentShader);
        var vertexView = new VertexBufferView(
            vertexBuffer.Resource.GPUVirtualAddress,
            checked((uint)vertexBuffer.Size),
            checked((uint)vertexArray.Format.Size));

        BindCurrentRenderTargets(list);
        ApplyCurrentScissor(list);
        SetPipelineTracked(list, pipeline);
        SetGraphicsRootSignatureTracked(list, layout.RootSignature);
        BindShaderResources(list, layout);
        SetTopologyTracked(list, D3D12Formats.ToTopology(topology));
        list.IASetVertexBuffers(0, 1, &vertexView);
        list.DrawInstanced(count, 1, checked((uint)first), 0);
    }

    private void DrawIndexed(
        ID3D12GraphicsCommandList list,
        GraphicsVertexArray? vao,
        Topology topology,
        uint indexCount,
        uint startIndex,
        int baseVertex,
        bool index32Bit)
    {
        if (_currentShader?.Bytecode?.Format != ShaderBytecodeFormat.Dxil || vao == null || vao.Handle == 0)
        {
            WarnDrawNoPso();
            return;
        }
        if (!_device.VertexArrays.TryGetValue(vao.Handle, out D3D12VertexArrayResource? vertexArray) ||
            !_device.Buffers.TryGetValue(vertexArray.VertexBuffer, out D3D12BufferResource? vertexBuffer) ||
            !_device.Buffers.TryGetValue(vertexArray.IndexBuffer, out D3D12BufferResource? indexBuffer) ||
            vertexBuffer.Resource == null || indexBuffer.Resource == null)
        {
            WarnOnce(CommandOpcode.DrawIndexed, "D3D12 DrawIndexed skipped: vertex-array resources are incomplete.");
            return;
        }
        if (vertexArray.InstanceFormat != null)
            throw new NotSupportedException("D3D12 DrawIndexed does not consume an instance stream; use DrawIndexedInstanced.");

        var key = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
        ID3D12PipelineState pipeline = _device.GetOrCreateGraphicsPipeline(key, _currentShader, GetCurrentTargetFormats());
        D3D12ShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentShader);
        var vertexView = new VertexBufferView(
            vertexBuffer.Resource.GPUVirtualAddress,
            checked((uint)vertexBuffer.Size),
            checked((uint)vertexArray.Format.Size));
        var indexView = new IndexBufferView(
            indexBuffer.Resource.GPUVirtualAddress,
            checked((uint)indexBuffer.Size),
            index32Bit);

        BindCurrentRenderTargets(list);
        ApplyCurrentScissor(list);
        SetPipelineTracked(list, pipeline);
        SetGraphicsRootSignatureTracked(list, layout.RootSignature);
        BindShaderResources(list, layout);
        SetTopologyTracked(list, D3D12Formats.ToTopology(topology));
        list.IASetVertexBuffers(0, 1, &vertexView);
        list.IASetIndexBuffer(&indexView);
        list.DrawIndexedInstanced(indexCount, 1, startIndex, baseVertex, 0);
    }

    private void DrawIndexedInstanced(
        ID3D12GraphicsCommandList list,
        GraphicsVertexArray? vao,
        Topology topology,
        uint indexCount,
        uint instanceCount,
        uint startIndex,
        int baseVertex,
        bool index32Bit)
    {
        if (_currentShader?.Bytecode?.Format != ShaderBytecodeFormat.Dxil || vao == null || vao.Handle == 0)
        {
            WarnDrawNoPso();
            return;
        }
        if (!_device.VertexArrays.TryGetValue(vao.Handle, out D3D12VertexArrayResource? vertexArray) ||
            vertexArray.InstanceFormat == null ||
            !_device.Buffers.TryGetValue(vertexArray.VertexBuffer, out D3D12BufferResource? vertexBuffer) ||
            !_device.Buffers.TryGetValue(vertexArray.InstanceBuffer, out D3D12BufferResource? instanceBuffer) ||
            !_device.Buffers.TryGetValue(vertexArray.IndexBuffer, out D3D12BufferResource? indexBuffer) ||
            vertexBuffer.Resource == null || instanceBuffer.Resource == null || indexBuffer.Resource == null)
        {
            WarnOnce(CommandOpcode.DrawIndexedInstanced, "D3D12 DrawIndexedInstanced skipped: vertex-array resources are incomplete.");
            return;
        }

        var key = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
        ID3D12PipelineState pipeline = _device.GetOrCreateGraphicsPipeline(key, _currentShader, GetCurrentTargetFormats());
        D3D12ShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentShader);
        VertexBufferView* vertexViews = stackalloc VertexBufferView[2]
        {
            new(vertexBuffer.Resource.GPUVirtualAddress, checked((uint)vertexBuffer.Size), checked((uint)vertexArray.Format.Size)),
            new(instanceBuffer.Resource.GPUVirtualAddress, checked((uint)instanceBuffer.Size), checked((uint)vertexArray.InstanceFormat.Size)),
        };
        var indexView = new IndexBufferView(indexBuffer.Resource.GPUVirtualAddress, checked((uint)indexBuffer.Size), index32Bit);

        BindCurrentRenderTargets(list);
        ApplyCurrentScissor(list);
        SetPipelineTracked(list, pipeline);
        SetGraphicsRootSignatureTracked(list, layout.RootSignature);
        BindShaderResources(list, layout);
        SetTopologyTracked(list, D3D12Formats.ToTopology(topology));
        list.IASetVertexBuffers(0, 2, vertexViews);
        list.IASetIndexBuffer(&indexView);
        list.DrawIndexedInstanced(indexCount, instanceCount, startIndex, baseVertex, 0);
    }

    private void DispatchCompute(
        ID3D12GraphicsCommandList list,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ)
    {
        if (!TryBindComputePipeline(list))
            return;
        list.Dispatch(groupCountX, groupCountY, groupCountZ);
    }

    private void DispatchIndirect(ID3D12GraphicsCommandList list, GraphicsBuffer? args, uint offset)
    {
        if (!TryBindComputePipeline(list))
            return;
        if (!TryGetBufferResource(args, out D3D12BufferResource? argsResource) || argsResource.Resource == null)
        {
            WarnOnce(CommandOpcode.DispatchIndirect, "D3D12 DispatchIndirect skipped: args buffer is missing.");
            return;
        }

        TransitionBuffer(list, argsResource, ResourceStates.IndirectArgument);
        list.ExecuteIndirect(_device.GetDispatchCommandSignature(), 1, argsResource.Resource, offset, null!, 0);
    }

    private void DrawIndexedIndirect(
        ID3D12GraphicsCommandList list,
        GraphicsVertexArray? vao,
        Topology topology,
        GraphicsBuffer? args,
        uint offset,
        bool index32Bit)
    {
        if (_currentShader?.Bytecode?.Format != ShaderBytecodeFormat.Dxil || vao == null || vao.Handle == 0)
        {
            WarnDrawNoPso();
            return;
        }
        if (!_device.VertexArrays.TryGetValue(vao.Handle, out D3D12VertexArrayResource? vertexArray) ||
            !_device.Buffers.TryGetValue(vertexArray.VertexBuffer, out D3D12BufferResource? vertexBuffer) ||
            !_device.Buffers.TryGetValue(vertexArray.IndexBuffer, out D3D12BufferResource? indexBuffer) ||
            vertexBuffer.Resource == null || indexBuffer.Resource == null)
        {
            WarnOnce(CommandOpcode.DrawIndexedIndirect, "D3D12 DrawIndexedIndirect skipped: vertex-array resources are incomplete.");
            return;
        }
        if (!TryGetBufferResource(args, out D3D12BufferResource? argsResource) || argsResource.Resource == null)
        {
            WarnOnce(CommandOpcode.DrawIndexedIndirect, "D3D12 DrawIndexedIndirect skipped: args buffer is missing.");
            return;
        }
        if (vertexArray.InstanceFormat != null)
            throw new NotSupportedException("D3D12 DrawIndexedIndirect does not consume an instance stream.");

        var key = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
        ID3D12PipelineState pipeline = _device.GetOrCreateGraphicsPipeline(key, _currentShader, GetCurrentTargetFormats());
        D3D12ShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentShader);
        var vertexView = new VertexBufferView(
            vertexBuffer.Resource.GPUVirtualAddress,
            checked((uint)vertexBuffer.Size),
            checked((uint)vertexArray.Format.Size));
        var indexView = new IndexBufferView(
            indexBuffer.Resource.GPUVirtualAddress,
            checked((uint)indexBuffer.Size),
            index32Bit);

        TransitionBuffer(list, argsResource, ResourceStates.IndirectArgument);
        BindCurrentRenderTargets(list);
        ApplyCurrentScissor(list);
        SetPipelineTracked(list, pipeline);
        SetGraphicsRootSignatureTracked(list, layout.RootSignature);
        BindShaderResources(list, layout, compute: false);
        SetTopologyTracked(list, D3D12Formats.ToTopology(topology));
        list.IASetVertexBuffers(0, 1, &vertexView);
        list.IASetIndexBuffer(&indexView);
        list.ExecuteIndirect(_device.GetDrawIndexedCommandSignature(), 1, argsResource.Resource, offset, null!, 0);
    }

    private bool TryBindComputePipeline(ID3D12GraphicsCommandList list)
    {
        if (_currentComputeShader?.Bytecode is not { Format: ShaderBytecodeFormat.Dxil, HasCompute: true })
        {
            WarnOnce(CommandOpcode.DispatchCompute, "D3D12 dispatch skipped: no compute shader is bound.");
            return false;
        }

        ID3D12PipelineState pipeline = _device.GetOrCreateComputePipeline(_currentComputeShader);
        D3D12ShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentComputeShader);
        list.SetPipelineState(pipeline);
        _lastPipelineState = pipeline;
        list.SetComputeRootSignature(layout.RootSignature);
        BindShaderResources(list, layout, compute: true);
        return true;
    }

    private void RecordResourceBarrier(ID3D12GraphicsCommandList list, GpuBarrierKind kind, object? resource)
    {
        if (kind == GpuBarrierKind.None || !TryGetBufferResource(resource as GraphicsBuffer, out D3D12BufferResource? buffer) ||
            buffer.Resource == null)
        {
            return;
        }

        switch (kind)
        {
            case GpuBarrierKind.ComputeToCompute:
                TransitionBuffer(list, buffer, ResourceStates.UnorderedAccess);
                list.ResourceBarrierUnorderedAccessView(buffer.Resource);
                break;
            case GpuBarrierKind.ComputeToIndirect:
                TransitionBuffer(list, buffer, ResourceStates.IndirectArgument);
                break;
            case GpuBarrierKind.ComputeToVertex:
                TransitionBuffer(list, buffer, ResourceStates.NonPixelShaderResource);
                break;
            case GpuBarrierKind.ComputeToPixel:
                TransitionBuffer(list, buffer, ResourceStates.PixelShaderResource);
                break;
        }
    }

    private bool TryGetBufferResource(GraphicsBuffer? buffer, out D3D12BufferResource resource)
    {
        resource = null!;
        if (buffer == null || buffer.Handle == 0)
            return false;
        return _device.Buffers.TryGetValue(buffer.Handle, out resource!) && resource.Resource != null;
    }

    private static void TransitionBuffer(
        ID3D12GraphicsCommandList list,
        D3D12BufferResource resource,
        ResourceStates after)
    {
        if (resource.Resource == null || resource.State == after)
            return;
        list.ResourceBarrierTransition(resource.Resource, resource.State, after);
        resource.State = after;
    }

    private void BindShaderResources(
        ID3D12GraphicsCommandList list,
        D3D12ShaderLayoutResource layout,
        bool compute = false)
    {
        ShaderBindingLayout bindingLayout = layout.BindingLayout;
        ShaderBindingSlot[] buffers = bindingLayout.Buffers;
        EnsureObjectUniformsBuffer(buffers);
        EnsureStandardLightingBuffer(buffers);
        EnsureStandardShadowsBuffer(buffers);
        EnsureStandardReflectionProbesBuffer(buffers);
        EnsureStandardLightCookiesBuffer(buffers);
        EnsureMaterialUniformsBuffer(buffers);
        EnsureUIVertexUniformsBuffer(buffers);
        EnsureUIFragmentUniformsBuffer(buffers);
        for (int i = 0; i < buffers.Length; i++)
            BindBufferRootParameter(list, (uint)i, buffers[i], compute);

        int textureCount = bindingLayout.Textures.Length;
        int samplerCount = bindingLayout.Samplers.Length;
        if (textureCount == 0 && samplerCount == 0)
            return;

        list.SetDescriptorHeaps(2, _descriptorHeaps);

        for (int i = 0; i < textureCount; i++)
        {
            ShaderBindingSlot binding = bindingLayout.Textures[i];
            D3D12TextureResource resource = GetTextureResource(binding.Name);
            uint rootIndex = checked((uint)(buffers.Length + i));
            if (compute)
                list.SetComputeRootDescriptorTable(rootIndex, resource.SrvDescriptor.Gpu);
            else
                list.SetGraphicsRootDescriptorTable(rootIndex, resource.SrvDescriptor.Gpu);
        }

        for (int i = 0; i < samplerCount; i++)
        {
            ShaderBindingSlot binding = bindingLayout.Samplers[i];
            string textureName = FindTextureNameForSampler(bindingLayout.Textures, binding.Slot);
            D3D12TextureResource resource = GetTextureResource(textureName);
            uint rootIndex = checked((uint)(buffers.Length + textureCount + i));
            if (compute)
                list.SetComputeRootDescriptorTable(rootIndex, resource.SamplerDescriptor.Gpu);
            else
                list.SetGraphicsRootDescriptorTable(rootIndex, resource.SamplerDescriptor.Gpu);
        }
    }

    private void BindBufferRootParameter(
        ID3D12GraphicsCommandList list,
        uint rootIndex,
        ShaderBindingSlot binding,
        bool compute)
    {
        if (binding.Kind == ShaderBindingKind.Buffer)
        {
            ulong address;
            if (_transientUniformBuffers.TryGetValue(binding.Name, out D3D12TransientUniformBinding transientBuffer))
            {
                address = transientBuffer.Resource.GPUVirtualAddress + transientBuffer.Offset;
            }
            else
            {
                if (!_uniformBuffers.TryGetValue(binding.Name, out GraphicsBuffer? buffer) || buffer.Handle == 0)
                {
                    // Degrade instead of terminating the editor over one bad material: bind a
                    // zeroed transient (degenerate constants → invisible output), log once.
                    LogDrawDegradationOnce($"constant buffer '{binding.Name}'");
                    D3D12TransientUniformBinding zeros = AllocateZeroedTransientUniform(binding.ByteSize);
                    _transientUniformBuffers[binding.Name] = zeros;
                    address = zeros.Resource.GPUVirtualAddress + zeros.Offset;
                }
                else if (!_device.Buffers.TryGetValue(buffer.Handle, out D3D12BufferResource? resource) || resource.Resource == null)
                {
                    LogDrawDegradationOnce($"constant buffer '{binding.Name}' (device resource missing)");
                    D3D12TransientUniformBinding zeros = AllocateZeroedTransientUniform(binding.ByteSize);
                    _transientUniformBuffers[binding.Name] = zeros;
                    address = zeros.Resource.GPUVirtualAddress + zeros.Offset;
                }
                else
                {
                    if (resource.Type != BufferType.UniformBuffer)
                        throw new InvalidOperationException($"D3D12 resource '{binding.Name}' is not a uniform buffer.");
                    address = resource.Resource.GPUVirtualAddress;
                }
            }

            if (compute)
                list.SetComputeRootConstantBufferView(rootIndex, address);
            else
                list.SetGraphicsRootConstantBufferView(rootIndex, address);
            return;
        }

        if (!_uniformBuffers.TryGetValue(binding.Name, out GraphicsBuffer? storage) || storage.Handle == 0)
            throw new InvalidOperationException($"D3D12 requires storage buffer '{binding.Name}'.");
        if (!_device.Buffers.TryGetValue(storage.Handle, out D3D12BufferResource? storageResource) ||
            storageResource.Resource == null)
        {
            throw new InvalidOperationException($"D3D12 storage buffer '{binding.Name}' is not available.");
        }
        if (storageResource.Type is not (BufferType.StructuredBuffer or BufferType.IndirectBuffer))
            throw new InvalidOperationException($"D3D12 resource '{binding.Name}' is not a structured or indirect buffer.");

        ulong gpuAddress = storageResource.Resource.GPUVirtualAddress;
        if (binding.Kind == ShaderBindingKind.StorageBuffer)
        {
            TransitionBuffer(list, storageResource, ResourceStates.UnorderedAccess);
            if (compute)
                list.SetComputeRootUnorderedAccessView(rootIndex, gpuAddress);
            else
                list.SetGraphicsRootUnorderedAccessView(rootIndex, gpuAddress);
            return;
        }

        if (binding.Kind == ShaderBindingKind.StructuredBuffer)
        {
            TransitionBuffer(
                list,
                storageResource,
                ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource);
            if (compute)
                list.SetComputeRootShaderResourceView(rootIndex, gpuAddress);
            else
                list.SetGraphicsRootShaderResourceView(rootIndex, gpuAddress);
            return;
        }

        throw new NotSupportedException($"D3D12 root parameters do not support buffer kind {binding.Kind}.");
    }

    private void EnsureObjectUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        bool required = false;
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name == "ObjectUniforms")
            {
                required = true;
                break;
            }
        }
        if (!required || !_objectUniformsDirty)
            return;

        ReadOnlySpan<byte> sourceBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref _objectUniforms, 1));
        _transientUniformBuffers["ObjectUniforms"] = AllocateTransientUniform(sourceBytes);
        _objectUniformsDirty = false;
    }

    private void ApplyObjectProperties(Rendering.PropertyState? properties)
    {
        _objectUniforms = Rendering.ObjectUniformsData.Identity;
        // Instance-bound textures (a text's font atlas via _MainTex, a renderable's _Lightmap)
        // are owned per draw: restore the previous draw's names to their material/global binding
        // before adopting this draw's set. Mirrors the Vulkan translator's mechanism.
        ClearObjectTextureBindings();
        if (properties != null)
        {
            if (properties._matrices.TryGetValue("xengine_ObjectToWorld", out Vector.Float4x4 objectToWorld))
                _objectUniforms.xengine_ObjectToWorld = objectToWorld;
            if (properties._matrices.TryGetValue("xengine_WorldToObject", out Vector.Float4x4 worldToObject))
                _objectUniforms.xengine_WorldToObject = worldToObject;
            if (properties._matrices.TryGetValue("xengine_PrevObjectToWorld", out Vector.Float4x4 previousObjectToWorld))
                _objectUniforms.xengine_PrevObjectToWorld = previousObjectToWorld;
            if (properties._ints.TryGetValue("_ObjectID", out int objectId))
                _objectUniforms._ObjectID = objectId;
            // Per-object baked-GI state (StandardCalculateGI). _GIMode selects realtime-ambient /
            // lightmap / light-probe-SH; LightmapBinding fills these per renderable.
            if (properties._ints.TryGetValue("_GIMode", out int giMode))
                _objectUniforms._GIMode = giMode;
            if (properties._ints.TryGetValue("_LightmapUV", out int lightmapUV))
                _objectUniforms._LightmapUV = lightmapUV;
            if (properties._ints.TryGetValue("_RenderingLayerMask", out int renderingLayerMask))
                _objectUniforms._RenderingLayerMask = renderingLayerMask;
            if (properties._vectors4.TryGetValue("_LightmapScaleOffset", out Vector.Float4 lightmapScaleOffset))
                _objectUniforms._LightmapScaleOffset = lightmapScaleOffset;
            if (properties._vectors4.TryGetValue("xengine_SHAr", out Vector.Float4 shAr)) _objectUniforms.xengine_SHAr = shAr;
            if (properties._vectors4.TryGetValue("xengine_SHAg", out Vector.Float4 shAg)) _objectUniforms.xengine_SHAg = shAg;
            if (properties._vectors4.TryGetValue("xengine_SHAb", out Vector.Float4 shAb)) _objectUniforms.xengine_SHAb = shAb;
            if (properties._vectors4.TryGetValue("xengine_SHBr", out Vector.Float4 shBr)) _objectUniforms.xengine_SHBr = shBr;
            if (properties._vectors4.TryGetValue("xengine_SHBg", out Vector.Float4 shBg)) _objectUniforms.xengine_SHBg = shBg;
            if (properties._vectors4.TryGetValue("xengine_SHBb", out Vector.Float4 shBb)) _objectUniforms.xengine_SHBb = shBb;
            if (properties._vectors4.TryGetValue("xengine_SHC", out Vector.Float4 shC)) _objectUniforms.xengine_SHC = shC;
            // Instance-bound textures reach the named-texture dictionary the draw resolves from,
            // and are tracked so ClearObjectTextureBindings can undo them for the next draw.
            // Missing/unloaded textures fall through to the material/shader-default binding.
            foreach (var entry in properties._textures)
            {
                Resources.Texture2D? texture = entry.Value.Res;
                if (!texture.IsValid())
                    continue;

                _textures[entry.Key] = texture.Handle;
                _objectTextureNames.Add(entry.Key);
            }
        }
        _objectUniformsDirty = true;
    }

    private void ClearObjectTextureBindings()
    {
        foreach (string name in _objectTextureNames)
        {
            if (_materialTextures.TryGetValue(name, out GraphicsTexture materialTexture))
                _textures[name] = materialTexture;
            else if (Rendering.PropertyState.s_globalTextures.TryGetValue(name, out Resources.Texture2D globalTexture) && globalTexture.IsValid())
                _textures[name] = globalTexture.Handle;
            else
                _textures.Remove(name);
        }

        _objectTextureNames.Clear();
    }

    private void EnsureStandardLightingBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_standardLightingDirty)
            return;

        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name != "StandardLighting")
                continue;

            Rendering.StandardLightingUniformsData data = Rendering.MaterialUniformPacking.PackStandardLighting();
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
            _transientUniformBuffers["StandardLighting"] = AllocateTransientUniform(bytes);
            _standardLightingDirty = false;
            return;
        }
    }

    private void EnsureStandardShadowsBuffer(ShaderBindingSlot[] buffers)
    {
        // The shadow block is repacked unconditionally when the shader declares it: it cannot share
        // the StandardLighting dirty flag because EnsureStandardLightingBuffer clears that flag.
        // Per-draw command-ordered shadow globals (atlas params, cascade matrices, shadow slots)
        // must reach every draw even when the lighting block is already clean. The all-float4
        // StandardShadows cbuffer layout now matches the C# StandardShadowsUniformsData marshal
        // exactly, so binding the pack is safe.
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name != "StandardShadows")
                continue;

            Rendering.StandardShadowsUniformsData data = Rendering.MaterialUniformPacking.PackStandardShadows();
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
            _transientUniformBuffers["StandardShadows"] = AllocateTransientUniform(bytes);
            return;
        }
    }

    private void EnsureStandardReflectionProbesBuffer(ShaderBindingSlot[] buffers)
    {
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name != "StandardReflectionProbes")
                continue;

            Rendering.StandardReflectionProbesUniformsData data = Rendering.MaterialUniformPacking.PackStandardReflectionProbes();
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
            _transientUniformBuffers["StandardReflectionProbes"] = AllocateTransientUniform(bytes);
            return;
        }
    }

    private void EnsureStandardLightCookiesBuffer(ShaderBindingSlot[] buffers)
    {
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name != "StandardLightCookies")
                continue;

            Rendering.StandardLightCookiesUniformsData data = Rendering.MaterialUniformPacking.PackStandardLightCookies();
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
            _transientUniformBuffers["StandardLightCookies"] = AllocateTransientUniform(bytes);
            return;
        }
    }

    /// <summary>
    /// The PropertyState the material uniform pack reads from: the bound material snapshot,
    /// overlaid with the current draw's instance properties when those carry material-block
    /// keys (see <see cref="Rendering.MaterialUniformPacking.HasMaterialBlockOverrides"/>).
    /// Instance properties are documented to apply AFTER material properties so they can
    /// override them; without this merge a per-draw _MainColor/_Tiling/_Clip* override is
    /// silently replaced by the shared material's value (UI images tinted via instance
    /// _MainColor rendered solid white).
    /// </summary>
    private Rendering.PropertyState? EffectiveMaterialProperties
    {
        get
        {
            if (_instanceMaterialOverrides == null)
                return _materialProperties;
            _mergedMaterialProperties.Clear();
            if (_materialProperties != null)
                _mergedMaterialProperties.ApplyOverride(_materialProperties);
            _mergedMaterialProperties.ApplyOverride(_instanceMaterialOverrides);
            return _mergedMaterialProperties;
        }
    }

    private void EnsureMaterialUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_materialUniformsDirty)
            return;

        Rendering.PropertyState? materialProperties = EffectiveMaterialProperties;
        for (int i = 0; i < buffers.Length; i++)
        {
            string name = buffers[i].Name;
            if (name == "BlitMaterial")
            {
                Rendering.BlitMaterialUniformsData data = Rendering.MaterialUniformPacking.PackBlit(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "UnlitMaterial")
            {
                Rendering.UnlitMaterialUniformsData data = Rendering.MaterialUniformPacking.PackUnlit(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "ParticleMaterial")
            {
                Rendering.ParticleMaterialUniformsData data = Rendering.MaterialUniformPacking.PackParticle(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "VfxOutputMaterial")
            {
                Rendering.VfxOutputMaterialUniformsData data = Rendering.MaterialUniformPacking.PackVfxOutput(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "DefaultUIMaterial")
            {
                Rendering.DefaultUIMaterialUniformsData data = Rendering.MaterialUniformPacking.PackDefaultUI(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "StandardMaterial")
            {
                Rendering.StandardMaterialUniformsData data = Rendering.MaterialUniformPacking.PackStandard(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "AnisotropicMaterial")
            {
                Rendering.AnisotropicMaterialUniformsData data = Rendering.MaterialUniformPacking.PackAnisotropic(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "TerrainMaterial")
            {
                Rendering.TerrainMaterialUniformsData data = Rendering.MaterialUniformPacking.PackTerrain(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "GrassMaterial")
            {
                Rendering.GrassMaterialUniformsData data = Rendering.MaterialUniformPacking.PackGrass(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "PrepassMaterial" || name == "ShadowMaterial")
            {
                Rendering.CutoutMaterialUniformsData data = Rendering.MaterialUniformPacking.PackCutout(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "GradientPS")
            {
                Rendering.GradientSkyboxUniformsData data = Rendering.MaterialUniformPacking.PackGradientSkybox(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "CubemapSkyboxPS")
            {
                Rendering.CubemapSkyboxUniformsData data = Rendering.MaterialUniformPacking.PackCubemapSkybox(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "GizmoIconMaterial")
            {
                Rendering.GizmoIconUniformsData data = Rendering.MaterialUniformPacking.PackGizmoIcon(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SkyVS")
            {
                Rendering.ProceduralSkyboxUniformsData data = Rendering.MaterialUniformPacking.PackProceduralSkybox(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "TonemapperPS")
            {
                Rendering.TonemapperUniformsData data = Rendering.MaterialUniformPacking.PackTonemapper(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "FrameDebugPreviewPS")
            {
                Rendering.FrameDebugPreviewUniformsData data = Rendering.MaterialUniformPacking.PackFrameDebugPreview(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "BlurDownPS" || name == "BlurUpPS")
            {
                Rendering.UIBlurUniformsData data = Rendering.MaterialUniformPacking.PackUIBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "FXAAPS")
            {
                Rendering.FXAAUniformsData data = Rendering.MaterialUniformPacking.PackFXAA(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SMAAPS")
            {
                Rendering.SMAAUniformsData data = Rendering.MaterialUniformPacking.PackSMAA(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "FogTemporalPS")
            {
                Rendering.FogTemporalUniformsData data = Rendering.MaterialUniformPacking.PackFogTemporal(materialProperties);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "FogMarchPS")
            {
                Rendering.FogMarchUniformsData data = Rendering.MaterialUniformPacking.PackFogMarch(materialProperties);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "FogCompositePS")
            {
                Rendering.FogCompositeUniformsData data = Rendering.MaterialUniformPacking.PackFogComposite(materialProperties);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "RefractionMaterial")
            {
                Rendering.RefractionMaterialUniformsData data = Rendering.MaterialUniformPacking.PackRefraction(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "CinematicEffectsPS")
            {
                Rendering.CinematicEffectsUniformsData data = Rendering.MaterialUniformPacking.PackCinematicEffects(materialProperties);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "BloomThresholdPS")
            {
                Rendering.BloomThresholdUniformsData data = Rendering.MaterialUniformPacking.PackBloomThreshold(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "BloomCompositePS")
            {
                Rendering.BloomCompositeUniformsData data = Rendering.MaterialUniformPacking.PackBloomComposite(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "BokehDoFPS")
            {
                Rendering.BokehDoFUniformsData data = Rendering.MaterialUniformPacking.PackBokehDoF(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SSRRayCastPS")
            {
                Rendering.SSRRayCastUniformsData data = Rendering.MaterialUniformPacking.PackSSRRayCast(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SSRSceneBlurPS")
            {
                Rendering.SSRSceneBlurUniformsData data = Rendering.MaterialUniformPacking.PackSSRSceneBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SSRResolvePS")
            {
                Rendering.SSRResolveUniformsData data = Rendering.MaterialUniformPacking.PackSSRResolve(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SSRTemporalPS")
            {
                Rendering.SSRTemporalUniformsData data = Rendering.MaterialUniformPacking.PackSSRTemporal(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SSRCombinePS")
            {
                Rendering.SSRCombineUniformsData data = Rendering.MaterialUniformPacking.PackSSRCombine(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "MotionBlurPS")
            {
                Rendering.MotionBlurUniformsData data = Rendering.MaterialUniformPacking.PackMotionBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "AutoExposureAdaptPS")
            {
                Rendering.AutoExposureAdaptUniformsData data = Rendering.MaterialUniformPacking.PackAutoExposureAdapt(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "AutoExposureApplyPS")
            {
                Rendering.AutoExposureApplyUniformsData data = Rendering.MaterialUniformPacking.PackAutoExposureApply(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "TAAResolvePS")
            {
                Rendering.TAAUniformsData data = Rendering.MaterialUniformPacking.PackTAA(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SsgiGatherPS")
            {
                Rendering.SsgiGatherUniformsData data = Rendering.MaterialUniformPacking.PackSsgiGather(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SsgiTemporalPS")
            {
                Rendering.SsgiTemporalUniformsData data = Rendering.MaterialUniformPacking.PackSsgiTemporal(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SsgiBlurPS")
            {
                Rendering.SsgiBlurUniformsData data = Rendering.MaterialUniformPacking.PackSsgiBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SsgiCompositePS")
            {
                Rendering.SsgiCompositeUniformsData data = Rendering.MaterialUniformPacking.PackSsgiComposite(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "SsgiProbePS")
            {
                Rendering.SsgiProbeUniformsData data = Rendering.MaterialUniformPacking.PackSsgiProbe(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "GTAOCalculatePS")
            {
                Rendering.GTAOCalculateUniformsData data = Rendering.MaterialUniformPacking.PackGTAOCalculate(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "GTAOBlurPS")
            {
                Rendering.GTAOBlurUniformsData data = Rendering.MaterialUniformPacking.PackGTAOBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "GTAOTemporalPS")
            {
                Rendering.GTAOTemporalUniformsData data = Rendering.MaterialUniformPacking.PackGTAOTemporal(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "ScreenSpaceDecalPS")
            {
                Rendering.ScreenSpaceDecalUniformsData data = Rendering.MaterialUniformPacking.PackScreenSpaceDecal(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "ScreenSpaceShadowsPS")
            {
                Rendering.ScreenSpaceShadowsUniformsData data = Rendering.MaterialUniformPacking.PackScreenSpaceShadows(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            if (name == "GridPS")
            {
                Rendering.GridUniformsData data = Rendering.MaterialUniformPacking.PackGrid(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                continue;
            }
            else if (!_uniformBuffers.ContainsKey(name))
            {
                // Generic fallback: reflection-driven cbuffer packing for game-owned shaders.
                // Explicitly opcode-bound buffers (SetBuffer, e.g. Paper's UiBrushes records) are
                // excluded: a reflection-packed transient must not shadow the explicit binding.
                Rendering.GenericMaterialUniformPacking.GenericUniformOp[]? plan =
                    Rendering.GenericMaterialUniformPacking.GetWritePlan(_currentShader?.Bytecode, name);
                if (plan != null)
                {
                    int byteSize = buffers[i].ByteSize;
                    if (byteSize > 0)
                    {
                        byte[] scratch = GetPooledScratch(byteSize);
                        Rendering.GenericMaterialUniformPacking.Pack(plan, materialProperties, _materialShader, scratch);
                        _transientUniformBuffers[name] = AllocateTransientUniform(scratch);
                    }
                    continue;
                }
            }
        }

        _materialUniformsDirty = false;
    }

    private void EnsureUIVertexUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_uiVertexUniformsDirty)
            return;

        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name != "UIVS")
                continue;

            Rendering.UIVertexUniformsData data = _uiVertexUniforms;
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
            _transientUniformBuffers["UIVS"] = AllocateTransientUniform(bytes);
            _uiVertexUniformsDirty = false;
            return;
        }
    }

    private void EnsureUIFragmentUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_uiFragmentUniformsDirty)
            return;

        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name != "UIPS")
                continue;

            Rendering.UIFragmentUniformsData data = _uiFragmentUniforms;
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
            _transientUniformBuffers["UIPS"] = AllocateTransientUniform(bytes);
            _uiFragmentUniformsDirty = false;
            return;
        }
    }

    private void ApplyMaterialTextureBindings()
    {
        ClearObjectTextureBindings();
        ShaderBindingSlot[]? textureSlots = _currentShader?.Bytecode?.BindingLayout?.Textures;
        Rendering.MaterialUniformPacking.ApplyTextureBindings(_materialTextures, _materialProperties, _materialShader, textureSlots);
        Rendering.MaterialUniformPacking.ApplyTextureBindings(_textures, _materialProperties, _materialShader, textureSlots);
    }

    private D3D12TransientUniformBinding AllocateTransientUniform(ReadOnlySpan<byte> bytes)
    {
        const ulong alignment = 256;
        ulong alignedSize = ((ulong)bytes.Length + alignment - 1) / alignment * alignment;
        if (_transientUniformArena == null || _transientUniformArenaOffset + alignedSize > TransientUniformArenaSize)
        {
            _transientUniformArena = _device.CreateCommittedBuffer(
                TransientUniformArenaSize,
                HeapType.Upload,
                ResourceStates.GenericRead);
            _submissionTransientResources!.Add(_transientUniformArena);
            _transientUniformArenaOffset = 0;
            _transientUniformArenaPtr = _transientUniformArena.Map<byte>(0);
        }

        ulong offset = _transientUniformArenaOffset;
        // All uniform producers share the same persistent mapping. Unmapping here
        // invalidates the pointer subsequently used for per-object draws.
        fixed (byte* source = bytes)
            System.Buffer.MemoryCopy(source, _transientUniformArenaPtr + offset, checked((long)alignedSize), bytes.Length);
        _transientUniformArenaOffset += alignedSize;
        return new D3D12TransientUniformBinding(_transientUniformArena, offset);
    }

    /// <summary>Zero-filled transient uniform of <paramref name="byteSize"/> bytes (256-byte
    /// aligned). Used by the missing-cbuffer degradation path.</summary>
    private D3D12TransientUniformBinding AllocateZeroedTransientUniform(int byteSize)
    {
        if (byteSize <= 0)
            byteSize = 16;
        byte[] scratch = GetPooledScratch(byteSize);
        scratch.AsSpan(0, byteSize).Clear();
        return AllocateTransientUniform(scratch.AsSpan(0, byteSize));
    }

    // Draw-degradation log throttle: one loud error per unique missing binding per process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_degradedDraws = new();

    private static void LogDrawDegradationOnce(string what)
    {
        if (s_degradedDraws.TryAdd(what, 0))
            Debug.LogError($"[D3D12] Draw degraded — missing {what}; bound zeros and continuing. (logged once per binding; fix the material/shader)");
    }

    /// <summary>Pooled scratch byte array for generic cbuffer packing (fallback path only).
    /// Grows on demand; not cleared between uses — the generic packer zeroes the destination.</summary>
    private byte[] GetPooledScratch(int size)
    {
        if (_genericUniformScratch == null || _genericUniformScratch.Length < size)
            _genericUniformScratch = new byte[size];
        return _genericUniformScratch;
    }

    private void SetRenderTarget(ID3D12GraphicsCommandList list, GraphicsFrameBuffer? framebuffer)
    {
        if (ReferenceEquals(_pendingRenderTarget, framebuffer))
            return;
        RestorePendingRenderTarget(list);
        _pendingRenderTarget = framebuffer;
    }

    private void BindCurrentRenderTargets(ID3D12GraphicsCommandList list)
    {
        D3D12FramebufferResource? framebuffer = GetPendingFramebuffer();
        if (framebuffer == null)
        {
            if (!_lastBoundBackbuffer)
            {
                list.OMSetRenderTargets(_device.CurrentRtv, null);
                _lastBoundBackbuffer = true;
                _lastBoundFramebuffer = null;
            }
            return;
        }

        // TransitionFramebuffer tracks resource states itself and no-ops when already in
        // RenderTarget state, so it stays outside the dedup.
        TransitionFramebuffer(list, framebuffer, ResourceStates.RenderTarget);
        if (!ReferenceEquals(_lastBoundFramebuffer, framebuffer))
        {
            list.OMSetRenderTargets(
                framebuffer.Rtvs,
                framebuffer.DepthFormat == Format.Unknown ? null : framebuffer.Dsv);
            _lastBoundFramebuffer = framebuffer;
            _lastBoundBackbuffer = false;
        }
        if (_currentRaster.StencilEnabled && _lastStencilRef != _currentRaster.StencilRef)
        {
            list.OMSetStencilRef(checked((uint)_currentRaster.StencilRef));
            _lastStencilRef = _currentRaster.StencilRef;
        }
    }

    private void ApplyCurrentScissor(ID3D12GraphicsCommandList list)
        => list.RSSetScissorRect(_scissorEnabled ? _scissorRect : _viewportRect);

    private D3D12RenderTargetFormats GetCurrentTargetFormats()
    {
        D3D12FramebufferResource? framebuffer = GetPendingFramebuffer();
        return framebuffer == null
            ? new D3D12RenderTargetFormats(new D3D12ColorAttachmentFormats(Format.R8G8B8A8_UNorm), Format.Unknown)
            : new D3D12RenderTargetFormats(framebuffer.ColorFormats, framebuffer.DepthFormat, framebuffer.SampleCount);
    }

    /// <summary>
    /// Sample count of the currently bound render target. The swapchain is always single-sample
    /// (flip-model swapchains forbid MSAA back buffers), so an unbound target reports 1.
    /// </summary>
    private int GetCurrentSampleCount()
    {
        D3D12FramebufferResource? framebuffer = GetPendingFramebuffer();
        return framebuffer == null ? 1 : (int)framebuffer.SampleCount;
    }

    private D3D12FramebufferResource? GetPendingFramebuffer()
    {
        if (_pendingRenderTarget == null)
            return null;
        if (_pendingRenderTarget.Handle == 0 ||
            !_device.Framebuffers.TryGetValue(_pendingRenderTarget.Handle, out D3D12FramebufferResource? framebuffer))
        {
            throw new InvalidOperationException("D3D12 custom framebuffer is not available.");
        }
        return framebuffer;
    }

    /// <summary>
    /// Resolve a multisample color texture into a same-size single-sample texture with the
    /// fixed-function <c>ResolveSubresource</c>. Both resources are moved to the dedicated
    /// resolve states and then restored to the states the rest of the translator expects
    /// (RenderTarget for the MSAA source, PixelShaderResource for the destination, which post
    /// processing samples immediately afterwards).
    /// </summary>
    private void DoResolveTexture(ID3D12GraphicsCommandList list, GraphicsTexture? source, GraphicsTexture? destination)
    {
        if (source == null || destination == null)
            throw new InvalidOperationException("D3D12 ResolveTexture requires both a source and a destination texture.");
        if (source.Handle == 0 || !_device.Textures.TryGetValue(source.Handle, out D3D12TextureResource? src) || src.Resource == null)
            throw new InvalidOperationException("D3D12 ResolveTexture source is not available.");
        if (destination.Handle == 0 || !_device.Textures.TryGetValue(destination.Handle, out D3D12TextureResource? dst) || dst.Resource == null)
            throw new InvalidOperationException("D3D12 ResolveTexture destination is not available.");
        if (!src.IsMultisampled)
            throw new InvalidOperationException("D3D12 ResolveTexture source must be a multisample texture.");
        if (dst.IsMultisampled)
            throw new InvalidOperationException("D3D12 ResolveTexture destination must be single-sample.");
        if (src.Width != dst.Width || src.Height != dst.Height)
            throw new InvalidOperationException("D3D12 ResolveTexture requires matching source and destination extents.");
        if (src.Format != dst.Format)
            throw new InvalidOperationException("D3D12 ResolveTexture requires matching source and destination formats.");

        ResourceStates sourceBefore = src.State;
        ResourceStates destinationBefore = dst.State;
        if (sourceBefore != ResourceStates.ResolveSource)
            list.ResourceBarrierTransition(src.Resource, sourceBefore, ResourceStates.ResolveSource);
        if (destinationBefore != ResourceStates.ResolveDest)
            list.ResourceBarrierTransition(dst.Resource, destinationBefore, ResourceStates.ResolveDest);

        list.ResolveSubresource(dst.Resource, 0, src.Resource, 0, src.Format);

        // The MSAA target goes back to RenderTarget so the next camera pass can keep drawing into
        // it without another transition; the resolved copy lands in PixelShaderResource because
        // post processing samples it as _CameraColorTexture on the very next pass.
        list.ResourceBarrierTransition(src.Resource, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        src.State = ResourceStates.RenderTarget;
        list.ResourceBarrierTransition(dst.Resource, ResourceStates.ResolveDest, ResourceStates.PixelShaderResource);
        dst.State = ResourceStates.PixelShaderResource;

        // ResolveSubresource does not disturb OMSetRenderTargets, but the tracked binding is
        // rebuilt anyway so a follow-up draw re-emits it against the restored resource states.
        InvalidateTrackedRenderTargets();
    }

    private void ReadTextureData(GraphicsTexture? texture, int mip, byte[]? destination)
    {
        byte[] source = ReadTexture2DPixels(texture, mip);
        if (destination == null)
            throw new InvalidOperationException("D3D12 texture readback requires a destination buffer.");
        if (destination.Length < source.Length)
            throw new ArgumentException("D3D12 texture readback destination is smaller than the requested mip level.", nameof(destination));
        source.AsSpan().CopyTo(destination);
    }

    private void ReadTextureData(GraphicsTexture? texture, int mip, nint destination)
    {
        if (destination == 0)
            throw new ArgumentNullException(nameof(destination));
        byte[] source = ReadTexture2DPixels(texture, mip);
        fixed (byte* sourcePtr = source)
            System.Buffer.MemoryCopy(sourcePtr, (void*)destination, source.Length, source.Length);
    }

    private byte[] ReadTexture2DPixels(GraphicsTexture? texture, int mip)
    {
        if (texture == null)
            throw new InvalidOperationException("D3D12 texture readback requires a texture.");
        if (texture.Type != TextureType.Texture2D)
            throw new NotSupportedException("D3D12 GetTextureData only supports Texture2D resources.");
        if (mip < 0)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (texture.Handle == 0 ||
            !_device.Textures.TryGetValue(texture.Handle, out D3D12TextureResource? resource) ||
            resource.Resource == null)
        {
            throw new InvalidOperationException("D3D12 texture readback references a texture that has not been created.");
        }
        if ((uint)mip >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (resource.IsMultisampled)
            throw new NotSupportedException("D3D12 texture readback cannot copy from a multisample resource; resolve it first.");
        return _device.ReadTextureSubresource(resource, (uint)mip, 0);
    }

    private void ReadTextureCubeFaceData(GraphicsTexture? texture, int face, int mip, byte[]? destination)
    {
        if (texture == null)
            throw new InvalidOperationException("D3D12 cubemap readback requires a texture.");
        if (texture.Type != TextureType.TextureCubeMap)
            throw new NotSupportedException("D3D12 GetTextureCubeFaceData only supports cubemap resources.");
        if (face < 0 || face >= 6)
            throw new ArgumentOutOfRangeException(nameof(face));
        if (mip < 0)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (destination == null)
            throw new InvalidOperationException("D3D12 cubemap readback requires a destination buffer.");
        if (texture.Handle == 0 ||
            !_device.Textures.TryGetValue(texture.Handle, out D3D12TextureResource? resource) ||
            resource.Resource == null)
        {
            throw new InvalidOperationException("D3D12 cubemap readback references a texture that has not been created.");
        }
        if ((uint)mip >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip));

        byte[] source = _device.ReadTextureSubresource(resource, (uint)mip, (uint)face);
        if (destination.Length < source.Length)
            throw new ArgumentException("D3D12 cubemap readback destination is smaller than the requested face mip level.", nameof(destination));
        source.AsSpan().CopyTo(destination);
    }

    private void RestorePendingRenderTarget(ID3D12GraphicsCommandList list)
    {
        D3D12FramebufferResource? framebuffer = GetPendingFramebuffer();
        if (framebuffer != null)
            TransitionFramebuffer(list, framebuffer, ResourceStates.PixelShaderResource);
    }

    private void TransitionFramebuffer(
        ID3D12GraphicsCommandList list,
        D3D12FramebufferResource framebuffer,
        ResourceStates after)
    {
        for (int i = 0; i < framebuffer.ColorHandles.Length; i++)
        {
            if (!_device.Textures.TryGetValue(framebuffer.ColorHandles[i], out D3D12TextureResource? texture) || texture.Resource == null)
                throw new InvalidOperationException("D3D12 framebuffer color attachment is not available.");
            if (framebuffer.SubresourceOnlyByAttachment[i])
            {
                if (texture.State != ResourceStates.PixelShaderResource)
                    throw new InvalidOperationException("D3D12 cubemap framebuffer attachment requires pixel-shader resource state.");
                ResourceStates before = after == ResourceStates.RenderTarget
                    ? ResourceStates.PixelShaderResource
                    : ResourceStates.RenderTarget;
                list.ResourceBarrierTransition(texture.Resource, before, after, framebuffer.ColorSubresources[i]);
                continue;
            }
            if (texture.State == after)
                continue;

            list.ResourceBarrierTransition(texture.Resource, texture.State, after);
            texture.State = after;
        }

        // Depth attachment transitions mirror the color attachments: when the framebuffer is being
        // bound for rendering, the depth buffer must be in DepthWrite; when it is being released for
        // sampling (after == PixelShaderResource), transition it to PixelShaderResource so the
        // shadow atlas / depth reads can sample it. This is what lets the runtime shadow atlas flow
        // from the shadow pass into the forward pass as _ShadowAtlas.
        if (framebuffer.DepthHandle != 0 &&
            _device.Textures.TryGetValue(framebuffer.DepthHandle, out D3D12TextureResource? depthTexture) &&
            depthTexture.Resource != null)
        {
            ResourceStates depthAfter = after == ResourceStates.RenderTarget
                ? ResourceStates.DepthWrite
                : ResourceStates.PixelShaderResource;
            if (depthTexture.State != depthAfter)
            {
                list.ResourceBarrierTransition(depthTexture.Resource, depthTexture.State, depthAfter);
                depthTexture.State = depthAfter;
            }
        }
    }

    private bool TryGetBoundTexture(string name, out GraphicsTexture texture)
    {
        if (_textures.TryGetValue(name, out texture) && texture.Handle != 0)
            return true;
        if (name == "_Lightmap" && _textures.TryGetValue("_MainTex", out texture) && texture.Handle != 0)
            return true;
        if (Rendering.PropertyState.s_globalTextures.TryGetValue(name, out Texture2D globalTexture)
            && globalTexture.IsValid() && globalTexture.Handle.Handle != 0)
        {
            texture = globalTexture.Handle;
            return true;
        }

        if (Rendering.PropertyState.s_globalTexturesCube.TryGetValue(name, out Cubemap cube)
            && cube.IsValid() && cube.Handle.Handle != 0)
        {
            texture = cube.Handle;
            return true;
        }

        // Final fallback: bind the per-device 1x1 white texture instead of leaving the descriptor
        // empty. Missing texture bindings used to crash the draw; now the sampler reads white.
        if (_whiteTextureFallbackRef is { IsDisposed: false } white && white.Handle.Handle != 0)
        {
            texture = white.Handle;
            return true;
        }

        texture = default!;
        return false;
    }

    /// <summary>
    /// Creates the per-device white fallback texture. Called from device Initialize AFTER the
    /// translator field is assigned: Texture2D.LoadDefault submits through the global
    /// Graphics.Device which is still null during device construction (the op would be dropped and
    /// the handle stay 0), and lazy creation from the render thread mid-translation would nest an
    /// Execute/Translate inside the in-flight one. Encoding through the owning device with
    /// wait:true fills the handle synchronously.
    /// </summary>
    internal void InitializeMissingBindingFallbacks()
    {
        if (_whiteTextureFallbackRef is { IsDisposed: false } existing && existing.Handle.Handle != 0)
            return;

        _whiteTextureFallbackRef = new Texture2D(1, 1, false, TextureImageFormat.Color4b);
        using CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("WhiteFallback.Create");
        cmd.EncodeCreateTexture(_whiteTextureFallbackRef.Handle);
        cmd.EncodeAllocateTexture2D(_whiteTextureFallbackRef.Handle, 0, 1, 1, 0, [255, 255, 255, 255]);
        cmd.EncodeSetTextureFilters(_whiteTextureFallbackRef.Handle, TextureMin.Nearest, TextureMag.Nearest);
        _device.Execute(cmd, wait: true);
    }

    private D3D12TextureResource GetTextureResource(string name)
    {
        if (!TryGetBoundTexture(name, out GraphicsTexture texture))
            throw new InvalidOperationException($"D3D12 draw requires texture '{name}'.");

        if (!_device.Textures.TryGetValue(texture.Handle, out D3D12TextureResource? resource) ||
            resource.Resource == null || !resource.HasSrvDescriptor || !resource.HasSamplerDescriptor)
            throw new InvalidOperationException($"D3D12 texture '{name}' is not sample-ready.");
        if (resource.Type == TextureType.TextureCubeMap && resource.CubeInitializedFaces != 0b0011_1111)
            throw new InvalidOperationException($"D3D12 cubemap '{name}' is missing one or more faces.");
        return resource;
    }

    private static string FindTextureNameForSampler(ShaderBindingSlot[] textures, int samplerSlot)
    {
        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i].Slot == samplerSlot)
                return textures[i].Name;
        }
        throw new NotSupportedException($"D3D12 sampler slot s{samplerSlot} has no texture at matching t{samplerSlot}.");
    }

    private void WarnDrawNoPso()
    {
        if (_warnedDrawNoPso)
            return;
        _warnedDrawNoPso = true;
        Debug.LogWarning(_currentShader == null
            ? "D3D12 draw skipped: no backend-neutral shader variant is bound."
            : "D3D12 draw skipped: pipeline state objects are not ready yet.");
    }

    private void TouchPipelineKey(GraphicsVertexArray? vao, Topology topology, bool index32Bit)
    {
        if (_currentShader == null || vao == null)
            return;

        _ = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
    }

    private void WarnOnce(CommandOpcode op, string message)
    {
        if (!_warnedOpcodes.Add(op))
            return;
        Debug.LogWarning(message);
    }

    private void SkipOpcode(
        CommandOpcode op,
        ReadOnlySpan<byte> stream,
        ref int pos,
        List<object?> objects,
        TransientStore store)
    {
        WarnOnce(op, $"D3D12CommandTranslator: unhandled opcode {op} (skipped).");

        switch (op)
        {
            case CommandOpcode.BeginOcclusionQuery:
            case CommandOpcode.EndOcclusionQuery:
                // B7b — occlusion pairs are only recorded on devices that report support.
                _ = ReadI64(stream, ref pos);
                break;
            case CommandOpcode.CompileShader:
            case CommandOpcode.DisposeShader:
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.SetGlobalTexture3D:
            case CommandOpcode.SetGlobalTextureCube:
            case CommandOpcode.SetUniformTexture:
                _ = objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.SetGlobalInt:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                break;
            case CommandOpcode.SetGlobalFloat:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadF32(stream, ref pos);
                break;
            case CommandOpcode.SetGlobalVec2:
                _ = objects[ReadU16(stream, ref pos)];
                pos += Unsafe.SizeOf<Vector.Float2>();
                break;
            case CommandOpcode.SetGlobalVec3:
            case CommandOpcode.SetUniformVec3:
                _ = objects[ReadU16(stream, ref pos)];
                pos += Unsafe.SizeOf<Vector.Float3>();
                break;
            case CommandOpcode.SetGlobalVec4:
            case CommandOpcode.SetGlobalColor:
                _ = objects[ReadU16(stream, ref pos)];
                pos += Unsafe.SizeOf<Vector.Float4>();
                break;
            case CommandOpcode.SetGlobalMatrix:
            case CommandOpcode.SetGlobalBuffer:
                _ = objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU32(stream, ref pos);
                break;
            case CommandOpcode.ClearAllGlobals:
                break;
            case CommandOpcode.SetUniformMatrixArray:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadBlob<Vector.Float4x4>(stream, ref pos, store);
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
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadU8(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                break;
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
            case CommandOpcode.GetTextureDataPtr:
                _ = objects[ReadU16(stream, ref pos)];
                _ = ReadI32(stream, ref pos);
                if (op == CommandOpcode.GetTextureData)
                    _ = objects[ReadU16(stream, ref pos)];
                else
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
                throw new InvalidOperationException($"D3D12CommandTranslator: cannot skip unknown opcode {op}.");
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

internal readonly record struct D3D12TransientUniformBinding(ID3D12Resource Resource, ulong Offset);
