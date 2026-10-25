// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Backends.Metal;
using XEngine.Runtime.Resources;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

public sealed class RuntimeSlangShaderCompilerReflectionTests(ITestOutputHelper output)
{
    private const string VertexSource = """
cbuffer Globals : register(b2, space1)
{
    float4 Color;
    row_major float4x4 Transform;
    float Weights[3];
};
Texture2D<float4> Textures[2] : register(t5, space1);
SamplerState MainSampler : register(s7, space1);
RWTexture2D<float4> OutputTexture : register(u3, space2);
StructuredBuffer<float4> InputData : register(t9, space2);
RWStructuredBuffer<float4> OutputData : register(u4, space2);

struct VSInput
{
    float3 Position : POSITION;
    float2 UV : TEXCOORD0;
    uint4 Bones : BLENDINDICES0;
};

float4 vertexMain(VSInput input) : SV_Position
{
    return mul(Transform, float4(input.Position, 1)) + Color + Weights[0];
}
""";

    private const string FragmentSource = """
cbuffer Globals : register(b2, space1)
{
    float4 Color;
    row_major float4x4 Transform;
    float Weights[3];
};
Texture2D<float4> Textures[2] : register(t5, space1);
SamplerState MainSampler : register(s7, space1);
RWTexture2D<float4> OutputTexture : register(u3, space2);
StructuredBuffer<float4> InputData : register(t9, space2);
RWStructuredBuffer<float4> OutputData : register(u4, space2);

float4 fragmentMain(float4 position : SV_Position) : SV_Target
{
    return Color + Textures[0].SampleLevel(MainSampler, position.xy, 0) + InputData[0] + OutputData[0];
}
""";

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ReflectsResourcesUniformFieldsAndVertexInputs(GraphicsBackend backend)
    {
        ShaderCompileResult result = CompileSlang(backend, VertexSource, FragmentSource);

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        Assert.Collection(
            layout.Buffers,
            slot => AssertBinding(slot, ShaderBindingKind.Buffer, 1, 2, 1, "Globals", backend),
            slot => AssertBinding(slot, ShaderBindingKind.StorageBuffer, 2, 4, 1, "OutputData", backend),
            slot => AssertBinding(slot, ShaderBindingKind.StructuredBuffer, 2, 9, 1, "InputData", backend));
        Assert.Collection(
            layout.Textures,
            slot => AssertBinding(slot, ShaderBindingKind.Texture, 1, 5, 2, "Textures", backend),
            slot => AssertBinding(slot, ShaderBindingKind.StorageTexture, 2, 3, 1, "OutputTexture", backend));
        Assert.Collection(
            layout.Samplers,
            slot => AssertBinding(slot, ShaderBindingKind.Sampler, 1, 7, 1, "MainSampler", backend));

        ShaderBindingSlot globals = layout.Buffers[0];
        Assert.Equal(128, globals.ByteSize);
        Assert.Collection(
            globals.UniformFields,
            field => AssertField(field, "Color", 0, 16, ShaderScalarType.Float32, 1, 4, 1, 0, 0),
            field => AssertField(field, "Transform", 16, 64, ShaderScalarType.Float32, 4, 4, 1, 0, 16),
            field => AssertField(field, "Weights", 80, 48, ShaderScalarType.Float32, 1, 1, 3, 16, 0));

        ShaderVertexInputLayout vertexInputs = Assert.IsType<ShaderVertexInputLayout>(result.VertexInputLayout);
        Assert.Collection(
            vertexInputs.Inputs,
            input => AssertVertexInput(input, VertexFormat.VertexSemantic.Position, 0, "POSITION", 0, ShaderScalarType.Float32, 3),
            input => AssertVertexInput(input, VertexFormat.VertexSemantic.TexCoord0, 1, "TEXCOORD", 0, ShaderScalarType.Float32, 2),
            input => AssertVertexInput(input, VertexFormat.VertexSemantic.BoneIndex, 2, "BLENDINDICES", 0, ShaderScalarType.UInt32, 4));

        output.WriteLine($"Backend: {backend}");
        output.WriteLine($"Bindings: buffers={layout.Buffers.Length}, textures={layout.Textures.Length}, samplers={layout.Samplers.Length}");
        output.WriteLine($"Globals: bytes={globals.ByteSize}, fields={globals.UniformFields.Length}");
        output.WriteLine($"Vertex inputs: {vertexInputs.Inputs.Length}");
    }

