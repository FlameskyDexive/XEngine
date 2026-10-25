using System.Collections.Generic;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class StandardDirectionalVariantTests
{
    [Fact]
    public void ReusedMaterialReturnsToFullLightingWhenEitherTreeGainsALight()
    {
        Shader shader = Shader.LoadDefault(DefaultShader.Standard);
        ShaderPass pass = shader.GetPass(0);
        using var material = new Material(shader);
        var globals = PropertyState.s_globalInts;
        bool hadStatic = globals.TryGetValue("_StaticLightRoot", out int previousStatic);
        bool hadDynamic = globals.TryGetValue("_DynamicLightRoot", out int previousDynamic);
        try
        {
            globals.Remove("_StaticLightRoot");
            globals.Remove("_DynamicLightRoot");
            pass.UpdateLightingVariant(material, GraphicsBackend.Vulkan);
            Assert.DoesNotContain("DIRECTIONAL_ONLY", material.KeywordKey);
            globals["_StaticLightRoot"] = -1;
            globals["_DynamicLightRoot"] = -1;
            pass.UpdateLightingVariant(material, GraphicsBackend.Vulkan);
            Assert.Contains("DIRECTIONAL_ONLY;", material.KeywordKey);
            globals["_DynamicLightRoot"] = 0;
            pass.UpdateLightingVariant(material, GraphicsBackend.Vulkan);
            Assert.DoesNotContain("DIRECTIONAL_ONLY", material.KeywordKey);
            globals["_DynamicLightRoot"] = -1;
            globals["_StaticLightRoot"] = 0;
            pass.UpdateLightingVariant(material, GraphicsBackend.Vulkan);
            Assert.DoesNotContain("DIRECTIONAL_ONLY", material.KeywordKey);
            globals["_StaticLightRoot"] = -1;
            pass.UpdateLightingVariant(material, GraphicsBackend.Vulkan);
            Assert.Contains("DIRECTIONAL_ONLY;", material.KeywordKey);
            pass.UpdateLightingVariant(material, GraphicsBackend.OpenGL);
            Assert.DoesNotContain("DIRECTIONAL_ONLY", material.KeywordKey);
        }
        finally
        {
            if (hadStatic) globals["_StaticLightRoot"] = previousStatic;
            else globals.Remove("_StaticLightRoot");
            if (hadDynamic) globals["_DynamicLightRoot"] = previousDynamic;
            else globals.Remove("_DynamicLightRoot");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothLightingPathsCompileForSkinnedVulkanGeometry(bool directionalOnly)
    {
        ShaderPass pass = Shader.LoadDefault(DefaultShader.Standard).GetPass(0);
        var keywords = new Dictionary<string, bool>
        {
            ["SKINNED"] = true, ["HAS_BONEINDICES"] = true, ["HAS_BONEWEIGHTS"] = true,
            ["HAS_TANGENTS"] = true, ["DIRECTIONAL_ONLY"] = directionalOnly,
        };
        ShaderCompileRequest request = pass.CreateHlslCompileRequest(
            GraphicsBackend.Vulkan, keywords, ShaderPass.BuildKeywordKey(keywords));
        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(request);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.VertexBytecode!);
        Assert.NotEmpty(result.FragmentBytecode!);
    }
}
