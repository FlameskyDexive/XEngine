// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WG7: WebGPU as a shader artifact target. WebGPU accepts no binary shader form, so its artifacts
/// carry WGSL text - the contract that has to hold is that every layer (producer, artifact, reader)
/// agrees on that, and that WGSL never leaks into a backend that cannot consume it.
/// </summary>
[Collection(ShaderCompilerEnvironmentCollection.Name)]
public class WebGPUShaderTargetTests
{
    [Theory]
    [InlineData(GraphicsBackend.Vulkan, ShaderBytecodeFormat.SpirV)]
    [InlineData(GraphicsBackend.Direct3D12, ShaderBytecodeFormat.Dxil)]
    [InlineData(GraphicsBackend.WebGPU, ShaderBytecodeFormat.Wgsl)]
    [InlineData(GraphicsBackend.Metal, ShaderBytecodeFormat.MetalLib)]
    [InlineData(GraphicsBackend.WebGL2, ShaderBytecodeFormat.GlslEs300)]
    public void ArtifactBackends_MapToExactlyOneBytecodeFormat(GraphicsBackend backend, ShaderBytecodeFormat expected)
    {
        Assert.True(ShaderBytecodeFormats.IsArtifactBackend(backend));
        Assert.Equal(expected, ShaderBytecodeFormats.GetExpected(backend));
    }

    [Theory]
    [InlineData(GraphicsBackend.OpenGL)]
    [InlineData(GraphicsBackend.Null)]
    [InlineData(GraphicsBackend.Auto)]
    public void RuntimeCompilingBackends_DoNotConsumeArtifacts(GraphicsBackend backend)
    {
        Assert.False(ShaderBytecodeFormats.IsArtifactBackend(backend));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShaderBytecodeFormats.GetExpected(backend));
    }

    [Fact]
    public void WebGPU_CompilesThroughSlangBecauseDxcCannotEmitWgsl()
    {
        Assert.Equal(ShaderCompilerKind.Slang, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.WebGPU));
        Assert.True(ShaderCompilerFactory.RequiresHlsl(GraphicsBackend.WebGPU));
    }

    [Fact]
    public void WebGPU_IgnoresAnExplicitDxcSelection()
    {
        string? previous = Environment.GetEnvironmentVariable("XENGINE_SHADER_COMPILER");
        try
        {
            Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "dxc");

            Assert.Equal(ShaderCompilerKind.Dxc, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.Vulkan));
            Assert.Equal(ShaderCompilerKind.Slang, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.WebGPU));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", previous);
        }
    }

    [Fact]
    public void Artifact_RejectsBytecodeThatIsNotWgsl()
    {
        var ex = Assert.Throws<ArgumentException>(() => CreateArtifact(GraphicsBackend.WebGPU, ShaderBytecodeFormat.SpirV));
        Assert.Contains("Wgsl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_RejectsWgslForABackendThatCannotReadIt()
    {
        Assert.Throws<ArgumentException>(() => CreateArtifact(GraphicsBackend.Vulkan, ShaderBytecodeFormat.Wgsl));
    }

    [Fact]
    public void GetTargetProfile_MapsWebGPUToWgsl()
    {
        Assert.Equal("wgsl_1_0", RuntimeSlangShaderCompiler.GetTargetProfile(GraphicsBackend.WebGPU));
    }

    [Fact]
    public void Artifact_AcceptsWgslForWebGPU()
    {
        ShaderArtifact artifact = CreateArtifact(GraphicsBackend.WebGPU, ShaderBytecodeFormat.Wgsl);

        Assert.Equal(GraphicsBackend.WebGPU, artifact.Backend);
        Assert.Equal(ShaderBytecodeFormat.Wgsl, artifact.Bytecode.Format);
    }

    [Fact]
    public void FactoryCompilesHlslToWgslForWebGPU()
    {
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(GraphicsBackend.WebGPU);
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.WebGPU,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 vertexMain(uint id : SV_VertexID) : SV_Position { float2 p = float2((id << 1) & 2, id & 2); return float4(p * 2 - 1, 0, 1); }",
            FragmentSource = "float4 fragmentMain() : SV_Target { return float4(1, 0, 1, 1); }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
            ShaderName = "Test/WebGPU",
            PassName = "Pass",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ShaderBytecodeFormat.Wgsl, result.Format);
        Assert.NotNull(result.VertexBytecode);
        Assert.NotNull(result.FragmentBytecode);
        string vertex = System.Text.Encoding.UTF8.GetString(result.VertexBytecode);
        string fragment = System.Text.Encoding.UTF8.GetString(result.FragmentBytecode);
        Assert.Contains("@vertex", vertex, StringComparison.Ordinal);
        Assert.Contains("@fragment", fragment, StringComparison.Ordinal);
    }

    private static ShaderArtifact CreateArtifact(GraphicsBackend backend, ShaderBytecodeFormat format)
    {
        var request = new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
            FragmentSource = "float4 main() : SV_Target { return 1; }",
            ShaderName = "Test/Shader",
            PassName = "Pass",
            VariantKey = "",
        };

        return new ShaderArtifact(
            backend,
            ShaderCompilerKind.Slang,
            "Slang 2026.12",
            ShaderArtifactKey.ComputeSourceHash(request),
            "wgsl_1_0",
            ShaderArtifact.DefaultMatrixLayout,
            "Test/Shader",
            "Pass",
            "",
            "main",
            "main",
            [],
            "wg7-test",
            new CompiledShaderBytecode(ShaderLanguage.Hlsl, format, [1, 2, 3, 4], [5, 6, 7, 8]));
    }
}
