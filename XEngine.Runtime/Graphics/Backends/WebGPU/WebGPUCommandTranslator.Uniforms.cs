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
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Backends.WebGPU;

internal sealed unsafe partial class WebGPUCommandTranslator
{
    private readonly Dictionary<string, nint> _transientUniformBuffers = new(StringComparer.Ordinal);
    private readonly List<nint> _transientUniformGpu = new();
    private readonly Dictionary<string, GraphicsTexture> _materialTextures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _objectTextureNames = new(StringComparer.Ordinal);
    private PropertyState? _materialProperties;
    private Resources.Shader? _materialShader;
    // Instance PropertyState carrying material-block overrides for the current draw (null when
    // the instance state only holds per-object data). Merged over _materialProperties by
    // EffectiveMaterialProperties; the scratch below backs that merge without allocating.
    private PropertyState? _instanceMaterialOverrides;
    private readonly PropertyState _mergedMaterialProperties = new();
    private ObjectUniformsData _objectUniforms = ObjectUniformsData.Identity;
    private bool _objectUniformsDirty = true;
    private GlobalUniformsData _globalUniformsSnapshot;
    private bool _hasGlobalUniformsSnapshot;
    private bool _globalUniformsExplicitlyBound;
    private bool _materialUniformsDirty = true;
    // Pooled scratch for generic cbuffer packing (fallback path). Grows on demand.
    private byte[]? _genericUniformScratch;
    private bool _standardLightingDirty = true;
    private UIVertexUniformsData _uiVertexUniforms;
    private bool _uiVertexUniformsDirty = true;
    private UIFragmentUniformsData _uiFragmentUniforms;
    private bool _uiFragmentUniformsDirty = true;

    // White texture fallback for missing texture bindings. WebGPU requires all bind group
    // bindings to be valid; when a shader declares a sampler but no texture is provided
    // (e.g., material override missing and no shader default), bind a 1x1 white texture
    // instead of crashing the draw with "WebGPU bind group missing texture '<name>'".
    // Per-device instance, created by InitializeMissingBindingFallbacks during device init.
    private Texture2D? _whiteTextureFallbackRef;

    private void ResetUniformState(CommandBuffer commandBuffer)
    {
        _transientUniformBuffers.Clear();
        _objectUniforms = ObjectUniformsData.Identity;
        _objectUniformsDirty = true;
        _uiVertexUniforms = default;
        _uiVertexUniformsDirty = true;
        _uiFragmentUniforms = default;
        _uiFragmentUniformsDirty = true;
        _globalUniformsSnapshot = commandBuffer._globalUniformsSnapshot;
        _hasGlobalUniformsSnapshot = commandBuffer._hasGlobalUniformsSnapshot;
        _globalUniformsExplicitlyBound = false;
        _materialProperties = null;
        _materialShader = null;
        _instanceMaterialOverrides = null;
        _materialUniformsDirty = true;
        _standardLightingDirty = true;
        _objectTextureNames.Clear();
        _materialTextures.Clear();
        GraphicsBuffer? globalUniforms = GlobalUniforms.GetBuffer();
        if (globalUniforms is { Handle: not 0 })
            _uniformBuffers["GlobalUniforms"] = globalUniforms;
    }

    private void EnsurePackedUniforms(ShaderVariant shader)
    {
        ShaderBindingLayout bindingLayout = shader.Bytecode?.BindingLayout ?? new ShaderBindingLayout();
        EnsureGlobalUniformsBuffer(bindingLayout.Buffers);
        EnsureObjectUniformsBuffer(bindingLayout.Buffers);
        EnsureStandardLightingBuffer(bindingLayout.Buffers);
        EnsureStandardShadowsBuffer(bindingLayout.Buffers);
        EnsureStandardReflectionProbesBuffer(bindingLayout.Buffers);
        EnsureStandardLightCookiesBuffer(bindingLayout.Buffers);
        EnsureMaterialUniformsBuffer(bindingLayout.Buffers, shader);
        EnsureUIVertexUniformsBuffer(bindingLayout.Buffers);
        EnsureUIFragmentUniformsBuffer(bindingLayout.Buffers);
    }

