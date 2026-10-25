// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.RHI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Guards the material-block/object-block split the packed-uniform backends rely on:
/// a draw whose instance PropertyState only carries per-object data (transforms, object id,
/// baked GI, textures) must not force a material cbuffer repack, while any material-block
/// key (colors, floats, tiling vectors, clip uniforms) must. Regression coverage for UI
/// images whose instance _MainColor was dropped and rendered solid white on Vulkan/D3D12.
/// </summary>
public sealed class MaterialUniformPackingTests
{
    [Fact]
    public void EmptyStateHasNoMaterialOverrides()
    {
        var state = new PropertyState();
        Assert.False(MaterialUniformPacking.HasMaterialBlockOverrides(state));
    }

    [Fact]
    public void ObjectBlockOnlyStateHasNoMaterialOverrides()
    {
        // The shape DrawRenderables produces per object: transforms, object id, GI ints,
        // SH probe vectors, lightmap scale/offset. None of these live in a material cbuffer.
        var state = new PropertyState();
        state.SetMatrix("xengine_ObjectToWorld", Float4x4.Identity);
        state.SetMatrix("xengine_WorldToObject", Float4x4.Identity);
        state.SetMatrix("xengine_PrevObjectToWorld", Float4x4.Identity);
        state.SetInt("_ObjectID", 42);
        state.SetInt("_GIMode", 1);
        state.SetInt("_LightmapUV", 0);
        state.SetInt("_RenderingLayerMask", -1);
        state.SetVector("_LightmapScaleOffset", new Float4(1f, 1f, 0f, 0f));
        state.SetVector("xengine_SHAr", Float4.Zero);
        state.SetVector("xengine_SHC", Float4.Zero);
        Assert.False(MaterialUniformPacking.HasMaterialBlockOverrides(state));
    }

    [Fact]
    public void MainColorIsAMaterialOverride()
    {
        var state = new PropertyState();
        state.SetColor("_MainColor", new Color(0.2f, 0.3f, 0.4f, 1f));
        Assert.True(MaterialUniformPacking.HasMaterialBlockOverrides(state));
    }

    [Fact]
    public void ClipUniformsAreMaterialOverrides()
    {
        // The clip block a Canvas node writes per draw: enable flag (float), rect (vec4),
        // and world-to-mask matrix. All three categories must trigger a repack on their own.
        var floats = new PropertyState();
        floats.SetFloat("_ClipEnable", 1f);
        Assert.True(MaterialUniformPacking.HasMaterialBlockOverrides(floats));

        var vectors = new PropertyState();
        vectors.SetInt("_ObjectID", 7);
        vectors.SetVector("_ClipRect", new Float4(0f, 0f, 100f, 100f));
        Assert.True(MaterialUniformPacking.HasMaterialBlockOverrides(vectors));

        var matrices = new PropertyState();
        matrices.SetMatrix("_ClipToLocal", Float4x4.Identity);
        Assert.True(MaterialUniformPacking.HasMaterialBlockOverrides(matrices));
    }

    [Fact]
    public void TilingIsAMaterialOverride()
    {
        var state = new PropertyState();
        state.SetVector("_Tiling", new Float2(2f, 2f));
        Assert.True(MaterialUniformPacking.HasMaterialBlockOverrides(state));
    }

    [Fact]
    public void NonObjectIntIsAMaterialOverride()
    {
        var state = new PropertyState();
        state.SetInt("_ObjectID", 7);
        state.SetInt("_ParallaxSteps", 16);
        Assert.True(MaterialUniformPacking.HasMaterialBlockOverrides(state));
    }

    // --- N2: generic texture bindings (shader-declared sampler slots resolved by name) ---

    private static ShaderBindingSlot[] CustomTextureSlot(string name) =>
        [new ShaderBindingSlot(ShaderBindingKind.Texture, 0, name)];

    [Fact]
    public void GenericTextureBinding_ResolvesCustomMapFromMaterialProperties()
    {
        // A game-owned shader declares a sampler the engine whitelist doesn't know about.
        using var texture = new Texture2D();
        var propertyState = new PropertyState();
        propertyState.SetTexture("_CustomMap", texture);

        var bindings = new Dictionary<string, GraphicsTexture>();
        MaterialUniformPacking.ApplyTextureBindings(
            bindings, propertyState, shader: null, textureSlots: CustomTextureSlot("_CustomMap"));

        Assert.True(bindings.ContainsKey("_CustomMap"));
        Assert.False(bindings["_CustomMap"].IsDisposed);
    }

