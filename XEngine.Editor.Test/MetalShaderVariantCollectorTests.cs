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
/// S1: Metal variant collection stamps Metal / metal profile without changing default
/// ShaderImporter Vulkan+D3D12 artifacts.
/// </summary>
public sealed class MetalShaderVariantCollectorTests
{
    [Fact]
    public void CollectsMetalVariantsWithMetalLibProfile()
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
        var shader = new Shader("Tests/Metal", [], [pass], ["FEATURE_B;FEATURE_A;"], ["FEATURE_A", "FEATURE_B"]);
        VulkanShaderVariantCollection collection = MetalShaderVariantCollector.Collect(
        [
            new VulkanShaderVariantRoot(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
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
        Assert.All(collection.Descriptors, descriptor =>
        {
            Assert.Equal("Metal", descriptor.Backend);
            Assert.Equal("metal", descriptor.TargetProfile);
            Assert.Equal(MetalShaderVariantCollector.TargetEnvironment, descriptor.TargetEnvironment);
            Assert.Equal("baseline", descriptor.ShaderTier);
            Assert.Equal("Forward", descriptor.PassName);
        });
        Assert.Equal("", collection.Descriptors[0].CanonicalKeywordSet);
        Assert.Equal("FEATURE_A;FEATURE_B;", collection.Descriptors[1].CanonicalKeywordSet);

        IReadOnlyList<ShaderArtifactBuildSelection> selections =
            MetalShaderVariantCollector.CreateArtifactSelections(collection);
        Assert.Equal(collection.Descriptors.Count, selections.Count);
        Assert.All(selections, selection => Assert.Equal(GraphicsBackend.Metal, selection.Backend));

        IReadOnlyList<ShaderArtifactBuildSelection> simulatorSelections =
            MetalShaderVariantCollector.CreateArtifactSelections(collection, ShaderTargetEnvironment.IOSSimulator);
        Assert.All(simulatorSelections, selection =>
            Assert.Equal(ShaderTargetEnvironment.IOSSimulator, selection.TargetEnvironment));
        Assert.Equal(ShaderBytecodeFormat.MetalLib, ShaderBytecodeFormats.GetExpected(GraphicsBackend.Metal));
    }

    [Fact]
    public void CollectsDeclaredRuntimeVariantsWithoutSceneUsage()
    {
        const string skinnedInstancingVariant =
            "GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;HAS_UV2;SKINNED;";
        var pass = new ShaderPass(
            "StandardShadow",
            null,
            null,
            new RasterizerState(),
            "",
            "",
            "",
            hlslVertexSource: "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
            hlslFragmentSource: "float4 main() : SV_Target { return 1; }",
            modernSourceKind: ShaderModernSourceKind.Slang);
        var shader = new Shader(
            "Default/Standard",
            [],
            [pass],
            [skinnedInstancingVariant],
            ["GPU_INSTANCING", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS", "HAS_UV2", "SKINNED"]);

        VulkanShaderVariantCollection collection = MetalShaderVariantCollector.Collect(
        [
            new VulkanShaderVariantRoot(Guid.NewGuid(), shader, []),
        ]);

        Assert.Contains(collection.Descriptors, descriptor =>
            descriptor.PassName == "StandardShadow" &&
            descriptor.CanonicalKeywordSet == skinnedInstancingVariant);
    }
}