    private void EnsureGlobalUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_hasGlobalUniformsSnapshot || _globalUniformsExplicitlyBound)
            return;

        bool wantsGlobal = RequiresBuffer(buffers, "GlobalUniforms");
        bool wantsPerFrame = RequiresBuffer(buffers, "XEnginePerFrame");
        if (!wantsGlobal && !wantsPerFrame)
            return;

        if (!_transientUniformBuffers.ContainsKey("GlobalUniforms"))
            StorePackedUniform("GlobalUniforms", _globalUniformsSnapshot);

        if (wantsPerFrame && _transientUniformBuffers.TryGetValue("GlobalUniforms", out nint packed))
            _transientUniformBuffers["XEnginePerFrame"] = packed;
    }

    private void EnsureObjectUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!RequiresBuffer(buffers, "ObjectUniforms") || !_objectUniformsDirty)
            return;

        StorePackedUniform("ObjectUniforms", _objectUniforms);
        _objectUniformsDirty = false;
    }

    private void EnsureStandardLightingBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_standardLightingDirty || !RequiresBuffer(buffers, "StandardLighting"))
            return;

        StorePackedUniform("StandardLighting", MaterialUniformPacking.PackStandardLighting());
        _standardLightingDirty = false;
    }

    private void EnsureStandardShadowsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!RequiresBuffer(buffers, "StandardShadows"))
            return;

        StorePackedUniform("StandardShadows", MaterialUniformPacking.PackStandardShadows());
    }

    private void EnsureStandardReflectionProbesBuffer(ShaderBindingSlot[] buffers)
    {
        if (!RequiresBuffer(buffers, "StandardReflectionProbes"))
            return;

        StorePackedUniform("StandardReflectionProbes", MaterialUniformPacking.PackStandardReflectionProbes());
    }

    private void EnsureStandardLightCookiesBuffer(ShaderBindingSlot[] buffers)
    {
        if (!RequiresBuffer(buffers, "StandardLightCookies"))
            return;

        StorePackedUniform("StandardLightCookies", MaterialUniformPacking.PackStandardLightCookies());
    }

    /// <summary>
    /// The PropertyState the material uniform pack reads from: the bound material snapshot,
    /// overlaid with the current draw's instance properties when those carry material-block
    /// keys (see <see cref="MaterialUniformPacking.HasMaterialBlockOverrides"/>).
    /// Instance properties are documented to apply AFTER material properties so they can
    /// override them; without this merge a per-draw _MainColor/_Tiling/_Clip* override is
    /// silently replaced by the shared material's value (UI images tinted via instance
    /// _MainColor rendered solid white).
    /// </summary>
    private PropertyState? EffectiveMaterialProperties
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

    private void EnsureMaterialUniformsBuffer(ShaderBindingSlot[] buffers, ShaderVariant shader)
    {
        if (!_materialUniformsDirty)
            return;

        PropertyState? materialProperties = EffectiveMaterialProperties;
        for (int i = 0; i < buffers.Length; i++)
        {
            string name = buffers[i].Name;
            bool packed = name switch
            {
                "BlitMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackBlit(materialProperties, _materialShader)),
                "UnlitMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackUnlit(materialProperties, _materialShader)),
                "ParticleMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackParticle(materialProperties, _materialShader)),
                "VfxOutputMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackVfxOutput(materialProperties, _materialShader)),
                "DefaultUIMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackDefaultUI(materialProperties, _materialShader)),
                "StandardMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackStandard(materialProperties, _materialShader)),
                "AnisotropicMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackAnisotropic(materialProperties, _materialShader)),
                "TerrainMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackTerrain(materialProperties, _materialShader)),
                "GrassMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackGrass(materialProperties, _materialShader)),
                "PrepassMaterial" or "ShadowMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackCutout(materialProperties, _materialShader)),
                "GradientPS" => StorePackedUniform(name, MaterialUniformPacking.PackGradientSkybox(materialProperties, _materialShader)),
                "CubemapSkyboxPS" => StorePackedUniform(name, MaterialUniformPacking.PackCubemapSkybox(materialProperties, _materialShader)),
                "GizmoIconMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackGizmoIcon(materialProperties, _materialShader)),
                "SkyVS" => StorePackedUniform(name, MaterialUniformPacking.PackProceduralSkybox(materialProperties, _materialShader)),
                "TonemapperPS" => StorePackedUniform(name, MaterialUniformPacking.PackTonemapper(materialProperties, _materialShader)),
                "FrameDebugPreviewPS" => StorePackedUniform(name, MaterialUniformPacking.PackFrameDebugPreview(materialProperties, _materialShader)),
                "BlurDownPS" or "BlurUpPS" => StorePackedUniform(name, MaterialUniformPacking.PackUIBlur(materialProperties, _materialShader)),
                "FXAAPS" => StorePackedUniform(name, MaterialUniformPacking.PackFXAA(materialProperties, _materialShader)),
                "SMAAPS" => StorePackedUniform(name, MaterialUniformPacking.PackSMAA(materialProperties, _materialShader)),
                "FogTemporalPS" => StorePackedUniform(name, MaterialUniformPacking.PackFogTemporal(materialProperties)),
                "FogMarchPS" => StorePackedUniform(name, MaterialUniformPacking.PackFogMarch(materialProperties)),
                "FogCompositePS" => StorePackedUniform(name, MaterialUniformPacking.PackFogComposite(materialProperties)),
                "RefractionMaterial" => StorePackedUniform(name, MaterialUniformPacking.PackRefraction(materialProperties, _materialShader)),
                "CinematicEffectsPS" => StorePackedUniform(name, MaterialUniformPacking.PackCinematicEffects(materialProperties)),
                "BloomThresholdPS" => StorePackedUniform(name, MaterialUniformPacking.PackBloomThreshold(materialProperties, _materialShader)),
                "BloomCompositePS" => StorePackedUniform(name, MaterialUniformPacking.PackBloomComposite(materialProperties, _materialShader)),
                "BokehDoFPS" => StorePackedUniform(name, MaterialUniformPacking.PackBokehDoF(materialProperties, _materialShader)),
                "SSRRayCastPS" => StorePackedUniform(name, MaterialUniformPacking.PackSSRRayCast(materialProperties, _materialShader)),
                "SSRSceneBlurPS" => StorePackedUniform(name, MaterialUniformPacking.PackSSRSceneBlur(materialProperties, _materialShader)),
                "SSRResolvePS" => StorePackedUniform(name, MaterialUniformPacking.PackSSRResolve(materialProperties, _materialShader)),
                "SSRTemporalPS" => StorePackedUniform(name, MaterialUniformPacking.PackSSRTemporal(materialProperties, _materialShader)),
                "SSRCombinePS" => StorePackedUniform(name, MaterialUniformPacking.PackSSRCombine(materialProperties, _materialShader)),
                "MotionBlurPS" => StorePackedUniform(name, MaterialUniformPacking.PackMotionBlur(materialProperties, _materialShader)),
                "AutoExposureAdaptPS" => StorePackedUniform(name, MaterialUniformPacking.PackAutoExposureAdapt(materialProperties, _materialShader)),
                "AutoExposureApplyPS" => StorePackedUniform(name, MaterialUniformPacking.PackAutoExposureApply(materialProperties, _materialShader)),
                "TAAResolvePS" => StorePackedUniform(name, MaterialUniformPacking.PackTAA(materialProperties, _materialShader)),
                "GTAOCalculatePS" => StorePackedUniform(name, MaterialUniformPacking.PackGTAOCalculate(materialProperties, _materialShader)),
                "GTAOBlurPS" => StorePackedUniform(name, MaterialUniformPacking.PackGTAOBlur(materialProperties, _materialShader)),
                "GTAOTemporalPS" => StorePackedUniform(name, MaterialUniformPacking.PackGTAOTemporal(materialProperties, _materialShader)),
                "ScreenSpaceDecalPS" => StorePackedUniform(name, MaterialUniformPacking.PackScreenSpaceDecal(materialProperties, _materialShader)),
                "ScreenSpaceShadowsPS" => StorePackedUniform(name, MaterialUniformPacking.PackScreenSpaceShadows(materialProperties, _materialShader)),
                "GridPS" => StorePackedUniform(name, MaterialUniformPacking.PackGrid(materialProperties, _materialShader)),
                _ => PackGenericUniform(name, buffers[i], materialProperties, shader),
            };
            if (!packed)
                continue;
        }

        _materialUniformsDirty = false;
    }

    /// <summary>Generic fallback: reflection-driven cbuffer packing for game-owned shaders.</summary>
    private bool PackGenericUniform(string name, ShaderBindingSlot slot, PropertyState? materialProperties, ShaderVariant shader)
    {
        // Explicitly opcode-bound buffers (SetBuffer, e.g. Paper's UiBrushes records) are
        // excluded: a reflection-packed transient must not shadow the explicit binding.
        if (_uniformBuffers.ContainsKey(name))
            return false;

        Rendering.GenericMaterialUniformPacking.GenericUniformOp[]? plan =
            Rendering.GenericMaterialUniformPacking.GetWritePlan(shader.Bytecode, name);
        if (plan == null)
            return false;

        int byteSize = slot.ByteSize;
        if (byteSize <= 0)
            return false;

        byte[] scratch = GetPooledScratch(byteSize);
        Rendering.GenericMaterialUniformPacking.Pack(plan, materialProperties, _materialShader, scratch);
        _transientUniformBuffers[name] = AllocateTransientUniform(scratch);
        return true;
    }

    private void EnsureUIVertexUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_uiVertexUniformsDirty || !RequiresBuffer(buffers, "UIVS"))
            return;

        StorePackedUniform("UIVS", _uiVertexUniforms);
        _uiVertexUniformsDirty = false;
    }

    private void EnsureUIFragmentUniformsBuffer(ShaderBindingSlot[] buffers)
    {
        if (!_uiFragmentUniformsDirty || !RequiresBuffer(buffers, "UIPS"))
            return;

        StorePackedUniform("UIPS", _uiFragmentUniforms);
        _uiFragmentUniformsDirty = false;
    }

    private static bool RequiresBuffer(ShaderBindingSlot[] buffers, string name)
    {
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name == name)
                return true;
        }

        return false;
    }

    private bool StorePackedUniform<T>(string name, T data) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1));
        _transientUniformBuffers[name] = AllocateTransientUniform(bytes);
        return true;
    }

    /// <summary>Zero-filled transient uniform buffer (AllocateTransientUniform clears the mapped
    /// range). Used by the missing-buffer degradation path in bind-group creation.</summary>
    private nint AllocateZeroedTransientUniform(int byteSize)
        => AllocateTransientUniform(byteSize > 0 ? new byte[byteSize] : ReadOnlySpan<byte>.Empty);

    // Draw-degradation log throttle: one loud error per unique missing binding per process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_degradedDraws = new();

    private static void LogDrawDegradationOnce(string what)
    {
        if (s_degradedDraws.TryAdd(what, 0))
            Debug.LogError($"[WebGPU] Draw degraded — missing {what}; bound zeros and continuing. (logged once per binding; fix the material/shader)");
    }

    private nint AllocateTransientUniform(ReadOnlySpan<byte> bytes)
    {
        ulong size = WebGPUFormats.Align((ulong)Math.Max(bytes.Length, 16), 16);
        nint gpu = CreateGpuBuffer(size, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc, mappedAtCreation: true);
        void* mapped = WebGPUNative.wgpuBufferGetMappedRange(gpu, 0, (nuint)size);
        if (mapped == null)
        {
            WebGPUNative.wgpuBufferRelease(gpu);
            throw new InvalidOperationException("wgpuBufferGetMappedRange returned null for a packed uniform.");
        }

        NativeMemory.Clear(mapped, (nuint)size);
        fixed (byte* source = bytes)
            Buffer.MemoryCopy(source, mapped, (long)size, bytes.Length);
        WebGPUNative.wgpuBufferUnmap(gpu);
        _transientUniformGpu.Add(gpu);
        return gpu;
    }

    /// <summary>Pooled scratch byte array for generic cbuffer packing (fallback path only).
    /// Grows on demand; not cleared between uses — the generic packer zeroes the destination.</summary>
    private byte[] GetPooledScratch(int size)
    {
        if (_genericUniformScratch == null || _genericUniformScratch.Length < size)
            _genericUniformScratch = new byte[size];
        return _genericUniformScratch;
    }

    private void ApplyObjectProperties(PropertyState? properties)
    {
        _objectUniforms = ObjectUniformsData.Identity;
        ClearObjectTextureBindings();
        if (properties != null)
        {
            if (properties._matrices.TryGetValue("xengine_ObjectToWorld", out Float4x4 objectToWorld))
                _objectUniforms.xengine_ObjectToWorld = objectToWorld;
            if (properties._matrices.TryGetValue("xengine_WorldToObject", out Float4x4 worldToObject))
                _objectUniforms.xengine_WorldToObject = worldToObject;
            if (properties._matrices.TryGetValue("xengine_PrevObjectToWorld", out Float4x4 previousObjectToWorld))
                _objectUniforms.xengine_PrevObjectToWorld = previousObjectToWorld;
            if (properties._ints.TryGetValue("_ObjectID", out int objectId))
                _objectUniforms._ObjectID = objectId;
            if (properties._ints.TryGetValue("_GIMode", out int giMode))
                _objectUniforms._GIMode = giMode;
            if (properties._ints.TryGetValue("_LightmapUV", out int lightmapUV))
                _objectUniforms._LightmapUV = lightmapUV;
            if (properties._ints.TryGetValue("_RenderingLayerMask", out int renderingLayerMask))
                _objectUniforms._RenderingLayerMask = renderingLayerMask;
            if (properties._vectors4.TryGetValue("_LightmapScaleOffset", out Float4 lightmapScaleOffset))
                _objectUniforms._LightmapScaleOffset = lightmapScaleOffset;
            if (properties._vectors4.TryGetValue("xengine_SHAr", out Float4 shAr)) _objectUniforms.xengine_SHAr = shAr;
            if (properties._vectors4.TryGetValue("xengine_SHAg", out Float4 shAg)) _objectUniforms.xengine_SHAg = shAg;
            if (properties._vectors4.TryGetValue("xengine_SHAb", out Float4 shAb)) _objectUniforms.xengine_SHAb = shAb;
            if (properties._vectors4.TryGetValue("xengine_SHBr", out Float4 shBr)) _objectUniforms.xengine_SHBr = shBr;
            if (properties._vectors4.TryGetValue("xengine_SHBg", out Float4 shBg)) _objectUniforms.xengine_SHBg = shBg;
            if (properties._vectors4.TryGetValue("xengine_SHBb", out Float4 shBb)) _objectUniforms.xengine_SHBb = shBb;
            if (properties._vectors4.TryGetValue("xengine_SHC", out Float4 shC)) _objectUniforms.xengine_SHC = shC;
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
    }

    private void ApplyMaterialTextureBindings()
    {
        ClearObjectTextureBindings();
        ShaderBindingSlot[]? textureSlots = _currentShader?.Bytecode?.BindingLayout?.Textures;
        MaterialUniformPacking.ApplyTextureBindings(_materialTextures, _materialProperties, _materialShader, textureSlots);
        MaterialUniformPacking.ApplyTextureBindings(_textures, _materialProperties, _materialShader, textureSlots);
    }

    private void ClearObjectTextureBindings()
    {
        foreach (string name in _objectTextureNames)
        {
            if (_materialTextures.TryGetValue(name, out GraphicsTexture materialTexture))
                _textures[name] = materialTexture;
            else if (PropertyState.s_globalTextures.TryGetValue(name, out Resources.Texture2D globalTexture) && globalTexture.IsValid())
                _textures[name] = globalTexture.Handle;
            else
                _textures.Remove(name);
        }

        _objectTextureNames.Clear();
    }

    private void BindNamedBuffer(string? name, GraphicsBuffer? buffer)
    {
        if (name == null || buffer == null)
            return;

        _uniformBuffers[name] = buffer;
        if (name == "GlobalUniforms")
        {
            _globalUniformsExplicitlyBound = true;
            _transientUniformBuffers.Remove(name);
        }
    }

    private void BindNamedTexture(string? name, GraphicsTexture? texture)
    {
        if (name == null)
            return;
        if (texture != null)
            _textures[name] = texture;
        else
            _textures.Remove(name);
    }

    private void ApplySetUniformFloat(string name, float value)
    {
        if (UIUniformPacking.TrySetFloat(ref _uiFragmentUniforms, name, value))
            _uiFragmentUniformsDirty = true;
    }

    private void ApplySetUniformInt(string name, int value)
    {
        if (name == "_ObjectID")
        {
            _objectUniforms._ObjectID = value;
            _objectUniformsDirty = true;
        }
        else if (UIUniformPacking.TrySetInt(ref _uiFragmentUniforms, name, value))
        {
            _uiFragmentUniformsDirty = true;
        }
    }

    private void ApplySetUniformVec2(string name, Float2 value)
    {
        if (UIUniformPacking.TrySetVector2(ref _uiFragmentUniforms, name, value))
            _uiFragmentUniformsDirty = true;
    }

    private void ApplySetUniformVec4(string name, Float4 value)
    {
        if (UIUniformPacking.TrySetVector4(ref _uiFragmentUniforms, name, value))
            _uiFragmentUniformsDirty = true;
    }

    private void ApplySetUniformMatrix(string name, Float4x4 value)
    {
        if (name == "projection")
        {
            _uiVertexUniforms.projection = value;
            _uiVertexUniformsDirty = true;
        }
        else if (name == "xengine_ObjectToWorld")
        {
            _objectUniforms.xengine_ObjectToWorld = value;
            _objectUniformsDirty = true;
        }
        else if (name == "xengine_WorldToObject")
        {
            _objectUniforms.xengine_WorldToObject = value;
            _objectUniformsDirty = true;
        }
        else if (name == "xengine_PrevObjectToWorld")
        {
            _objectUniforms.xengine_PrevObjectToWorld = value;
            _objectUniformsDirty = true;
        }
        else if (UIUniformPacking.TrySetMatrix(ref _uiFragmentUniforms, name, value))
        {
            _uiFragmentUniformsDirty = true;
        }
    }

    private bool TryGetBoundTexture(string name, out GraphicsTexture texture)
    {
        if (_textures.TryGetValue(name, out texture) && texture.Handle != 0)
            return true;
        if (name == "_Lightmap" && _textures.TryGetValue("_MainTex", out texture) && texture.Handle != 0)
            return true;
        if (PropertyState.s_globalTextures.TryGetValue(name, out Texture2D globalTexture)
            && globalTexture.IsValid() && globalTexture.Handle.Handle != 0)
        {
            texture = globalTexture.Handle;
            return true;
        }

        if (PropertyState.s_globalTexturesCube.TryGetValue(name, out Cubemap globalCube)
            && globalCube.IsValid() && globalCube.Handle.Handle != 0)
        {
            texture = globalCube.Handle;
            return true;
        }

        // Final fallback: bind a 1x1 white texture instead of leaving the bind group entry empty.
        // Missing texture bindings used to crash the draw; now the sampler reads white.
        // WebGPU-specific note: unlike Vulkan/D3D12 (which pre-create the fallback during device
        // Initialize), executing a command buffer from inside WebGPU Initialize disturbs later
        // frame state (broke the FXAA comparison in the pipeline contract tests), while the lazy
        // LoadDefault below is empirically safe here — WebGPU's CreateTexture assigns its handle
        // synchronously and its nested inline Execute is well-tolerated.
        texture = GetOrCreateWhiteTextureFallback();
        return true;
    }

    private GraphicsTexture GetOrCreateWhiteTextureFallback()
    {
        if (_whiteTextureFallbackRef != null && !_whiteTextureFallbackRef.IsDisposed)
            return _whiteTextureFallbackRef.Handle;

        _whiteTextureFallbackRef = Texture2D.LoadDefault(DefaultTexture.White);
        return _whiteTextureFallbackRef.Handle;
    }

    private static string? FindTextureNameForSampler(ShaderBindingSlot[] textures, int samplerSlot)
    {
        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i].Slot == samplerSlot)
                return textures[i].Name;
        }

        return null;
    }

    private void ReleaseTransientUniforms()
    {
        for (int i = 0; i < _transientUniformGpu.Count; i++)
        {
            if (_transientUniformGpu[i] != 0)
                WebGPUNative.wgpuBufferRelease(_transientUniformGpu[i]);
        }

        _transientUniformGpu.Clear();
        _transientUniformBuffers.Clear();
    }
}