    [Fact]
    public void GenericTextureBinding_ResolvesCustomMapFromShaderPropertiesDefault()
    {
        // No material override; the shader Properties block declares the sampler with a default.
        using var defaultTexture = new Texture2D();
        var shaderProperty = new Rendering.Shaders.ShaderProperty(defaultTexture)
        {
            Name = "_CustomMap",
            DisplayName = "Custom Map",
        };
        using var shader = new Shader("Game Shader", [shaderProperty], []);

        var bindings = new Dictionary<string, GraphicsTexture>();
        MaterialUniformPacking.ApplyTextureBindings(
            bindings, properties: null, shader, textureSlots: CustomTextureSlot("_CustomMap"));

        Assert.True(bindings.ContainsKey("_CustomMap"));
        Assert.False(bindings["_CustomMap"].IsDisposed);
    }

    [Fact]
    public void GenericTextureBinding_MaterialOverrideWinsOverShaderDefault()
    {
        using var defaultTexture = new Texture2D();
        using var overrideTexture = new Texture2D();
        var shaderProperty = new Rendering.Shaders.ShaderProperty(defaultTexture)
        {
            Name = "_CustomMap",
            DisplayName = "Custom Map",
        };
        using var shader = new Shader("Game Shader", [shaderProperty], []);
        var propertyState = new PropertyState();
        propertyState.SetTexture("_CustomMap", overrideTexture);

        var bindings = new Dictionary<string, GraphicsTexture>();
        MaterialUniformPacking.ApplyTextureBindings(
            bindings, propertyState, shader, textureSlots: CustomTextureSlot("_CustomMap"));

        Assert.True(bindings.ContainsKey("_CustomMap"));
        Assert.False(bindings["_CustomMap"].IsDisposed);
    }

    [Fact]
    public void GenericTextureBinding_WhitelistStillBindsWhenNoSlotsPassed()
    {
        // The old overload (empty textureSlots) must keep the whitelist behavior unchanged.
        using var shader = new Shader("Whitelist Only", [], []);
        var bindings = new Dictionary<string, GraphicsTexture>();
        MaterialUniformPacking.ApplyTextureBindings(bindings, properties: null, shader, textureSlots: null);

        // _MainTex has no override and no default, so it must stay unbound (whitelist reads it
        // but AddTextureBinding skips null) — no exception, no custom-slot side effects.
        Assert.False(bindings.ContainsKey("_MainTex"));
    }

    [Fact]
    public void GenericTextureBinding_SkipsNonTextureSlots()
    {
        // Sampler and buffer slots must not be resolved as textures.
        var bindings = new Dictionary<string, GraphicsTexture>();
        ShaderBindingSlot[] slots =
        [
            new ShaderBindingSlot(ShaderBindingKind.Sampler, 0, "_CustomSampler"),
            new ShaderBindingSlot(ShaderBindingKind.Buffer, 0, "MaterialA"),
        ];
        MaterialUniformPacking.ApplyTextureBindings(bindings, properties: null, shader: null, textureSlots: slots);

        Assert.Empty(bindings);
    }

    [Fact]
    public void GenericTextureBinding_UnresolvedSlotStaysUnbound()
    {
        // A shader-declared sampler with no material override and no Properties-block default must
        // stay UNBOUND. Eagerly binding white here shadowed the backend's draw-time global-texture
        // resolution (SetGlobalTexture pass inputs) and the entry was never cleared for
        // non-whitelist names — bloom/SSAO/soft particles read pure white and the whole editor
        // rendered washed out (2026-08-25 regression).
        var bindings = new Dictionary<string, GraphicsTexture>();
        MaterialUniformPacking.ApplyTextureBindings(
            bindings, properties: null, shader: null, textureSlots: CustomTextureSlot("_UnsetMap"));

        Assert.False(bindings.ContainsKey("_UnsetMap"));
    }
}
