// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Vector;

namespace XEngine.Runtime.Backends.Metal;

/// <summary>
/// Uniform / texture ingestion and draw-time resource binding. Mirrors the Vulkan command
/// translator's data model (PropertyState globals, material-block packing, object uniforms) but
/// binds per-draw through explicit Metal API calls instead of descriptor sets: every binding
/// slot's physical index is the per-kind Metal index assigned at MetalLib reflection
/// (see <see cref="RHI.Shaders.RuntimeSlangReflection"/>), and vertex data owns the dedicated
/// high buffer index <see cref="MetalVertexDataBufferIndex"/> so it can never collide with a
/// cbuffer slot.
/// </summary>
internal sealed unsafe partial class MetalCommandTranslator
{
    /// <summary>
    /// Buffer index reserved for instance data. Metal shares one per-stage buffer index space
    /// between vertex fetch (via the vertex descriptor) and shader buffer arguments. Mesh data
    /// uses the next index so both streams stay above every cbuffer slot.
    /// </summary>
    internal const uint MetalInstanceDataBufferIndex = 29;
    internal const uint MetalVertexDataBufferIndex = 30;

    private const int UniformSlotLimit = (int)MetalInstanceDataBufferIndex;
    private const int InlineUniformByteLimit = 4 * 1024;

    private readonly Dictionary<string, GraphicsBuffer> _uniformBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphicsTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphicsTexture> _materialTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetalPackedUniform> _packedUniforms = new(StringComparer.Ordinal);
    private readonly List<string> _objectTextureNames = new();
    private readonly HashSet<string> _degradationWarnings = new(StringComparer.Ordinal);
    private byte[]? _genericUniformScratch;

    private PropertyState? _materialProperties;
    private Resources.Shader? _materialShader;
    private PropertyState? _instanceMaterialOverrides;
    private PropertyState? _instanceTextureState;
    private readonly PropertyState _mergedMaterialProperties = new();
    private readonly PropertyState _mergedTextureProperties = new();

    private ObjectUniformsData _objectUniforms = ObjectUniformsData.Identity;
    private UIVertexUniformsData _uiVertexUniforms;
    private UIFragmentUniformsData _uiFragmentUniforms;
    private bool _materialUniformsDirty = true;
    private bool _standardLightingDirty = true;
    private bool _uiVertexUniformsDirty;
    private bool _uiFragmentUniformsDirty;
    private byte[]? _objectUniformScratch;

    private GlobalUniformsData _globalUniformsSnapshot;
    private bool _hasGlobalUniformsSnapshot;
    private bool _globalUniformsExplicitlyBound;

    private MetalTextureResource? _whiteTextureFallback;
    private MetalTextureResource? _whiteCubeTextureFallback;

    /// <summary>Texture readbacks deferred until the owning command buffer completes. Face -1 = plain 2D.</summary>
    private readonly List<(MetalTextureResource Resource, int Mip, int Face, byte[]? Destination, nint DestinationPtr)> _pendingReadbacks = new();

    private readonly struct MetalPackedUniform
    {
        public MetalPackedUniform(nint buffer, int capacity, byte[] source, bool usesInlineBytes)
        {
            Buffer = buffer;
            Capacity = capacity;
            Source = source;
            UsesInlineBytes = usesInlineBytes;
        }

        public nint Buffer { get; }
        public int Capacity { get; }
        public byte[] Source { get; }
        public bool UsesInlineBytes { get; }
    }

    /// <summary>Per-submission reset; mirrors the Vulkan translator's Translate preamble.</summary>
    private void ResetFrameBindings(CommandBuffer recorded)
    {
        _uniformBuffers.Clear();
        _textures.Clear();
        _objectUniforms = ObjectUniformsData.Identity;
        _uiVertexUniforms = default;
        _uiVertexUniformsDirty = true;
        _uiFragmentUniforms = default;
        _uiFragmentUniformsDirty = true;
        _globalUniformsSnapshot = recorded._globalUniformsSnapshot;
        _hasGlobalUniformsSnapshot = recorded._hasGlobalUniformsSnapshot;
        _globalUniformsExplicitlyBound = false;
        _materialProperties = null;
        _materialShader = null;
        _instanceMaterialOverrides = null;
        _instanceTextureState = null;
        _materialUniformsDirty = true;
        _standardLightingDirty = true;
    }

    // ───────────────────────────── Ingestion helpers ─────────────────────────────

    private void IngestGlobalValue(string name, Action store)
    {
        store();
        _standardLightingDirty = true;
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
    }

