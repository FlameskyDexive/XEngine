// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;
using XEngine.Runtime;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// WebGL2 variant collection stamps WebGL2 / GLSL ES profile without changing default
/// ShaderImporter Vulkan+D3D12 artifacts.
/// </summary>
public sealed class WebGL2ShaderVariantCollectorTests
{
    [Fact]
    public void CollectsWebGL2VariantsWithGlslEsProfile()
    {
        var pass = new ShaderPass(
            "Forward",
            null,
            null,
            new RasterizerState(),
            "",
            "",
            "",
            hlslVertexSource: "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
            hlslFragmentSource: "float4 main() : SV_Target { return 1; }",
            modernSourceKind: ShaderModernSourceKind.Slang);
        var shader = new Shader("Tests/WebGL2", [], [pass], ["FEATURE_B;FEATURE_A;"], ["FEATURE_A", "FEATURE_B"]);
        VulkanShaderVariantCollection collection = WebGL2ShaderVariantCollector.Collect(
        [
            new VulkanShaderVariantRoot(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                shader,
                [
                    new VulkanShaderVariantUsage("material", new Dictionary<string, bool>
                    {
                        ["FEATURE_B"] = true,
                        ["FEATURE_A"] = true,
                    }),
                ]),
        ]);

        Assert.Equal(2, collection.Descriptors.Count);
        string expectedProfile = RuntimeSlangShaderCompiler.GetTargetProfile(GraphicsBackend.WebGL2);
        Assert.All(collection.Descriptors, descriptor =>
        {
            Assert.Equal("WebGL2", descriptor.Backend);
            Assert.Equal(expectedProfile, descriptor.TargetProfile);
            Assert.Equal(WebGL2ShaderVariantCollector.TargetEnvironment, descriptor.TargetEnvironment);
            Assert.Equal("baseline", descriptor.ShaderTier);
            Assert.Equal("Forward", descriptor.PassName);
        });
        Assert.Equal("", collection.Descriptors[0].CanonicalKeywordSet);
        Assert.Equal("FEATURE_A;FEATURE_B;", collection.Descriptors[1].CanonicalKeywordSet);

        IReadOnlyList<ShaderArtifactBuildSelection> selections =
            WebGL2ShaderVariantCollector.CreateArtifactSelections(collection);
        Assert.Equal(collection.Descriptors.Count, selections.Count);
        Assert.All(selections, selection => Assert.Equal(GraphicsBackend.WebGL2, selection.Backend));
        Assert.Equal(ShaderBytecodeFormat.GlslEs300, ShaderBytecodeFormats.GetExpected(GraphicsBackend.WebGL2));
    }
}