    [Fact]
    public void VulkanAndDirect3D12LogicalReflectionContractsAreIdentical()
    {
        ShaderCompileResult vulkan = CompileSlang(GraphicsBackend.Vulkan, VertexSource, FragmentSource);
        ShaderCompileResult direct3D12 = CompileSlang(GraphicsBackend.Direct3D12, VertexSource, FragmentSource);

        Assert.True(vulkan.Success, vulkan.ErrorMessage);
        Assert.True(direct3D12.Success, direct3D12.ErrorMessage);
        string differences = ShaderReflectionContractComparer.CompareLogical(vulkan, direct3D12);
        output.WriteLine(string.IsNullOrEmpty(differences) ? "Normalized difference count: 0" : differences);
        Assert.Equal(string.Empty, differences);
    }

    [Fact]
    public void VulkanPreservesLogicalRegistersAndCapturesSpirvPhysicalBindings()
    {
        const string vertexSource = "cbuffer Globals : register(b2) { float4 Color; }; " +
            "float4 vertexMain(uint id : SV_VertexID) : SV_Position { return Color; }";
        const string fragmentSource = "cbuffer Globals : register(b2) { float4 Color; }; " +
            "Texture2D MainTexture : register(t3); SamplerState MainSampler : register(s4); " +
            "float4 fragmentMain(float2 uv : TEXCOORD0) : SV_Target { return Color + MainTexture.Sample(MainSampler, uv); }";

        ShaderCompileResult result = CompileSlang(GraphicsBackend.Vulkan, vertexSource, fragmentSource);

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        AssertPhysicalBindings(
            Assert.Single(layout.Buffers),
            logicalSlot: 2,
            new ShaderPhysicalBinding(2, 0, ShaderStageMask.Vertex | ShaderStageMask.Fragment));
        AssertPhysicalBindings(
            Assert.Single(layout.Textures),
            logicalSlot: 3,
            new ShaderPhysicalBinding(67, 0, ShaderStageMask.Fragment));
        AssertPhysicalBindings(
            Assert.Single(layout.Samplers),
            logicalSlot: 4,
            new ShaderPhysicalBinding(132, 0, ShaderStageMask.Fragment));
        Assert.Equal(new uint[] { 2 }, SlangCompilerOptionMarshallingTests.ReadSpirvBindings(result.VertexBytecode!));
        Assert.Equal(new uint[] { 2, 67, 132 }, SlangCompilerOptionMarshallingTests.ReadSpirvBindings(result.FragmentBytecode!));
    }

    [Fact]
    public void WebGPUPreservesLogicalRegistersAndCapturesWgslPhysicalBindings()
    {
        const string vertexSource = "cbuffer Globals : register(b2) { float4 Color; }; " +
            "float4 vertexMain(uint id : SV_VertexID) : SV_Position { return Color; }";
        const string fragmentSource = "cbuffer Globals : register(b2) { float4 Color; }; " +
            "Texture2D MainTexture : register(t3); SamplerState MainSampler : register(s4); " +
            "float4 fragmentMain(float2 uv : TEXCOORD0) : SV_Target { return Color + MainTexture.Sample(MainSampler, uv); }";

        ShaderCompileResult result = CompileSlang(GraphicsBackend.WebGPU, vertexSource, fragmentSource);

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        ShaderBindingSlot globals = Assert.Single(layout.Buffers);
        ShaderBindingSlot texture = Assert.Single(layout.Textures);
        ShaderBindingSlot sampler = Assert.Single(layout.Samplers);
        AssertPhysicalBindingsExist(globals, logicalSlot: 2);
        AssertPhysicalBindingsExist(texture, logicalSlot: 3);
        AssertPhysicalBindingsExist(sampler, logicalSlot: 4);
        output.WriteLine(
            $"WG3 Globals physical=[{FormatPhysical(globals)}]; " +
            $"MainTexture=[{FormatPhysical(texture)}]; MainSampler=[{FormatPhysical(sampler)}]");
    }

    [Fact]
    public void Direct3D12PreservesLogicalRegistersWithoutSpirvPhysicalBindings()
    {
        const string vertexSource = "cbuffer Globals : register(b2) { float4 Color; }; " +
            "float4 vertexMain(uint id : SV_VertexID) : SV_Position { return Color; }";
        const string fragmentSource = "cbuffer Globals : register(b2) { float4 Color; }; " +
            "Texture2D MainTexture : register(t3); SamplerState MainSampler : register(s4); " +
            "float4 fragmentMain(float2 uv : TEXCOORD0) : SV_Target { return Color + MainTexture.Sample(MainSampler, uv); }";

        ShaderCompileResult result = CompileSlang(GraphicsBackend.Direct3D12, vertexSource, fragmentSource);

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        AssertPhysicalBindings(Assert.Single(layout.Buffers), logicalSlot: 2);
        AssertPhysicalBindings(Assert.Single(layout.Textures), logicalSlot: 3);
        AssertPhysicalBindings(Assert.Single(layout.Samplers), logicalSlot: 4);
    }

