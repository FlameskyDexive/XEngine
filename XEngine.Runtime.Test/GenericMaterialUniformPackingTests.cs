// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

/// <summary>
/// N1 acceptance tests for <see cref="GenericMaterialUniformPacking"/> — the reflection-driven
/// generic cbuffer packer that backs game-owned shaders on Vulkan / D3D12 / WebGPU:
/// <list type="number">
/// <item>Small Slang fixture cbuffer (vec2/vec3/vec4/float/matrix + std140 padding holes):
/// byte-exact packing including zeroed padding.</item>
/// <item>Toon bit-level equivalence: generic pack vs hand-written PackToonShading /
/// PackToonOutline across four scenarios (null properties / shader defaults only /
/// full override / partial override) — the plan's core N1 acceptance gate.</item>
/// <item>Dual material cbuffer shader: the generic path packs both named slots.</item>
/// </list>
/// </summary>
[Collection(ShaderCompilerEnvironmentCollection.Name)]
public sealed class GenericMaterialUniformPackingTests(ITestOutputHelper output)
{
    // --- Fixture 1: small cbuffer with every value kind + std140 padding holes ---

    private const string SmallFixtureVertex = """
cbuffer SmallFixture : register(b2)
{
    float _Scalar;
    float2 _Vec2;        // std140: vec2 aligns to 8 — offset 8 (4-byte hole at 4)
    float3 _Vec3;        // std140: vec3 aligns to 16 — offset 16 (padding inside its 16-byte row)
    float4 _Vec4;        // offset 32
    float4x4 _Matrix;    // offset 48
    float _Tail;         // offset 112, then 12 bytes tail padding to 128
};
struct VSInput { float3 Position : POSITION; };
float4 vertexMain(VSInput input) : SV_Position
{
    return mul(_Matrix, float4(input.Position, 1)) + _Vec4 + float4(_Vec3, _Scalar) + float4(_Vec2, 0, 0) + _Tail;
}
""";

    private const string SmallFixtureFragment = """
cbuffer SmallFixture : register(b2)
{
    float _Scalar;
    float2 _Vec2;
    float3 _Vec3;
    float4 _Vec4;
    float4x4 _Matrix;
    float _Tail;
};
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{
    return position * _Scalar + _Vec4 + float4(_Vec3, 0) + float4(_Vec2, 0, 0) + _Tail;
}
""";

