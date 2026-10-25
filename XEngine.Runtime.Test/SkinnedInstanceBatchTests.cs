using System.Reflection;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public class SkinnedInstanceBatchTests
{
    [Fact]
    public void PerObjectLightingAndUnknownOverridesPreventMerging()
    {
        using var texture = new Texture2D(120, 1, false, TextureImageFormat.Float4);
        var p = new PropertyState();
        p.SetTexture("boneMatrixTexture", texture);
        p.SetColor("_MainColor", Color.White);
        p.SetInt("boneCount", 30);
        p.SetInt("_GIMode", 0);
        Assert.True(SkinnedInstanceBatch.SupportsProperties(p));
        p.SetInt("_GIMode", 2);
        Assert.False(SkinnedInstanceBatch.SupportsProperties(p));
        p.SetInt("_GIMode", 1);
        Assert.False(SkinnedInstanceBatch.SupportsProperties(p));
        p.SetInt("_GIMode", 3);
        Assert.True(SkinnedInstanceBatch.SupportsProperties(p));
        p.SetFloat("_CustomEffect", .5f);
        Assert.False(SkinnedInstanceBatch.SupportsProperties(p));
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void StandardSkinnedInstanceVariantsCompile(GraphicsBackend backend)
    {
        Shader shader = Shader.LoadDefault(DefaultShader.Standard);
        var compiler = new RuntimeSlangShaderCompiler();
        foreach (ShaderPass pass in shader.Passes)
        {
            string vertex = (string)typeof(ShaderPass).GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
            string fragment = (string)typeof(ShaderPass).GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
            const string defines = "#define GPU_INSTANCING\n#define SKINNED\n#define HAS_BONEINDICES\n#define HAS_BONEWEIGHTS\n#define HAS_TANGENTS\n#define HAS_UV2\n";
            var result = compiler.Compile(new ShaderCompileRequest {
                TargetBackend = backend, Language = ShaderLanguage.Hlsl,
                VertexSource = defines + vertex, FragmentSource = defines + fragment });
            Assert.True(result.Success, pass.Name + ": " + result.ErrorMessage);
        }
    }
}