    [Fact]
    public void DxcAndSlangLogicalContractsAreIdentical()
    {
        // Compiles through DXC as the reference; DXC is vendored for Windows only.
        if (!OperatingSystem.IsWindows())
            return;

        const string vertexSource = """
cbuffer Globals : register(b0) { float4x4 Transform; };
Texture2D MainTexture : register(t3);
SamplerState MainSampler : register(s3);
struct VSInput { float3 Position : POSITION; float2 UV : TEXCOORD0; };
float4 vertexMain(VSInput input) : SV_Position { return mul(Transform, float4(input.Position, 1)); }
""";
        const string fragmentSource = """
cbuffer Globals : register(b0) { float4x4 Transform; };
Texture2D MainTexture : register(t3);
SamplerState MainSampler : register(s3);
float4 fragmentMain(float4 position : SV_Position) : SV_Target { return MainTexture.Sample(MainSampler, position.xy); }
""";

        ShaderCompileRequest request = CreateRequest(GraphicsBackend.Direct3D12, vertexSource, fragmentSource);
        ShaderCompileResult dxc = new DxcShaderCompiler().Compile(request);
        ShaderCompileResult slang = new RuntimeSlangShaderCompiler().Compile(request);

        Assert.True(dxc.Success, dxc.ErrorMessage);
        Assert.True(slang.Success, slang.ErrorMessage);
        string differences = ShaderReflectionContractComparer.CompareLogical(dxc, slang);
        output.WriteLine(string.IsNullOrEmpty(differences) ? "DXC/Slang logical difference count: 0" : differences);
        Assert.Equal(string.Empty, differences);
    }