    private void ApplyMaterialTextureBindings()
    {
        ClearObjectTextureBindings();
        ShaderBindingSlot[]? textureSlots = _currentShader?.Bytecode?.BindingLayout?.Textures;
        PropertyState? effective = EffectiveTextureProperties;
        MaterialUniformPacking.ApplyTextureBindings(_materialTextures, effective, _materialShader, textureSlots);
        MaterialUniformPacking.ApplyTextureBindings(_textures, effective, _materialShader, textureSlots);
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

    private PropertyState? EffectiveTextureProperties
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

    private void WarnOnce(string kind, string message)
    {
        if (_degradationWarnings.Add(kind))
            Console.Error.WriteLine($"MetalCommandTranslator: {message}");
    }

    // ───────────────────────────── Draw-time binding ─────────────────────────────

    /// <summary>
    /// Bind every declared cbuffer/texture/sampler of the current shader for this draw. Packed
    /// blocks are (re)built on demand; missing textures fall back to a 1×1 white texture so a
    /// bad material degrades instead of terminating the frame.
    /// </summary>
    private void BindShaderResources(ShaderVariant shader)
    {
        CompiledShaderBytecode? bytecode = shader.Bytecode;
        if (bytecode?.BindingLayout == null)
            return;

        ShaderBindingLayout layout = bytecode.BindingLayout;
        bool traceMetal = Environment.GetEnvironmentVariable("XENGINE_TRACE_METAL") == "1";
        if (traceMetal)
        {
            using var sw = new System.IO.StreamWriter("/tmp/metal_bind.log", append: true);
            sw.WriteLine($"variant={shader.Id} buffers=[{TraceBufferBindings(layout.Buffers)}] textures=[{TraceTextureBindings(layout.Textures)}]");
        }
        foreach (ShaderBindingSlot slot in layout.Buffers)
        {
            if (slot.Kind is not (ShaderBindingKind.Buffer or ShaderBindingKind.StructuredBuffer or ShaderBindingKind.StorageBuffer))
                continue;
            if (slot.Name == "ObjectUniforms")
            {
                ReadOnlySpan<byte> objectBytes = ObjectUniformBytes(slot.ByteSize);
                if ((slot.Stages & ShaderStageMask.Vertex) != 0)
                {
                    int vertexIndex = PhysicalIndex(slot, ShaderStageMask.Vertex);
                    if (vertexIndex >= UniformSlotLimit)
                        throw new InvalidOperationException($"Metal vertex buffer slot {vertexIndex} for '{slot.Name}' exceeds the reserved range below vertex data ({UniformSlotLimit}).");
                    MetalNative.SetVertexBytes(_renderEncoder, objectBytes, (nuint)vertexIndex);
                }
                if ((slot.Stages & ShaderStageMask.Fragment) != 0)
                {
                    int fragmentIndex = PhysicalIndex(slot, ShaderStageMask.Fragment);
                    if (fragmentIndex >= UniformSlotLimit)
                        throw new InvalidOperationException($"Metal fragment buffer slot {fragmentIndex} for '{slot.Name}' exceeds the reserved range below vertex data ({UniformSlotLimit}).");
                    MetalNative.SetFragmentBytes(_renderEncoder, objectBytes, (nuint)fragmentIndex);
                }
                continue;
            }
            nint gpu = ResolveUniformBuffer(slot);
            if (gpu == 0 && _packedUniforms.TryGetValue(slot.Name, out MetalPackedUniform packed) &&
                packed.UsesInlineBytes)
            {
                BindUniformBytes(slot, packed.Source.AsSpan(0, slot.ByteSize));
                continue;
            }
            if (gpu == 0)
                continue;
            if ((slot.Stages & ShaderStageMask.Vertex) != 0)
            {
                int vertexIndex = PhysicalIndex(slot, ShaderStageMask.Vertex);
                if (vertexIndex >= UniformSlotLimit)
                    throw new InvalidOperationException($"Metal vertex buffer slot {vertexIndex} for '{slot.Name}' exceeds the reserved range below vertex data ({UniformSlotLimit}).");
                if (traceMetal)
                    Console.WriteLine($"[MetalBindings] vertex {slot.Name} buffer={gpu:x} index={vertexIndex}");
                MetalNative.SetVertexBuffer(_renderEncoder, gpu, 0, (uint)vertexIndex);
            }
            if ((slot.Stages & ShaderStageMask.Fragment) != 0)
            {
                int fragmentIndex = PhysicalIndex(slot, ShaderStageMask.Fragment);
                if (fragmentIndex >= UniformSlotLimit)
                    throw new InvalidOperationException($"Metal fragment buffer slot {fragmentIndex} for '{slot.Name}' exceeds the reserved range below vertex data ({UniformSlotLimit}).");
                if (traceMetal)
                    Console.WriteLine($"[MetalBindings] fragment {slot.Name} buffer={gpu:x} index={fragmentIndex}");
                MetalNative.SetFragmentBuffer(_renderEncoder, gpu, 0, (uint)fragmentIndex);
            }
        }

        foreach (ShaderBindingSlot slot in layout.Textures)
        {
            MetalTextureResource? resource = TryGetBoundTextureResource(slot.Name, out string failure)
                ? ResolveResource(slot.Name, failure)
                : EnsureWhiteTextureFallback(slot.ResourceShape);
            if (resource == null || resource.Texture == 0)
            {
                WarnOnce($"texture:{slot.Name}", $"texture '{slot.Name}' is not device-ready; binding skipped.");
                continue;
            }

            if ((slot.Stages & ShaderStageMask.Vertex) != 0)
                MetalNative.SetVertexTexture(_renderEncoder, resource.Texture,
                    (uint)PhysicalIndex(slot, ShaderStageMask.Vertex));
            if ((slot.Stages & ShaderStageMask.Fragment) != 0)
                MetalNative.SetFragmentTexture(_renderEncoder, resource.Texture,
                    (uint)PhysicalIndex(slot, ShaderStageMask.Fragment));
        }

        foreach (ShaderBindingSlot slot in layout.Samplers)
        {
            ShaderBindingSlot textureSlot = FindTextureForSampler(layout.Textures, slot.Slot);
            MetalTextureResource? samplerResource = TryGetBoundTextureResource(textureSlot.Name, out _)
                ? ResolveResource(textureSlot.Name, "unavailable")
                : EnsureWhiteTextureFallback(textureSlot.ResourceShape);
            if (samplerResource == null)
                continue;

            EnsureSampler(samplerResource);
            if ((slot.Stages & ShaderStageMask.Fragment) != 0)
                MetalNative.SetFragmentSampler(_renderEncoder, samplerResource.Sampler,
                    (uint)PhysicalIndex(slot, ShaderStageMask.Fragment));
        }
    }

    private string TraceBufferBindings(ShaderBindingSlot[] slots)
    {
        if (slots.Length == 0)
            return string.Empty;
        var entries = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            nint buffer = slot.Name == "ObjectUniforms" ? 0 : ResolveUniformBuffer(slot);
            bool inline = slot.Name == "ObjectUniforms" ||
                (buffer == 0 && _packedUniforms.TryGetValue(slot.Name, out MetalPackedUniform packed) &&
                 packed.UsesInlineBytes);
            string state = inline ? "inline" : buffer != 0 ? "ok" : "MISSING";
            entries[i] = $"{slot.Name}@{PhysicalIndex(slot)}={state}";
        }
        return string.Join(", ", entries);
    }

