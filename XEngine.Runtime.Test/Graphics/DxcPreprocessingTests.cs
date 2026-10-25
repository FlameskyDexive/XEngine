using System;
using System.Collections.Generic;
using System.IO;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class DxcPreprocessingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "XEngine DXC preprocessing", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, false)]
    [InlineData(GraphicsBackend.Direct3D12, false)]
    [InlineData(GraphicsBackend.Vulkan, true)]
    [InlineData(GraphicsBackend.Direct3D12, true)]
    public void NestedIncludesKeywordsAndStageMacros_ReflectOnlyActiveDeclarations(GraphicsBackend backend, bool colors)
    {
        // The DXC toolchain is vendored for Windows only.
        if (!OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "registers.hlsl"), """
            #define TEXTURE_SLOT t3
            #define SAMPLER_SLOT s2
            #if defined(DISABLED_KEYWORD)
            #error Disabled keyword must not be defined
            #endif
            #if EXPECT_SPIRV != defined(__spirv__)
            #error Preprocessing and compilation must use the same target
            #endif
            """);
        string includePath = Path.Combine(_directory, "stage.hlsl");
        File.WriteAllText(includePath, """
            #include "registers.hlsl"
            #if __SHADER_TARGET_STAGE == 1
            cbuffer VertexData : register(b4) { float4 tint; };
            struct VSInput {
                float3 position : POSITION;
            #ifdef HAS_COLORS
                float4 color : COLOR0;
            #endif
            };
            #else
            Texture2D FragmentTexture : register(TEXTURE_SLOT);
            SamplerState FragmentSampler : register(SAMPLER_SLOT);
            #endif
            """);
        string include = "#include \"" + includePath.Replace('\\', '/') + "\"\n";
        var request = new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexEntryPoint = "vertexEntry",
            FragmentEntryPoint = "fragmentEntry",
            Keywords = new Dictionary<string, bool> { ["HAS_COLORS"] = colors, ["DISABLED_KEYWORD"] = false },
            ExtraDefines = [backend == GraphicsBackend.Vulkan ? "EXPECT_SPIRV 1" : "EXPECT_SPIRV 0"],
            VertexSource = include + """
                float4 vertexEntry(VSInput input) : SV_Position {
                    float4 result = float4(input.position, 1) * tint;
                #ifdef HAS_COLORS
                    result *= input.color;
                #endif
                    return result;
                }
                """,
            FragmentSource = include + "float4 fragmentEntry() : SV_Target { return FragmentTexture.Sample(FragmentSampler, 0.5.xx); }",
        };

        ShaderCompileResult result = new DxcShaderCompiler().Compile(request);
        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        ShaderBindingSlot buffer = Assert.Single(layout.Buffers);
        Assert.Equal("VertexData", buffer.Name);
        Assert.Equal(4, buffer.Slot);
        Assert.Equal(ShaderStageMask.Vertex, buffer.Stages);
        ShaderBindingSlot texture = Assert.Single(layout.Textures);
        Assert.Equal("FragmentTexture", texture.Name);
        Assert.Equal(3, texture.Slot);
        Assert.Equal(ShaderStageMask.Fragment, texture.Stages);
        ShaderBindingSlot sampler = Assert.Single(layout.Samplers);
        Assert.Equal(2, sampler.Slot);
        Assert.Equal(ShaderStageMask.Fragment, sampler.Stages);
        Assert.Equal(colors ? 2 : 1, Assert.IsType<ShaderVertexInputLayout>(result.VertexInputLayout).Inputs.Length);
        Assert.Equal(backend == GraphicsBackend.Vulkan ? ShaderBytecodeFormat.SpirV : ShaderBytecodeFormat.Dxil, result.Format);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, false)]
    [InlineData(GraphicsBackend.Direct3D12, false)]
    [InlineData(GraphicsBackend.Vulkan, true)]
    [InlineData(GraphicsBackend.Direct3D12, true)]
    public void CompileDiagnostics_PreserveAuthoredLineAndFailingStage(GraphicsBackend backend, bool fragment)
    {
        // The DXC toolchain is vendored for Windows only.
        if (!OperatingSystem.IsWindows())
            return;

        string vertex = "float4 main(float4 position : POSITION) : SV_Position { return position; }";
        string pixel = "float4 main() : SV_Target { return 1.0.xxxx; }";
        string broken = "#line 71 \"Hovl/MappedError.hlsl\"\n" + (fragment
            ? "float4 main() : SV_Target { return missing_value; }"
            : "float4 main(float4 position : POSITION) : SV_Position { return missing_value; }");
        ShaderCompileResult result = new DxcShaderCompiler().Compile(new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexSource = fragment ? vertex : broken,
            FragmentSource = fragment ? broken : pixel,
            ExtraDefines = ["UNUSED_DEFINE"],
        });
        Assert.False(result.Success);
        Assert.Contains(fragment ? "fragment compile failed" : "vertex compile failed", result.ErrorMessage);
        Assert.Contains("Hovl/MappedError.hlsl:71:", result.ErrorMessage);
    }
}
