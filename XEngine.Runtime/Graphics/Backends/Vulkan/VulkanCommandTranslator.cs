// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.Vulkan;

using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Resources;

using CommandBuffer = XEngine.Runtime.CommandBuffer;
using FrameDebugger = XEngine.Runtime.Rendering.FrameDebugger;
using VkCommandBuffer = Silk.NET.Vulkan.CommandBuffer;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace XEngine.Runtime.Backends.Vulkan;

/// <summary>
/// Translates engine <see cref="CommandBuffer"/> recordings into Vulkan commands.
/// Clears, viewport/scissor, and resource create/dispose are implemented; draws stay stubs.
/// </summary>
internal sealed unsafe class VulkanCommandTranslator
{
    private const ulong TransientUniformArenaSize = 64 * 1024;
    // vkCmdUpdateBuffer records the bytes directly into the command buffer and accepts at most
    // 64 KiB per call. Keeping dynamic uploads in the command stream prevents the next frame
    // from overwriting host-visible destination memory while the GPU still reads the prior frame.
    private const int MaxInlineBufferUpdateSize = 64 * 1024;

    private readonly VulkanGraphicsDevice _device;
    // Mali drivers (HarmonyOS) do not correctly handle negative viewport height even with
    // VK_KHR_maintenance1 enabled — the image renders upside-down. Desktop drivers (Intel Arc,
    // NVIDIA, AMD) handle it fine. Detect at construction time so we use positive height on Mali
    // and negative height (the original behavior) everywhere else, keeping desktop unchanged.
    private readonly bool _usePositiveViewportHeight;
    private readonly HashSet<CommandOpcode> _warnedOpcodes = new();
    private bool _warnedDrawNoPso;
    private bool _inRenderPass;

    private GraphicsFrameBuffer? _pendingRenderTarget;
    private GraphicsFrameBuffer? _pendingReadTarget;
    private ShaderVariant? _currentShader;
    private ShaderVariant? _currentComputeShader;
    private RasterizerState _currentRaster = new();
    private readonly Dictionary<string, GraphicsBuffer> _uniformBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VkTransientUniformBinding> _transientUniformBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphicsTexture> _textures = new(StringComparer.Ordinal);
    // Material texture slots are reused by each draw. Retain the resolved baseline separately so
    // an instance texture (UI text atlases, sprites, and lightmaps) can override one draw without
    // leaking into the next item in the batch.
    private readonly Dictionary<string, GraphicsTexture> _materialTextures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _objectTextureNames = new(StringComparer.Ordinal);
    private Rendering.ObjectUniformsData _objectUniforms;
    private bool _objectUniformsDirty;
    private Rendering.UIVertexUniformsData _uiVertexUniforms;
    private bool _uiVertexUniformsDirty;
    private Rendering.UIFragmentUniformsData _uiFragmentUniforms;
    private bool _uiFragmentUniformsDirty;
    private Rendering.GlobalUniformsData _globalUniformsSnapshot;
    private bool _hasGlobalUniformsSnapshot;
    private bool _globalUniformsExplicitlyBound;
    private bool _viewportSet;
    private bool _scissorSet;
    private Rendering.PropertyState? _materialProperties;
    private Resources.Shader? _materialShader;
    // Instance PropertyState carrying material-block overrides for the current draw (null when
    // the instance state only holds per-object data). Merged over _materialProperties by
    // EffectiveMaterialProperties; the scratch below backs that merge without allocating.
    private Rendering.PropertyState? _instanceMaterialOverrides;
    private readonly Rendering.PropertyState _mergedMaterialProperties = new();
    // The RAW instance state of the current draw, regardless of whether it carries material-block
    // overrides. SkinnedMeshRenderer attaches boneMatrixTexture (and morph textures) to an
    // object-block-only state, which HasMaterialBlockOverrides rejects — texture resolution must
    // still see it or every skinned draw samples a foreign/stale bone texture.
    private Rendering.PropertyState? _instanceTextureState;
    private readonly Rendering.PropertyState _mergedTextureProperties = new();
    private bool _materialUniformsDirty;
    private bool _standardLightingDirty;
    private DescriptorSet _currentDescriptorSet;
    private bool _descriptorDirty;
    // Descriptor sets allocated during this submission are owned by the per-frame descriptor pool
    // (reset atomically in BeginFrame via vkResetDescriptorPool). No per-set tracking is needed.
    private List<Sampler>? _submissionRetiredSamplers;
    private readonly List<GraphicsFrameBuffer> _framebuffersToRebuild = new();
    private List<VkBufferResource>? _submissionTransientBuffers;
    private List<VkImageResource>? _submissionTransientImages;
    private List<VkFramebufferResource>? _submissionTransientFramebuffers;
    private VkBufferResource? _transientUniformArena;
    private ulong _transientUniformArenaOffset;
    // Pooled scratch for generic cbuffer packing (fallback path). Grows on demand, cleared per use.
    private byte[]? _genericUniformScratch;

    // White texture fallback for missing texture bindings. Vulkan requires all descriptor
    // set bindings to be valid; when a shader declares a sampler but no texture is provided
    // (e.g., material override missing and no shader default), bind a 1x1 white texture
    // instead of crashing the draw with "Vulkan draw requires texture '<name>'".
    // Per-device instance, created by InitializeMissingBindingFallbacks during device init.
    private Resources.Texture2D? _whiteTextureFallbackRef;

    public VulkanCommandTranslator(VulkanGraphicsDevice device)
    {
        _device = device;
        // Mali drivers report themselves via the device name; check for known Mali prefixes.
        // These do not correctly handle negative viewport height and need positive height +
        // shader-level _FlipY instead.
        _usePositiveViewportHeight = device.UsesPositiveViewportHeight;
    }

    /// <summary>
    /// Creates the per-device white fallback texture. Called from device Initialize AFTER the
    /// translator field is assigned, on the initializing thread, before the render loop starts:
    /// (a) Texture2D.LoadDefault submits through the global Graphics.Device which is still null
    /// during device construction (the op would be dropped and the handle stay 0); (b) creating
    /// GPU resources lazily from the render thread mid-translation would nest an Execute/Translate
    /// inside the in-flight one (Execute runs inline on the render thread) and corrupt per-draw
    /// state. Encoding through the owning device with wait:true fills the handle synchronously.
    /// </summary>
    internal void InitializeMissingBindingFallbacks()
    {
        if (_whiteTextureFallbackRef is { IsDisposed: false } existing && existing.Handle.Handle != 0)
            return;

        _whiteTextureFallbackRef = new Resources.Texture2D(1, 1, false, TextureImageFormat.Color4b);
        using CommandBuffer cmd = Graphics.GetCommandBuffer("WhiteFallback.Create");
        cmd.EncodeCreateTexture(_whiteTextureFallbackRef.Handle);
        cmd.EncodeAllocateTexture2D(_whiteTextureFallbackRef.Handle, 0, 1, 1, 0, [255, 255, 255, 255]);
        cmd.EncodeSetTextureFilters(_whiteTextureFallbackRef.Handle, TextureMin.Nearest, TextureMag.Nearest);
        _device.Execute(cmd, wait: true);
    }

