// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test;

[Collection(ShaderCompilerEnvironmentCollection.Name)]
public sealed class HovlShaderCompileTests
{
    [Theory]
    [InlineData("Add_CenterGlow")]
    [InlineData("Add_CenterGlowFade")]
    [InlineData("Blend_CenterGlow")]
    [InlineData("AddTrail")]
    [InlineData("SmoothSmoke")]
    [InlineData("Blend_TwoSides")]
    [InlineData("LightGlow")]
    [InlineData("Distortion")]
    [InlineData("DissolveNoise")]
    [InlineData("Electricity")]
    public void PackageShader_CompilesDefaultBillboardMeshAndRibbon(string shaderName)
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        using Shader shader = Shader.LoadFromFile(Path.Combine(repoRoot, "Packages", "com.xengine.rpgvfx",
            "Runtime", "Shaders", shaderName + ".shader"));
        if (shaderName == "Distortion")
        {
            var grabPass = Assert.Single(shader.Passes);
            Assert.True(grabPass.HasGrabTexture);
            Assert.Equal("_GrabTexture", grabPass.GrabTextureName);
        }
        var compiler = new RuntimeSlangShaderCompiler();
        var keywords = new Dictionary<string, bool>();
        foreach (GraphicsBackend backend in new[] { GraphicsBackend.Vulkan, GraphicsBackend.Direct3D12 })
        {
            for (int variant = 0; variant < 4; variant++)
            {
                keywords["GPU_INSTANCING"] = variant == 1 || variant == 2;
                keywords["PARTICLE_MESH"] = variant == 2;
                keywords["HAS_COLORS"] = variant == 3;
                foreach (var pass in shader.Passes)
                {
                    ShaderCompileResult result = compiler.Compile(pass.CreateHlslCompileRequest(
                        backend, keywords, variant.ToString()));
                    Assert.True(result.Success, $"{shaderName}/{backend}/variant {variant}: {result.ErrorMessage}");
                    Assert.NotEmpty(result.VertexBytecode!);
                    Assert.NotEmpty(result.FragmentBytecode!);
                }
            }
        }
    }
}
