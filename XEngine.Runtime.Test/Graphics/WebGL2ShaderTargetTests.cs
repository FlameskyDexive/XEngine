// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WebGL2 as a shader artifact target. Artifacts carry UTF-8 GLSL ES 3.00 text; the device
/// never runs Slang.
/// </summary>
[Collection(ShaderCompilerEnvironmentCollection.Name)]
public class WebGL2ShaderTargetTests
{
    [Fact]
    public void WebGL2_MapsToGlslEs300AndCompilesThroughSlang()
    {
        Assert.True(ShaderBytecodeFormats.IsArtifactBackend(GraphicsBackend.WebGL2));
        Assert.Equal(ShaderBytecodeFormat.GlslEs300, ShaderBytecodeFormats.GetExpected(GraphicsBackend.WebGL2));
        Assert.Equal(ShaderCompilerKind.Slang, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.WebGL2));
        Assert.True(ShaderCompilerFactory.RequiresHlsl(GraphicsBackend.WebGL2));
        string profile = RuntimeSlangShaderCompiler.GetTargetProfile(GraphicsBackend.WebGL2);
        Assert.Contains(profile, RuntimeSlangShaderCompiler.GlslEsProfileCandidates);
    }

    [Fact]
    public void WebGL2_IgnoresAnExplicitDxcSelection()
    {
        string? previous = Environment.GetEnvironmentVariable("XENGINE_SHADER_COMPILER");
        try
        {
            Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "dxc");

            Assert.Equal(ShaderCompilerKind.Dxc, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.Vulkan));
            Assert.Equal(ShaderCompilerKind.Slang, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.WebGL2));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", previous);
        }
    }

    [Fact]
    public void Artifact_RejectsBytecodeThatIsNotGlslEs300()
    {
        var ex = Assert.Throws<ArgumentException>(() => CreateArtifact(GraphicsBackend.WebGL2, ShaderBytecodeFormat.SpirV));
        Assert.Contains("GlslEs300", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_RejectsGlslEs300ForABackendThatCannotReadIt()
    {
        Assert.Throws<ArgumentException>(() => CreateArtifact(GraphicsBackend.Vulkan, ShaderBytecodeFormat.GlslEs300));
    }

    [Fact]
    public void Artifact_AcceptsGlslEs300ForWebGL2()
    {
        ShaderArtifact artifact = CreateArtifact(GraphicsBackend.WebGL2, ShaderBytecodeFormat.GlslEs300);

        Assert.Equal(GraphicsBackend.WebGL2, artifact.Backend);
        Assert.Equal(ShaderBytecodeFormat.GlslEs300, artifact.Bytecode.Format);
    }

    [Fact]
    public void FactoryCompilesHlslToGlslEsForWebGL2()
    {
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(GraphicsBackend.WebGL2);
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.WebGL2,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 vertexMain(uint id : SV_VertexID) : SV_Position { float2 p = float2((id << 1) & 2, id & 2); return float4(p * 2 - 1, 0, 1); }",
            FragmentSource = "float4 fragmentMain() : SV_Target { return float4(1, 0, 1, 1); }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
            ShaderName = "Test/WebGL2",
            PassName = "Pass",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ShaderBytecodeFormat.GlslEs300, result.Format);
        Assert.NotNull(result.VertexBytecode);
        Assert.NotNull(result.FragmentBytecode);
        string vertex = Encoding.UTF8.GetString(result.VertexBytecode);
        string fragment = Encoding.UTF8.GetString(result.FragmentBytecode);
        Assert.Contains("#version", vertex, StringComparison.Ordinal);
        Assert.Contains("#version", fragment, StringComparison.Ordinal);
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
            RuntimeSlangShaderCompiler.GetTargetProfile(backend == GraphicsBackend.WebGL2 ? GraphicsBackend.WebGL2 : backend),
            ShaderArtifact.DefaultMatrixLayout,
            "Test/Shader",
            "Pass",
            "",
            "main",
            "main",
            [],
            "gl2-test",
            new CompiledShaderBytecode(ShaderLanguage.Hlsl, format, [1, 2, 3, 4], [5, 6, 7, 8]));
    }
}