    public void Translate(
        CommandBuffer commandBuffer,
        VkCommandBuffer vkCmd,
        List<Sampler> retiredSamplers,
        List<VkBufferResource> transientBuffers,
        List<VkImageResource> transientImages,
        List<VkFramebufferResource> transientFramebuffers,
        bool preserveUniformArena = false)
    {
        var stream = commandBuffer._stream.AsSpan(0, commandBuffer._streamPos);
        var objects = commandBuffer._objects;
        var store = commandBuffer._store;
        var fdScope = commandBuffer._fdScopeKind;
        int pos = 0;
        _inRenderPass = false;
        _uniformBuffers.Clear();
        _transientUniformBuffers.Clear();
        _textures.Clear();
        _objectUniforms = Rendering.ObjectUniformsData.Identity;
        _objectUniformsDirty = true;
        _uiVertexUniforms = default;
        _uiVertexUniformsDirty = true;
        _uiFragmentUniforms = default;
        _uiFragmentUniformsDirty = true;
        _globalUniformsSnapshot = commandBuffer._globalUniformsSnapshot;
        _hasGlobalUniformsSnapshot = commandBuffer._hasGlobalUniformsSnapshot;
        _globalUniformsExplicitlyBound = false;
        _viewportSet = false;
        _scissorSet = false;
        _materialProperties = null;
        _materialShader = null;
        _instanceMaterialOverrides = null;
        _materialUniformsDirty = true;
        _standardLightingDirty = true;
        _currentShader = null;
        _currentComputeShader = null;
        GraphicsBuffer? globalUniforms = Rendering.GlobalUniforms.GetBuffer();
        if (globalUniforms is { Handle: not 0 })
            _uniformBuffers["GlobalUniforms"] = globalUniforms;
        _currentDescriptorSet = default;
        _descriptorDirty = true;
        _submissionRetiredSamplers = retiredSamplers;
        _submissionTransientBuffers = transientBuffers;
        _submissionTransientImages = transientImages;
        _submissionTransientFramebuffers = transientFramebuffers;
        if (!preserveUniformArena)
        {
            _transientUniformArena = null;
            _transientUniformArenaOffset = 0;
        }

        while (pos < stream.Length)
        {
            CommandOpcode op = ReadOpcode(stream, ref pos);
            switch (op)
            {
                case CommandOpcode.SetRenderTarget:
                {
                    EndRenderPassIfNeeded(vkCmd);
                    var fb = (GraphicsFrameBuffer?)objects[ReadU16(stream, ref pos)];
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    _pendingRenderTarget = fb;
                    _pendingReadTarget = fb;
                    break;
                }
                case CommandOpcode.SetRenderTargets:
                {
                    EndRenderPassIfNeeded(vkCmd);
                    var draw = (GraphicsFrameBuffer?)objects[ReadU16(stream, ref pos)];
                    var read = (GraphicsFrameBuffer?)objects[ReadU16(stream, ref pos)];
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    _pendingRenderTarget = draw;
                    _pendingReadTarget = read;
                    break;
                }
                case CommandOpcode.SetViewport:
                {
                    int x = ReadI32(stream, ref pos);
                    int y = ReadI32(stream, ref pos);
                    uint w = ReadU32(stream, ref pos);
                    uint h = ReadU32(stream, ref pos);
                    Viewport vp;
                    if (_usePositiveViewportHeight)
                    {
                        // Mali path: positive height, Blit/Tonemapper _FlipY handles the flip.
                        vp = new Viewport { X = x, Y = y, Width = w, Height = h, MinDepth = 0f, MaxDepth = 1f };
                    }
                    else
                    {
                        // Desktop path: negative height compensates for Vulkan's top-left origin.
                        vp = new Viewport { X = x, Y = y + h, Width = w, Height = -(float)h, MinDepth = 0f, MaxDepth = 1f };
                    }
                    _device.Vk.CmdSetViewport(vkCmd, 0, 1, &vp);
                    _viewportSet = true;
                    break;
                }
                case CommandOpcode.SetScissor:
                {
                    int x = ReadI32(stream, ref pos);
                    int y = ReadI32(stream, ref pos);
                    uint w = ReadU32(stream, ref pos);
                    uint h = ReadU32(stream, ref pos);
                    var scissor = new Rect2D
                    {
                        Offset = new Offset2D(x, y),
                        Extent = new Extent2D(w, h),
                    };
                    _device.Vk.CmdSetScissor(vkCmd, 0, 1, &scissor);
                    _scissorSet = true;
                    break;
                }
                case CommandOpcode.DisableScissor:
                {
                    var scissor = new Rect2D
                    {
                        Offset = new Offset2D(0, 0),
                        Extent = new Extent2D(16384, 16384),
                    };
                    _device.Vk.CmdSetScissor(vkCmd, 0, 1, &scissor);
                    _scissorSet = true;
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
                    DoClear(vkCmd, flags, r, g, b, a, depth, stencil);
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
                    DoBlit(vkCmd, srcX, srcY, srcWidth, srcHeight, dstX, dstY, dstWidth, dstHeight, mask, filter);
                    break;
                }
                case CommandOpcode.ResolveTexture:
                {
                    var source = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    var destination = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DoResolveTexture(vkCmd, source, destination);
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
                    _descriptorDirty = true;
                    if (_currentShader?.Bytecode?.Format != ShaderBytecodeFormat.SpirV)
                        WarnOnce(CommandOpcode.SetShader, "Vulkan shader bind skipped: expected a SPIR-V ShaderVariant.");
                    else
                    {
                        _device.GetOrCreateShaderLayout(_currentShader);
                        _device.GetOrCreateShaderModules(_currentShader);
                    }
                    break;
                }
                case CommandOpcode.SetInstanceProperties:
                {
                    var properties = (Rendering.PropertyState?)objects[ReadU16(stream, ref pos)];
                    ApplyObjectProperties(properties);
                    // Texture resolution must see the RAW instance state every draw (boneMatrixTexture
                    // rides object-block-only states); material-uniform repacking stays gated below so
                    // the common object-block-only batch case doesn't repack the cbuffer per draw.
                    _instanceTextureState = properties;
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
                    // Instance state carries per-renderer TEXTURES (boneMatrixTexture, morph
                    // textures). The stream binds the ShaderVariant BEFORE the instance
                    // properties, so resolve the texture bindings NOW — waiting for the next
                    // SetShader would leave this draw sampling stale/foreign textures.
                    ApplyMaterialTextureBindings();
                    _descriptorDirty = true;
                    break;
                }
                case CommandOpcode.SetProperties:
                {
                    _materialProperties = (Rendering.PropertyState?)objects[ReadU16(stream, ref pos)];
                    _materialShader = null;
                    _instanceMaterialOverrides = null;
                    _instanceTextureState = null;
                    _materialUniformsDirty = true;
                    _descriptorDirty = true;
                    ApplyMaterialTextureBindings();
                    break;
                }
                case CommandOpcode.SetMaterialProperties:
                {
                    _materialProperties = (Rendering.PropertyState?)objects[ReadU16(stream, ref pos)];
                    _materialShader = (Resources.Shader?)objects[ReadU16(stream, ref pos)];
                    _instanceMaterialOverrides = null;
                    _instanceTextureState = null;
                    _materialUniformsDirty = true;
                    _descriptorDirty = true;
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
                    _descriptorDirty = true;
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
                    _descriptorDirty = true;
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
                    _descriptorDirty = true;
                    break;
                }
                case CommandOpcode.ClearGlobalTexture:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    _textures.Remove(name);
                    Rendering.PropertyState.s_globalTextures.Remove(name);
                    Rendering.PropertyState.s_globalTexturesCube.Remove(name);
                    _descriptorDirty = true;
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
                    _descriptorDirty = true;
                    break;
                case CommandOpcode.ClearInstanceProperties:
                {
                    _objectUniforms = Rendering.ObjectUniformsData.Identity;
                    _objectUniformsDirty = true;
                    _descriptorDirty = true;
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
                    {
                        _uniformBuffers[name] = buffer;
                        if (name == "GlobalUniforms")
                        {
                            _globalUniformsExplicitlyBound = true;
                            _transientUniformBuffers.Remove(name);
                        }
                    }
                    _descriptorDirty = true;
                    break;
                }
                case CommandOpcode.SetUniformFloat:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    float value = ReadF32(stream, ref pos);
                    if (Rendering.UIUniformPacking.TrySetFloat(ref _uiFragmentUniforms, name, value))
                    {
                        _uiFragmentUniformsDirty = true;
                        _descriptorDirty = true;
                    }
                    else
                        WarnOnce(CommandOpcode.SetUniformFloat, $"VulkanCommandTranslator: uniform '{name}' is not packable yet.");
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
                        _descriptorDirty = true;
                    }
                    else if (Rendering.UIUniformPacking.TrySetInt(ref _uiFragmentUniforms, name, value))
                    {
                        _uiFragmentUniformsDirty = true;
                        _descriptorDirty = true;
                    }
                    else
                    {
                        WarnOnce(CommandOpcode.SetUniformInt, $"VulkanCommandTranslator: uniform '{name}' is not packable yet.");
                    }
                    break;
                }
                case CommandOpcode.SetUniformVec2:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Vector.Float2 value = ReadStruct<Vector.Float2>(stream, ref pos);
                    if (Rendering.UIUniformPacking.TrySetVector2(ref _uiFragmentUniforms, name, value))
                    {
                        _uiFragmentUniformsDirty = true;
                        _descriptorDirty = true;
                    }
                    else
                        WarnOnce(CommandOpcode.SetUniformVec2, $"VulkanCommandTranslator: uniform '{name}' is not packable yet.");
                    break;
                }
                case CommandOpcode.SetUniformVec4:
                {
                    string name = (string)objects[ReadU16(stream, ref pos)]!;
                    Vector.Float4 value = ReadStruct<Vector.Float4>(stream, ref pos);
                    if (Rendering.UIUniformPacking.TrySetVector4(ref _uiFragmentUniforms, name, value))
                    {
                        _uiFragmentUniformsDirty = true;
                        _descriptorDirty = true;
                    }
                    else
                        WarnOnce(CommandOpcode.SetUniformVec4, $"VulkanCommandTranslator: uniform '{name}' is not packable yet.");
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
                        _descriptorDirty = true;
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
                        _descriptorDirty = true;
                    }
                    else
                    {
                        WarnOnce(CommandOpcode.SetUniformMatrix, $"VulkanCommandTranslator: uniform '{name}' is not packable yet.");
                        break;
                    }
                    _objectUniformsDirty = true;
                    _descriptorDirty = true;
                    break;
                }
                case CommandOpcode.SetUniformTexture:
                {
                    string? name = (string?)objects[ReadU16(stream, ref pos)];
                    GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    if (name != null && texture != null)
                        _textures[name] = texture;
                    _descriptorDirty = true;
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
                    DrawIndexed(vkCmd, vao, topology, indexCount, startIndex, baseVertex, index32Bit);
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
                    DrawIndexedInstanced(vkCmd, vao, topology, indexCount, instanceCount, startIndex, baseVertex, index32Bit);
                    break;
                }
                case CommandOpcode.DrawArrays:
                {
                    var vao = (GraphicsVertexArray?)objects[ReadU16(stream, ref pos)];
                    Topology topology = (Topology)ReadU8(stream, ref pos);
                    int first = ReadI32(stream, ref pos);
                    uint count = ReadU32(stream, ref pos);
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DrawArrays(vkCmd, vao, topology, first, count);
                    break;
                }
                case CommandOpcode.SetComputeShader:
                {
                    _currentComputeShader = objects[ReadU16(stream, ref pos)] as ShaderVariant;
                    if (_currentComputeShader?.Bytecode?.Format == ShaderBytecodeFormat.SpirV
                        && _currentComputeShader.Bytecode.HasCompute)
                    {
                        _device.GetOrCreateShaderLayout(_currentComputeShader);
                    }
                    _descriptorDirty = true;
                    break;
                }
                case CommandOpcode.DispatchCompute:
                {
                    uint groupCountX = ReadU32(stream, ref pos);
                    uint groupCountY = ReadU32(stream, ref pos);
                    uint groupCountZ = ReadU32(stream, ref pos);
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DispatchCompute(vkCmd, groupCountX, groupCountY, groupCountZ);
                    break;
                }
                case CommandOpcode.DispatchIndirect:
                {
                    var args = objects[ReadU16(stream, ref pos)] as GraphicsBuffer;
                    uint offset = ReadU32(stream, ref pos);
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    DispatchIndirect(vkCmd, args, offset);
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
                    DrawIndexedIndirect(vkCmd, vao, topology, args, offset, index32Bit);
                    break;
                }
                case CommandOpcode.ResourceBarrier:
                {
                    GpuBarrierKind kind = (GpuBarrierKind)ReadU8(stream, ref pos);
                    object? resource = objects[ReadU16(stream, ref pos)];
                    if (FrameDebugger.NoteLeafGpuOpAndShouldSkip(fdScope)) break;
                    RecordResourceBarrier(vkCmd, kind, resource);
                    break;
                }
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
                    if (buf.Handle != 0 && _device.Buffers.Remove(buf.Handle, out VkBufferResource? res))
                    {
                        _submissionTransientBuffers!.Add(res);
                        buf.Handle = 0;
                    }
                    break;
                }
                case CommandOpcode.UpdateBuffer:
                {
                    var buf = (GraphicsBuffer?)objects[ReadU16(stream, ref pos)];
                    uint dstOffset = ReadU32(stream, ref pos);
                    ReadOnlySpan<byte> blob = ReadBlob<byte>(stream, ref pos, store);
                    if (blob.Length == 0)
                        break;
                    if (buf != null && buf.Handle != 0 &&
                        _device.Buffers.TryGetValue(buf.Handle, out VkBufferResource? res))
                    {
                        // Only a complete overwrite can rename storage without copying its tail.
                        // An offset-zero dirty page is still a partial update. End the pass for
                        // partial transfers; the next draw resumes with attachment load semantics.
                        if (_inRenderPass && dstOffset == 0 && (ulong)blob.Length >= res.Size)
                        {
                            ReplaceBufferStorage(buf, res, blob);
                        }
                        else
                        {
                            EndRenderPassIfNeeded(vkCmd);
                            UpdateBuffer(vkCmd, buf, res, dstOffset, blob);
                        }
                    }
                    break;
                }
                case CommandOpcode.CreateTexture:
                {
                    var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                    uint handle = _device.AllocateHandle();
                    tex.Handle = handle;
                    _device.Images[handle] = new VkImageResource
                    {
                        EngineFormat = tex.ImageFormat,
                        Format = VulkanFormats.ToVkFormat(tex.ImageFormat),
                        Type = tex.Type,
                    };
                    break;
                }
                case CommandOpcode.DisposeTexture:
                {
                    var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                    if (tex.Handle != 0 && _device.Images.Remove(tex.Handle, out VkImageResource? res))
                    {
                        _submissionTransientImages!.Add(res);
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
                    AllocateTexture2D(vkCmd, tex, mip, w, h, data);
                    break;
                }
                case CommandOpcode.UpdateTexture:
                {
                    EndRenderPassIfNeeded(vkCmd);
                    var tex = (GraphicsTexture?)objects[ReadU16(stream, ref pos)];
                    int x = ReadI32(stream, ref pos);
                    int y = ReadI32(stream, ref pos);
                    uint w = ReadU32(stream, ref pos);
                    uint h = ReadU32(stream, ref pos);
                    int mip = ReadI32(stream, ref pos);
                    ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                    if (tex != null)
                        RecordTexture2DUpdate(vkCmd, tex, mip, x, y, w, h, data);
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
                    AllocateTexture3D(vkCmd, tex, mip, w, h, d, data);
                    break;
                }
                case CommandOpcode.UpdateTexture3D:
                {
                    EndRenderPassIfNeeded(vkCmd);
                    var tex = (GraphicsTexture?)objects[ReadU16(stream, ref pos)];
                    int mip = ReadI32(stream, ref pos);
                    int x = ReadI32(stream, ref pos);
                    int y = ReadI32(stream, ref pos);
                    int z = ReadI32(stream, ref pos);
                    uint w = ReadU32(stream, ref pos);
                    uint h = ReadU32(stream, ref pos);
                    uint d = ReadU32(stream, ref pos);
                    ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                    if (tex != null)
                        RecordTexture3DUpdate(vkCmd, tex, mip, x, y, z, w, h, d, data);
                    break;
                }
                case CommandOpcode.AllocateTextureCubeFace:
                {
                    var tex = (GraphicsTexture)objects[ReadU16(stream, ref pos)]!;
                    int face = ReadI32(stream, ref pos);
                    int mip = ReadI32(stream, ref pos);
                    uint size = ReadU32(stream, ref pos);
                    ReadOnlySpan<byte> data = ReadBlob<byte>(stream, ref pos, store);
                    AllocateTextureCubeFace(vkCmd, tex, face, mip, size, data);
                    break;
                }
                case CommandOpcode.GenerateMipmap:
                {
                    EndRenderPassIfNeeded(vkCmd);
                    GraphicsTexture? texture = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                    if (texture != null)
                        GenerateMipmaps(vkCmd, texture);
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
                    DisposeFramebuffer(framebuffer);
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
                case CommandOpcode.BeginSample:
                {
                    _ = objects[ReadU16(stream, ref pos)];
                    break;
                }
                case CommandOpcode.EndSample:
                    break;
                case CommandOpcode.BeginOcclusionQuery:
                {
                    ulong queryId = ReadU64(stream, ref pos);
                    _device.BeginOcclusionQueryRecord(vkCmd, _device.CurrentFrameSlot, queryId);
                    break;
                }
                case CommandOpcode.EndOcclusionQuery:
                {
                    ulong queryId = ReadU64(stream, ref pos);
                    _device.EndOcclusionQueryRecord(vkCmd, _device.CurrentFrameSlot, queryId);
                    break;
                }
                default:
                    SkipOpcode(op, stream, ref pos, objects, store);
                    break;
            }
        }

        EndRenderPassIfNeeded(vkCmd);
    }

    private void DoClear(
        VkCommandBuffer vkCmd,
        ClearFlags flags,
        float r, float g, float b, float a,
        float depth,
        int stencil)
    {
        if (_pendingRenderTarget != null)
        {
            VkFramebufferResource resource = GetPendingFramebuffer();
            EnsureDrawRenderPass(vkCmd);
            ClearAttachment* clearAttachments = stackalloc ClearAttachment[resource.ColorFormats.Count + (resource.DepthFormat == Format.Undefined ? 0 : 1)];
            int clearCount = 0;
            if ((flags & ClearFlags.Color) != 0)
            {
                for (int i = 0; i < resource.ColorFormats.Count; i++)
                {
                    clearAttachments[clearCount] = new ClearAttachment
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = checked((uint)i),
                        ClearValue = new ClearValue { Color = new ClearColorValue(r, g, b, a) },
                    };
                    clearCount++;
                }
            }
            ImageAspectFlags depthAspects = 0;
            if ((flags & ClearFlags.Depth) != 0)
                depthAspects |= ImageAspectFlags.DepthBit;
            if ((flags & ClearFlags.Stencil) != 0
                && resource.DepthFormat is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint)
                depthAspects |= ImageAspectFlags.StencilBit;
            if (depthAspects != 0 && resource.DepthFormat != Format.Undefined)
            {
                clearAttachments[clearCount] = new ClearAttachment
                {
                    AspectMask = depthAspects,
                    ClearValue = new ClearValue
                    {
                        DepthStencil = new ClearDepthStencilValue(depth, checked((uint)stencil)),
                    },
                };
                clearCount++;
            }
            if (clearCount > 0)
            {
                var clearRect = new ClearRect
                {
                    Rect = new Rect2D(new Offset2D(0, 0), new Extent2D(resource.Width, resource.Height)),
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                };
                _device.Vk.CmdClearAttachments(vkCmd, checked((uint)clearCount), clearAttachments, 1, &clearRect);
            }
            return;
        }
        if (!_device.HasSwapchain)
        {
            if ((flags & ClearFlags.Color) == 0)
                return;
            EnsureDrawRenderPass(vkCmd);
            var attachment = new ClearAttachment
            {
                AspectMask = ImageAspectFlags.ColorBit,
                ColorAttachment = 0,
                ClearValue = new ClearValue { Color = new ClearColorValue(r, g, b, a) },
            };
            var rect = new ClearRect
            {
                Rect = new Rect2D(new Offset2D(0, 0), _device.CurrentRenderExtent),
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            _device.Vk.CmdClearAttachments(vkCmd, 1, &attachment, 1, &rect);
            return;
        }

        // The swapchain has only color. A UI stencil clear must not discard the scene.
        if (ShouldClearSwapchainColor(flags))
            EnsureSwapchainRenderPass(vkCmd, flags, r, g, b, a, depth, stencil);
    }

    internal static bool ShouldClearSwapchainColor(ClearFlags flags) => (flags & ClearFlags.Color) != 0;

    // Graphics emits these operations in a dedicated synchronous command buffer. The helper's
    // transfer therefore follows earlier queue work, waits for completion, and restores the image
    // layout before this otherwise-empty buffer is submitted.
    private void ReadTextureData(GraphicsTexture? texture, int mip, byte[]? destination)
    {
        byte[] source = ReadTexture2D(texture, mip);
        if (destination == null)
            throw new InvalidOperationException("Vulkan texture readback requires a destination buffer.");
        if (destination.Length < source.Length)
            throw new ArgumentException("Vulkan texture readback destination is smaller than the requested mip level.", nameof(destination));
        source.AsSpan().CopyTo(destination);
    }

    private void ReadTextureData(GraphicsTexture? texture, int mip, nint destination)
    {
        if (destination == 0)
            throw new ArgumentNullException(nameof(destination));
        byte[] source = ReadTexture2D(texture, mip);
        fixed (byte* sourcePtr = source)
            System.Buffer.MemoryCopy(sourcePtr, (void*)destination, source.Length, source.Length);
    }

    private byte[] ReadTexture2D(GraphicsTexture? texture, int mip)
    {
        if (texture == null)
            throw new InvalidOperationException("Vulkan texture readback requires a texture.");
        if (texture.Type != TextureType.Texture2D)
            throw new NotSupportedException("Vulkan GetTextureData only supports Texture2D resources.");
        if (mip < 0)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (!_device.Images.TryGetValue(texture.Handle, out VkImageResource? resource))
            throw new InvalidOperationException("Vulkan texture readback references a texture that has not been created.");
        return _device.ReadTexture2D(resource, (uint)mip, VulkanFormats.BytesPerPixel(texture.ImageFormat));
    }

    private void ReadTextureCubeFaceData(GraphicsTexture? texture, int face, int mip, byte[]? destination)
    {
        if (texture == null)
            throw new InvalidOperationException("Vulkan cubemap readback requires a texture.");
        if (texture.Type != TextureType.TextureCubeMap)
            throw new NotSupportedException("Vulkan GetTextureCubeFaceData only supports cubemap resources.");
        if (face < 0 || mip < 0)
            throw new ArgumentOutOfRangeException(face < 0 ? nameof(face) : nameof(mip));
        if (destination == null)
            throw new InvalidOperationException("Vulkan cubemap readback requires a destination buffer.");
        if (!_device.Images.TryGetValue(texture.Handle, out VkImageResource? resource))
            throw new InvalidOperationException("Vulkan cubemap readback references a texture that has not been created.");

        byte[] source = _device.ReadTextureCubeFace(resource, (uint)face, (uint)mip,
            VulkanFormats.BytesPerPixel(texture.ImageFormat));
        if (destination.Length < source.Length)
            throw new ArgumentException("Vulkan cubemap readback destination is smaller than the requested face mip level.", nameof(destination));
        source.AsSpan().CopyTo(destination);
    }

    private void DoBlit(
        VkCommandBuffer vkCmd,
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
        EndRenderPassIfNeeded(vkCmd);
        if (mask == 0)
            return;

        VkFramebufferResource destination = GetFramebuffer(_pendingRenderTarget, "draw");
        if (_pendingReadTarget == null)
        {
            if (mask != ClearFlags.Color)
                throw new NotSupportedException("Vulkan default framebuffer blits support color-only masks.");
            DoDefaultColorBlit(vkCmd, destination, srcX, srcY, srcWidth, srcHeight, dstX, dstY, dstWidth, dstHeight, filter);
            return;
        }
        VkFramebufferResource source = GetFramebuffer(_pendingReadTarget, "read");
        if (mask == ClearFlags.Depth)
        {
            DoDepthBlit(vkCmd, source, destination, srcX, srcY, srcWidth, srcHeight, dstX, dstY, dstWidth, dstHeight, filter);
            return;
        }
        if (mask != ClearFlags.Color)
            throw new NotSupportedException("Vulkan framebuffer blits currently support color-only or depth-only masks.");

        if (source.ColorHandles.Length != 1 || destination.ColorHandles.Length != 1)
            throw new NotSupportedException("Vulkan framebuffer blits currently require one color attachment on each framebuffer.");
        ValidateBlitRect(srcX, srcY, srcWidth, srcHeight, source.Width, source.Height, "source");
        ValidateBlitRect(dstX, dstY, dstWidth, dstHeight, destination.Width, destination.Height, "destination");

        VkImageResource sourceImage = _device.Images[source.ColorHandles[0]];
        VkImageResource destinationImage = _device.Images[destination.ColorHandles[0]];
        uint sourceMip = source.ColorMipLevels[0];
        uint destinationMip = destination.ColorMipLevels[0];
        uint sourceLayer = source.ColorArrayLayers[0];
        uint destinationLayer = destination.ColorArrayLayers[0];
        if (sourceImage.Image.Handle == destinationImage.Image.Handle &&
            sourceMip == destinationMip && sourceLayer == destinationLayer)
            throw new InvalidOperationException("Vulkan framebuffer blit source and destination subresources must differ.");
        if (sourceImage.Layout != ImageLayout.ShaderReadOnlyOptimal ||
            destinationImage.Layout != ImageLayout.ShaderReadOnlyOptimal)
            throw new InvalidOperationException("Vulkan framebuffer blit attachments must be in shader-read layout before transfer.");

        ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[2];
        barriers[0] = CreateMipBarrier(
            sourceImage.Image,
            sourceMip,
            sourceLayer,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferSrcOptimal,
            AccessFlags.ShaderReadBit | AccessFlags.ColorAttachmentWriteBit,
            AccessFlags.TransferReadBit);
        barriers[1] = CreateMipBarrier(
            destinationImage.Image,
            destinationMip,
            destinationLayer,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferDstOptimal,
            AccessFlags.ShaderReadBit | AccessFlags.ColorAttachmentWriteBit,
            AccessFlags.TransferWriteBit);
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            2,
            barriers);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = sourceMip,
                BaseArrayLayer = sourceLayer,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = destinationMip,
                BaseArrayLayer = destinationLayer,
                LayerCount = 1,
            },
        };
        blit.SrcOffsets[0] = new Offset3D(srcX, srcY, 0);
        blit.SrcOffsets[1] = new Offset3D(srcWidth, srcHeight, 1);
        blit.DstOffsets[0] = new Offset3D(dstX, dstY, 0);
        blit.DstOffsets[1] = new Offset3D(dstWidth, dstHeight, 1);
        _device.Vk.CmdBlitImage(
            vkCmd,
            sourceImage.Image,
            ImageLayout.TransferSrcOptimal,
            destinationImage.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &blit,
            filter == BlitFilter.Linear ? Filter.Linear : Filter.Nearest);

        barriers[0] = CreateMipBarrier(
            sourceImage.Image,
            sourceMip,
            sourceLayer,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferReadBit,
            AccessFlags.ShaderReadBit);
        barriers[1] = CreateMipBarrier(
            destinationImage.Image,
            destinationMip,
            destinationLayer,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit);
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            2,
            barriers);
    }

    /// <summary>
    /// Resolve a multisample color image into a same-size single-sample image with
    /// <c>vkCmdResolveImage</c>. This is a transfer command, so like <see cref="DoBlit"/> it must
    /// run outside a render pass; the surrounding layout protocol matches DoBlit exactly — both
    /// images enter and leave in <c>ShaderReadOnlyOptimal</c>, which is the layout every custom
    /// framebuffer render pass declares as its initial and final layout.
    /// </summary>
    private void DoResolveTexture(VkCommandBuffer vkCmd, GraphicsTexture? source, GraphicsTexture? destination)
    {
        EndRenderPassIfNeeded(vkCmd);
        if (source == null || destination == null)
            throw new InvalidOperationException("Vulkan ResolveTexture requires both a source and a destination texture.");
        if (source.Handle == 0 || !_device.Images.TryGetValue(source.Handle, out VkImageResource? sourceImage) ||
            sourceImage.Image.Handle == 0)
            throw new InvalidOperationException("Vulkan ResolveTexture source is not available.");
        if (destination.Handle == 0 || !_device.Images.TryGetValue(destination.Handle, out VkImageResource? destinationImage) ||
            destinationImage.Image.Handle == 0)
            throw new InvalidOperationException("Vulkan ResolveTexture destination is not available.");
        if (!sourceImage.IsMultisampled)
            throw new InvalidOperationException("Vulkan ResolveTexture source must be a multisample image.");
        if (destinationImage.IsMultisampled)
            throw new InvalidOperationException("Vulkan ResolveTexture destination must be single-sample.");
        if (sourceImage.Width != destinationImage.Width || sourceImage.Height != destinationImage.Height)
            throw new InvalidOperationException("Vulkan ResolveTexture requires matching source and destination extents.");
        if (sourceImage.Format != destinationImage.Format)
            throw new InvalidOperationException("Vulkan ResolveTexture requires matching source and destination formats.");
        if (sourceImage.Layout != ImageLayout.ShaderReadOnlyOptimal ||
            destinationImage.Layout != ImageLayout.ShaderReadOnlyOptimal)
            throw new InvalidOperationException("Vulkan ResolveTexture images must be in shader-read layout before transfer.");

        ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[2];
        barriers[0] = CreateMipBarrier(
            sourceImage.Image,
            0,
            0,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferSrcOptimal,
            AccessFlags.ShaderReadBit | AccessFlags.ColorAttachmentWriteBit,
            AccessFlags.TransferReadBit);
        barriers[1] = CreateMipBarrier(
            destinationImage.Image,
            0,
            0,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferDstOptimal,
            AccessFlags.ShaderReadBit | AccessFlags.ColorAttachmentWriteBit,
            AccessFlags.TransferWriteBit);
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            2,
            barriers);

        var region = new ImageResolve
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            Extent = new Extent3D(sourceImage.Width, sourceImage.Height, 1),
        };
        _device.Vk.CmdResolveImage(
            vkCmd,
            sourceImage.Image,
            ImageLayout.TransferSrcOptimal,
            destinationImage.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &region);

        barriers[0] = CreateMipBarrier(
            sourceImage.Image,
            0,
            0,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferReadBit,
            AccessFlags.ShaderReadBit);
        barriers[1] = CreateMipBarrier(
            destinationImage.Image,
            0,
            0,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit);
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            2,
            barriers);
    }

    private void DoDefaultColorBlit(VkCommandBuffer vkCmd, VkFramebufferResource destination,
        int srcX, int srcY, int srcWidth, int srcHeight, int dstX, int dstY, int dstWidth, int dstHeight, BlitFilter filter)
    {
        if (destination.ColorHandles.Length != 1)
            throw new NotSupportedException("Vulkan default framebuffer blits require one color destination attachment.");
        Extent2D extent = _device.CurrentRenderExtent;
        srcX = Math.Clamp(srcX, 0, checked((int)extent.Width));
        srcY = Math.Clamp(srcY, 0, checked((int)extent.Height));
        srcWidth = Math.Clamp(srcWidth, 0, checked((int)extent.Width));
        srcHeight = Math.Clamp(srcHeight, 0, checked((int)extent.Height));
        ValidateBlitRect(srcX, srcY, srcWidth, srcHeight, extent.Width, extent.Height, "source");
        ValidateBlitRect(dstX, dstY, dstWidth, dstHeight, destination.Width, destination.Height, "destination");
        VkImageResource target = _device.Images[destination.ColorHandles[0]];
        uint mip = destination.ColorMipLevels[0];
        uint layer = destination.ColorArrayLayers[0];
        ImageLayout sourceLayout = _device.HasSwapchain ? ImageLayout.PresentSrcKhr : ImageLayout.ColorAttachmentOptimal;
        ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[2];
        barriers[0] = CreateMipBarrier(_device.CurrentColorImage, 0, 0, sourceLayout, ImageLayout.TransferSrcOptimal, AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        barriers[1] = CreateMipBarrier(target.Image, mip, layer, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        _device.Vk.CmdPipelineBarrier(vkCmd, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 2, barriers);
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
            DstSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = mip, BaseArrayLayer = layer, LayerCount = 1 },
        };
        blit.SrcOffsets[0] = new Offset3D(srcX, srcY, 0); blit.SrcOffsets[1] = new Offset3D(srcWidth, srcHeight, 1);
        blit.DstOffsets[0] = new Offset3D(dstX, dstY, 0); blit.DstOffsets[1] = new Offset3D(dstWidth, dstHeight, 1);
        _device.Vk.CmdBlitImage(vkCmd, _device.CurrentColorImage, ImageLayout.TransferSrcOptimal, target.Image, ImageLayout.TransferDstOptimal, 1, &blit, filter == BlitFilter.Linear ? Filter.Linear : Filter.Nearest);
        barriers[0] = CreateMipBarrier(_device.CurrentColorImage, 0, 0, ImageLayout.TransferSrcOptimal, sourceLayout, AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentWriteBit);
        barriers[1] = CreateMipBarrier(target.Image, mip, layer, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        _device.Vk.CmdPipelineBarrier(vkCmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit, 0, 0, null, 0, null, 2, barriers);
    }

    private void DoDepthBlit(
        VkCommandBuffer vkCmd,
        VkFramebufferResource source,
        VkFramebufferResource destination,
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
            throw new NotSupportedException("Vulkan depth framebuffer blits require nearest filtering.");
        if (source.DepthHandle == 0 || destination.DepthHandle == 0)
            throw new InvalidOperationException("Vulkan depth framebuffer blits require depth attachments on both framebuffers.");
        if (source.DepthFormat != destination.DepthFormat)
            throw new NotSupportedException("Vulkan depth framebuffer blits require matching depth formats.");
        ValidateBlitRect(srcX, srcY, srcWidth, srcHeight, source.Width, source.Height, "source");
        ValidateBlitRect(dstX, dstY, dstWidth, dstHeight, destination.Width, destination.Height, "destination");

        VkImageResource sourceImage = _device.Images[source.DepthHandle];
        VkImageResource destinationImage = _device.Images[destination.DepthHandle];
        if (sourceImage.Image.Handle == destinationImage.Image.Handle)
            throw new InvalidOperationException("Vulkan depth framebuffer blit source and destination textures must differ.");

        ImageLayout sourceLayout = sourceImage.Layout;
        ImageLayout destinationLayout = destinationImage.Layout;
        GetDepthLayoutSynchronization(sourceLayout, out AccessFlags sourceAccess, out PipelineStageFlags sourceStage);
        GetDepthLayoutSynchronization(destinationLayout, out AccessFlags destinationAccess, out PipelineStageFlags destinationStage);

        ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[2];
        barriers[0] = CreateMipBarrier(
            sourceImage.Image,
            0,
            0,
            sourceLayout,
            ImageLayout.TransferSrcOptimal,
            sourceAccess,
            AccessFlags.TransferReadBit,
            ImageAspectFlags.DepthBit);
        barriers[1] = CreateMipBarrier(
            destinationImage.Image,
            0,
            0,
            destinationLayout,
            ImageLayout.TransferDstOptimal,
            destinationAccess,
            AccessFlags.TransferWriteBit,
            ImageAspectFlags.DepthBit);
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            sourceStage | destinationStage,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            2,
            barriers);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.DepthBit,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.DepthBit,
                LayerCount = 1,
            },
        };
        blit.SrcOffsets[0] = new Offset3D(srcX, srcY, 0);
        blit.SrcOffsets[1] = new Offset3D(srcWidth, srcHeight, 1);
        blit.DstOffsets[0] = new Offset3D(dstX, dstY, 0);
        blit.DstOffsets[1] = new Offset3D(dstWidth, dstHeight, 1);
        _device.Vk.CmdBlitImage(
            vkCmd,
            sourceImage.Image,
            ImageLayout.TransferSrcOptimal,
            destinationImage.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &blit,
            Filter.Nearest);

        barriers[0] = CreateMipBarrier(
            sourceImage.Image,
            0,
            0,
            ImageLayout.TransferSrcOptimal,
            sourceLayout,
            AccessFlags.TransferReadBit,
            sourceAccess,
            ImageAspectFlags.DepthBit);
        barriers[1] = CreateMipBarrier(
            destinationImage.Image,
            0,
            0,
            ImageLayout.TransferDstOptimal,
            destinationLayout,
            AccessFlags.TransferWriteBit,
            destinationAccess,
            ImageAspectFlags.DepthBit);
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TransferBit,
            sourceStage | destinationStage,
            0,
            0,
            null,
            0,
            null,
            2,
            barriers);
    }

    private static void GetDepthLayoutSynchronization(
        ImageLayout layout,
        out AccessFlags access,
        out PipelineStageFlags stage)
    {
        switch (layout)
        {
            case ImageLayout.DepthStencilAttachmentOptimal:
                access = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
                stage = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
                return;
            case ImageLayout.ShaderReadOnlyOptimal:
                access = AccessFlags.ShaderReadBit;
                stage = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit;
                return;
            default:
                throw new InvalidOperationException($"Vulkan depth framebuffer blit does not support {layout} attachments.");
        }
    }

    private VkFramebufferResource GetFramebuffer(GraphicsFrameBuffer? framebuffer, string role)
    {
        if (framebuffer == null || framebuffer.Handle == 0 ||
            !_device.Framebuffers.TryGetValue(framebuffer.Handle, out VkFramebufferResource? resource))
            throw new NotSupportedException($"Vulkan framebuffer blits currently require a custom {role} framebuffer.");
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
            x0 == x1 || y0 == y1)
            throw new ArgumentOutOfRangeException(role, $"Vulkan {role} blit rectangle must be non-empty and within the framebuffer extent.");
    }

    private void EnsureSwapchainRenderPass(
        VkCommandBuffer vkCmd,
        ClearFlags flags,
        float r, float g, float b, float a,
        float depth,
        int stencil)
    {
        EndRenderPassIfNeeded(vkCmd);

        ClearValue clearValue = default;
        clearValue.Color = new ClearColorValue(r, g, b, a);
        // Depth attachment is not part of the swapchain render pass yet.
        _ = flags;
        _ = depth;
        _ = stencil;

        Extent2D extent = _device.SwapchainExtent;
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _device.SwapchainRenderPass,
            Framebuffer = _device.CurrentSwapchainFramebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            ClearValueCount = 1,
            PClearValues = &clearValue,
        };
        _device.Vk.CmdBeginRenderPass(vkCmd, &begin, SubpassContents.Inline);
        _inRenderPass = true;

        // Clear was applied via load-op; end so subsequent resource ops aren't inside the pass.
        EndRenderPassIfNeeded(vkCmd);
    }

    private void EndRenderPassIfNeeded(VkCommandBuffer vkCmd)
    {
        if (!_inRenderPass)
            return;
        _device.Vk.CmdEndRenderPass(vkCmd);
        _inRenderPass = false;
        // Custom-render-pass attachments end the pass in ShaderReadOnlyOptimal (their FinalLayout),
        // so update the CPU-side layout tracking to match. This lets the next framebuffer that owns
        // the same texture (the shadow atlas being re-rendered next frame, or a color/depth target
        // being sampled this frame) see the correct incoming layout.
        if (_pendingRenderTarget != null &&
            _pendingRenderTarget.Handle != 0 &&
            _device.Framebuffers.TryGetValue(_pendingRenderTarget.Handle, out VkFramebufferResource? resource))
        {
            for (int i = 0; i < resource.ColorHandles.Length; i++)
            {
                if (_device.Images.TryGetValue(resource.ColorHandles[i], out VkImageResource? image))
                    image.Layout = ImageLayout.ShaderReadOnlyOptimal;
            }
            if (resource.DepthHandle != 0 && _device.Images.TryGetValue(resource.DepthHandle, out VkImageResource? depthImage))
                depthImage.Layout = ImageLayout.ShaderReadOnlyOptimal;
        }
    }

    private void EnsureDrawRenderPass(VkCommandBuffer vkCmd)
    {
        if (_inRenderPass)
            return;
        RenderPass renderPass;
        Framebuffer framebuffer;
        Extent2D extent;
        if (_pendingRenderTarget != null)
        {
            if (_pendingRenderTarget.Handle == 0 ||
                !_device.Framebuffers.TryGetValue(_pendingRenderTarget.Handle, out VkFramebufferResource? resource))
                throw new InvalidOperationException("Vulkan custom framebuffer is not available.");
            renderPass = resource.RenderPass;
            framebuffer = resource.Framebuffer;
            extent = new Extent2D(resource.Width, resource.Height);
        }
        else
        {
            renderPass = _device.CurrentRenderPass;
            framebuffer = _device.CurrentFramebuffer;
            extent = _device.CurrentRenderExtent;
        }
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
        };
        _device.Vk.CmdBeginRenderPass(vkCmd, &begin, SubpassContents.Inline);
        _inRenderPass = true;

        // The graphics pipeline declares VK_DYNAMIC_STATE_VIEWPORT and VK_DYNAMIC_STATE_SCISSOR,
        // so the Vulkan spec requires both to be set before any draw. Some render paths (notably
        // the first UI pass) issue a draw without an explicit SetViewport/SetScissor command,
        // relying on the framebuffer extent as the implicit viewport. Drivers that strictly
        // validate dynamic state (Intel Arc) segfault on vkCmdDrawIndexed if either is unset.
        // Seed defaults from the render area so the first draw is always valid.
        if (!_viewportSet)
        {
            var defaultViewport = new Viewport { X = 0, Y = 0, Width = extent.Width, Height = extent.Height, MinDepth = 0f, MaxDepth = 1f };
            _device.Vk.CmdSetViewport(vkCmd, 0, 1, &defaultViewport);
            _viewportSet = true;
        }
        if (!_scissorSet)
        {
            var defaultScissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = extent };
            _device.Vk.CmdSetScissor(vkCmd, 0, 1, &defaultScissor);
            _scissorSet = true;
        }
    }

    private VkRenderTargetFormats GetCurrentTargetFormats()
    {
        if (_pendingRenderTarget == null)
            return new VkRenderTargetFormats(new VkColorAttachmentFormats(_device.CurrentColorFormat), Format.Undefined);
        VkFramebufferResource resource = GetPendingFramebuffer();
        return new VkRenderTargetFormats(resource.ColorFormats, resource.DepthFormat, resource.SampleCount);
    }

    /// <summary>
    /// Sample count of the currently bound render target. The swapchain is always single-sample,
    /// so an unbound target reports 1.
    /// </summary>
    private int GetCurrentSampleCount()
    {
        if (_pendingRenderTarget == null)
            return 1;
        return (int)GetPendingFramebuffer().SampleCount;
    }

    private VkFramebufferResource GetPendingFramebuffer()
    {
        if (_pendingRenderTarget == null || _pendingRenderTarget.Handle == 0 ||
            !_device.Framebuffers.TryGetValue(_pendingRenderTarget.Handle, out VkFramebufferResource? resource))
            throw new InvalidOperationException("Vulkan custom framebuffer is not available.");
        return resource;
    }

    private void CreateBuffer(GraphicsBuffer buf, bool dynamic, ReadOnlySpan<byte> data)
    {
        uint handle = _device.AllocateHandle();
        ulong size = data.Length > 0 ? (ulong)data.Length : Math.Max(1u, buf.SizeInBytes);

        BufferUsageFlags usage = GetBufferUsage(buf.OriginalType);

        MemoryPropertyFlags mem = dynamic
            ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            : MemoryPropertyFlags.DeviceLocalBit;

        _device.CreateBuffer(size, usage, mem, out VkBuffer buffer, out DeviceMemory memory);

        if (data.Length > 0)
        {
            if (dynamic)
            {
                void* mapped;
                VulkanGraphicsDevice.Check(
                    _device.Vk.MapMemory(_device.Device, memory, 0, size, 0, &mapped),
                    "vkMapMemory");
                try
                {
                    fixed (byte* src = data)
                        System.Buffer.MemoryCopy(src, mapped, data.Length, data.Length);
                }
                finally
                {
                    _device.Vk.UnmapMemory(_device.Device, memory);
                }
            }
            else
            {
                // Staging upload
                _device.CreateBuffer(
                    size,
                    BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out VkBuffer staging,
                    out DeviceMemory stagingMem);

                void* mapped;
                VulkanGraphicsDevice.Check(
                    _device.Vk.MapMemory(_device.Device, stagingMem, 0, size, 0, &mapped),
                    "vkMapMemory");
                try
                {
                    fixed (byte* src = data)
                        System.Buffer.MemoryCopy(src, mapped, data.Length, data.Length);
                }
                finally
                {
                    _device.Vk.UnmapMemory(_device.Device, stagingMem);
                }

                // One-shot copy on a transient command buffer would be ideal; for MVP map device-local
                // via host-visible fallback if available. Re-create as host-visible when staging copy
                // can't run mid-translate safely without nested submits.
                _device.Vk.DestroyBuffer(_device.Device, buffer, null);
                _device.Vk.FreeMemory(_device.Device, memory, null);
                buffer = staging;
                memory = stagingMem;
            }
        }

        buf.Handle = handle;
        buf.SizeInBytes = (uint)size;
        _device.Buffers[handle] = new VkBufferResource
        {
            Buffer = buffer,
            Memory = memory,
            Size = size,
            Type = buf.OriginalType,
            Dynamic = dynamic,
        };
    }

    private void CreateFramebuffer(GraphicsFrameBuffer framebuffer)
    {
        GraphicsFrameBuffer.Attachment[] attachments = framebuffer.Attachments;
        if (attachments.Length is < 1 or > 9)
            throw new NotSupportedException("Vulkan custom framebuffers support one through eight color attachments and one depth attachment.");

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
            throw new NotSupportedException("Vulkan custom framebuffers require up to eight color attachments, at most one depth attachment, and at least one attachment.");

        var attachmentViews = new ImageView[colorCount + depthCount];
        var attachmentImages = new ulong[colorCount + depthCount];
        var colorHandles = new uint[colorCount];
        var colorMipLevels = new uint[colorCount];
        var colorArrayLayers = new uint[colorCount];
        var colorFormats = new Format[colorCount];
        var descriptions = new AttachmentDescription[colorCount + depthCount];
        var colorReferences = new AttachmentReference[colorCount];
        uint depthHandle = 0;
        Format depthFormat = Format.Undefined;
        RenderPass renderPass = default;
        try
        {
            int colorIndex = 0;
            for (int i = 0; i < attachments.Length; i++)
            {
                GraphicsFrameBuffer.Attachment attachment = attachments[i];
                GraphicsTexture texture = attachment.Texture;
                if (texture.Handle == 0 || !_device.Images.TryGetValue(texture.Handle, out VkImageResource? image) ||
                    image.View.Handle == 0)
                    throw new InvalidOperationException("Vulkan framebuffer attachment is not ready.");
                bool isDepth = attachment.IsDepth;
                if (isDepth != VulkanFormats.IsDepth(image.EngineFormat))
                    throw new ArgumentException("Vulkan framebuffer attachment depth flag must match the texture format.");
                // Attachments leave every custom render pass in ShaderReadOnlyOptimal. Keeping the
                // same layout as the next pass's InitialLayout is required by Vulkan: declaring a
                // depth attachment as DepthStencilAttachmentOptimal here after the previous pass
                // returned it to ShaderReadOnlyOptimal is undefined and breaks on strict mobile
                // drivers. The subpass still transitions depth to DepthStencilAttachmentOptimal
                // while it is bound for drawing.
                const ImageLayout expectedLayout = ImageLayout.ShaderReadOnlyOptimal;
                if (image.Layout != expectedLayout)
                    throw new InvalidOperationException("Vulkan framebuffer attachment layout is not ready.");
                if (attachment.MipLevel < 0 || (uint)attachment.MipLevel >= image.MipLevels)
                    throw new ArgumentOutOfRangeException(nameof(attachment.MipLevel));
                if (isDepth && attachment.IsCubeFace)
                    throw new NotSupportedException("Vulkan depth cubemap framebuffer attachments are not supported yet.");

                uint arrayLayer = 0;
                if (!isDepth && attachment.IsCubeFace)
                {
                    if (image.Type != TextureType.TextureCubeMap || (uint)attachment.CubeFace >= 6)
                        throw new ArgumentException("Vulkan cubemap framebuffer attachment requires a valid face 0 through 5.");
                    arrayLayer = checked((uint)attachment.CubeFace);
                }
                else if (image.Type != TextureType.Texture2D)
                {
                    throw new NotSupportedException("Vulkan non-cubemap framebuffer attachments must be Texture2D resources.");
                }

                uint expectedWidth = Math.Max(1u, image.Width >> attachment.MipLevel);
                uint expectedHeight = Math.Max(1u, image.Height >> attachment.MipLevel);
                if (framebuffer.Width != expectedWidth || framebuffer.Height != expectedHeight)
                    throw new ArgumentException($"Vulkan framebuffer extent must match attachment mip extent {expectedWidth}x{expectedHeight}.");

                int nativeIndex = isDepth ? colorCount : colorIndex;
                attachmentImages[nativeIndex] = image.Image.Handle;
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image.Image,
                    ViewType = ImageViewType.Type2D,
                    Format = image.Format,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = VulkanFormats.AspectFor(image.EngineFormat),
                        BaseMipLevel = checked((uint)attachment.MipLevel),
                        LevelCount = 1,
                        BaseArrayLayer = arrayLayer,
                        LayerCount = 1,
                    },
                };
                VulkanGraphicsDevice.Check(
                    _device.Vk.CreateImageView(_device.Device, &viewInfo, null, out attachmentViews[nativeIndex]),
                    "vkCreateImageView(framebuffer attachment)");

                descriptions[nativeIndex] = new AttachmentDescription
                {
                    Format = image.Format,
                    Samples = VulkanFormats.ToSampleCount(image.SampleCount),
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = isDepth ? AttachmentLoadOp.Load : AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = expectedLayout,
                    // Color attachments return to ShaderReadOnlyOptimal (sampleable) after the pass;
                    // depth attachments also return to ShaderReadOnlyOptimal so the runtime shadow
                    // atlas (_ShadowAtlas) can be sampled by the forward pass without a manual
                    // layout barrier. The Vulkan render pass handles the layout transition.
                    FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
                if (isDepth)
                {
                    depthHandle = texture.Handle;
                    depthFormat = image.Format;
                }
                else
                {
                    colorHandles[colorIndex] = texture.Handle;
                    colorMipLevels[colorIndex] = checked((uint)attachment.MipLevel);
                    colorArrayLayers[colorIndex] = arrayLayer;
                    colorFormats[colorIndex] = image.Format;
                    colorReferences[colorIndex] = new AttachmentReference
                    {
                        Attachment = checked((uint)nativeIndex),
                        Layout = ImageLayout.ColorAttachmentOptimal,
                    };
                    colorIndex++;
                }
            }

            fixed (AttachmentDescription* descriptionPointer = descriptions)
            fixed (AttachmentReference* referencePointer = colorReferences)
            fixed (ImageView* viewPointer = attachmentViews)
            {
                AttachmentReference depthReference = new()
                {
                    Attachment = checked((uint)colorCount),
                    Layout = ImageLayout.DepthStencilAttachmentOptimal,
                };
                var subpass = new SubpassDescription
                {
                    PipelineBindPoint = PipelineBindPoint.Graphics,
                    ColorAttachmentCount = checked((uint)colorCount),
                    PColorAttachments = colorCount == 0 ? null : referencePointer,
                    PDepthStencilAttachment = depthCount == 0 ? null : &depthReference,
                };
                SubpassDependency* dependencies = stackalloc SubpassDependency[2];
                dependencies[0] = new SubpassDependency
                {
                    SrcSubpass = Vk.SubpassExternal,
                    DstSubpass = 0,
                    SrcStageMask = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
                    DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit |
                                   PipelineStageFlags.EarlyFragmentTestsBit |
                                   PipelineStageFlags.LateFragmentTestsBit,
                    SrcAccessMask = AccessFlags.ShaderReadBit,
                    // ColorAttachmentReadBit keeps load-op LOAD passes (alpha-blended UI over the
                    // swapchain) valid on tile-based implementations. Bit-identical to the
                    // device's swapchain and pipeline render passes — RenderPass compatibility
                    // compares each dependency's stage/access content, and any divergence makes
                    // pipeline draws in these passes driver-undefined (VUID-02684 family).
                    DstAccessMask = AccessFlags.ColorAttachmentReadBit |
                                    AccessFlags.ColorAttachmentWriteBit |
                                    AccessFlags.DepthStencilAttachmentWriteBit,
                    DependencyFlags = DependencyFlags.ByRegionBit,
                };
                dependencies[1] = new SubpassDependency
                {
                    SrcSubpass = 0,
                    DstSubpass = Vk.SubpassExternal,
                    SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit |
                                   PipelineStageFlags.LateFragmentTestsBit,
                    DstStageMask = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
                    SrcAccessMask = AccessFlags.ColorAttachmentWriteBit |
                                    AccessFlags.DepthStencilAttachmentWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    DependencyFlags = DependencyFlags.ByRegionBit,
                };
                var renderPassInfo = new RenderPassCreateInfo
                {
                    SType = StructureType.RenderPassCreateInfo,
                    AttachmentCount = checked((uint)(colorCount + depthCount)),
                    PAttachments = descriptionPointer,
                    SubpassCount = 1,
                    PSubpasses = &subpass,
                    DependencyCount = 2,
                    PDependencies = dependencies,
                };
                VulkanGraphicsDevice.Check(_device.Vk.CreateRenderPass(_device.Device, &renderPassInfo, null, out renderPass), "vkCreateRenderPass(custom)");

                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = checked((uint)(colorCount + depthCount)),
                    PAttachments = viewPointer,
                    Width = framebuffer.Width,
                    Height = framebuffer.Height,
                    Layers = 1,
                };
                VulkanGraphicsDevice.Check(_device.Vk.CreateFramebuffer(_device.Device, &framebufferInfo, null, out Framebuffer nativeFramebuffer), "vkCreateFramebuffer(custom)");
                uint handle = _device.AllocateHandle();
                framebuffer.Handle = handle;
                _device.Framebuffers[handle] = new VkFramebufferResource
                {
                    Owner = framebuffer,
                    Framebuffer = nativeFramebuffer,
                    RenderPass = renderPass,
                    Width = framebuffer.Width,
                    Height = framebuffer.Height,
                    ColorFormat = colorCount == 0 ? Format.Undefined : colorFormats[0],
                    ColorFormats = colorCount == 0 ? default : new VkColorAttachmentFormats(colorFormats),
                    DepthFormat = depthFormat,
                    ColorHandles = colorHandles,
                    ColorMipLevels = colorMipLevels,
                    ColorArrayLayers = colorArrayLayers,
                    AttachmentViews = attachmentViews,
                    AttachmentImages = attachmentImages,
                    DepthHandle = depthHandle,
                    SampleCount = framebuffer.SampleCount,
                };
            }
        }
        catch
        {
            if (renderPass.Handle != 0)
                _device.Vk.DestroyRenderPass(_device.Device, renderPass, null);
            for (int i = 0; i < attachmentViews.Length; i++)
            {
                if (attachmentViews[i].Handle != 0)
                    _device.Vk.DestroyImageView(_device.Device, attachmentViews[i], null);
            }
            throw;
        }
    }

    private void DisposeFramebuffer(GraphicsFrameBuffer framebuffer)
    {
        if (framebuffer.Handle == 0)
            return;
        if (_device.Framebuffers.Remove(framebuffer.Handle, out VkFramebufferResource? resource))
            _submissionTransientFramebuffers!.Add(resource);
        framebuffer.Handle = 0;
    }

    private void UpdateBuffer(
        VkCommandBuffer vkCmd,
        GraphicsBuffer buffer,
        VkBufferResource resource,
        uint dstOffset,
        ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        ulong requiredSize = (ulong)dstOffset + (ulong)data.Length;
        if (requiredSize > resource.Size)
        {
            if (dstOffset != 0)
                throw new ArgumentOutOfRangeException(nameof(dstOffset), "Vulkan partial buffer update exceeds the destination size.");

            ReplaceBufferStorage(buffer, resource, data);
            return;
        }

        // vkCmdUpdateBuffer requires four-byte alignment for both destination offset and
        // size. Engine vertex/index/uniform payloads satisfy that contract. Retain a staged
        // path for byte-granular callers without reintroducing a direct write to in-flight data.
        if ((dstOffset & 3) != 0 || (data.Length & 3) != 0)
        {
            RecordStagedBufferUpdate(vkCmd, resource, dstOffset, data);
            return;
        }

        RecordBufferWriteBarrier(vkCmd, resource, dstOffset, (ulong)data.Length);
        fixed (byte* source = data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int size = Math.Min(MaxInlineBufferUpdateSize, data.Length - offset);
                _device.Vk.CmdUpdateBuffer(
                    vkCmd,
                    resource.Buffer,
                    checked((ulong)dstOffset + (ulong)offset),
                    checked((ulong)size),
                    source + offset);
                offset += size;
            }
        }

        RecordBufferReadBarrier(vkCmd, resource, dstOffset, (ulong)data.Length);
    }

    private void RecordStagedBufferUpdate(
        VkCommandBuffer vkCmd,
        VkBufferResource resource,
        uint dstOffset,
        ReadOnlySpan<byte> data)
    {
        _device.CreateBuffer(
            checked((ulong)data.Length),
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out VkBuffer stagingBuffer,
            out DeviceMemory stagingMemory);
        var staging = new VkBufferResource
        {
            Buffer = stagingBuffer,
            Memory = stagingMemory,
            Size = checked((ulong)data.Length),
        };
        _submissionTransientBuffers!.Add(staging);

        void* mapped;
        VulkanGraphicsDevice.Check(
            _device.Vk.MapMemory(_device.Device, stagingMemory, 0, checked((ulong)data.Length), 0, &mapped),
            "vkMapMemory(buffer update)");
        try
        {
            fixed (byte* source = data)
                System.Buffer.MemoryCopy(source, mapped, data.Length, data.Length);
        }
        finally
        {
            _device.Vk.UnmapMemory(_device.Device, stagingMemory);
        }

        RecordBufferWriteBarrier(vkCmd, resource, dstOffset, (ulong)data.Length);
        var copy = new BufferCopy
        {
            SrcOffset = 0,
            DstOffset = dstOffset,
            Size = checked((ulong)data.Length),
        };
        _device.Vk.CmdCopyBuffer(vkCmd, stagingBuffer, resource.Buffer, 1, &copy);
        RecordBufferReadBarrier(vkCmd, resource, dstOffset, (ulong)data.Length);
    }

    private void RecordBufferWriteBarrier(
        VkCommandBuffer vkCmd,
        VkBufferResource resource,
        ulong offset,
        ulong size)
    {
        GetBufferReadAccess(resource.Type, out PipelineStageFlags sourceStages, out AccessFlags sourceAccess);
        sourceStages |= PipelineStageFlags.TransferBit;
        sourceAccess |= AccessFlags.TransferWriteBit;
        var barrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = sourceAccess,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = resource.Buffer,
            Offset = offset,
            Size = size,
        };
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            sourceStages,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            1,
            &barrier,
            0,
            null);
    }

    private void RecordBufferReadBarrier(
        VkCommandBuffer vkCmd,
        VkBufferResource resource,
        ulong offset,
        ulong size)
    {
        GetBufferReadAccess(resource.Type, out PipelineStageFlags destinationStages, out AccessFlags destinationAccess);
        var barrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = destinationAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = resource.Buffer,
            Offset = offset,
            Size = size,
        };
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TransferBit,
            destinationStages,
            0,
            0,
            null,
            1,
            &barrier,
            0,
            null);
    }

    private static void GetBufferReadAccess(
        BufferType type,
        out PipelineStageFlags stages,
        out AccessFlags access)
    {
        switch (type)
        {
            case BufferType.VertexBuffer:
                stages = PipelineStageFlags.VertexInputBit;
                access = AccessFlags.VertexAttributeReadBit;
                return;
            case BufferType.ElementsBuffer:
                stages = PipelineStageFlags.VertexInputBit;
                access = AccessFlags.IndexReadBit;
                return;
            case BufferType.UniformBuffer:
                stages = PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit;
                access = AccessFlags.UniformReadBit;
                return;
            case BufferType.StructuredBuffer:
                stages = PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit;
                access = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                return;
            case BufferType.IndirectBuffer:
                stages = PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.ComputeShaderBit;
                access = AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderWriteBit;
                return;
            default:
                stages = PipelineStageFlags.AllCommandsBit;
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                return;
        }
    }

    private void ReplaceBufferStorage(GraphicsBuffer buffer, VkBufferResource previous, ReadOnlySpan<byte> data)
    {
        ulong size = (ulong)data.Length;
        VkBuffer vkBuffer = default;
        DeviceMemory memory = default;
        try
        {
            _device.CreateBuffer(
                size,
                GetBufferUsage(buffer.OriginalType),
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out vkBuffer,
                out memory);

            void* mapped;
            VulkanGraphicsDevice.Check(
                _device.Vk.MapMemory(_device.Device, memory, 0, size, 0, &mapped),
                "vkMapMemory");
            try
            {
                fixed (byte* source = data)
                    System.Buffer.MemoryCopy(source, mapped, data.Length, data.Length);
            }
            finally
            {
                _device.Vk.UnmapMemory(_device.Device, memory);
            }

            var replacement = new VkBufferResource
            {
                Buffer = vkBuffer,
                Memory = memory,
                Size = size,
                Type = previous.Type,
                Dynamic = previous.Dynamic,
            };
            _device.Buffers[buffer.Handle] = replacement;
            buffer.SizeInBytes = checked((uint)size);
            _submissionTransientBuffers!.Add(previous);
            _descriptorDirty = true;

            vkBuffer = default;
            memory = default;
        }
        finally
        {
            if (vkBuffer.Handle != 0)
                _device.Vk.DestroyBuffer(_device.Device, vkBuffer, null);
            if (memory.Handle != 0)
                _device.Vk.FreeMemory(_device.Device, memory, null);
        }
    }

    private static BufferUsageFlags GetBufferUsage(BufferType type) => type switch
    {
        BufferType.VertexBuffer => BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
        BufferType.ElementsBuffer => BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
        BufferType.UniformBuffer => BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit,
        BufferType.StructuredBuffer => BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndirectBufferBit,
        BufferType.IndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
        _ => BufferUsageFlags.TransferDstBit,
    };

    private void AllocateTexture2D(VkCommandBuffer vkCmd, GraphicsTexture tex, int mip, uint width, uint height, ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Images.TryGetValue(tex.Handle, out VkImageResource? res))
            return;
        bool compressed = VulkanFormats.IsCompressed(tex.ImageFormat);
        if (mip != 0)
        {
            if (!compressed)
                return;
            if ((uint)mip >= res.MipLevels)
                throw new ArgumentOutOfRangeException(nameof(mip), mip, "Vulkan texture mip exceeds the allocated mip chain.");
            uint expectedWidth = Math.Max(1u, res.Width >> mip);
            uint expectedHeight = Math.Max(1u, res.Height >> mip);
            if (width != expectedWidth || height != expectedHeight)
                throw new ArgumentException($"Vulkan compressed texture mip {mip} expects {expectedWidth}x{expectedHeight}, got {width}x{height}.");
            if (data.Length == 0)
                return;
            _device.UploadCompressedTexture2D(
                res,
                data,
                width,
                height,
                VulkanFormats.CompressedBlockBytes(tex.ImageFormat),
                checked((uint)mip),
                ImageLayout.Undefined,
                setResourceLayout: false);
            uint available = Math.Max(res.AvailableMipLevels, checked((uint)mip + 1));
            if (available != res.AvailableMipLevels)
            {
                res.AvailableMipLevels = available;
                ReplaceSamplerIfAllocated(res);
            }
            return;
        }

        if (res.Image.Handle != 0)
            _submissionTransientImages!.Add(res.CloneForRetirement());

        Format format = VulkanFormats.ToVkFormat(tex.ImageFormat);
        uint sampleCount = tex.SampleCount < 1 ? 1 : tex.SampleCount;
        // A multisample image is a render target only: exactly one mip level, never uploaded to,
        // never sampled — it is resolved into a single-sample image first.
        uint mipLevels = sampleCount > 1 ? 1u : Math.Max(1u, tex.MipLevelsHint);
        if (sampleCount > 1)
        {
            if (compressed)
                throw new NotSupportedException("Vulkan block-compressed textures cannot be multisampled.");
            if (data.Length > 0)
                throw new NotSupportedException("Vulkan multisample textures cannot be uploaded to; resolve into a single-sample texture instead.");
        }
        ImageUsageFlags usage = VulkanFormats.IsDepth(tex.ImageFormat)
            ? ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit |
              ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit
            : compressed
                ? ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit
            : ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
              ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Samples = VulkanFormats.ToSampleCount(sampleCount),
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        VulkanGraphicsDevice.Check(
            _device.Vk.CreateImage(_device.Device, &imageInfo, null, out Image image),
            "vkCreateImage");
        _device.Vk.GetImageMemoryRequirements(_device.Device, image, out MemoryRequirements reqs);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = reqs.Size,
            MemoryTypeIndex = _device.FindMemoryType(reqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        _device.AllocateDeviceMemory(in alloc, out DeviceMemory memory, "vkAllocateMemory");
        VulkanGraphicsDevice.Check(
            _device.Vk.BindImageMemory(_device.Device, image, memory, 0),
            "vkBindImageMemory");

        ImageAspectFlags aspect = VulkanFormats.AspectFor(tex.ImageFormat);
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                LevelCount = mipLevels,
                LayerCount = 1,
            },
        };
        VulkanGraphicsDevice.Check(
            _device.Vk.CreateImageView(_device.Device, &viewInfo, null, out ImageView view),
            "vkCreateImageView");

        res.Image = image;
        res.Memory = memory;
        res.View = view;
        res.Format = format;
        res.Width = width;
        res.Height = height;
        res.MipLevels = mipLevels;
        res.SampleCount = sampleCount;
        res.Layout = ImageLayout.Undefined;
        res.AvailableMipLevels = 0;

        if (data.Length > 0)
        {
            if (compressed)
            {
                _device.UploadCompressedTexture2D(
                    res,
                    data,
                    width,
                    height,
                    VulkanFormats.CompressedBlockBytes(tex.ImageFormat));
            }
            else
            {
                _device.UploadTexture2D(
                    res,
                    data,
                    width,
                    height,
                    VulkanFormats.BytesPerPixel(tex.ImageFormat));
            }
            res.AvailableMipLevels = 1;
        }
        else
        {
            // Custom framebuffer render passes always enter from and return to shader-read layout.
            // This gives depth shadow atlases a legal, repeatable sample -> attachment -> sample
            // cycle across frames on strict Vulkan implementations.
            const ImageLayout targetLayout = ImageLayout.ShaderReadOnlyOptimal;
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.Undefined,
                NewLayout = targetLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = res.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspect,
                    LevelCount = mipLevels,
                    LayerCount = 1,
                },
                DstAccessMask = AccessFlags.ShaderReadBit,
            };
            _device.Vk.CmdPipelineBarrier(
                vkCmd,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &barrier);
            res.Layout = targetLayout;
            res.AvailableMipLevels = mipLevels;
        }
        res.Sampler = CreateSampler(res);
    }

    private void RecordTexture2DUpdate(
        VkCommandBuffer vkCmd,
        GraphicsTexture texture,
        int mip,
        int x,
        int y,
        uint width,
        uint height,
        ReadOnlySpan<byte> data)
    {
        if (texture.Handle == 0 || !_device.Images.TryGetValue(texture.Handle, out VkImageResource? resource))
            return;
        if (VulkanFormats.IsCompressed(texture.ImageFormat))
            throw new NotSupportedException("Partial updates are not supported for block-compressed Vulkan textures.");
        if (resource.Type != TextureType.Texture2D || VulkanFormats.IsDepth(resource.EngineFormat))
            throw new NotSupportedException("Vulkan UpdateTexture requires a non-depth 2D texture.");

        if (mip < 0 || (uint)mip >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (x < 0 || y < 0 || (uint)x + width > Math.Max(1u, resource.Width >> mip) ||
            (uint)y + height > Math.Max(1u, resource.Height >> mip))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Vulkan texture update exceeds the mip extent.");
        }
        int expectedBytes = checked((int)(width * height * (uint)VulkanFormats.BytesPerPixel(resource.EngineFormat)));
        if (data.Length != expectedBytes)
            throw new ArgumentException($"Vulkan texture update expected {expectedBytes} bytes, got {data.Length}.", nameof(data));
        // Layouts other than shader-read are legal here as long as they can be transitioned:
        // data textures created WITHOUT initial pixels (SkinnedMeshRenderer's bone-matrix
        // texture, LUTs, GPU data textures) start at Undefined, and rejecting them meant the
        // per-frame upload threw forever, the texture stayed zero-filled, and every skinned
        // vertex collapsed to its renderer origin.
        if (resource.Layout is not (ImageLayout.ShaderReadOnlyOptimal or ImageLayout.Undefined or ImageLayout.General))
            throw new InvalidOperationException($"Vulkan texture update requires shader-read, undefined or general layout, got {resource.Layout}.");
        bool fromShaderRead = resource.Layout == ImageLayout.ShaderReadOnlyOptimal;

        _device.CreateBuffer(
            checked((ulong)data.Length),
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out VkBuffer stagingBuffer,
            out DeviceMemory stagingMemory);
        var staging = new VkBufferResource
        {
            Buffer = stagingBuffer,
            Memory = stagingMemory,
            Size = checked((ulong)data.Length),
        };
        _submissionTransientBuffers!.Add(staging);

        void* mapped;
        VulkanGraphicsDevice.Check(
            _device.Vk.MapMemory(_device.Device, stagingMemory, 0, checked((ulong)data.Length), 0, &mapped),
            "vkMapMemory(texture update)");
        try
        {
            fixed (byte* source = data)
                System.Buffer.MemoryCopy(source, mapped, data.Length, data.Length);
        }
        finally
        {
            _device.Vk.UnmapMemory(_device.Device, stagingMemory);
        }

        ImageMemoryBarrier toTransfer = CreateMipBarrier(
            resource.Image,
            checked((uint)mip),
            0,
            fromShaderRead ? ImageLayout.ShaderReadOnlyOptimal : resource.Layout,
            ImageLayout.TransferDstOptimal,
            fromShaderRead ? AccessFlags.ShaderReadBit : AccessFlags.None,
            AccessFlags.TransferWriteBit);
        // Previous reads may come from the VERTEX stage (bone-matrix textures are sampled in the
        // vertex shader), not just fragment — the source stage must cover both or the transfer can
        // overwrite texels a vertex read is still using.
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            fromShaderRead
                ? PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit
                : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toTransfer);

        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = checked((uint)mip),
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };
        _device.Vk.CmdCopyBufferToImage(
            vkCmd,
            stagingBuffer,
            resource.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        ImageMemoryBarrier toShader = CreateMipBarrier(
            resource.Image,
            checked((uint)mip),
            0,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit);
        // Destination must include the VERTEX stage: skinned draws sample this texture in the
        // vertex shader (bone matrices), and a fragment-only availability op leaves those reads
        // unsynchronized with the upload (VUID 09558 family — undefined texel contents).
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toShader);

        if (mip == 0 && resource.AvailableMipLevels > 1)
        {
            resource.AvailableMipLevels = 1;
            ReplaceSamplerIfAllocated(resource);
        }

        resource.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>
    /// Executes <see cref="CommandOpcode.UpdateTexture3D"/> (TexSubImage3D) for Texture3D and
    /// Texture2DArray resources. Without this the per-layer uploads (voxel atlas SetLayerData)
    /// were decoded and discarded, leaving the image allocated but zero-filled — sampled black.
    /// </summary>
    private void RecordTexture3DUpdate(
        VkCommandBuffer vkCmd,
        GraphicsTexture texture,
        int mip,
        int x,
        int y,
        int z,
        uint width,
        uint height,
        uint depth,
        ReadOnlySpan<byte> data)
    {
        if (texture.Handle == 0 || !_device.Images.TryGetValue(texture.Handle, out VkImageResource? resource))
            return;
        if (VulkanFormats.IsCompressed(texture.ImageFormat))
            throw new NotSupportedException("Partial updates are not supported for block-compressed Vulkan textures.");
        if (resource.Type is not (TextureType.Texture3D or TextureType.Texture2DArray) || VulkanFormats.IsDepth(resource.EngineFormat))
            throw new NotSupportedException("Vulkan UpdateTexture3D requires a non-depth Texture3D or Texture2DArray resource.");

        if (mip < 0 || (uint)mip >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (x < 0 || y < 0 || z < 0 ||
            (uint)x + width > resource.Width ||
            (uint)y + height > resource.Height ||
            (uint)z + depth > resource.Depth)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Vulkan 3D texture update exceeds the image extent.");
        }
        int expectedBytes = checked((int)(width * height * depth * (uint)VulkanFormats.BytesPerPixel(resource.EngineFormat)));
        if (data.Length != expectedBytes)
            throw new ArgumentException($"Vulkan 3D texture update expected {expectedBytes} bytes, got {data.Length}.", nameof(data));
        if (resource.Layout is not (ImageLayout.ShaderReadOnlyOptimal or ImageLayout.Undefined or ImageLayout.General))
            throw new InvalidOperationException($"Vulkan 3D texture update requires shader-read, undefined or general layout, got {resource.Layout}.");
        bool fromShaderRead = resource.Layout == ImageLayout.ShaderReadOnlyOptimal;

        _device.CreateBuffer(
            checked((ulong)data.Length),
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out VkBuffer stagingBuffer,
            out DeviceMemory stagingMemory);
        var staging = new VkBufferResource
        {
            Buffer = stagingBuffer,
            Memory = stagingMemory,
            Size = checked((ulong)data.Length),
        };
        _submissionTransientBuffers!.Add(staging);

        void* mapped;
        VulkanGraphicsDevice.Check(
            _device.Vk.MapMemory(_device.Device, stagingMemory, 0, checked((ulong)data.Length), 0, &mapped),
            "vkMapMemory(3d texture update)");
        try
        {
            fixed (byte* source = data)
                System.Buffer.MemoryCopy(source, mapped, data.Length, data.Length);
        }
        finally
        {
            _device.Vk.UnmapMemory(_device.Device, stagingMemory);
        }

        // For 2D arrays z selects the base layer and one copy spans `depth` layers with an
        // extent depth of 1; for true 3D images z/depth are the volume offset and extent.
        bool isArray = resource.Type == TextureType.Texture2DArray;
        uint baseLayer = isArray ? checked((uint)z) : 0u;
        uint layerCount = isArray ? depth : 1u;

        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = checked((uint)mip),
            LevelCount = 1,
            BaseArrayLayer = baseLayer,
            LayerCount = layerCount,
        };

        ImageMemoryBarrier toTransfer = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = fromShaderRead ? ImageLayout.ShaderReadOnlyOptimal : resource.Layout,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = resource.Image,
            SrcAccessMask = fromShaderRead ? AccessFlags.ShaderReadBit : AccessFlags.None,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SubresourceRange = range,
        };
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            fromShaderRead
                ? PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit
                : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toTransfer);

        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = checked((uint)mip),
                BaseArrayLayer = baseLayer,
                LayerCount = layerCount,
            },
            ImageOffset = new Offset3D(x, y, isArray ? 0 : z),
            ImageExtent = new Extent3D(width, height, isArray ? 1u : depth),
        };
        _device.Vk.CmdCopyBufferToImage(
            vkCmd,
            stagingBuffer,
            resource.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        ImageMemoryBarrier toShader = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = resource.Image,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SubresourceRange = range,
        };
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toShader);

        resource.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    private void SetTextureWrap(GraphicsTexture texture, byte axis, TextureWrap wrap)
    {
        if (texture.Handle == 0 || !_device.Images.TryGetValue(texture.Handle, out VkImageResource? resource))
            return;

        bool changed;
        switch (axis)
        {
            case 0:
                changed = resource.WrapS != wrap;
                resource.WrapS = wrap;
                break;
            case 1:
                changed = resource.WrapT != wrap;
                resource.WrapT = wrap;
                break;
            case 2:
                changed = resource.WrapR != wrap;
                resource.WrapR = wrap;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis), axis, "Vulkan texture wrap axis must be 0, 1, or 2.");
        }

        if (changed)
            ReplaceSamplerIfAllocated(resource);
    }

    private void AllocateTexture3D(
        VkCommandBuffer vkCmd,
        GraphicsTexture tex,
        int mip,
        uint width,
        uint height,
        uint depth,
        ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Images.TryGetValue(tex.Handle, out VkImageResource? resource))
            return;
        if (mip != 0)
            return;
        if (tex.Type != TextureType.Texture3D && tex.Type != TextureType.Texture2DArray)
            throw new InvalidOperationException("Vulkan AllocateTexture3D requires a Texture3D or Texture2DArray resource.");

        if (resource.Image.Handle != 0)
            _submissionTransientImages!.Add(resource.CloneForRetirement());

        bool isArray = tex.Type == TextureType.Texture2DArray;
        uint arrayLayers = isArray ? depth : 1u;
        Format format = VulkanFormats.ToVkFormat(tex.ImageFormat);
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = isArray ? ImageType.Type2D : ImageType.Type3D,
            Format = format,
            Extent = isArray ? new Extent3D(width, height, 1) : new Extent3D(width, height, depth),
            MipLevels = 1,
            ArrayLayers = arrayLayers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        VulkanGraphicsDevice.Check(
            _device.Vk.CreateImage(_device.Device, &imageInfo, null, out Image image),
            "vkCreateImage");
        _device.Vk.GetImageMemoryRequirements(_device.Device, image, out MemoryRequirements requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _device.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        _device.AllocateDeviceMemory(in allocation, out DeviceMemory memory, "vkAllocateMemory");
        VulkanGraphicsDevice.Check(
            _device.Vk.BindImageMemory(_device.Device, image, memory, 0),
            "vkBindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = isArray ? ImageViewType.Type2DArray : ImageViewType.Type3D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = arrayLayers,
            },
        };
        VulkanGraphicsDevice.Check(
            _device.Vk.CreateImageView(_device.Device, &viewInfo, null, out ImageView view),
            "vkCreateImageView");

        resource.Image = image;
        resource.Memory = memory;
        resource.View = view;
        resource.Format = format;
        resource.Width = width;
        resource.Height = height;
        resource.Depth = depth;
        resource.Layout = ImageLayout.Undefined;
        resource.Sampler = CreateSampler(resource);

        if (data.Length > 0)
        {
            if (isArray)
            {
                _device.UploadTexture2DArray(
                    resource,
                    data,
                    width,
                    height,
                    depth,
                    VulkanFormats.BytesPerPixel(tex.ImageFormat));
            }
            else
            {
                _device.UploadTexture3D(
                    resource,
                    data,
                    width,
                    height,
                    depth,
                    VulkanFormats.BytesPerPixel(tex.ImageFormat));
            }
            return;
        }

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = resource.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = arrayLayers,
            },
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &barrier);
        resource.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    private void AllocateTextureCubeFace(
        VkCommandBuffer vkCmd,
        GraphicsTexture tex,
        int face,
        int mip,
        uint size,
        ReadOnlySpan<byte> data)
    {
        if (tex.Handle == 0 || !_device.Images.TryGetValue(tex.Handle, out VkImageResource? resource))
            return;
        if ((uint)face >= 6)
            throw new ArgumentOutOfRangeException(nameof(face), face, "Vulkan cubemap face must be 0 through 5.");
        if (mip < 0)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (tex.Type != TextureType.TextureCubeMap)
            throw new InvalidOperationException("Vulkan AllocateTextureCubeFace requires a cubemap resource.");

        if (resource.Image.Handle == 0 && mip != 0)
            throw new InvalidOperationException("Vulkan cubemap mip 0 must be allocated before higher mip levels.");

        if (resource.Image.Handle == 0 || (mip == 0 && (resource.Width != size || resource.Height != size)))
        {
            if (resource.Image.Handle != 0)
                _submissionTransientImages!.Add(resource.CloneForRetirement());

            Format format = VulkanFormats.ToVkFormat(tex.ImageFormat);
            uint mipLevels = CalculateMipLevels(size);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = ImageCreateFlags.ImageCreateCubeCompatibleBit,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(size, size, 1),
                MipLevels = mipLevels,
                ArrayLayers = 6,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            VulkanGraphicsDevice.Check(_device.Vk.CreateImage(_device.Device, &imageInfo, null, out Image image), "vkCreateImage");
            _device.Vk.GetImageMemoryRequirements(_device.Device, image, out MemoryRequirements requirements);
            var allocation = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = _device.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            _device.AllocateDeviceMemory(in allocation, out DeviceMemory memory, "vkAllocateMemory");
            VulkanGraphicsDevice.Check(_device.Vk.BindImageMemory(_device.Device, image, memory, 0), "vkBindImageMemory");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.TypeCube,
                Format = format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LevelCount = mipLevels,
                    LayerCount = 6,
                },
            };
            VulkanGraphicsDevice.Check(_device.Vk.CreateImageView(_device.Device, &viewInfo, null, out ImageView view), "vkCreateImageView");

            resource.Image = image;
            resource.Memory = memory;
            resource.View = view;
            resource.Format = format;
            resource.Width = size;
            resource.Height = size;
            resource.Depth = 1;
            resource.MipLevels = mipLevels;
            resource.Layout = ImageLayout.ShaderReadOnlyOptimal;
            resource.CubeInitializedFaces = 0;
            resource.CubeInitializedFacesByMip = new byte[mipLevels];
            resource.AvailableMipLevels = 0;
            _device.InitializeImageForSampling(image, mipLevels, 6);
            resource.Sampler = CreateSampler(resource);
        }

        if ((uint)mip >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip), mip, "Vulkan cubemap mip exceeds the allocated mip chain.");
        uint expectedSize = Math.Max(1u, resource.Width >> mip);
        if (size != expectedSize)
            throw new ArgumentException($"Vulkan cubemap mip {mip} expects size {expectedSize}, got {size}.", nameof(size));

        _ = vkCmd;
        byte faceBit = checked((byte)(1 << face));
        if (data.Length > 0)
        {
            _device.UploadTextureCubeFace(
                resource,
                data,
                size,
                checked((uint)face),
                checked((uint)mip),
                ImageLayout.ShaderReadOnlyOptimal,
                VulkanFormats.BytesPerPixel(tex.ImageFormat));
        }

        resource.CubeInitializedFacesByMip[mip] |= faceBit;
        resource.CubeInitializedFaces = resource.CubeInitializedFacesByMip[0];
        uint availableMipLevels = 0;
        while (availableMipLevels < resource.MipLevels &&
            resource.CubeInitializedFacesByMip[availableMipLevels] == 0b0011_1111)
        {
            availableMipLevels++;
        }
        uint oldMaxLod = resource.AvailableMipLevels > 0 ? resource.AvailableMipLevels - 1 : 0;
        uint newMaxLod = availableMipLevels > 0 ? availableMipLevels - 1 : 0;
        resource.AvailableMipLevels = availableMipLevels;
        if (newMaxLod != oldMaxLod)
            ReplaceSamplerIfAllocated(resource);
    }

    private void SetTextureFilters(GraphicsTexture texture, TextureMin min, TextureMag mag)
    {
        if (texture.Handle == 0 || !_device.Images.TryGetValue(texture.Handle, out VkImageResource? resource))
            return;
        if (resource.MinFilter == min && resource.MagFilter == mag)
            return;

        resource.MinFilter = min;
        resource.MagFilter = mag;
        ReplaceSamplerIfAllocated(resource);
    }

    private void GenerateMipmaps(VkCommandBuffer vkCmd, GraphicsTexture texture)
    {
        if (texture.Handle == 0 || !_device.Images.TryGetValue(texture.Handle, out VkImageResource? resource))
            return;
        if (resource.Type is not TextureType.Texture2D and not TextureType.TextureCubeMap)
            throw new NotSupportedException("Vulkan mip generation supports 2D textures and cubemaps.");
        bool isCube = resource.Type == TextureType.TextureCubeMap;
        if (isCube && resource.CubeInitializedFaces != 0b0011_1111)
            throw new InvalidOperationException("Vulkan cubemap mip generation requires all base-level faces.");
        if (!isCube && resource.MipLevels == 1)
            RebuildTexture2DMipChain(vkCmd, resource);
        if (resource.MipLevels <= 1)
            return;

        ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[2];
        for (uint mip = 1; mip < resource.MipLevels; mip++)
        {
            int sourceWidth = checked((int)Math.Max(1u, resource.Width >> checked((int)(mip - 1))));
            int sourceHeight = checked((int)Math.Max(1u, resource.Height >> checked((int)(mip - 1))));
            int destinationWidth = checked((int)Math.Max(1u, resource.Width >> checked((int)mip)));
            int destinationHeight = checked((int)Math.Max(1u, resource.Height >> checked((int)mip)));
            uint layerCount = isCube ? 6u : 1u;
            for (uint face = 0; face < layerCount; face++)
            {
                barriers[0] = CreateMipBarrier(
                    resource.Image,
                    mip - 1,
                    face,
                    ImageLayout.ShaderReadOnlyOptimal,
                    ImageLayout.TransferSrcOptimal,
                    AccessFlags.ShaderReadBit,
                    AccessFlags.TransferReadBit);
                barriers[1] = CreateMipBarrier(
                    resource.Image,
                    mip,
                    face,
                    ImageLayout.ShaderReadOnlyOptimal,
                    ImageLayout.TransferDstOptimal,
                    AccessFlags.ShaderReadBit,
                    AccessFlags.TransferWriteBit);
                _device.Vk.CmdPipelineBarrier(
                    vkCmd,
                    PipelineStageFlags.FragmentShaderBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    2,
                    barriers);

                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = mip - 1,
                        BaseArrayLayer = face,
                        LayerCount = 1,
                    },
                    DstSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = mip,
                        BaseArrayLayer = face,
                        LayerCount = 1,
                    },
                };
                blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
                blit.SrcOffsets[1] = new Offset3D(sourceWidth, sourceHeight, 1);
                blit.DstOffsets[0] = new Offset3D(0, 0, 0);
                blit.DstOffsets[1] = new Offset3D(destinationWidth, destinationHeight, 1);
                _device.Vk.CmdBlitImage(
                    vkCmd,
                    resource.Image,
                    ImageLayout.TransferSrcOptimal,
                    resource.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &blit,
                    Filter.Linear);

                barriers[0] = CreateMipBarrier(
                    resource.Image,
                    mip - 1,
                    face,
                    ImageLayout.TransferSrcOptimal,
                    ImageLayout.ShaderReadOnlyOptimal,
                    AccessFlags.TransferReadBit,
                    AccessFlags.ShaderReadBit);
                barriers[1] = CreateMipBarrier(
                    resource.Image,
                    mip,
                    face,
                    ImageLayout.TransferDstOptimal,
                    ImageLayout.ShaderReadOnlyOptimal,
                    AccessFlags.TransferWriteBit,
                    AccessFlags.ShaderReadBit);
                _device.Vk.CmdPipelineBarrier(
                    vkCmd,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    2,
                    barriers);
            }
            if (isCube)
                resource.CubeInitializedFacesByMip[mip] = 0b0011_1111;
        }

        uint oldMaxLod = resource.AvailableMipLevels > 0 ? resource.AvailableMipLevels - 1 : 0;
        resource.AvailableMipLevels = resource.MipLevels;
        uint newMaxLod = resource.MipLevels - 1;
        if (newMaxLod != oldMaxLod)
            ReplaceSamplerIfAllocated(resource);
    }

    private void RebuildTexture2DMipChain(VkCommandBuffer vkCmd, VkImageResource resource)
    {
        uint mipLevels = CalculateMipLevels(Math.Max(resource.Width, resource.Height));
        if (mipLevels <= 1)
            return;
        if (resource.Layout != ImageLayout.ShaderReadOnlyOptimal)
            throw new InvalidOperationException($"Vulkan 2D mip rebuild requires shader-read layout, got {resource.Layout}.");

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = resource.Format,
            Extent = new Extent3D(resource.Width, resource.Height, 1),
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                    ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        VulkanGraphicsDevice.Check(
            _device.Vk.CreateImage(_device.Device, &imageInfo, null, out Image replacementImage),
            "vkCreateImage(2D mip chain)");
        _device.Vk.GetImageMemoryRequirements(_device.Device, replacementImage, out MemoryRequirements requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _device.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        _device.AllocateDeviceMemory(in allocation, out DeviceMemory replacementMemory, "vkAllocateMemory(2D mip chain)");
        VulkanGraphicsDevice.Check(
            _device.Vk.BindImageMemory(_device.Device, replacementImage, replacementMemory, 0),
            "vkBindImageMemory(2D mip chain)");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = replacementImage,
            ViewType = ImageViewType.Type2D,
            Format = resource.Format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = mipLevels,
                LayerCount = 1,
            },
        };
        VulkanGraphicsDevice.Check(
            _device.Vk.CreateImageView(_device.Device, &viewInfo, null, out ImageView replacementView),
            "vkCreateImageView(2D mip chain)");

        var previous = new VkImageResource
        {
            Image = resource.Image,
            Memory = resource.Memory,
            View = resource.View,
        };
        _submissionTransientImages!.Add(previous);

        ImageMemoryBarrier initialize = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = replacementImage,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = mipLevels,
                LayerCount = 1,
            },
        };
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &initialize);

        ImageMemoryBarrier* copyBarriers = stackalloc ImageMemoryBarrier[2];
        copyBarriers[0] = CreateMipBarrier(
            resource.Image, 0, 0, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.ShaderReadBit, AccessFlags.TransferReadBit);
        copyBarriers[1] = CreateMipBarrier(
            replacementImage, 0, 0, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.FragmentShaderBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            2,
            copyBarriers);

        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
            DstSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
            Extent = new Extent3D(resource.Width, resource.Height, 1),
        };
        _device.Vk.CmdCopyImage(
            vkCmd,
            resource.Image,
            ImageLayout.TransferSrcOptimal,
            replacementImage,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        ImageMemoryBarrier ready = CreateMipBarrier(
            replacementImage, 0, 0, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        _device.Vk.CmdPipelineBarrier(
            vkCmd,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &ready);

        resource.Image = replacementImage;
        resource.Memory = replacementMemory;
        resource.View = replacementView;
        resource.MipLevels = mipLevels;
        resource.AvailableMipLevels = 1;
        RebuildFramebuffersForImage(previous.Image.Handle);
        _descriptorDirty = true;
    }

    private void RebuildFramebuffersForImage(ulong replacedImage)
    {
        // A VkFramebuffer owns immutable views of the original VkImage, not of our
        // mutable texture wrapper. Mip-chain promotion replaces that image; keeping
        // the framebuffer would render into freed memory after the retirement fence.
        _framebuffersToRebuild.Clear();
        foreach (var pair in _device.Framebuffers)
        {
            VkFramebufferResource resource = pair.Value;
            if (resource.Owner != null && Array.IndexOf(resource.AttachmentImages, replacedImage) >= 0)
                _framebuffersToRebuild.Add(resource.Owner);
        }
        for (int i = 0; i < _framebuffersToRebuild.Count; i++)
        {
            GraphicsFrameBuffer framebuffer = _framebuffersToRebuild[i];
            DisposeFramebuffer(framebuffer); // retire immutable views with the old image
            CreateFramebuffer(framebuffer);
        }
        _framebuffersToRebuild.Clear();
    }

    private static ImageMemoryBarrier CreateMipBarrier(
        Image image,
        uint mipLevel,
        uint arrayLayer,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags sourceAccess,
        AccessFlags destinationAccess,
        ImageAspectFlags aspectMask = ImageAspectFlags.ColorBit) => new()
    {
        SType = StructureType.ImageMemoryBarrier,
        OldLayout = oldLayout,
        NewLayout = newLayout,
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Image = image,
        SrcAccessMask = sourceAccess,
        DstAccessMask = destinationAccess,
        SubresourceRange = new ImageSubresourceRange
        {
            AspectMask = aspectMask,
            BaseMipLevel = mipLevel,
            LevelCount = 1,
            BaseArrayLayer = arrayLayer,
            LayerCount = 1,
        },
    };

    private void ReplaceSamplerIfAllocated(VkImageResource resource)
    {
        if (resource.Sampler.Handle == 0)
            return;

        Sampler previous = resource.Sampler;
        resource.Sampler = CreateSampler(resource);
        _submissionRetiredSamplers!.Add(previous);
        _descriptorDirty = true;
    }

    private Sampler CreateSampler(VkImageResource resource)
    {
        // Anisotropic filtering may ignore the nearest/linear filter choice. Preserve
        // point-sampled textures (depth, pixel art, neighborhood data) exactly.
        bool useAnisotropy = _device.MaxSamplerAnisotropy > 1f
            && VulkanFormats.ToFilter(resource.MinFilter) == Filter.Linear
            && VulkanFormats.ToFilter(resource.MagFilter) == Filter.Linear;
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = VulkanFormats.ToFilter(resource.MagFilter),
            MinFilter = VulkanFormats.ToFilter(resource.MinFilter),
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = VulkanFormats.ToAddressMode(resource.WrapS),
            AddressModeV = VulkanFormats.ToAddressMode(resource.WrapT),
            AddressModeW = VulkanFormats.ToAddressMode(resource.WrapR),
            MinLod = 0,
            MaxLod = resource.AvailableMipLevels > 0 ? resource.AvailableMipLevels - 1 : 0,
            AnisotropyEnable = useAnisotropy,
            MaxAnisotropy = useAnisotropy ? _device.MaxSamplerAnisotropy : 1f,
        };
        VulkanGraphicsDevice.Check(
            _device.Vk.CreateSampler(_device.Device, &samplerInfo, null, out Sampler sampler),
            "vkCreateSampler");
        return sampler;
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

    private void CreateVertexArray(GraphicsVertexArray vao)
    {
        uint handle = _device.AllocateHandle();
        vao.Handle = handle;
        _device.VertexArrays[handle] = new VkVertexArrayResource
        {
            VertexBuffer = vao.Vertices.Handle,
            IndexBuffer = vao.Indices?.Handle ?? 0,
            InstanceBuffer = vao.InstanceBuffer?.Handle ?? 0,
            Format = vao.Format,
            InstanceFormat = vao.InstanceFormat,
        };
    }

    private void DisposeVertexArray(GraphicsVertexArray vao)
    {
        if (vao.Handle == 0)
            return;

        _device.VertexArrays.Remove(vao.Handle);
        vao.Handle = 0;
    }

    private void DrawArrays(
        VkCommandBuffer vkCmd,
        GraphicsVertexArray? vao,
        Topology topology,
        int first,
        uint count)
    {
        if (_currentShader?.Bytecode is not { Format: ShaderBytecodeFormat.SpirV, HasGraphics: true } || vao == null || vao.Handle == 0)
        {
            WarnDrawNoPso();
            return;
        }
        if (!_device.VertexArrays.TryGetValue(vao.Handle, out VkVertexArrayResource? vertexArray) ||
            !_device.Buffers.TryGetValue(vertexArray.VertexBuffer, out VkBufferResource? vertexBuffer) ||
            vertexBuffer.Buffer.Handle == 0)
        {
            WarnOnce(CommandOpcode.DrawArrays, "Vulkan DrawArrays skipped: vertex-array resources are incomplete.");
            return;
        }
        if (vertexArray.InstanceFormat != null)
            throw new NotSupportedException("Vulkan DrawArrays does not consume an instance stream; use an instanced draw opcode.");

        var key = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit: false, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
        Pipeline pipeline = _device.GetOrCreateGraphicsPipeline(key, _currentShader, GetCurrentTargetFormats());
        VkShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentShader);
        EnsureDrawRenderPass(vkCmd);
        _device.Vk.CmdBindPipeline(vkCmd, PipelineBindPoint.Graphics, pipeline);
        BindDescriptorSetIfNeeded(vkCmd, layout, _currentShader, PipelineBindPoint.Graphics);
        VkBuffer buffer = vertexBuffer.Buffer;
        ulong offset = 0;
        _device.Vk.CmdBindVertexBuffers(vkCmd, 0, 1, &buffer, &offset);
        _device.Vk.CmdDraw(vkCmd, count, 1, checked((uint)first), 0);
        _ = layout;
    }

    private void DrawIndexed(
        VkCommandBuffer vkCmd,
        GraphicsVertexArray? vao,
        Topology topology,
        uint indexCount,
        uint startIndex,
        int baseVertex,
        bool index32Bit)
    {
        if (_currentShader?.Bytecode is not { Format: ShaderBytecodeFormat.SpirV, HasGraphics: true } || vao == null || vao.Handle == 0)
        {
            WarnDrawNoPso();
            return;
        }
        if (!_device.VertexArrays.TryGetValue(vao.Handle, out VkVertexArrayResource? vertexArray) ||
            !_device.Buffers.TryGetValue(vertexArray.VertexBuffer, out VkBufferResource? vertexBuffer) ||
            !_device.Buffers.TryGetValue(vertexArray.IndexBuffer, out VkBufferResource? indexBuffer) ||
            vertexBuffer.Buffer.Handle == 0 || indexBuffer.Buffer.Handle == 0)
        {
            WarnOnce(CommandOpcode.DrawIndexed, "Vulkan DrawIndexed skipped: vertex-array resources are incomplete.");
            return;
        }
        if (vertexArray.InstanceFormat != null)
            throw new NotSupportedException("Vulkan DrawIndexed does not consume an instance stream; use DrawIndexedInstanced.");

        var key = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
        Pipeline pipeline = _device.GetOrCreateGraphicsPipeline(key, _currentShader, GetCurrentTargetFormats());
        VkShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentShader);
        EnsureDrawRenderPass(vkCmd);
        _device.Vk.CmdBindPipeline(vkCmd, PipelineBindPoint.Graphics, pipeline);
        BindDescriptorSetIfNeeded(vkCmd, layout, _currentShader, PipelineBindPoint.Graphics);
        VkBuffer buffer = vertexBuffer.Buffer;
        ulong offset = 0;
        _device.Vk.CmdBindVertexBuffers(vkCmd, 0, 1, &buffer, &offset);
        _device.Vk.CmdBindIndexBuffer(vkCmd, indexBuffer.Buffer, 0, index32Bit ? IndexType.Uint32 : IndexType.Uint16);
        _device.Vk.CmdDrawIndexed(vkCmd, indexCount, 1, startIndex, baseVertex, 0);
        _ = layout;
    }

    private void DrawIndexedInstanced(
        VkCommandBuffer vkCmd,
        GraphicsVertexArray? vao,
        Topology topology,
        uint indexCount,
        uint instanceCount,
        uint startIndex,
        int baseVertex,
        bool index32Bit)
    {
        if (_currentShader?.Bytecode is not { Format: ShaderBytecodeFormat.SpirV, HasGraphics: true } || vao == null || vao.Handle == 0)
        {
            WarnDrawNoPso();
            return;
        }
        if (!_device.VertexArrays.TryGetValue(vao.Handle, out VkVertexArrayResource? vertexArray) ||
            vertexArray.InstanceFormat == null ||
            !_device.Buffers.TryGetValue(vertexArray.VertexBuffer, out VkBufferResource? vertexBuffer) ||
            !_device.Buffers.TryGetValue(vertexArray.InstanceBuffer, out VkBufferResource? instanceBuffer) ||
            !_device.Buffers.TryGetValue(vertexArray.IndexBuffer, out VkBufferResource? indexBuffer) ||
            vertexBuffer.Buffer.Handle == 0 || instanceBuffer.Buffer.Handle == 0 || indexBuffer.Buffer.Handle == 0)
        {
            WarnOnce(CommandOpcode.DrawIndexedInstanced, "Vulkan DrawIndexedInstanced skipped: vertex-array resources are incomplete.");
            return;
        }

        var key = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
        Pipeline pipeline = _device.GetOrCreateGraphicsPipeline(key, _currentShader, GetCurrentTargetFormats());
        VkShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentShader);
        EnsureDrawRenderPass(vkCmd);
        _device.Vk.CmdBindPipeline(vkCmd, PipelineBindPoint.Graphics, pipeline);
        BindDescriptorSetIfNeeded(vkCmd, layout, _currentShader, PipelineBindPoint.Graphics);
        VkBuffer* buffers = stackalloc VkBuffer[2] { vertexBuffer.Buffer, instanceBuffer.Buffer };
        ulong* offsets = stackalloc ulong[2];
        _device.Vk.CmdBindVertexBuffers(vkCmd, 0, 2, buffers, offsets);
        _device.Vk.CmdBindIndexBuffer(vkCmd, indexBuffer.Buffer, 0, index32Bit ? IndexType.Uint32 : IndexType.Uint16);
        _device.Vk.CmdDrawIndexed(vkCmd, indexCount, instanceCount, startIndex, baseVertex, 0);
        _ = layout;
    }

    private void DispatchCompute(VkCommandBuffer vkCmd, uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        if (!TryBindComputePipeline(vkCmd))
            return;
        _device.Vk.CmdDispatch(vkCmd, groupCountX, groupCountY, groupCountZ);
    }

    private void DispatchIndirect(VkCommandBuffer vkCmd, GraphicsBuffer? args, uint offset)
    {
        if (!TryBindComputePipeline(vkCmd))
            return;
        if (args == null || args.Handle == 0 ||
            !_device.Buffers.TryGetValue(args.Handle, out VkBufferResource? argsResource) ||
            argsResource.Buffer.Handle == 0)
        {
            WarnOnce(CommandOpcode.DispatchIndirect, "Vulkan DispatchIndirect skipped: args buffer is missing.");
            return;
        }

        _device.Vk.CmdDispatchIndirect(vkCmd, argsResource.Buffer, offset);
    }

    private void DrawIndexedIndirect(
        VkCommandBuffer vkCmd,
        GraphicsVertexArray? vao,
        Topology topology,
        GraphicsBuffer? args,
        uint offset,
        bool index32Bit)
    {
        if (_currentShader?.Bytecode is not { Format: ShaderBytecodeFormat.SpirV, HasGraphics: true } ||
            vao == null || vao.Handle == 0)
        {
            WarnDrawNoPso();
            return;
        }
        if (!_device.VertexArrays.TryGetValue(vao.Handle, out VkVertexArrayResource? vertexArray) ||
            !_device.Buffers.TryGetValue(vertexArray.VertexBuffer, out VkBufferResource? vertexBuffer) ||
            !_device.Buffers.TryGetValue(vertexArray.IndexBuffer, out VkBufferResource? indexBuffer) ||
            vertexBuffer.Buffer.Handle == 0 || indexBuffer.Buffer.Handle == 0)
        {
            WarnOnce(CommandOpcode.DrawIndexedIndirect, "Vulkan DrawIndexedIndirect skipped: vertex-array resources are incomplete.");
            return;
        }
        if (args == null || args.Handle == 0 ||
            !_device.Buffers.TryGetValue(args.Handle, out VkBufferResource? argsResource) ||
            argsResource.Buffer.Handle == 0)
        {
            WarnOnce(CommandOpcode.DrawIndexedIndirect, "Vulkan DrawIndexedIndirect skipped: args buffer is missing.");
            return;
        }

        var key = new GraphicsPipelineKey(_currentShader, vao.Handle, topology, in _currentRaster, index32Bit, vao.Format, vao.InstanceFormat, GetCurrentSampleCount());
        Pipeline pipeline = _device.GetOrCreateGraphicsPipeline(key, _currentShader, GetCurrentTargetFormats());
        VkShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentShader);
        EnsureDrawRenderPass(vkCmd);
        _device.Vk.CmdBindPipeline(vkCmd, PipelineBindPoint.Graphics, pipeline);
        BindDescriptorSetIfNeeded(vkCmd, layout, _currentShader, PipelineBindPoint.Graphics);
        VkBuffer vb = vertexBuffer.Buffer;
        ulong vbOffset = 0;
        _device.Vk.CmdBindVertexBuffers(vkCmd, 0, 1, &vb, &vbOffset);
        _device.Vk.CmdBindIndexBuffer(vkCmd, indexBuffer.Buffer, 0, index32Bit ? IndexType.Uint32 : IndexType.Uint16);
        const uint indexedIndirectStride = 20;
        _device.Vk.CmdDrawIndexedIndirect(vkCmd, argsResource.Buffer, offset, 1, indexedIndirectStride);
    }

    private bool TryBindComputePipeline(VkCommandBuffer vkCmd)
    {
        if (_currentComputeShader?.Bytecode is not { Format: ShaderBytecodeFormat.SpirV, HasCompute: true })
        {
            WarnOnce(CommandOpcode.DispatchCompute, "Vulkan dispatch skipped: no compute shader is bound.");
            return false;
        }

        EndRenderPassIfNeeded(vkCmd);
        Pipeline pipeline = _device.GetOrCreateComputePipeline(_currentComputeShader);
        VkShaderLayoutResource layout = _device.GetOrCreateShaderLayout(_currentComputeShader);
        _device.Vk.CmdBindPipeline(vkCmd, PipelineBindPoint.Compute, pipeline);
        BindDescriptorSetIfNeeded(vkCmd, layout, _currentComputeShader, PipelineBindPoint.Compute);
        return true;
    }

    private void RecordResourceBarrier(VkCommandBuffer vkCmd, GpuBarrierKind kind, object? resource)
    {
        if (kind == GpuBarrierKind.None || resource is not GraphicsBuffer buffer || buffer.Handle == 0)
            return;
        if (!_device.Buffers.TryGetValue(buffer.Handle, out VkBufferResource? bufferResource) ||
            bufferResource.Buffer.Handle == 0)
            return;

        EndRenderPassIfNeeded(vkCmd);
        GetBarrierAccess(kind, out PipelineStageFlags src, out AccessFlags srcAccess,
            out PipelineStageFlags dst, out AccessFlags dstAccess);
        var barrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = bufferResource.Buffer,
            Offset = 0,
            Size = Vk.WholeSize,
        };
        _device.Vk.CmdPipelineBarrier(vkCmd, src, dst, 0, 0, null, 1, &barrier, 0, null);
    }

    private static void GetBarrierAccess(
        GpuBarrierKind kind,
        out PipelineStageFlags src,
        out AccessFlags srcAccess,
        out PipelineStageFlags dst,
        out AccessFlags dstAccess)
    {
        src = PipelineStageFlags.ComputeShaderBit;
        srcAccess = AccessFlags.ShaderWriteBit;
        switch (kind)
        {
            case GpuBarrierKind.ComputeToCompute:
                dst = PipelineStageFlags.ComputeShaderBit;
                dstAccess = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                return;
            case GpuBarrierKind.ComputeToVertex:
                dst = PipelineStageFlags.VertexInputBit | PipelineStageFlags.VertexShaderBit;
                dstAccess = AccessFlags.VertexAttributeReadBit | AccessFlags.ShaderReadBit;
                return;
            case GpuBarrierKind.ComputeToIndirect:
                dst = PipelineStageFlags.DrawIndirectBit;
                dstAccess = AccessFlags.IndirectCommandReadBit;
                return;
            case GpuBarrierKind.ComputeToPixel:
                dst = PipelineStageFlags.FragmentShaderBit;
                dstAccess = AccessFlags.ShaderReadBit;
                return;
            default:
                dst = PipelineStageFlags.AllCommandsBit;
                dstAccess = AccessFlags.MemoryReadBit;
                return;
        }
    }

    private void BindDescriptorSetIfNeeded(
        VkCommandBuffer vkCmd,
        VkShaderLayoutResource layout,
        ShaderVariant? shader,
        PipelineBindPoint bindPoint)
    {
        ShaderBindingLayout bindingLayout = shader?.Bytecode?.BindingLayout ?? new ShaderBindingLayout();
        EnsureGlobalUniformsBuffer(bindingLayout.Buffers);
        EnsureObjectUniformsBuffer(bindingLayout.Buffers);
        EnsureStandardLightingBuffer(bindingLayout.Buffers);
        EnsureStandardShadowsBuffer(bindingLayout.Buffers);
        EnsureStandardReflectionProbesBuffer(bindingLayout.Buffers);
        EnsureStandardLightCookiesBuffer(bindingLayout.Buffers);
        EnsureMaterialUniformsBuffer(bindingLayout.Buffers);
        EnsureUIVertexUniformsBuffer(bindingLayout.Buffers);
        EnsureUIFragmentUniformsBuffer(bindingLayout.Buffers);
        int descriptorCount = layout.Plan.Bindings.Length;
        if (descriptorCount == 0)
            return;

        if (_descriptorDirty)
        {
            _currentDescriptorSet = _device.AllocateDescriptorSet(layout);
            DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[descriptorCount];
            DescriptorImageInfo* imageInfos = stackalloc DescriptorImageInfo[descriptorCount];
            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[descriptorCount];
            int bufferInfoIndex = 0;
            int imageInfoIndex = 0;
            int writeIndex = 0;
            for (int i = 0; i < layout.Plan.Bindings.Length; i++)
            {
                ShaderDescriptorBinding descriptorBinding = layout.Plan.Bindings[i];
                ShaderBindingSlot binding = descriptorBinding.Slot;
                switch (binding.Kind)
                {
                    case ShaderBindingKind.Buffer:
                    {
                        DescriptorBufferInfo* bufferInfo = &bufferInfos[bufferInfoIndex++];
                        if (_transientUniformBuffers.TryGetValue(binding.Name, out VkTransientUniformBinding transientBuffer))
                        {
                            *bufferInfo = new DescriptorBufferInfo
                            {
                                Buffer = transientBuffer.Resource.Buffer,
                                Offset = transientBuffer.Offset,
                                Range = transientBuffer.Range,
                            };
                        }
                        else
                        {
                            if (!_uniformBuffers.TryGetValue(binding.Name, out GraphicsBuffer? uniformBuffer) || uniformBuffer.Handle == 0)
                            {
                                // Degrade instead of terminating: a missing cbuffer binds a zeroed
                                // transient so the draw still submits (degenerate constants →
                                // invisible output). Historically this threw, the render thread
                                // captured it, and the next main-thread Submit rethrew — killing
                                // the whole editor over one bad material. Logged once per name.
                                LogDrawDegradationOnce($"uniform buffer '{binding.Name}'");
                                VkTransientUniformBinding zeros = AllocateZeroedTransientUniform(binding.ByteSize);
                                _transientUniformBuffers[binding.Name] = zeros;
                                *bufferInfo = new DescriptorBufferInfo
                                {
                                    Buffer = zeros.Resource.Buffer,
                                    Offset = zeros.Offset,
                                    Range = zeros.Range,
                                };
                            }
                            else if (!_device.Buffers.TryGetValue(uniformBuffer.Handle, out VkBufferResource? bufferResource) ||
                                bufferResource.Buffer.Handle == 0)
                            {
                                LogDrawDegradationOnce($"uniform buffer '{binding.Name}' (device resource missing)");
                                VkTransientUniformBinding zeros = AllocateZeroedTransientUniform(binding.ByteSize);
                                _transientUniformBuffers[binding.Name] = zeros;
                                *bufferInfo = new DescriptorBufferInfo
                                {
                                    Buffer = zeros.Resource.Buffer,
                                    Offset = zeros.Offset,
                                    Range = zeros.Range,
                                };
                            }
                            else
                            {
                                *bufferInfo = new DescriptorBufferInfo
                                {
                                    Buffer = bufferResource.Buffer,
                                    Offset = 0,
                                    Range = bufferResource.Size,
                                };
                            }
                        }

                        writes[writeIndex++] = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet,
                            DstSet = _currentDescriptorSet,
                            DstBinding = checked((uint)descriptorBinding.PhysicalBinding),
                            DescriptorCount = 1,
                            DescriptorType = DescriptorType.UniformBuffer,
                            PBufferInfo = bufferInfo,
                        };
                        break;
                    }
                    case ShaderBindingKind.Texture:
                    {
                        VkImageResource textureResource = GetTextureResource(binding.Name);
                        DescriptorImageInfo* imageInfo = &imageInfos[imageInfoIndex++];
                        *imageInfo = new DescriptorImageInfo
                        {
                            ImageView = textureResource.View,
                            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                        };
                        writes[writeIndex++] = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet,
                            DstSet = _currentDescriptorSet,
                            DstBinding = checked((uint)descriptorBinding.PhysicalBinding),
                            DescriptorCount = 1,
                            DescriptorType = DescriptorType.SampledImage,
                            PImageInfo = imageInfo,
                        };
                        break;
                    }
                    case ShaderBindingKind.Sampler:
                    {
                        string textureName = FindTextureNameForSampler(bindingLayout.Textures, binding.Slot);
                        VkImageResource textureResource = GetTextureResource(textureName);
                        DescriptorImageInfo* imageInfo = &imageInfos[imageInfoIndex++];
                        *imageInfo = new DescriptorImageInfo { Sampler = textureResource.Sampler };
                        writes[writeIndex++] = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet,
                            DstSet = _currentDescriptorSet,
                            DstBinding = checked((uint)descriptorBinding.PhysicalBinding),
                            DescriptorCount = 1,
                            DescriptorType = DescriptorType.Sampler,
                            PImageInfo = imageInfo,
                        };
                        break;
                    }
                    case ShaderBindingKind.StructuredBuffer:
                    case ShaderBindingKind.StorageBuffer:
                    {
                        if (!_uniformBuffers.TryGetValue(binding.Name, out GraphicsBuffer? storageBuffer) || storageBuffer.Handle == 0)
                            throw new InvalidOperationException($"Vulkan compute requires storage buffer '{binding.Name}'.");
                        if (!_device.Buffers.TryGetValue(storageBuffer.Handle, out VkBufferResource? storageResource) ||
                            storageResource.Buffer.Handle == 0)
                            throw new InvalidOperationException($"Vulkan storage buffer '{binding.Name}' is not available.");

                        DescriptorBufferInfo* bufferInfo = &bufferInfos[bufferInfoIndex++];
                        *bufferInfo = new DescriptorBufferInfo
                        {
                            Buffer = storageResource.Buffer,
                            Offset = 0,
                            Range = storageResource.Size,
                        };
                        writes[writeIndex++] = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet,
                            DstSet = _currentDescriptorSet,
                            DstBinding = checked((uint)descriptorBinding.PhysicalBinding),
                            DescriptorCount = 1,
                            DescriptorType = DescriptorType.StorageBuffer,
                            PBufferInfo = bufferInfo,
                        };
                        break;
                    }
                    default:
                        throw new NotSupportedException($"Vulkan descriptor writes do not support {binding.Kind} resources.");
                }
            }
            _device.Vk.UpdateDescriptorSets(_device.Device, checked((uint)writeIndex), writes, 0, null);
            _descriptorDirty = false;
        }

        DescriptorSet descriptorSet = _currentDescriptorSet;
        _device.Vk.CmdBindDescriptorSets(
            vkCmd,
            bindPoint,
            layout.PipelineLayout,
            0,
            1,
            &descriptorSet,
            0,
            null);
    }

    private void EnsureGlobalUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_hasGlobalUniformsSnapshot || _globalUniformsExplicitlyBound ||
            _transientUniformBuffers.ContainsKey("GlobalUniforms"))
        {
            return;
        }

        bool required = false;
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name == "GlobalUniforms")
            {
                required = true;
                break;
            }
        }
        if (!required)
            return;

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref _globalUniformsSnapshot, 1));
        _transientUniformBuffers["GlobalUniforms"] = AllocateTransientUniform(bytes);
        _descriptorDirty = true;
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

        ulong dataSize = checked((ulong)Unsafe.SizeOf<Rendering.ObjectUniformsData>());
        ulong alignment = _device.UniformBufferOffsetAlignment;
        ulong alignedSize = (dataSize + alignment - 1) / alignment * alignment;
        EnsureTransientUniformArena(alignedSize);

        ulong offset = _transientUniformArenaOffset;
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref _objectUniforms, 1));
            fixed (byte* source = bytes)
                System.Buffer.MemoryCopy(source, (byte*)_transientUniformArena!.Mapped + offset, checked((long)alignedSize), bytes.Length);
        }

        _transientUniformBuffers["ObjectUniforms"] = new VkTransientUniformBinding(
            _transientUniformArena,
            offset,
            dataSize);
        _transientUniformArenaOffset += alignedSize;
        _objectUniformsDirty = false;
        _descriptorDirty = true;
    }

    private void ApplyObjectProperties(Rendering.PropertyState? properties)
    {
        _objectUniforms = Rendering.ObjectUniformsData.Identity;
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
            foreach (KeyValuePair<string, AssetRef<Resources.Texture2D>> entry in properties._textures)
            {
                Resources.Texture2D? texture = entry.Value.Res;
                if (!texture.IsValid())
                    continue;

                _textures[entry.Key] = texture.Handle;
                _objectTextureNames.Add(entry.Key);
            }
        }
        _objectUniformsDirty = true;
        _descriptorDirty = true;
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
            _descriptorDirty = true;
            return;
        }
    }

    private void EnsureStandardShadowsBuffer(ShaderBindingSlot[] buffers)
    {
        // The shadow block is repacked unconditionally when the shader declares it: it cannot share
        // the StandardLighting dirty flag because EnsureStandardLightingBuffer clears that flag.
        // The all-float4 StandardShadows cbuffer layout now matches the C# marshal exactly.
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name != "StandardShadows")
                continue;

            Rendering.StandardShadowsUniformsData data = Rendering.MaterialUniformPacking.PackStandardShadows();
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
            _transientUniformBuffers["StandardShadows"] = AllocateTransientUniform(bytes);
            _descriptorDirty = true;
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
            _descriptorDirty = true;
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
            _descriptorDirty = true;
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

    /// <summary>
    /// Material snapshot merged with the RAW instance state (not just material-block overrides).
    /// Texture resolution uses this: per-renderer textures like boneMatrixTexture ride
    /// object-block-only instance states that <see cref="EffectiveMaterialProperties"/> drops.
    /// </summary>
    private Rendering.PropertyState? EffectiveTextureProperties
    {
        get
        {
            if (_instanceTextureState == null)
                return _materialProperties;
            _mergedTextureProperties.Clear();
            if (_materialProperties != null)
                _mergedTextureProperties.ApplyOverride(_materialProperties);
            _mergedTextureProperties.ApplyOverride(_instanceTextureState);
            return _mergedTextureProperties;
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
                _descriptorDirty = true;
                continue;
            }
            if (name == "UnlitMaterial")
            {
                Rendering.UnlitMaterialUniformsData data = Rendering.MaterialUniformPacking.PackUnlit(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "ParticleMaterial")
            {
                Rendering.ParticleMaterialUniformsData data = Rendering.MaterialUniformPacking.PackParticle(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "VfxOutputMaterial")
            {
                Rendering.VfxOutputMaterialUniformsData data = Rendering.MaterialUniformPacking.PackVfxOutput(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "DefaultUIMaterial")
            {
                Rendering.DefaultUIMaterialUniformsData data = Rendering.MaterialUniformPacking.PackDefaultUI(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "StandardMaterial")
            {
                Rendering.StandardMaterialUniformsData data = Rendering.MaterialUniformPacking.PackStandard(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "AnisotropicMaterial")
            {
                Rendering.AnisotropicMaterialUniformsData data = Rendering.MaterialUniformPacking.PackAnisotropic(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "TerrainMaterial")
            {
                Rendering.TerrainMaterialUniformsData data = Rendering.MaterialUniformPacking.PackTerrain(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "GrassMaterial")
            {
                Rendering.GrassMaterialUniformsData data = Rendering.MaterialUniformPacking.PackGrass(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "PrepassMaterial" || name == "ShadowMaterial")
            {
                Rendering.CutoutMaterialUniformsData data = Rendering.MaterialUniformPacking.PackCutout(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "GradientPS")
            {
                Rendering.GradientSkyboxUniformsData data = Rendering.MaterialUniformPacking.PackGradientSkybox(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "CubemapSkyboxPS")
            {
                Rendering.CubemapSkyboxUniformsData data = Rendering.MaterialUniformPacking.PackCubemapSkybox(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "GizmoIconMaterial")
            {
                Rendering.GizmoIconUniformsData data = Rendering.MaterialUniformPacking.PackGizmoIcon(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SkyVS")
            {
                Rendering.ProceduralSkyboxUniformsData data = Rendering.MaterialUniformPacking.PackProceduralSkybox(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "TonemapperPS")
            {
                Rendering.TonemapperUniformsData data = Rendering.MaterialUniformPacking.PackTonemapper(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "FrameDebugPreviewPS")
            {
                Rendering.FrameDebugPreviewUniformsData data = Rendering.MaterialUniformPacking.PackFrameDebugPreview(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "BlurDownPS" || name == "BlurUpPS")
            {
                Rendering.UIBlurUniformsData data = Rendering.MaterialUniformPacking.PackUIBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "FXAAPS")
            {
                Rendering.FXAAUniformsData data = Rendering.MaterialUniformPacking.PackFXAA(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SMAAPS")
            {
                Rendering.SMAAUniformsData data = Rendering.MaterialUniformPacking.PackSMAA(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "FogTemporalPS")
            {
                Rendering.FogTemporalUniformsData data = Rendering.MaterialUniformPacking.PackFogTemporal(materialProperties);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "FogMarchPS")
            {
                Rendering.FogMarchUniformsData data = Rendering.MaterialUniformPacking.PackFogMarch(materialProperties);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "FogCompositePS")
            {
                Rendering.FogCompositeUniformsData data = Rendering.MaterialUniformPacking.PackFogComposite(materialProperties);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "RefractionMaterial")
            {
                Rendering.RefractionMaterialUniformsData data = Rendering.MaterialUniformPacking.PackRefraction(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "CinematicEffectsPS")
            {
                Rendering.CinematicEffectsUniformsData data = Rendering.MaterialUniformPacking.PackCinematicEffects(materialProperties);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "BloomThresholdPS")
            {
                Rendering.BloomThresholdUniformsData data = Rendering.MaterialUniformPacking.PackBloomThreshold(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "BloomCompositePS")
            {
                Rendering.BloomCompositeUniformsData data = Rendering.MaterialUniformPacking.PackBloomComposite(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "BokehDoFPS")
            {
                Rendering.BokehDoFUniformsData data = Rendering.MaterialUniformPacking.PackBokehDoF(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SSRRayCastPS")
            {
                Rendering.SSRRayCastUniformsData data = Rendering.MaterialUniformPacking.PackSSRRayCast(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SSRSceneBlurPS")
            {
                Rendering.SSRSceneBlurUniformsData data = Rendering.MaterialUniformPacking.PackSSRSceneBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SSRResolvePS")
            {
                Rendering.SSRResolveUniformsData data = Rendering.MaterialUniformPacking.PackSSRResolve(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SSRTemporalPS")
            {
                Rendering.SSRTemporalUniformsData data = Rendering.MaterialUniformPacking.PackSSRTemporal(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SSRCombinePS")
            {
                Rendering.SSRCombineUniformsData data = Rendering.MaterialUniformPacking.PackSSRCombine(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "MotionBlurPS")
            {
                Rendering.MotionBlurUniformsData data = Rendering.MaterialUniformPacking.PackMotionBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "AutoExposureAdaptPS")
            {
                Rendering.AutoExposureAdaptUniformsData data = Rendering.MaterialUniformPacking.PackAutoExposureAdapt(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "AutoExposureApplyPS")
            {
                Rendering.AutoExposureApplyUniformsData data = Rendering.MaterialUniformPacking.PackAutoExposureApply(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "TAAResolvePS")
            {
                Rendering.TAAUniformsData data = Rendering.MaterialUniformPacking.PackTAA(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SsgiGatherPS")
            {
                Rendering.SsgiGatherUniformsData data = Rendering.MaterialUniformPacking.PackSsgiGather(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SsgiTemporalPS")
            {
                Rendering.SsgiTemporalUniformsData data = Rendering.MaterialUniformPacking.PackSsgiTemporal(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SsgiBlurPS")
            {
                Rendering.SsgiBlurUniformsData data = Rendering.MaterialUniformPacking.PackSsgiBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SsgiCompositePS")
            {
                Rendering.SsgiCompositeUniformsData data = Rendering.MaterialUniformPacking.PackSsgiComposite(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "SsgiProbePS")
            {
                Rendering.SsgiProbeUniformsData data = Rendering.MaterialUniformPacking.PackSsgiProbe(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "GTAOCalculatePS")
            {
                Rendering.GTAOCalculateUniformsData data = Rendering.MaterialUniformPacking.PackGTAOCalculate(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "GTAOBlurPS")
            {
                Rendering.GTAOBlurUniformsData data = Rendering.MaterialUniformPacking.PackGTAOBlur(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "GTAOTemporalPS")
            {
                Rendering.GTAOTemporalUniformsData data = Rendering.MaterialUniformPacking.PackGTAOTemporal(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "ScreenSpaceDecalPS")
            {
                Rendering.ScreenSpaceDecalUniformsData data = Rendering.MaterialUniformPacking.PackScreenSpaceDecal(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "ScreenSpaceShadowsPS")
            {
                Rendering.ScreenSpaceShadowsUniformsData data = Rendering.MaterialUniformPacking.PackScreenSpaceShadows(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            if (name == "GridPS")
            {
                Rendering.GridUniformsData data = Rendering.MaterialUniformPacking.PackGrid(materialProperties, _materialShader);
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
                _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
                _materialUniformsDirty = false;
                _descriptorDirty = true;
                continue;
            }
            else if (!_uniformBuffers.ContainsKey(name))
            {
                // Generic fallback: reflection-driven cbuffer packing for game-owned shaders
                // that have no hand-written packer. Uses the bytecode's binding layout.
                // Explicitly opcode-bound buffers (SetBuffer, e.g. Paper's UiBrushes records)
                // are excluded: a reflection-packed transient would shadow the explicit
                // binding (transient wins at descriptor-write time) with zeroed data.
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
                        _descriptorDirty = true;
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
            _descriptorDirty = true;
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
            _descriptorDirty = true;
            return;
        }
    }

    private void ApplyMaterialTextureBindings()
    {
        ClearObjectTextureBindings();
        ShaderBindingSlot[]? textureSlots = _currentShader?.Bytecode?.BindingLayout?.Textures;
        // EffectiveTextureProperties (material snapshot + raw instance state) — NOT just the
        // material-block overrides: SkinnedMeshRenderer sets boneMatrixTexture on an
        // object-block-only instance state, which only exists in this merged view. Resolution
        // must see it or the vertex shader samples whatever the descriptor happens to hold.
        Rendering.PropertyState? effective = EffectiveTextureProperties;
        Rendering.MaterialUniformPacking.ApplyTextureBindings(_materialTextures, effective, _materialShader, textureSlots);
        Rendering.MaterialUniformPacking.ApplyTextureBindings(_textures, effective, _materialShader, textureSlots);
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

    /// <summary>Drops the current transient uniform arena so the next allocation starts a fresh
    /// one. Called by the device when a frame-recording session begins (the old arena retires
    /// with that session's transient-buffer list).</summary>
    internal void ResetTransientUniformArena()
    {
        _transientUniformArena = null;
        _transientUniformArenaOffset = 0;
    }

    /// <summary>Ensures the persistently-mapped arena has room for <paramref name="alignedSize"/>
    /// bytes. Previously every allocation mapped and unmapped the whole arena and every engine CB
    /// recreated the buffer; the arena is now mapped once at creation and (within a frame
    /// recording) grows in 64 KiB chunks across the whole frame.</summary>
    private void EnsureTransientUniformArena(ulong alignedSize)
    {
        if (_transientUniformArena != null && _transientUniformArenaOffset + alignedSize <= TransientUniformArenaSize)
            return;

        _device.CreateBuffer(
            TransientUniformArenaSize,
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out VkBuffer buffer,
            out DeviceMemory memory);
        void* mapped;
        VulkanGraphicsDevice.Check(
            _device.Vk.MapMemory(_device.Device, memory, 0, TransientUniformArenaSize, 0, &mapped),
            "vkMapMemory(arena)");
        _transientUniformArena = new VkBufferResource
        {
            Buffer = buffer,
            Memory = memory,
            Size = TransientUniformArenaSize,
            Type = BufferType.UniformBuffer,
            Dynamic = true,
            Mapped = (IntPtr)mapped,
        };
        _submissionTransientBuffers!.Add(_transientUniformArena);
        _transientUniformArenaOffset = 0;
    }

    private VkTransientUniformBinding AllocateTransientUniform(ReadOnlySpan<byte> bytes)
    {
        ulong alignment = _device.UniformBufferOffsetAlignment;
        ulong alignedSize = ((ulong)bytes.Length + alignment - 1) / alignment * alignment;
        EnsureTransientUniformArena(alignedSize);

        ulong offset = _transientUniformArenaOffset;
        fixed (byte* source = bytes)
            System.Buffer.MemoryCopy(source, (byte*)_transientUniformArena!.Mapped + offset, checked((long)alignedSize), bytes.Length);
        _transientUniformArenaOffset += alignedSize;
        return new VkTransientUniformBinding(_transientUniformArena, offset, checked((ulong)bytes.Length));
    }

    /// <summary>Zero-filled transient uniform of <paramref name="byteSize"/> bytes (rounded to the
    /// device's offset alignment). Used by the missing-cbuffer degradation path.</summary>
    private VkTransientUniformBinding AllocateZeroedTransientUniform(int byteSize)
    {
        if (byteSize <= 0)
            byteSize = 16;
        byte[] scratch = GetPooledScratch(byteSize);
        scratch.AsSpan(0, byteSize).Clear();
        return AllocateTransientUniform(scratch.AsSpan(0, byteSize));
    }

    // Draw-degradation log throttle: one loud error per unique missing binding per process. The
    // degraded draw runs every frame; logging it every frame would drown the console and cost
    // string formatting on the render thread.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_degradedDraws = new();

    private static void LogDrawDegradationOnce(string what)
    {
        if (s_degradedDraws.TryAdd(what, 0))
            Debug.LogError($"[Vulkan] Draw degraded — missing {what}; bound zeros and continuing. (logged once per binding; fix the material/shader)");
    }

    /// <summary>Pooled scratch byte array for generic cbuffer packing (fallback path only).
    /// Grows on demand; not cleared between uses — the generic packer zeroes the destination.</summary>
    private byte[] GetPooledScratch(int size)
    {
        if (_genericUniformScratch == null || _genericUniformScratch.Length < size)
            _genericUniformScratch = new byte[size];
        return _genericUniformScratch;
    }

    private bool TryGetBoundTexture(string name, out GraphicsTexture texture)
    {
        if (_textures.TryGetValue(name, out texture) && texture.Handle != 0)
            return true;
        if (name == "_Lightmap" && _textures.TryGetValue("_MainTex", out texture) && texture.Handle != 0)
            return true;
        if (Rendering.PropertyState.s_globalTextures.TryGetValue(name, out Resources.Texture2D globalTexture)
            && globalTexture.IsValid() && globalTexture.Handle.Handle != 0)
        {
            texture = globalTexture.Handle;
            return true;
        }

        if (Rendering.PropertyState.s_globalTexturesCube.TryGetValue(name, out Resources.Cubemap cube)
            && cube.IsValid() && cube.Handle.Handle != 0)
        {
            texture = cube.Handle;
            return true;
        }

        // Final fallback: bind the per-device 1x1 white texture instead of leaving the descriptor
        // empty. Missing texture bindings used to crash the draw with "Vulkan draw requires
        // texture" and take the whole editor down; now the sampler reads white.
        if (_whiteTextureFallbackRef is { IsDisposed: false } white && white.Handle.Handle != 0)
        {
            texture = white.Handle;
            return true;
        }

        texture = default!;
        return false;
    }

    private VkImageResource GetTextureResource(string name)
    {
        if (!TryGetBoundTexture(name, out GraphicsTexture texture))
            throw new InvalidOperationException($"Vulkan draw requires texture '{name}'.");
        if (!_device.Images.TryGetValue(texture.Handle, out VkImageResource? resource))
            throw new InvalidOperationException($"Vulkan texture '{name}' handle {texture.Handle} is not available.");
        if (resource.View.Handle == 0 || resource.Sampler.Handle == 0 ||
            resource.Layout != ImageLayout.ShaderReadOnlyOptimal)
        {
            throw new InvalidOperationException(
                $"Vulkan texture '{name}' is not sample-ready: handle={texture.Handle}, image={resource.Image.Handle}, " +
                $"view={resource.View.Handle}, sampler={resource.Sampler.Handle}, layout={resource.Layout}, " +
                $"size={resource.Width}x{resource.Height}, mips={resource.MipLevels}, availableMips={resource.AvailableMipLevels}.");
        }
        return resource;
    }

    private static string FindTextureNameForSampler(ShaderBindingSlot[] textures, int samplerSlot)
    {
        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i].Slot == samplerSlot)
                return textures[i].Name;
        }
        throw new NotSupportedException($"Vulkan sampler slot s{samplerSlot} has no texture at matching t{samplerSlot}.");
    }

    private void WarnDrawNoPso()
    {
        if (_warnedDrawNoPso)
            return;
        _warnedDrawNoPso = true;
        Debug.LogWarning(_currentShader == null
            ? "Vulkan draw skipped: no backend-neutral shader variant is bound."
            : "Vulkan draw skipped: pipeline state objects are not ready yet.");
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
        WarnOnce(op, $"VulkanCommandTranslator: unhandled opcode {op} (skipped).");

        switch (op)
        {
            case CommandOpcode.BlitFramebuffer:
                pos += sizeof(int) * 8 + 2;
                break;
            case CommandOpcode.CompileShader:
            case CommandOpcode.DisposeShader:
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.SetGlobalTexture3D:
            case CommandOpcode.SetGlobalTextureCube:
                _ = objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                break;
            case CommandOpcode.SetGlobalInt:
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
                throw new InvalidOperationException($"VulkanCommandTranslator: cannot skip unknown opcode {op}.");
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

    private static ulong ReadU64(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<ulong>(s.Slice(pos)); pos += sizeof(ulong); return v; }
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

internal readonly record struct VkTransientUniformBinding(VkBufferResource Resource, ulong Offset, ulong Range);