    private void BindUniformBytes(ShaderBindingSlot slot, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > InlineUniformByteLimit)
            throw new InvalidOperationException(
                $"Metal inline uniform '{slot.Name}' is {bytes.Length} bytes; setBytes supports at most {InlineUniformByteLimit} bytes.");

        if ((slot.Stages & ShaderStageMask.Vertex) != 0)
        {
            int vertexIndex = PhysicalIndex(slot, ShaderStageMask.Vertex);
            if (vertexIndex >= UniformSlotLimit)
                throw new InvalidOperationException($"Metal vertex buffer slot {vertexIndex} for '{slot.Name}' exceeds the reserved range below vertex data ({UniformSlotLimit}).");
            MetalNative.SetVertexBytes(_renderEncoder, bytes, (nuint)vertexIndex);
        }
        if ((slot.Stages & ShaderStageMask.Fragment) != 0)
        {
            int fragmentIndex = PhysicalIndex(slot, ShaderStageMask.Fragment);
            if (fragmentIndex >= UniformSlotLimit)
                throw new InvalidOperationException($"Metal fragment buffer slot {fragmentIndex} for '{slot.Name}' exceeds the reserved range below vertex data ({UniformSlotLimit}).");
            MetalNative.SetFragmentBytes(_renderEncoder, bytes, (nuint)fragmentIndex);
        }
    }

    private string TraceTextureBindings(ShaderBindingSlot[] slots)
    {
        if (slots.Length == 0)
            return string.Empty;
        var entries = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            string state = TryGetBoundTextureResource(slot.Name, out _) ? "ok" : "FALLBACK";
            entries[i] = $"{slot.Name}@{PhysicalIndex(slot)}={state}";
        }
        return string.Join(", ", entries);
    }

    private MetalTextureResource? ResolveResource(string name, string failure)
    {
        if (TryGetBoundTexture(name, out GraphicsTexture texture) &&
            _device.Textures.TryGetValue(texture.Handle, out MetalTextureResource? resource))
            return resource;
        WarnOnce($"texture:{name}", $"texture '{name}' is not device-ready ({failure}).");
        return null;
    }

    private bool TryGetBoundTextureResource(string name, out string failure)
    {
        failure = "no binding";
        if (TryGetBoundTexture(name, out GraphicsTexture texture) && texture.Handle != 0)
        {
            failure = "";
            return true;
        }
        if (name == "_Lightmap" && _textures.ContainsKey("_MainTex"))
        {
            failure = "";
            return true;
        }
        return false;
    }

    private static int PhysicalIndex(ShaderBindingSlot slot) =>
        slot.HasPhysicalLocation ? slot.PhysicalBindings[0].Binding : slot.Slot;

    internal static int PhysicalIndex(ShaderBindingSlot slot, ShaderStageMask stage)
    {
        if (!slot.HasPhysicalLocation)
            return slot.Slot;
        foreach (ShaderPhysicalBinding physical in slot.PhysicalBindings)
        {
            if ((physical.Stages & stage) != 0)
                return physical.Binding;
        }
        throw new InvalidOperationException(
            $"Metal resource '{slot.Name}' has no physical binding for {stage}.");
    }

    private static ShaderBindingSlot FindTextureForSampler(ShaderBindingSlot[] textures, int samplerSlot)
    {
        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i].Slot == samplerSlot)
                return textures[i];
        }
        throw new NotSupportedException($"Metal sampler slot s{samplerSlot} has no texture at matching t{samplerSlot}.");
    }

    /// <summary>
    /// Resolve the GPU buffer for one cbuffer slot: named packed blocks are built from the
    /// shared CPU-side packers; explicit SetUniformBuffer uploads bind directly.
    /// </summary>
    private nint ResolveUniformBuffer(ShaderBindingSlot slot)
    {
        switch (slot.Name)
        {
            case "GlobalUniforms":
                if (_globalUniformsExplicitlyBound)
                    break;
                if (_hasGlobalUniformsSnapshot)
                {
                    ReadOnlySpan<byte> globalBytes = BytesOf(in _globalUniformsSnapshot);
                    if (slot.ByteSize >= globalBytes.Length + 16)
                    {
                        Span<byte> metalGlobalBytes = stackalloc byte[globalBytes.Length + 16];
                        CopyGlobalUniformsForMetal(globalBytes, metalGlobalBytes);
                        return UploadPackedUniform(slot.Name, metalGlobalBytes, slot.ByteSize);
                    }
                    return UploadPackedUniform(slot.Name, globalBytes, slot.ByteSize);
                }
                break;
            case "StandardLighting":
                if (_standardLightingDirty ||
                    !_packedUniforms.TryGetValue(slot.Name, out MetalPackedUniform lightingPacked) ||
                    lightingPacked.Capacity < slot.ByteSize)
                {
                    StandardLightingUniformsData lighting = MaterialUniformPacking.PackStandardLighting();
                    nint buffer = UploadPackedUniform(slot.Name, BytesOf(in lighting), slot.ByteSize);
                    _standardLightingDirty = false;
                    return buffer;
                }
                return lightingPacked.Buffer;
            case "StandardShadows":
            {
                StandardShadowsUniformsData shadows = MaterialUniformPacking.PackStandardShadows();
                return UploadPackedUniform(slot.Name, BytesOf(in shadows), slot.ByteSize);
            }
            case "StandardReflectionProbes":
            {
                StandardReflectionProbesUniformsData probes = MaterialUniformPacking.PackStandardReflectionProbes();
                return UploadPackedUniform(slot.Name, BytesOf(in probes), slot.ByteSize);
            }
            case "StandardLightCookies":
            {
                StandardLightCookiesUniformsData cookies = MaterialUniformPacking.PackStandardLightCookies();
                return UploadPackedUniform(slot.Name, BytesOf(in cookies), slot.ByteSize);
            }
            // Paper/UI.shader declares these blocks as UIVS/UIPS. The other names
            // belong to older UI shaders; keep both so editor and game UI bind the
            // same frame uniforms on Metal as they do on Vulkan.
            case "UIVS":
            case "UIVertexUniforms":
                return UploadPackedUniform(slot.Name, BytesOf(in _uiVertexUniforms), slot.ByteSize);
            case "UIPS":
            case "UIFragmentUniforms":
                return UploadPackedUniform(slot.Name, BytesOf(in _uiFragmentUniforms), slot.ByteSize);
            case "MaterialUniforms":
            case "BlitMaterial":
            case "UnlitMaterial":
            case "ParticleMaterial":
            case "VfxOutputMaterial":
            case "DefaultUIMaterial":
            case "StandardMaterial":
            case "AnisotropicMaterial":
            case "TerrainMaterial":
            case "GrassMaterial":
            case "PrepassMaterial":
            case "ShadowMaterial":
            case "GradientPS":
            case "CubemapSkyboxPS":
            case "GizmoIconMaterial":
            case "SkyVS":
            case "TonemapperPS":
            case "FrameDebugPreviewPS":
            case "BlurDownPS":
            case "BlurUpPS":
            case "FXAAPS":
            case "SMAAPS":
            case "FogTemporalPS":
            case "FogMarchPS":
            case "FogCompositePS":
            case "RefractionMaterial":
            case "CinematicEffectsPS":
            case "BloomThresholdPS":
            case "BloomCompositePS":
            case "BokehDoFPS":
            case "SSRRayCastPS":
            case "SSRSceneBlurPS":
            case "SSRResolvePS":
            case "SSRTemporalPS":
            case "SSRCombinePS":
            case "MotionBlurPS":
            case "AutoExposureAdaptPS":
            case "AutoExposureApplyPS":
            case "TAAResolvePS":
            case "SsgiGatherPS":
            case "SsgiTemporalPS":
            case "SsgiBlurPS":
            case "SsgiCompositePS":
            case "SsgiProbePS":
            case "GTAOCalculatePS":
            case "GTAOBlurPS":
            case "GTAOTemporalPS":
            case "ScreenSpaceDecalPS":
            case "ScreenSpaceShadowsPS":
            case "GridPS":
                return ResolvePackedMaterialBuffer(slot.Name, slot.ByteSize);
        }

        if (_uniformBuffers.TryGetValue(slot.Name, out GraphicsBuffer? explicitBuffer) && explicitBuffer.Handle != 0 &&
            _device.Buffers.TryGetValue(explicitBuffer.Handle, out MetalBufferResource? resource) && resource.Buffer != 0)
        {
            return resource.Buffer;
        }

        if (slot.ByteSize > 0 && _currentShader?.Bytecode is { } bytecode &&
            GenericMaterialUniformPacking.GetWritePlan(bytecode, slot.Name) is { } plan)
        {
            if (_genericUniformScratch == null || _genericUniformScratch.Length < slot.ByteSize)
                _genericUniformScratch = new byte[slot.ByteSize];

            Span<byte> packed = _genericUniformScratch.AsSpan(0, slot.ByteSize);
            GenericMaterialUniformPacking.Pack(plan, EffectiveMaterialProperties, _materialShader, packed);
            return UploadPackedUniform(slot.Name, packed, slot.ByteSize);
        }

        if (slot.ByteSize > 0)
        {
            WarnOnce($"cbuffer:{slot.Name}", $"cbuffer '{slot.Name}' has no bound data; binding zeros.");
            Span<byte> zeros = stackalloc byte[Math.Min(slot.ByteSize, 512)];
            zeros.Clear();
            return UploadPackedUniform(slot.Name, zeros, slot.ByteSize);
        }
        return 0;
    }

    private nint ResolvePackedMaterialBuffer(string name, int requiredByteSize)
    {
        int reflectedCapacity = GetMetalMaterialBufferCapacity(name, requiredByteSize);
        if (!_materialUniformsDirty &&
            _packedUniforms.TryGetValue(name, out MetalPackedUniform cached) &&
            cached.Capacity >= reflectedCapacity)
            return cached.Buffer;

        PropertyState? materialProperties = EffectiveMaterialProperties;
        byte[] bytes;
        if (name == "MaterialUniforms")
        {
            // Generic material block: no per-material packer matched the cbuffer name, pack the
            // standard block so at least _MainColor-driven paths stay visible.
            StandardMaterialUniformsData data = MaterialUniformPacking.PackStandard(materialProperties, _materialShader);
            bytes = ToBytes(in data);
        }
        else
        {
            bytes = PackMaterialBlock(name, materialProperties);
            if (bytes.Length == 0)
            {
                WarnOnce($"material:{name}", $"material block '{name}' has no packer; binding skipped.");
                return 0;
            }
        }

        nint buffer = UploadPackedUniform(name, bytes, Math.Max(reflectedCapacity, bytes.Length));
        _materialUniformsDirty = false;
        return buffer;
    }

    private static int GetMetalMaterialBufferCapacity(string name, int reflectedByteSize)
    {
        // The Metal ABI lays a trailing float3 in the next 16-byte cbuffer register for the
        // Toon shadow block, while the shared CutoutMaterialUniformsData packer ends at 48 B.
        // Keep one zeroed register after those two cutout blocks so Metal Validation sees the
        // full 64-byte argument range even when reflection reports only the shared 48-byte data.
        return name is "PrepassMaterial" or "ShadowMaterial"
            ? checked(reflectedByteSize + 16)
            : reflectedByteSize;
    }

    private byte[] PackMaterialBlock(string name, PropertyState? properties)
    {
        Resources.Shader? shader = _materialShader;
        switch (name)
        {
            case "BlitMaterial": return ToBytes(MaterialUniformPacking.PackBlit(properties, shader));
            case "UnlitMaterial": return ToBytes(MaterialUniformPacking.PackUnlit(properties, shader));
            case "ParticleMaterial": return ToBytes(MaterialUniformPacking.PackParticle(properties, shader));
            case "VfxOutputMaterial": return ToBytes(MaterialUniformPacking.PackVfxOutput(properties, shader));
            case "DefaultUIMaterial": return ToBytes(MaterialUniformPacking.PackDefaultUI(properties, shader));
            case "StandardMaterial": return ToBytes(MaterialUniformPacking.PackStandard(properties, shader));
            case "AnisotropicMaterial": return ToBytes(MaterialUniformPacking.PackAnisotropic(properties, shader));
            case "TerrainMaterial": return ToBytes(MaterialUniformPacking.PackTerrain(properties, shader));
            case "GrassMaterial": return ToBytes(MaterialUniformPacking.PackGrass(properties, shader));
            case "PrepassMaterial":
            case "ShadowMaterial": return ToBytes(MaterialUniformPacking.PackCutout(properties, shader));
            case "GradientPS": return ToBytes(MaterialUniformPacking.PackGradientSkybox(properties, shader));
            case "CubemapSkyboxPS": return ToBytes(MaterialUniformPacking.PackCubemapSkybox(properties, shader));
            case "GizmoIconMaterial": return ToBytes(MaterialUniformPacking.PackGizmoIcon(properties, shader));
            case "SkyVS": return ToBytes(MaterialUniformPacking.PackProceduralSkybox(properties, shader));
            case "TonemapperPS": return ToBytes(MaterialUniformPacking.PackTonemapper(properties, shader));
            case "FrameDebugPreviewPS": return ToBytes(MaterialUniformPacking.PackFrameDebugPreview(properties, shader));
            case "BlurDownPS":
            case "BlurUpPS": return ToBytes(MaterialUniformPacking.PackUIBlur(properties, shader));
            case "FXAAPS": return ToBytes(MaterialUniformPacking.PackFXAA(properties, shader));
            case "SMAAPS": return ToBytes(MaterialUniformPacking.PackSMAA(properties, shader));
            case "FogTemporalPS": return ToBytes(MaterialUniformPacking.PackFogTemporal(properties));
            case "FogMarchPS": return ToBytes(MaterialUniformPacking.PackFogMarch(properties));
            case "FogCompositePS": return ToBytes(MaterialUniformPacking.PackFogComposite(properties));
            case "RefractionMaterial": return ToBytes(MaterialUniformPacking.PackRefraction(properties, shader));
            case "CinematicEffectsPS": return ToBytes(MaterialUniformPacking.PackCinematicEffects(properties));
            case "BloomThresholdPS": return ToBytes(MaterialUniformPacking.PackBloomThreshold(properties, shader));
            case "BloomCompositePS": return ToBytes(MaterialUniformPacking.PackBloomComposite(properties, shader));
            case "BokehDoFPS": return ToBytes(MaterialUniformPacking.PackBokehDoF(properties, shader));
            case "SSRRayCastPS": return ToBytes(MaterialUniformPacking.PackSSRRayCast(properties, shader));
            case "SSRSceneBlurPS": return ToBytes(MaterialUniformPacking.PackSSRSceneBlur(properties, shader));
            case "SSRResolvePS": return ToBytes(MaterialUniformPacking.PackSSRResolve(properties, shader));
            case "SSRTemporalPS": return ToBytes(MaterialUniformPacking.PackSSRTemporal(properties, shader));
            case "SSRCombinePS": return ToBytes(MaterialUniformPacking.PackSSRCombine(properties, shader));
            case "MotionBlurPS": return ToBytes(MaterialUniformPacking.PackMotionBlur(properties, shader));
            case "AutoExposureAdaptPS": return ToBytes(MaterialUniformPacking.PackAutoExposureAdapt(properties, shader));
            case "AutoExposureApplyPS": return ToBytes(MaterialUniformPacking.PackAutoExposureApply(properties, shader));
            case "TAAResolvePS": return ToBytes(MaterialUniformPacking.PackTAA(properties, shader));
            case "SsgiGatherPS": return ToBytes(MaterialUniformPacking.PackSsgiGather(properties, shader));
            case "SsgiTemporalPS": return ToBytes(MaterialUniformPacking.PackSsgiTemporal(properties, shader));
            case "SsgiBlurPS": return ToBytes(MaterialUniformPacking.PackSsgiBlur(properties, shader));
            case "SsgiCompositePS": return ToBytes(MaterialUniformPacking.PackSsgiComposite(properties, shader));
            case "SsgiProbePS": return ToBytes(MaterialUniformPacking.PackSsgiProbe(properties, shader));
            case "GTAOCalculatePS": return ToBytes(MaterialUniformPacking.PackGTAOCalculate(properties, shader));
            case "GTAOBlurPS": return ToBytes(MaterialUniformPacking.PackGTAOBlur(properties, shader));
            case "GTAOTemporalPS": return ToBytes(MaterialUniformPacking.PackGTAOTemporal(properties, shader));
            case "ScreenSpaceDecalPS": return ToBytes(MaterialUniformPacking.PackScreenSpaceDecal(properties, shader));
            case "ScreenSpaceShadowsPS": return ToBytes(MaterialUniformPacking.PackScreenSpaceShadows(properties, shader));
            case "GridPS": return ToBytes(MaterialUniformPacking.PackGrid(properties, shader));
            default:
                WarnOnce($"material:{name}", $"material block '{name}' has no packer; binding skipped.");
                return [];
        }
    }

    private static byte[] ToBytes<T>(in T data) where T : unmanaged
    {
        byte[] bytes = new byte[Unsafe.SizeOf<T>()];
        Unsafe.As<byte, T>(ref bytes[0]) = data;
        return bytes;
    }

    private static ReadOnlySpan<byte> BytesOf<T>(in T data) where T : unmanaged =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in Unsafe.AsRef(in data), 1));

    private ReadOnlySpan<byte> ObjectUniformBytes(int reflectedByteSize)
    {
        ReadOnlySpan<byte> source = BytesOf(in _objectUniforms);
        int requiredSize = Math.Max(source.Length, reflectedByteSize);
        if (requiredSize == source.Length)
            return source;

        if (_objectUniformScratch == null || _objectUniformScratch.Length < requiredSize)
            _objectUniformScratch = new byte[requiredSize];
        Span<byte> padded = _objectUniformScratch.AsSpan(0, requiredSize);
        padded.Clear();
        source.CopyTo(padded);
        return padded;
    }

    

    /// <summary>
    /// Persistent per-name uniform buffer. Rewritten only when the packed bytes actually change,
    /// so repeated draws against unchanged state stay memcpy-free.
    /// </summary>
    private nint UploadPackedUniform(string name, ReadOnlySpan<byte> bytes, int capacity = 0)
    {
        capacity = capacity < bytes.Length ? bytes.Length : capacity;
        bool useInlineBytes = capacity <= InlineUniformByteLimit;
        if (_packedUniforms.TryGetValue(name, out MetalPackedUniform existing))
        {
            if (existing.UsesInlineBytes == useInlineBytes && existing.Capacity >= capacity &&
                PackedUniformMatches(existing, bytes))
                return existing.Buffer;

            if (useInlineBytes)
            {
                if (existing.Buffer != 0)
                    MetalNative.Release(existing.Buffer);
                byte[] inlineBytes = new byte[capacity];
                bytes.CopyTo(inlineBytes);
                _packedUniforms[name] = new MetalPackedUniform(0, capacity, inlineBytes, usesInlineBytes: true);
                return 0;
            }

            // Earlier draws in this command buffer may still read the previous bytes. Allocate
            // fresh storage for changed large blocks instead of rewriting a buffer already
            // captured by an encoded draw; Metal retains encoded resources through completion.
            nint buffer = MetalNative.NewBuffer(_device.NativeDevice, (nuint)capacity, 0);
            if (buffer == 0)
                throw new InvalidOperationException("MTLDevice newBufferWithLength returned nil for a packed uniform.");
            WriteBuffer(buffer, 0, bytes);
            ClearPackedUniformTail(buffer, bytes.Length, capacity);
            _packedUniforms[name] = new MetalPackedUniform(buffer, capacity, bytes.ToArray(), usesInlineBytes: false);
            if (existing.Buffer != 0)
                MetalNative.Release(existing.Buffer);
            return buffer;
        }

        if (useInlineBytes)
        {
            byte[] inlineBytes = new byte[capacity];
            bytes.CopyTo(inlineBytes);
            _packedUniforms[name] = new MetalPackedUniform(0, capacity, inlineBytes, usesInlineBytes: true);
            return 0;
        }

        nint created = MetalNative.NewBuffer(_device.NativeDevice, (nuint)capacity, 0);
        if (created == 0)
            throw new InvalidOperationException("MTLDevice newBufferWithLength returned nil for a packed uniform.");
        WriteBuffer(created, 0, bytes);
        ClearPackedUniformTail(created, bytes.Length, capacity);
        _packedUniforms[name] = new MetalPackedUniform(created, capacity, bytes.ToArray(), usesInlineBytes: false);
        return created;
    }

    private static bool PackedUniformMatches(MetalPackedUniform existing, ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> cached = existing.Source;
        if (!existing.UsesInlineBytes)
            return cached.SequenceEqual(bytes);
        if (bytes.Length > cached.Length || !cached[..bytes.Length].SequenceEqual(bytes))
            return false;
        for (int i = bytes.Length; i < cached.Length; i++)
        {
            if (cached[i] != 0)
                return false;
        }
        return true;
    }

    private static void ClearPackedUniformTail(nint buffer, int writtenBytes, int capacity)
    {
        int tailBytes = capacity - writtenBytes;
        if (tailBytes <= 0)
            return;
        nint contents = MetalNative.BufferContents(buffer);
        if (contents == 0)
            throw new InvalidOperationException("MTLBuffer contents returned nil for a packed uniform.");
        new Span<byte>((void*)(contents + writtenBytes), tailBytes).Clear();
    }

    internal static void CopyGlobalUniformsForMetal(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        const int alignmentGapOffset = 528;
        const int alignmentGapBytes = 16;
        if (source.Length != GlobalUniformsData.SizeInBytes ||
            destination.Length != source.Length + alignmentGapBytes ||
            alignmentGapOffset > source.Length)
        {
            throw new ArgumentException("Global uniforms do not match the reflected Metal layout.");
        }

        destination.Clear();
        source[..alignmentGapOffset].CopyTo(destination);
        source[alignmentGapOffset..].CopyTo(destination[(alignmentGapOffset + alignmentGapBytes)..]);
    }

    internal void ReleasePackedUniforms()
    {
        foreach (MetalPackedUniform packed in _packedUniforms.Values)
        {
            if (packed.Buffer != 0)
                MetalNative.Release(packed.Buffer);
        }
        _packedUniforms.Clear();
    }

    /// <summary>Whether this submission recorded deferred texture readbacks.</summary>
    public bool HasPendingReadbacks => _pendingReadbacks.Count > 0;

    /// <summary>
    /// Copy out texture readbacks queued by GetTextureData/GetTextureDataPtr/
    /// GetTextureCubeFaceData. Must run after the owning MTLCommandBuffer completed — getBytes
    /// has no implicit sync with in-flight encoders, so an earlier copy would race the draws
    /// that produce the pixels.
    /// </summary>
    public void FlushPendingReadbacks_NoLock()
    {
        foreach ((MetalTextureResource resource, int mip, int face, byte[]? destination, nint destinationPtr) in _pendingReadbacks)
        {
            if (resource.Texture == 0)
                continue;
            byte[] pixels = face < 0
                ? _device.ReadTexture2D(resource, (uint)Math.Max(0, mip))
                : _device.ReadTextureCubeFace(resource, face, mip);
            if (destination != null)
                System.Buffer.BlockCopy(pixels, 0, destination, 0, Math.Min(pixels.Length, destination.Length));
            else if (destinationPtr != 0)
                Marshal.Copy(pixels, 0, destinationPtr, pixels.Length);
        }
        _pendingReadbacks.Clear();
    }

    // ───────────────────────────── Texture fallback ─────────────────────────────

    private bool TryGetBoundTexture(string name, out GraphicsTexture texture)
    {
        if (_textures.TryGetValue(name, out texture!) && texture.Handle != 0)
            return true;
        if (name == "_Lightmap" && _textures.TryGetValue("_MainTex", out texture!) && texture.Handle != 0)
            return true;
        if (PropertyState.s_globalTextures.TryGetValue(name, out Resources.Texture2D globalTexture)
            && globalTexture.IsValid() && globalTexture.Handle.Handle != 0)
        {
            texture = globalTexture.Handle;
            return true;
        }

        if (PropertyState.s_globalTexturesCube.TryGetValue(name, out Resources.Cubemap cube)
            && cube.IsValid() && cube.Handle.Handle != 0)
        {
            texture = cube.Handle;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 1×1 white texture created directly through Metal. This runs during command translation;
    /// creating a GraphicsTexture would enqueue a nested submission and overwrite this
    /// translator's active encoder state.
    /// </summary>
    private MetalTextureResource? EnsureWhiteTextureFallback(ShaderResourceShape shape)
    {
        return shape switch
        {
            ShaderResourceShape.Unknown or ShaderResourceShape.Texture2D => EnsureWhite2DTextureFallback(),
            ShaderResourceShape.TextureCube => EnsureWhiteCubeTextureFallback(),
            _ => throw new NotSupportedException(
                $"Metal has no default fallback texture for missing {shape} resources."),
        };
    }

    private MetalTextureResource EnsureWhite2DTextureFallback()
    {
        if (_whiteTextureFallback is { Texture: not 0 } existing)
            return existing;

        uint handle = _device.AllocateHandle();
        var resource = new MetalTextureResource
        {
            EngineFormat = TextureImageFormat.Color4b,
            Format = MetalFormats.ToPixelFormat(TextureImageFormat.Color4b),
            Type = TextureType.Texture2D,
            Width = 1,
            Height = 1,
            MinFilter = TextureMin.Nearest,
            MagFilter = TextureMag.Nearest,
        };
        resource.Texture = MetalNative.NewTexture2D(
            _device.NativeDevice,
            resource.Format,
            1,
            1,
            mipmapped: false,
            MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget,
            MTLStorageMode.Shared);
        if (resource.Texture == 0)
            throw new InvalidOperationException("MTLDevice newTextureWithDescriptor returned nil for the white fallback texture.");

        _device.Textures[handle] = resource;
        WriteTexture(resource, 0, 0, 0, 1, 1, [255, 255, 255, 255]);
        EnsureSampler(resource);
        _whiteTextureFallback = resource;
        return resource;
    }

    /// <summary>
    /// 1×1 white cubemap used when an environment-probe cube is not assigned. A 2D fallback
    /// cannot be bound to a TextureCube slot on Metal, even if every face would sample white.
    /// </summary>
    private unsafe MetalTextureResource EnsureWhiteCubeTextureFallback()
    {
        if (_whiteCubeTextureFallback is { Texture: not 0 } existing)
            return existing;

        uint handle = _device.AllocateHandle();
        var resource = new MetalTextureResource
        {
            EngineFormat = TextureImageFormat.Color4b,
            Format = MetalFormats.ToPixelFormat(TextureImageFormat.Color4b),
            Type = TextureType.TextureCubeMap,
            Width = 1,
            Height = 1,
            MinFilter = TextureMin.Nearest,
            MagFilter = TextureMag.Nearest,
        };
        resource.Texture = MetalNative.NewTextureCube(
            _device.NativeDevice,
            resource.Format,
            1,
            mipmapped: false,
            MTLTextureUsage.ShaderRead,
            MTLStorageMode.Shared);
        if (resource.Texture == 0)
            throw new InvalidOperationException("MTLDevice newTextureWithDescriptor returned nil for the white cube fallback.");

        _device.Textures[handle] = resource;
        byte[] white = [255, 255, 255, 255];
        fixed (byte* source = white)
        {
            for (uint face = 0; face < 6; face++)
                MetalNative.ReplaceRegionCube(resource.Texture, face, 0, 1, 1, (nint)source, bytesPerRow: 4);
        }
        EnsureSampler(resource);
        _whiteCubeTextureFallback = resource;
        return resource;
    }
}