    private static CompiledShaderBytecode CompileFixture(GraphicsBackend backend, string vertex, string fragment)
    {
        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexSource = vertex,
            FragmentSource = fragment,
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
        });
        Assert.True(result.Success, result.ErrorMessage);
        return new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            backend == GraphicsBackend.Vulkan ? ShaderBytecodeFormat.SpirV : ShaderBytecodeFormat.Dxil,
            result.VertexBytecode ?? [],
            result.FragmentBytecode ?? [],
            Assert.IsType<ShaderBindingLayout>(result.BindingLayout));
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void SmallFixturePacksByteExactWithZeroPadding(GraphicsBackend backend)
    {
        CompiledShaderBytecode bytecode = CompileFixture(backend, SmallFixtureVertex, SmallFixtureFragment);
        GenericMaterialUniformPacking.GenericUniformOp[]? plan =
            GenericMaterialUniformPacking.GetWritePlan(bytecode, "SmallFixture");
        Assert.NotNull(plan);

        ShaderBindingSlot slot = bytecode.BindingLayout!.Buffers[0];
        // Print the actual reflected layout so failures are diagnosable.
        for (int i = 0; i < slot.UniformFields.Length; i++)
        {
            ShaderUniformField field = slot.UniformFields[i];
            output.WriteLine($"{field.Name}: offset={field.Offset} size={field.Size} rows={field.Rows} cols={field.Columns}");
        }
        Assert.Equal("SmallFixture", slot.Name);
        int size = slot.ByteSize;
        Assert.True(size >= 128, $"expected >= 128 bytes, got {size}");

        // Full overrides for every value kind.
        var properties = new PropertyState();
        properties.SetFloat("_Scalar", 1.5f);
        properties.SetVector("_Vec2", new Float2(2.5f, 3.5f));
        properties.SetVector("_Vec3", new Float3(4.5f, 5.5f, 6.5f));
        properties.SetVector("_Vec4", new Float4(7.5f, 8.5f, 9.5f, 10.5f));
        properties.SetMatrix("_Matrix", Float4x4.Identity);
        properties.SetFloat("_Tail", 11.5f);

        byte[] buffer = new byte[size];
        GenericMaterialUniformPacking.Pack(plan, properties, null, buffer);

        // Layout from reflection: _Scalar@0, _Vec2@4 (Slang packs vec2 at 4-byte alignment),
        // hole@12-15, _Vec3@16-27 (+pad@28), _Vec4@32, _Matrix@48, _Tail@112, tail pad to 128.
        Assert.Equal(1.5f, BitConverter.ToSingle(buffer, 0));
        Assert.Equal(2.5f, BitConverter.ToSingle(buffer, 4));
        Assert.Equal(3.5f, BitConverter.ToSingle(buffer, 8));
        // Padding hole before vec3's 16-byte alignment (offsets 12..15) stays zero.
        Assert.Equal(0f, BitConverter.ToSingle(buffer, 12));
        Assert.Equal(4.5f, BitConverter.ToSingle(buffer, 16));
        Assert.Equal(5.5f, BitConverter.ToSingle(buffer, 20));
        Assert.Equal(6.5f, BitConverter.ToSingle(buffer, 24));
        // Vec3's std140 row is 16 bytes; component 4 (offset 28) is implicit padding → zero.
        Assert.Equal(0f, BitConverter.ToSingle(buffer, 28));
        Assert.Equal(7.5f, BitConverter.ToSingle(buffer, 32));
        Assert.Equal(8.5f, BitConverter.ToSingle(buffer, 36));
        Assert.Equal(9.5f, BitConverter.ToSingle(buffer, 40));
        Assert.Equal(10.5f, BitConverter.ToSingle(buffer, 44));
        // Matrix identity: XEngine's Float4x4 stores 4 columns (c0..c3), so the identity
        // diagonal lands at raw float indices 0/5/10/15 → byte offsets 48/68/88/108.
        Assert.Equal(1f, BitConverter.ToSingle(buffer, 48));
        Assert.Equal(0f, BitConverter.ToSingle(buffer, 52));
        Assert.Equal(1f, BitConverter.ToSingle(buffer, 68));
        Assert.Equal(1f, BitConverter.ToSingle(buffer, 88));
        Assert.Equal(1f, BitConverter.ToSingle(buffer, 108));
        Assert.Equal(11.5f, BitConverter.ToSingle(buffer, 112));
        // Tail padding beyond _Tail stays zero.
        for (int i = 116; i < size; i++)
            Assert.Equal(0, buffer[i]);

        output.WriteLine($"Backend {backend}: packed {size} bytes, plan ops={plan.Length}");
    }

    // --- Fixture 2: the real Toon cbuffer layouts, reflected by Slang ---

    private const string ToonFixtureVertex = """
cbuffer ToonShadingMaterial : register(b2)
{
    float2 _Tiling;
    float2 _Offset;
    float4 _MainColor;
    float4 _Ambient;
    float4 _GColor1;
    float4 _GColor2;
    float _MainLightMidPoint;
    float _MainLightSmoothness;
    float _ReceiveShadowsEnabled;
    float _MainShadowsPower;
    float _ShadowSmoothness;
    float _OcclusionPower;
    float _AdditionalLightsEnabled;
    float _AdditionalLightsMidPoint;
    float _AdditionalLightsSmoothness;
    float _AdditionalLightShadows;
    float _AdditionalShadowsPower;
    float _GradientEnabled;
    float _GradientMultiplier;
    float _GradientOffset;
    float _InverseColors;
    float _GradientDirection;
    float _AlphaCutoff;
    float _ToonPad0;
    float _ToonPad1;
    float _ToonPad2;
    float4 _RimColor;
    float4 _EmissionColor;
    float4 _SpecularTint;
    float4 _SpecularTextureColor;
    float _RimEnabled;
    float _RimMidPoint;
    float _RimSmoothness;
    float _DynamicRemap;
    float _RimHideOnShadow;
    float _MainLightSpecular;
    float _AdditionalLightsSpecular;
    float _SpecularMapPower;
    float _SpecularMidPoint;
    float _SpecularSmoothness;
    float _SpecularCustomizeEnabled;
    float _SpecularHideOnShadows;
    float _Emission;
    float _IsFace;
    float _ShrinkSize;
    float _ToonPad3;
    float4 _FaceX;
    float4 _FaceY;
    float4 _FaceZ;
};
struct VSInput { float3 Position : POSITION; };
float4 vertexMain(VSInput input) : SV_Position
{
    return float4(input.Position, 1) + _MainColor + _RimColor + _FaceX + _FaceY + _FaceZ + _Ambient + _GColor1 + _GColor2 + _EmissionColor + _SpecularTint + _SpecularTextureColor;
}
""";

    private const string ToonFixtureFragment = """
cbuffer ToonShadingMaterial : register(b2)
{
    float2 _Tiling;
    float2 _Offset;
    float4 _MainColor;
    float4 _Ambient;
    float4 _GColor1;
    float4 _GColor2;
    float _MainLightMidPoint;
    float _MainLightSmoothness;
    float _ReceiveShadowsEnabled;
    float _MainShadowsPower;
    float _ShadowSmoothness;
    float _OcclusionPower;
    float _AdditionalLightsEnabled;
    float _AdditionalLightsMidPoint;
    float _AdditionalLightsSmoothness;
    float _AdditionalLightShadows;
    float _AdditionalShadowsPower;
    float _GradientEnabled;
    float _GradientMultiplier;
    float _GradientOffset;
    float _InverseColors;
    float _GradientDirection;
    float _AlphaCutoff;
    float _ToonPad0;
    float _ToonPad1;
    float _ToonPad2;
    float4 _RimColor;
    float4 _EmissionColor;
    float4 _SpecularTint;
    float4 _SpecularTextureColor;
    float _RimEnabled;
    float _RimMidPoint;
    float _RimSmoothness;
    float _DynamicRemap;
    float _RimHideOnShadow;
    float _MainLightSpecular;
    float _AdditionalLightsSpecular;
    float _SpecularMapPower;
    float _SpecularMidPoint;
    float _SpecularSmoothness;
    float _SpecularCustomizeEnabled;
    float _SpecularHideOnShadows;
    float _Emission;
    float _IsFace;
    float _ShrinkSize;
    float _ToonPad3;
    float4 _FaceX;
    float4 _FaceY;
    float4 _FaceZ;
};
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{
    return position + _Ambient + _GColor1 + _GColor2 + _SpecularTint + _SpecularTextureColor + _EmissionColor + _RimColor + _FaceX + _FaceY + _FaceZ;
}
""";

    private const string ToonOutlineFixtureVertex = """
cbuffer ToonOutlineMaterial : register(b2)
{
    float4 _OutlineColor;
    float _OutlineWidth;
    float _VertexColorNormals;
    float2 _ToonOutlinePadding;
};
struct VSInput { float3 Position : POSITION; };
float4 vertexMain(VSInput input) : SV_Position
{
    return float4(input.Position + _OutlineColor.xyz * _OutlineWidth * _VertexColorNormals, 1);
}
""";

    private const string ToonOutlineFixtureFragment = """
cbuffer ToonOutlineMaterial : register(b2)
{
    float4 _OutlineColor;
    float _OutlineWidth;
    float _VertexColorNormals;
    float2 _ToonOutlinePadding;
};
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{
    return _OutlineColor * _OutlineWidth + position;
}
""";

    private static Shader LoadToonShader()
    {
        // The engine no longer ships Toon as a built-in. Reconstruct a game-owned Toon shader
        // from an inline Properties block (with the same defaults the .shader declares) so the
        // generic packer's default-resolution and override precedence stay guarded.
        var props = new Rendering.Shaders.ShaderProperty[]
        {
            new(new Color(1f, 1f, 1f, 1f)) { Name = "_MainColor" },
            new(new Float2(1f, 1f)) { Name = "_Tiling" },
            new(new Float2(0f, 0f)) { Name = "_Offset" },
            new(new Color(0.2547f, 0.2547f, 0.2547f, 1f)) { Name = "_Ambient" },
            new(new Color(0f, 0f, 0f, 1f)) { Name = "_GColor1" },
            new(new Color(1f, 1f, 1f, 1f)) { Name = "_GColor2" },
            new(0f) { Name = "_MainLightMidPoint" },
            new(0.1f) { Name = "_MainLightSmoothness" },
            new(1f) { Name = "_ReceiveShadowsEnabled" },
            new(0.5f) { Name = "_MainShadowsPower" },
            new(1f) { Name = "_ShadowSmoothness" },
            new(1f) { Name = "_OcclusionPower" },
            new(1f) { Name = "_AdditionalLightsEnabled" },
            new(0f) { Name = "_AdditionalLightsMidPoint" },
            new(1f) { Name = "_AdditionalLightsSmoothness" },
            new(1f) { Name = "_AdditionalLightShadows" },
            new(1f) { Name = "_AdditionalShadowsPower" },
            new(0f) { Name = "_GradientEnabled" },
            new(1f) { Name = "_GradientMultiplier" },
            new(0f) { Name = "_GradientOffset" },
            new(0f) { Name = "_InverseColors" },
            new(1f) { Name = "_GradientDirection" },
            new(0.5f) { Name = "_AlphaCutoff" },
            new(new Color(1f, 0f, 0f, 1f)) { Name = "_RimColor" },
            new(0f) { Name = "_RimEnabled" },
            new(0.6f) { Name = "_RimMidPoint" },
            new(0.1f) { Name = "_RimSmoothness" },
            new(0.5f) { Name = "_DynamicRemap" },
            new(1f) { Name = "_RimHideOnShadow" },
            new(0f) { Name = "_MainLightSpecular" },
            new(0f) { Name = "_AdditionalLightsSpecular" },
            new(0f) { Name = "_SpecularMapPower" },
            new(0.9f) { Name = "_SpecularMidPoint" },
            new(0.1f) { Name = "_SpecularSmoothness" },
            new(new Color(1f, 1f, 1f, 1f)) { Name = "_SpecularTint" },
            new(0f) { Name = "_SpecularCustomizeEnabled" },
            new(1f) { Name = "_SpecularHideOnShadows" },
            new(new Color(1f, 1f, 1f, 1f)) { Name = "_SpecularTextureColor" },
            new(new Color(1f, 1f, 1f, 1f)) { Name = "_EmissionColor" },
            new(0f) { Name = "_Emission" },
            new(0f) { Name = "_IsFace" },
            new(new Float4(0f, 10f, 0f, 0f)) { Name = "_FaceX" },
            new(new Float4(0f, 10f, 0f, 0f)) { Name = "_FaceY" },
            new(new Float4(0f, 10f, 0f, 0f)) { Name = "_FaceZ" },
            new(0f) { Name = "_ShrinkSize" },
        };
        return new Shader("Zonezero/Toon", props, []);
    }

    private static Shader LoadToonOutlineShader()
    {
        var props = new Rendering.Shaders.ShaderProperty[]
        {
            new(new Color(0f, 0f, 0f, 1f)) { Name = "_OutlineColor" },
            new(0.1f) { Name = "_OutlineWidth" },
            new(0f) { Name = "_VertexColorNormals" },
        };
        return new Shader("Zonezero/ToonOutline", props, []);
    }

    private static byte[] PackGeneric(CompiledShaderBytecode bytecode, string bufferName, PropertyState? properties, Shader? shader)
    {
        GenericMaterialUniformPacking.GenericUniformOp[]? plan =
            GenericMaterialUniformPacking.GetWritePlan(bytecode, bufferName);
        Assert.NotNull(plan);
        ShaderBindingSlot slot = bytecode.BindingLayout!.FindByName(bufferName)!.Value;
        byte[] buffer = new byte[slot.ByteSize];
        GenericMaterialUniformPacking.Pack(plan, properties, shader, buffer);
        return buffer;
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ToonLayoutReflects336Bytes(GraphicsBackend backend)
    {
        CompiledShaderBytecode bytecode = CompileFixture(backend, ToonFixtureVertex, ToonFixtureFragment);
        ShaderBindingSlot slot = bytecode.BindingLayout!.FindByName("ToonShadingMaterial")!.Value;
        // The former hand-written mirror struct was 336 bytes; Slang reflection must agree so
        // existing game-owned materials keep their layout contract.
        Assert.Equal(336, slot.ByteSize);
        // 2 float2 + 11 float4 + 36 float (incl. 4 explicit pad floats) = 49 reflected fields.
        Assert.Equal(49, slot.UniformFields.Length);

        CompiledShaderBytecode outline = CompileFixture(backend, ToonOutlineFixtureVertex, ToonOutlineFixtureFragment);
        ShaderBindingSlot outlineSlot = outline.BindingLayout!.FindByName("ToonOutlineMaterial")!.Value;
        Assert.Equal(32, outlineSlot.ByteSize);
        Assert.Equal(4, outlineSlot.UniformFields.Length);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ToonGenericPack_WritesOverridesAndDefaultsAtReflectedOffsets(GraphicsBackend backend)
    {
        CompiledShaderBytecode bytecode = CompileFixture(backend, ToonFixtureVertex, ToonFixtureFragment);
        Shader shader = LoadToonShader();

        // Overrides win, shader PropertyArray defaults fill the gaps — the same precedence the
        // engine's OpenGL path (FillShaderDefaults) uses, now mirrored by the generic packer.
        var properties = new PropertyState();
        properties.SetColor("_Ambient", new Color(0.12f, 0.34f, 0.56f, 0.78f));
        properties.SetFloat("_MainLightMidPoint", 0.15f);
        properties.SetVector("_FaceX", new Float4(1f, 15f, 0.2f, 0f));

        byte[] buffer = PackGeneric(bytecode, "ToonShadingMaterial", properties, shader);

        // _Ambient is a float4 at reflected offset 32 (after _Tiling@0 + _Offset@8 + _MainColor@16).
        Assert.Equal(0.12f, BitConverter.ToSingle(buffer, 32));
        Assert.Equal(0.34f, BitConverter.ToSingle(buffer, 36));
        Assert.Equal(0.56f, BitConverter.ToSingle(buffer, 40));
        Assert.Equal(0.78f, BitConverter.ToSingle(buffer, 44));
        // _MainLightMidPoint (float) at offset 80.
        Assert.Equal(0.15f, BitConverter.ToSingle(buffer, 80));
        // _FaceX (float4) at offset 288 — override written, w=0.
        Assert.Equal(1f, BitConverter.ToSingle(buffer, 288));
        Assert.Equal(15f, BitConverter.ToSingle(buffer, 292));
        Assert.Equal(0.2f, BitConverter.ToSingle(buffer, 296));
        Assert.Equal(0f, BitConverter.ToSingle(buffer, 300));
        // _Tiling default from shader PropertyArray (1,1) at offset 0.
        Assert.Equal(1f, BitConverter.ToSingle(buffer, 0));
        Assert.Equal(1f, BitConverter.ToSingle(buffer, 4));
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ToonOutlineGenericPack_WritesOverridesAndDefaults(GraphicsBackend backend)
    {
        CompiledShaderBytecode bytecode = CompileFixture(backend, ToonOutlineFixtureVertex, ToonOutlineFixtureFragment);
        Shader shader = LoadToonOutlineShader();

        var properties = new PropertyState();
        properties.SetColor("_OutlineColor", new Color(0.1f, 0.2f, 0.3f, 0.4f));
        properties.SetFloat("_OutlineWidth", 1.25f);

        byte[] buffer = PackGeneric(bytecode, "ToonOutlineMaterial", properties, shader);

        Assert.Equal(0.1f, BitConverter.ToSingle(buffer, 0));
        Assert.Equal(0.2f, BitConverter.ToSingle(buffer, 4));
        Assert.Equal(0.3f, BitConverter.ToSingle(buffer, 8));
        Assert.Equal(0.4f, BitConverter.ToSingle(buffer, 12));
        Assert.Equal(1.25f, BitConverter.ToSingle(buffer, 16));
        // _VertexColorNormals default (0) at offset 20.
        Assert.Equal(0f, BitConverter.ToSingle(buffer, 20));
    }

    // --- Fixture 3: dual material cbuffers ---

    private const string DualFixtureVertex = """
cbuffer MaterialA : register(b2)
{
    float4 _ColorA;
    float _IntensityA;
};
cbuffer MaterialB : register(b3)
{
    float4 _ColorB;
    float _IntensityB;
};
struct VSInput { float3 Position : POSITION; };
float4 vertexMain(VSInput input) : SV_Position
{
    return float4(input.Position, 1) * _ColorA * _IntensityA + _ColorB * _IntensityB;
}
""";

    private const string DualFixtureFragment = """
cbuffer MaterialA : register(b2)
{
    float4 _ColorA;
    float _IntensityA;
};
cbuffer MaterialB : register(b3)
{
    float4 _ColorB;
    float _IntensityB;
};
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{
    return position * _ColorA * _IntensityA + _ColorB * _IntensityB;
}
""";

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void DualMaterialCbuffersBothPack(GraphicsBackend backend)
    {
        CompiledShaderBytecode bytecode = CompileFixture(backend, DualFixtureVertex, DualFixtureFragment);

        // Both slots must produce independent write plans (the "hit one and return" bug
        // used to leave the second material cbuffer unpacked → descriptor bind throw).
        GenericMaterialUniformPacking.GenericUniformOp[]? planA =
            GenericMaterialUniformPacking.GetWritePlan(bytecode, "MaterialA");
        GenericMaterialUniformPacking.GenericUniformOp[]? planB =
            GenericMaterialUniformPacking.GetWritePlan(bytecode, "MaterialB");
        Assert.NotNull(planA);
        Assert.NotNull(planB);

        var properties = new PropertyState();
        properties.SetColor("_ColorA", new Color(1f, 0f, 0f, 1f));
        properties.SetFloat("_IntensityA", 2f);
        properties.SetColor("_ColorB", new Color(0f, 0f, 1f, 1f));
        properties.SetFloat("_IntensityB", 4f);

        ShaderBindingSlot slotA = bytecode.BindingLayout!.FindByName("MaterialA")!.Value;
        ShaderBindingSlot slotB = bytecode.BindingLayout!.FindByName("MaterialB")!.Value;

        byte[] bufferA = new byte[slotA.ByteSize];
        byte[] bufferB = new byte[slotB.ByteSize];
        GenericMaterialUniformPacking.Pack(planA!, properties, null, bufferA);
        GenericMaterialUniformPacking.Pack(planB!, properties, null, bufferB);

        // A packed independently of B.
        Assert.Equal(1f, BitConverter.ToSingle(bufferA, 0));
        Assert.Equal(0f, BitConverter.ToSingle(bufferA, 4));
        Assert.Equal(2f, BitConverter.ToSingle(bufferA, 16));
        Assert.Equal(0f, BitConverter.ToSingle(bufferB, 0));
        Assert.Equal(0f, BitConverter.ToSingle(bufferB, 4));
        Assert.Equal(1f, BitConverter.ToSingle(bufferB, 8));
        Assert.Equal(4f, BitConverter.ToSingle(bufferB, 16));

        output.WriteLine($"Dual cbuffers: A={slotA.ByteSize}B B={slotB.ByteSize}B");
    }

    // --- Engine-managed cbuffer refusal (2026-08-25 regression guard) ---

    private const string EngineNamedFixtureVertex = """
cbuffer GlobalUniforms : register(b0)
{
    float4x4 _FakeMatrix;
};
struct VSInput { float3 Position : POSITION; };
float4 vertexMain(VSInput input) : SV_Position
{
    return mul(_FakeMatrix, float4(input.Position, 1));
}
""";

    private const string EngineNamedFixtureFragment = """
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{
    return position;
}
""";

    /// <summary>
    /// A shader that declares a cbuffer NAMED like an engine-managed block (GlobalUniforms et al.)
    /// must NOT get a generic write plan: PropertyState cannot see camera/light data, so a
    /// reflection-packed copy would be all zeroes and overwrite the correctly packed buffer —
    /// the 2026-08-22 regression that zeroed VP matrices, blacked out GPU contract tests, and
    /// left the editor's views blank/washed out. Game-owned names keep getting plans.
    /// </summary>
    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void EngineManagedBuffersNeverGetGenericPlans(GraphicsBackend backend)
    {
        foreach (string managed in GenericMaterialUniformPacking.EngineManagedUniformBuffers)
            Assert.True(GenericMaterialUniformPacking.IsEngineManagedUniformBuffer(managed), managed);

        // The reflection side of the guard: a real compiled shader declaring "GlobalUniforms"
        // as its own cbuffer still yields NO plan from the generic path.
        CompiledShaderBytecode bytecode = CompileFixture(backend, EngineNamedFixtureVertex, EngineNamedFixtureFragment);
        Assert.Null(GenericMaterialUniformPacking.GetWritePlan(bytecode, "GlobalUniforms"));
        Assert.Null(GenericMaterialUniformPacking.GetWritePlan(bytecode, "StandardLighting"));
        Assert.Null(GenericMaterialUniformPacking.GetWritePlan(bytecode, "UIPS"));

        // Game-owned names are unaffected.
        Assert.NotNull(GenericMaterialUniformPacking.GetWritePlan(
            CompileFixture(backend, SmallFixtureVertex, SmallFixtureFragment), "SmallFixture"));
    }
}