    [Fact]
    public void ConflictingCrossStageBindingForOneNameFails()
    {
        ShaderCompileResult result = CompileSlang(
            GraphicsBackend.Vulkan,
            "Texture2D SharedTexture : register(t1); float4 vertexMain(uint id : SV_VertexID) : SV_Position { return 0; }",
            "Texture2D SharedTexture : register(t2); float4 fragmentMain() : SV_Target { return 0; }");

        Assert.False(result.Success);
        Assert.Contains("SharedTexture", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("conflicting", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverlappingSparseArrayBindingsFail()
    {
        ShaderCompileResult result = CompileSlang(
            GraphicsBackend.Vulkan,
            "Texture2D VertexTextures[2] : register(t5, space3); float4 vertexMain(uint id : SV_VertexID) : SV_Position { return 0; }",
            "Texture2D FragmentTexture : register(t6, space3); float4 fragmentMain() : SV_Target { return 0; }");

        Assert.False(result.Success);
        Assert.Contains("assigned to both", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VertexTextures", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("FragmentTexture", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictingCrossStageUniformLayoutsFail()
    {
        ShaderCompileResult result = CompileSlang(
            GraphicsBackend.Vulkan,
            "cbuffer Globals : register(b0) { float4 VertexColor; }; float4 vertexMain(uint id : SV_VertexID) : SV_Position { return VertexColor; }",
            "cbuffer Globals : register(b0) { float4 FragmentColor; }; float4 fragmentMain() : SV_Target { return FragmentColor; }");

        Assert.False(result.Success);
        Assert.Contains("Globals", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("conflicting layouts", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedLooseUniformFailsWithActionableDiagnostic()
    {
        ShaderCompileResult result = CompileSlang(
            GraphicsBackend.Vulkan,
            "float LooseUniform; float4 vertexMain(uint id : SV_VertexID) : SV_Position { return LooseUniform; }",
            "float4 fragmentMain() : SV_Target { return 0; }");

        Assert.False(result.Success);
        Assert.Contains("LooseUniform", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("explicit resource or cbuffer", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContractComparerReportsExactMismatchPath()
    {
        ShaderCompileResult expected = CreateContractResult(3, "MainTexture");
        ShaderCompileResult actual = CreateContractResult(4, "OtherTexture");

        string differences = ShaderReflectionContractComparer.CompareFull(expected, actual);

        Assert.Contains("texture[0].slot: expected 3, actual 4", differences, StringComparison.Ordinal);
        Assert.Contains("texture[0].name: expected MainTexture, actual OtherTexture", differences, StringComparison.Ordinal);
    }

    [Fact]
    public void FullContractComparerReportsEveryPhysicalBindingMismatch()
    {
        ShaderCompileResult expected = CreatePhysicalContractResult(
        [
            new ShaderPhysicalBinding(0, 0, ShaderStageMask.Vertex),
            new ShaderPhysicalBinding(1, 0, ShaderStageMask.Fragment),
        ]);
        ShaderCompileResult actual = CreatePhysicalContractResult(
        [
            new ShaderPhysicalBinding(0, 0, ShaderStageMask.Vertex),
            new ShaderPhysicalBinding(4, 0, ShaderStageMask.Fragment),
        ]);

        string differences = ShaderReflectionContractComparer.CompareFull(expected, actual);

        Assert.Contains("buffer[0].physicalBinding[1].binding: expected 1, actual 4", differences, StringComparison.Ordinal);
    }

    [Fact]
    public void MetalPhysicalSlotsAreScopedPerStageAndBoundByStage()
    {
        var vertex = new ShaderBindingLayout
        {
            Buffers = [new ShaderBindingSlot(ShaderBindingKind.Buffer, 2, "VertexMaterial",
                stages: ShaderStageMask.Vertex,
                physicalBindings: [new ShaderPhysicalBinding(2, 0, ShaderStageMask.Vertex)])],
        };
        var fragment = new ShaderBindingLayout
        {
            Buffers = [new ShaderBindingSlot(ShaderBindingKind.Buffer, 3, "FragmentLighting",
                stages: ShaderStageMask.Fragment,
                physicalBindings: [new ShaderPhysicalBinding(2, 0, ShaderStageMask.Fragment)])],
        };

        Assert.Throws<InvalidOperationException>(() => RuntimeSlangReflection.Merge(vertex, fragment));
        ShaderBindingLayout metal = RuntimeSlangReflection.Merge(vertex, fragment, metalPerKindSlots: true);
        Assert.Equal(2, metal.Buffers.Length);

        var shared = new ShaderBindingSlot(ShaderBindingKind.Buffer, 4, "Shared",
            stages: ShaderStageMask.Vertex | ShaderStageMask.Fragment,
            physicalBindings:
            [
                new ShaderPhysicalBinding(2, 0, ShaderStageMask.Vertex),
                new ShaderPhysicalBinding(0, 0, ShaderStageMask.Fragment),
            ]);
        Assert.Equal(2, MetalCommandTranslator.PhysicalIndex(shared, ShaderStageMask.Vertex));
        Assert.Equal(0, MetalCommandTranslator.PhysicalIndex(shared, ShaderStageMask.Fragment));
    }

    [Fact]
    public void MetalPhysicalSlotsCompactSparseIndicesPerArgumentKind()
    {
        var logical = new ShaderBindingLayout
        {
            Buffers =
            [
                new ShaderBindingSlot(ShaderBindingKind.Buffer, 8, "Globals", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.Buffer, 12, "Material", stages: ShaderStageMask.Fragment),
            ],
            Textures =
            [
                new ShaderBindingSlot(ShaderBindingKind.Texture, 3, "MainTexture", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.Texture, 34, "LightTexture", stages: ShaderStageMask.Fragment),
            ],
            Samplers =
            [
                new ShaderBindingSlot(ShaderBindingKind.Sampler, 3, "MainSampler", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.Sampler, 34, "LightSampler", stages: ShaderStageMask.Fragment),
            ],
        };

        ShaderBindingLayout compact = RuntimeSlangReflection.AttachMetalPhysicalSlots(logical, compactIndices: true);

        Assert.Equal(8, Assert.Single(compact.Buffers, x => x.Name == "Globals").Slot);
        Assert.Equal(0, Assert.Single(compact.Buffers, x => x.Name == "Globals").PhysicalBinding);
        Assert.Equal(1, Assert.Single(compact.Buffers, x => x.Name == "Material").PhysicalBinding);
        Assert.Equal(34, Assert.Single(compact.Textures, x => x.Name == "LightTexture").Slot);
        Assert.Equal(1, Assert.Single(compact.Textures, x => x.Name == "LightTexture").PhysicalBinding);
        Assert.Equal(1, Assert.Single(compact.Samplers, x => x.Name == "LightSampler").PhysicalBinding);
    }

    [Fact]
    public void MetalSourceCompactionRewritesMetalArgumentAttributes()
    {
        const string source = """
[[fragment]] float4 fragmentMain(
    constant Globals& globals_1 [[buffer(8)]],
    texture2d<float> lightTexture_1 [[texture(34)]],
    sampler lightSampler_1 [[sampler(34)]],
    float2 uv [[user(TEXCOORD0)]]
    ) [[color(0)]] { return lightTexture_1.sample(lightSampler_1, uv) + globals_1.color; }
""";
        var logical = new ShaderBindingLayout
        {
            Buffers = [new ShaderBindingSlot(ShaderBindingKind.Buffer, 8, "globals", stages: ShaderStageMask.Fragment)],
            Textures = [new ShaderBindingSlot(ShaderBindingKind.Texture, 34, "lightTexture", stages: ShaderStageMask.Fragment)],
            Samplers = [new ShaderBindingSlot(ShaderBindingKind.Sampler, 34, "lightSampler", stages: ShaderStageMask.Fragment)],
        };

        string compact = Encoding.UTF8.GetString(RuntimeSlangReflection.CompactMetalSourceIndices(
            Encoding.UTF8.GetBytes(source), logical));

        Assert.Contains("globals_1 [[buffer(0)]]", compact, StringComparison.Ordinal);
        Assert.Contains("lightTexture_1 [[texture(0)]]", compact, StringComparison.Ordinal);
        Assert.Contains("lightSampler_1 [[sampler(0)]]", compact, StringComparison.Ordinal);
        Assert.Contains("[[color(0)]]", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOSMetalSamplerCompactionPreservesBufferAndTextureIndices()
    {
        const string source = """
[[fragment]] float4 fragmentMain(
    constant Globals& globals_1 [[buffer(8)]],
    texture2d<float> shadowTexture_1 [[texture(24)]],
    sampler shadowSampler_1 [[sampler(24)]],
    float2 uv [[user(TEXCOORD0)]]
    ) [[color(0)]] { return shadowTexture_1.sample(shadowSampler_1, uv) + globals_1.color; }
""";
        var logical = new ShaderBindingLayout
        {
            Buffers = [new ShaderBindingSlot(ShaderBindingKind.Buffer, 8, "globals", stages: ShaderStageMask.Fragment)],
            Textures = [new ShaderBindingSlot(ShaderBindingKind.Texture, 24, "shadowTexture", stages: ShaderStageMask.Fragment)],
            Samplers = [new ShaderBindingSlot(ShaderBindingKind.Sampler, 24, "shadowSampler", stages: ShaderStageMask.Fragment)],
        };

        string compact = Encoding.UTF8.GetString(RuntimeSlangReflection.CompactMetalSamplerSourceIndices(
            Encoding.UTF8.GetBytes(source), logical));

        Assert.Contains("globals_1 [[buffer(8)]]", compact, StringComparison.Ordinal);
        Assert.Contains("shadowTexture_1 [[texture(24)]]", compact, StringComparison.Ordinal);
        Assert.Contains("shadowSampler_1 [[sampler(0)]]", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOSMetalSamplerCompactionUsesPhysicalReflectionIndices()
    {
        const string source = "sampler LogicalFirst_1 [[sampler(26)]]; sampler LogicalSecond_1 [[sampler(24)]];";
        var logicalLayout = new ShaderBindingLayout
        {
            Samplers =
            [
                new ShaderBindingSlot(ShaderBindingKind.Sampler, 24, "LogicalFirst", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.Sampler, 29, "LogicalSecond", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.Sampler, 37, "UnusedSampler", stages: ShaderStageMask.Fragment),
            ],
        };
        ShaderBindingLayout physicalLayout = RuntimeSlangReflection.AttachMetalSourcePhysicalSlots(
            logicalLayout,
            Encoding.UTF8.GetBytes(source));

        string compact = Encoding.UTF8.GetString(RuntimeSlangReflection.CompactMetalSamplerSourceIndices(
            Encoding.UTF8.GetBytes(source), physicalLayout));
        ShaderBindingLayout compactLayout = RuntimeSlangReflection.CompactMetalSamplerPhysicalSlots(physicalLayout);

        Assert.Contains("LogicalFirst_1 [[sampler(1)]]", compact, StringComparison.Ordinal);
        Assert.Contains("LogicalSecond_1 [[sampler(0)]]", compact, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(compactLayout.Samplers, x => x.Name == "LogicalFirst").PhysicalBinding);
        Assert.Equal(0, Assert.Single(compactLayout.Samplers, x => x.Name == "LogicalSecond").PhysicalBinding);
        Assert.DoesNotContain(compactLayout.Samplers, x => x.Name == "UnusedSampler");
        Assert.Equal(24, Assert.Single(compactLayout.Samplers, x => x.Name == "LogicalFirst").Slot);
        Assert.Equal(29, Assert.Single(compactLayout.Samplers, x => x.Name == "LogicalSecond").Slot);
    }

    [Fact]
    public void MetalCompactsOverlappingHlslNamespacesIntoDistinctArgumentIndices()
    {
        var logical = new ShaderBindingLayout
        {
            Textures =
            [
                new ShaderBindingSlot(ShaderBindingKind.Texture, 0, "ReadTexture", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.StorageTexture, 0, "WriteTexture", stages: ShaderStageMask.Fragment),
            ],
        };

        ShaderBindingLayout compact = RuntimeSlangReflection.AttachMetalPhysicalSlots(logical, compactIndices: true);

        Assert.Equal(0, Assert.Single(compact.Textures, slot => slot.Name == "ReadTexture").PhysicalBinding);
        Assert.Equal(1, Assert.Single(compact.Textures, slot => slot.Name == "WriteTexture").PhysicalBinding);
    }

    [Fact]
    public void MetalSourceCompactionDisambiguatesDuplicateSlangTextureIndicesByResourceName()
    {
        const string source = "texture2d<float> ReadTexture_1 [[texture(3)]]; texture2d<float> WriteTexture_1 [[texture(3)]];";
        var logical = new ShaderBindingLayout
        {
            Textures =
            [
                new ShaderBindingSlot(ShaderBindingKind.Texture, 5, "ReadTexture", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.StorageTexture, 9, "WriteTexture", stages: ShaderStageMask.Fragment),
            ],
        };

        ShaderBindingLayout physical = RuntimeSlangReflection.AttachMetalSourcePhysicalSlots(
            logical,
            Encoding.UTF8.GetBytes(source));
        string compactSource = Encoding.UTF8.GetString(RuntimeSlangReflection.CompactMetalSourceIndices(
            Encoding.UTF8.GetBytes(source), physical));
        ShaderBindingLayout compactLayout = RuntimeSlangReflection.CompactMetalPhysicalSlots(physical);

        Assert.Contains("ReadTexture_1 [[texture(0)]]", compactSource, StringComparison.Ordinal);
        Assert.Contains("WriteTexture_1 [[texture(1)]]", compactSource, StringComparison.Ordinal);
        Assert.Equal(0, Assert.Single(compactLayout.Textures, slot => slot.Name == "ReadTexture").PhysicalBinding);
        Assert.Equal(1, Assert.Single(compactLayout.Textures, slot => slot.Name == "WriteTexture").PhysicalBinding);
    }

    [Fact]
    public void MetalSourceCompactionMatchesSlangUPrefixedGlobalResourceNames()
    {
        const string source = "texture2d<float> U_SpecularMap_1 [[texture(3)]]; "
            + "texture2d<float> U_StaticLightData_1[[texture(3)]]; "
            + "texture2d<float> _DynamicLightData_1 [[texture(3)]];";
        var logical = new ShaderBindingLayout
        {
            Textures =
            [
                new ShaderBindingSlot(ShaderBindingKind.Texture, 3, "_SpecularMap", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.Texture, 4, "_StaticLightData", stages: ShaderStageMask.Fragment),
                new ShaderBindingSlot(ShaderBindingKind.Texture, 6, "_DynamicLightData", stages: ShaderStageMask.Fragment),
            ],
        };

        ShaderBindingLayout physical = RuntimeSlangReflection.AttachMetalSourcePhysicalSlots(
            logical,
            Encoding.UTF8.GetBytes(source));
        string compactSource = Encoding.UTF8.GetString(RuntimeSlangReflection.CompactMetalSourceIndices(
            Encoding.UTF8.GetBytes(source), physical));
        ShaderBindingLayout compactLayout = RuntimeSlangReflection.CompactMetalPhysicalSlots(physical);

        Assert.Contains("U_SpecularMap_1 [[texture(1)]]", compactSource, StringComparison.Ordinal);
        Assert.Contains("U_StaticLightData_1[[texture(2)]]", compactSource, StringComparison.Ordinal);
        Assert.Contains("_DynamicLightData_1 [[texture(0)]]", compactSource, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(compactLayout.Textures, slot => slot.Name == "_SpecularMap").PhysicalBinding);
        Assert.Equal(2, Assert.Single(compactLayout.Textures, slot => slot.Name == "_StaticLightData").PhysicalBinding);
        Assert.Equal(0, Assert.Single(compactLayout.Textures, slot => slot.Name == "_DynamicLightData").PhysicalBinding);
    }

    [Fact]
    public void MetalInterleavedSharedAndFragmentResourcesKeepDistinctSlots()
    {
        const string vertex = """
cbuffer Globals : register(b0) { float4 GlobalColor; };
cbuffer ObjectData : register(b1) { float4 ObjectColor; };
cbuffer Material : register(b2) { float4 MaterialColor; };
float4 vertexMain(uint id : SV_VertexID) : SV_Position
{ return GlobalColor + ObjectColor + MaterialColor; }
""";
        const string fragment = """
cbuffer Globals : register(b0) { float4 GlobalColor; };
cbuffer ObjectData : register(b1) { float4 ObjectColor; };
cbuffer Lighting : register(b3) { float4 LightColor; };
Texture2D MainTexture : register(t0);
SamplerState MainSampler : register(s0);
cbuffer Material : register(b2) { float4 MaterialColor; };
Texture2D LightTexture : register(t4);
SamplerState LightSampler : register(s4);
float4 fragmentMain(float2 uv : TEXCOORD0) : SV_Target
{ return GlobalColor + ObjectColor + LightColor + MaterialColor
    + MainTexture.Sample(MainSampler, uv) + LightTexture.Sample(LightSampler, uv); }
""";

        ShaderCompileResult result = CompileSlang(GraphicsBackend.Metal, vertex, fragment);
        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        ShaderBindingSlot mainTexture = Assert.Single(layout.Textures, x => x.Name == "MainTexture");
        ShaderBindingSlot lightTexture = Assert.Single(layout.Textures, x => x.Name == "LightTexture");
        Assert.NotEqual(
            MetalCommandTranslator.PhysicalIndex(mainTexture, ShaderStageMask.Fragment),
            MetalCommandTranslator.PhysicalIndex(lightTexture, ShaderStageMask.Fragment));
    }

    [Fact]
    public void MetalUniformBufferSizeUsesThePhysicalMetalLayout()
    {
        const string vertex = """
cbuffer Globals : register(b0) { float3 Position; float Padding; float4 Color; };
float4 vertexMain(uint id : SV_VertexID) : SV_Position
{ return Color + float4(Position, Padding); }
""";
        const string fragment = """
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{ return position; }
""";

        ShaderCompileResult result = CompileSlang(GraphicsBackend.Metal, vertex, fragment);

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        ShaderBindingSlot globals = Assert.Single(layout.Buffers);
        Assert.Equal("Globals", globals.Name);
        // HLSL reflection reports 32 bytes; Metal's float3 member occupies a full 16-byte slot
        // and the following float4 begins on the next 16-byte boundary.
        Assert.Equal(48, globals.ByteSize);
    }

    [Fact]
    public void MetalPhysicalReflectionRetainsExplicitSamplerIndex()
    {
        ShaderCompileResult result = CompileSlang(
            GraphicsBackend.Metal,
            "float4 vertexMain(uint id : SV_VertexID) : SV_Position { return 0; }",
            "Texture2D MainTexture : register(t0);\n" +
            "#ifdef XENGINE_METAL\nSamplerState MainSampler : register(s8);\n" +
            "#else\nSamplerState MainSampler : register(s32);\n#endif\n" +
            "float4 fragmentMain(float2 uv : TEXCOORD0) : SV_Target { return MainTexture.Sample(MainSampler, uv); }");

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        ShaderBindingSlot sampler = Assert.Single(layout.Samplers);
        Assert.Equal(8, sampler.PhysicalBinding);
    }

    [Fact]
    public void MetalTextureLoadResourcesDoNotNeedHighSlotSamplers()
    {
        const string vertex = "float4 vertexMain(uint id : SV_VertexID) : SV_Position { return 0; }";
        const string fragment = """
Texture2D ClusterLightList : register(t29);
#ifndef XENGINE_METAL
SamplerState ClusterLightListSampler : register(s29);
#endif
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{ return ClusterLightList.Load(int3(0, 0, 0)); }
""";

        ShaderCompileResult result = CompileSlang(GraphicsBackend.Metal, vertex, fragment);

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        Assert.Contains(layout.Textures, x => x.Name == "ClusterLightList");
        Assert.DoesNotContain(layout.Samplers, x => x.Name == "ClusterLightListSampler");
    }

    [Fact]
    public void MetalFragmentOnlyUniformBufferKeepsItsExplicitRegister()
    {
        const string vertex = "float4 vertexMain(uint id : SV_VertexID) : SV_Position { return 0; }";
        const string fragment = """
cbuffer BlitMaterial : register(b2) { float _FlipY; };
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{ return float4(_FlipY, _FlipY, _FlipY, _FlipY); }
""";

        ShaderCompileResult result = CompileSlang(GraphicsBackend.Metal, vertex, fragment);

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        ShaderBindingSlot blit = Assert.Single(layout.Buffers);
        Assert.Equal("BlitMaterial", blit.Name);
        Assert.Equal(2, blit.Slot);
        Assert.Equal(2, MetalCommandTranslator.PhysicalIndex(blit, ShaderStageMask.Fragment));
    }

    [Fact]
    public void ParsedDefaultBlitShader_RetainsFragmentBufferRegister()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string source = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Assets", "Defaults", "Blit.shader"));
        Assert.True(ShaderParser.ParseShader("Blit.shader", source, _ => null, out Shader? parsed));
        using Shader shader = parsed!;

        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(
            shader.GetPass("Blit").CreateHlslCompileRequest(GraphicsBackend.Metal, null, string.Empty));

        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        ShaderBindingSlot blit = Assert.Single(layout.Buffers);
        Assert.Equal(2, blit.Slot);
        Assert.Equal(2, MetalCommandTranslator.PhysicalIndex(blit, ShaderStageMask.Fragment));
    }

    private static ShaderCompileResult CompileSlang(
        GraphicsBackend backend,
        string vertexSource,
        string fragmentSource) =>
        new RuntimeSlangShaderCompiler().Compile(CreateRequest(backend, vertexSource, fragmentSource));

    private static ShaderCompileRequest CreateRequest(
        GraphicsBackend backend,
        string vertexSource,
        string fragmentSource) => new()
    {
        TargetBackend = backend,
        Language = ShaderLanguage.Hlsl,
        VertexSource = vertexSource,
        FragmentSource = fragmentSource,
        VertexEntryPoint = "vertexMain",
        FragmentEntryPoint = "fragmentMain",
    };

    private static ShaderCompileResult CreateContractResult(int slot, string name) => new()
    {
        Success = true,
        BindingLayout = new ShaderBindingLayout
        {
            Textures = [new ShaderBindingSlot(ShaderBindingKind.Texture, slot, name)],
        },
        VertexInputLayout = new ShaderVertexInputLayout(),
    };

    private static ShaderCompileResult CreatePhysicalContractResult(ShaderPhysicalBinding[] physicalBindings) => new()
    {
        Success = true,
        BindingLayout = new ShaderBindingLayout
        {
            Buffers =
            [
                new ShaderBindingSlot(
                    ShaderBindingKind.Buffer,
                    0,
                    "Globals",
                    physicalBindings: physicalBindings),
            ],
        },
        VertexInputLayout = new ShaderVertexInputLayout(),
    };

    private static void AssertBinding(
        ShaderBindingSlot binding,
        ShaderBindingKind kind,
        int space,
        int slot,
        int count,
        string name,
        GraphicsBackend backend)
    {
        Assert.Equal(kind, binding.Kind);
        Assert.Equal(space, binding.Space);
        Assert.Equal(slot, binding.Slot);
        Assert.Equal(count, binding.Count);
        Assert.Equal(name, binding.Name);
        Assert.Equal(ShaderStageMask.Vertex | ShaderStageMask.Fragment, binding.Stages);
        Assert.Equal(backend is GraphicsBackend.Vulkan or GraphicsBackend.WebGPU, binding.HasPhysicalLocation);
    }

    private static void AssertPhysicalBindings(
        ShaderBindingSlot binding,
        int logicalSlot,
        params ShaderPhysicalBinding[] expected)
    {
        Assert.Equal(logicalSlot, binding.Slot);
        Assert.Equal(0, binding.Space);
        Assert.Equal(expected, binding.PhysicalBindings);
    }

    private static void AssertPhysicalBindingsExist(ShaderBindingSlot binding, int logicalSlot)
    {
        Assert.Equal(logicalSlot, binding.Slot);
        Assert.Equal(0, binding.Space);
        Assert.True(binding.HasPhysicalLocation);
        Assert.NotEmpty(binding.PhysicalBindings);
    }

    private static string FormatPhysical(ShaderBindingSlot binding)
    {
        var parts = new string[binding.PhysicalBindings.Length];
        for (int i = 0; i < binding.PhysicalBindings.Length; i++)
        {
            ShaderPhysicalBinding physical = binding.PhysicalBindings[i];
            parts[i] = $"@group({physical.Space}) @binding({physical.Binding}) stages={physical.Stages}";
        }

        return string.Join(", ", parts);
    }

    private static void AssertField(
        ShaderUniformField field,
        string name,
        int offset,
        int size,
        ShaderScalarType scalarType,
        byte rows,
        byte columns,
        int elementCount,
        int arrayStride,
        int matrixStride)
    {
        Assert.Equal(name, field.Name);
        Assert.Equal(offset, field.Offset);
        Assert.Equal(size, field.Size);
        Assert.Equal(scalarType, field.ScalarType);
        Assert.Equal(rows, field.Rows);
        Assert.Equal(columns, field.Columns);
        Assert.Equal(elementCount, field.ElementCount);
        Assert.Equal(arrayStride, field.ArrayStride);
        Assert.Equal(matrixStride, field.MatrixStride);
    }

    private static void AssertVertexInput(
        ShaderVertexInputSlot input,
        VertexFormat.VertexSemantic semantic,
        uint location,
        string semanticName,
        uint semanticIndex,
        ShaderScalarType scalarType,
        byte componentCount)
    {
        Assert.Equal((uint)semantic, input.Semantic);
        Assert.Equal(location, input.Location);
        Assert.Equal(semanticName, input.SemanticName);
        Assert.Equal(semanticIndex, input.SemanticIndex);
        Assert.Equal(new ShaderVertexInputFormat(scalarType, componentCount), input.Format);
    }
}
