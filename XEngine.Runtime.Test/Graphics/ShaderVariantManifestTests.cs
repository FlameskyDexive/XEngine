// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using XEngine.Echo;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ShaderVariantManifestTests
{
    [Fact]
    public void ParserCanonicalizesDeclaredVariantsAndMaterialKeys()
    {
        Assert.True(ShaderParser.ParseShader("variants.shader", ManifestShader, out Shader? shader));
        Assert.NotNull(shader);
        Assert.Equal(
            ["FEATURE_A;FEATURE_B;", "FEATURE_C;"],
            shader!.OfflineVariantKeys);

        using var material = new Material(shader);
        material.SetKeyword("IGNORED", true);
        material.SetKeyword("FEATURE_B", true);
        material.SetKeyword("FEATURE_A", true);
        Assert.Equal("FEATURE_A;FEATURE_B;", material.KeywordKey);

        ShaderPass pass = shader.GetPass(0);
        Assert.Equal(
            "FEATURE_A;FEATURE_B;",
            ShaderPass.BuildKeywordKey(material._localKeywords, new HashSet<string>
            {
                "FEATURE_A",
                "FEATURE_B",
                "FEATURE_C",
            }));

        ShaderCompileRequest request = pass.CreateHlslCompileRequest(
            GraphicsBackend.Vulkan,
            material._localKeywords,
            material.KeywordKey);
        Assert.NotNull(request.Keywords);
        Assert.Equal(2, request.Keywords.Count);
        Assert.True(request.Keywords["FEATURE_A"]);
        Assert.True(request.Keywords["FEATURE_B"]);
        Assert.DoesNotContain("IGNORED", request.Keywords.Keys);
    }

    [Fact]
    public void ParserRejectsDuplicateAndInvalidVariantDeclarations()
    {
        Assert.False(ShaderParser.ParseShader(
            "duplicate.shader",
            ManifestShader.Replace(
                "Variant(\"FEATURE_C\")",
                "Variant(\"FEATURE_B\", \"FEATURE_A\")",
                StringComparison.Ordinal),
            out _));
        Assert.False(ShaderParser.ParseShader(
            "invalid.shader",
            ManifestShader.Replace("FEATURE_C", "feature-c", StringComparison.Ordinal),
            out _));
    }

    [Fact]
    public void ShaderWithoutManifestKeepsAllEnabledKeywords()
    {
        var pass = new ShaderPass(
            "Main",
            null,
            null,
            new RasterizerState(),
            "",
            "",
            "",
            hlslVertexSource: "float4 main() : SV_Position { return 0; }",
            hlslFragmentSource: "float4 main() : SV_Target { return 1; }");
        using var shader = new Shader("Tests/Legacy", [], [pass]);
        using var material = new Material(shader);
        material.SetKeyword("Z_LAST", true);
        material.SetKeyword("A_FIRST", true);

        Assert.False(shader.HasOfflineVariantManifest);
        Assert.Equal("A_FIRST;Z_LAST;", material.KeywordKey);
    }

    [Fact]
    public void EmptyManifestDeclaresDefaultOnlyAndFiltersAllKeywords()
    {
        Assert.True(ShaderParser.ParseShader(
            "default-only.shader",
            DefaultOnlyShader,
            out Shader? shader));
        Assert.NotNull(shader);
        Assert.True(shader!.HasOfflineVariantManifest);
        Assert.Empty(shader.OfflineVariantKeys);

        using var material = new Material(shader);
        material.SetKeyword("HAS_UV", true);
        material.SetKeyword("GPU_INSTANCING", true);
        Assert.Equal(string.Empty, material.KeywordKey);

        ShaderCompileRequest request = shader.GetPass(0).CreateHlslCompileRequest(
            GraphicsBackend.Vulkan,
            material._localKeywords,
            material.KeywordKey);
        Assert.Null(request.Keywords);

        using Shader restored = Assert.IsType<Shader>(
            Serializer.Deserialize<Shader>(Serializer.Serialize(shader)));
        Assert.True(restored.HasOfflineVariantManifest);
        using var restoredMaterial = new Material(restored);
        restoredMaterial.SetKeyword("HAS_UV", true);
        Assert.Equal(string.Empty, restoredMaterial.KeywordKey);
    }

    [Fact]
    public void SerializedShaderRestoresManifestAndArtifactIdentity()
    {
        Assert.True(ShaderParser.ParseShader("variants.shader", ManifestShader, out Shader? shader));
        Assert.NotNull(shader);
        shader!.Name = "DisplayName";

        using Shader restored = Assert.IsType<Shader>(
            Serializer.Deserialize<Shader>(Serializer.Serialize(shader)));
        using var material = new Material(restored);
        material.SetKeyword("IGNORED", true);
        material.SetKeyword("FEATURE_C", true);

        Assert.Equal("FEATURE_C;", material.KeywordKey);
        Assert.Equal("Tests/Variants", restored.GetPass(0).ShaderName);
        Assert.Equal(shader.OfflineVariantKeys, restored.OfflineVariantKeys);
    }

    [Fact]
    public void BuiltInShadersDeclareReviewedFiniteVariantSets()
    {
        // Reviewed snapshot of every builtin shader's offline Variants block (canonicalized
        // keyword keys, alphabetical). Update alongside any Variants{} edit — blendshape N3:
        // Standard gained 10 BLENDSHAPES combos (skinned characters + morph-only; deliberately
        // no GPU_INSTANCING×BLENDSHAPES — the instanced batch rejects morph meshes), and
        // StandardTransparent gained 2.
        var expected = new Dictionary<DefaultShader, string[]>
        {
            [DefaultShader.Standard] =
            [
                "BLENDSHAPES;",
                "BLENDSHAPES;DIRECTIONAL_ONLY;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;HAS_UV2;SKINNED;",
                "BLENDSHAPES;DIRECTIONAL_ONLY;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;SKINNED;",
                "BLENDSHAPES;DIRECTIONAL_ONLY;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_UV2;SKINNED;",
                "BLENDSHAPES;DIRECTIONAL_ONLY;HAS_BONEINDICES;HAS_BONEWEIGHTS;SKINNED;",
                "BLENDSHAPES;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;HAS_UV2;SKINNED;",
                "BLENDSHAPES;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;SKINNED;",
                "BLENDSHAPES;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_UV2;SKINNED;",
                "BLENDSHAPES;HAS_BONEINDICES;HAS_BONEWEIGHTS;SKINNED;",
                "BLENDSHAPES;HAS_TANGENTS;",
                "DIRECTIONAL_ONLY;",
                "DIRECTIONAL_ONLY;GPU_INSTANCING;",
                "DIRECTIONAL_ONLY;GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;HAS_UV2;SKINNED;",
                "DIRECTIONAL_ONLY;GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;SKINNED;",
                "DIRECTIONAL_ONLY;GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_UV2;SKINNED;",
                "DIRECTIONAL_ONLY;GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;SKINNED;",
                "DIRECTIONAL_ONLY;GPU_INSTANCING;HAS_TANGENTS;",
                "DIRECTIONAL_ONLY;GPU_INSTANCING;HAS_TANGENTS;HAS_UV2;",
                "DIRECTIONAL_ONLY;GPU_INSTANCING;HAS_UV2;",
                "DIRECTIONAL_ONLY;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;HAS_UV2;SKINNED;",
                "DIRECTIONAL_ONLY;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;SKINNED;",
                "DIRECTIONAL_ONLY;HAS_TANGENTS;",
                "DIRECTIONAL_ONLY;HAS_TANGENTS;HAS_UV2;",
                "GPU_INSTANCING;",
                "GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;HAS_UV2;SKINNED;",
                "GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;SKINNED;",
                "GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_UV2;SKINNED;",
                "GPU_INSTANCING;HAS_BONEINDICES;HAS_BONEWEIGHTS;SKINNED;",
                "GPU_INSTANCING;HAS_TANGENTS;",
                "GPU_INSTANCING;HAS_TANGENTS;HAS_UV2;",
                "HAS_BONEINDICES;",
                "HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;HAS_UV2;SKINNED;",
                "HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_TANGENTS;SKINNED;",
                "HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_UV2;SKINNED;",
                "HAS_BONEINDICES;HAS_BONEWEIGHTS;SKINNED;",
                "HAS_BONEWEIGHTS;",
                "HAS_TANGENTS;",
                "HAS_TANGENTS;HAS_UV2;",
                "HAS_UV2;",
                "SKINNED;",
            ],
            [DefaultShader.StandardTransparent] =
            [
                "BLENDSHAPES;",
                "BLENDSHAPES;HAS_TANGENTS;",
                "HAS_TANGENTS;",
            ],
            [DefaultShader.StandardAnisotropic] = ["HAS_TANGENTS;"],
            [DefaultShader.Particle] =
            [
                "GPU_INSTANCING;",
                "PARTICLE_MESH;",
            ],
            [DefaultShader.Tonemapper] =
            [
                "TONEMAP_ACES;",
                "TONEMAP_ACES_SIMPLE;",
                "TONEMAP_AGX;",
                "TONEMAP_MELON;",
                "TONEMAP_NONE;",
                "TONEMAP_REINHARD_LUMA;",
                "TONEMAP_REINHARD_SIMPLE;",
                "TONEMAP_REINHARD_WHITE;",
                "TONEMAP_ROMBINDAHOUSE;",
                "TONEMAP_UNCHARTED2;",
            ],
            [DefaultShader.BokehDoF] = ["AUTOFOCUS;"],
        };

        int artifactCount = 0;
        foreach (DefaultShader defaultShader in Enum.GetValues<DefaultShader>())
        {
            using Shader shader = Shader.ParseDefault(defaultShader);
            string[] expectedKeys = expected.GetValueOrDefault(defaultShader) ?? [];
            Assert.Equal(expectedKeys, shader.OfflineVariantKeys);
            Assert.True(shader.HasOfflineVariantManifest);
            int hlslPassCount = 0;
            foreach (ShaderPass pass in shader.Passes)
            {
                if (pass.HasHlsl)
                    hlslPassCount++;
            }
            artifactCount += hlslPassCount * (expectedKeys.Length + 1) * 2;
        }

        Assert.Equal(608, artifactCount);
    }

    private const string ManifestShader = """
        Shader "Tests/Variants"

        Variants
        {
            Variant("FEATURE_B", "FEATURE_A")
            Variant("FEATURE_C")
        }

        Pass "Main"
        {
            HLSLPROGRAM
                Vertex
                {
                    float4 main() : SV_Position { return 0; }
                }
                Fragment
                {
                    float4 main() : SV_Target { return 1; }
                }
            ENDHLSL
        }
        """;

    private const string DefaultOnlyShader = """
        Shader "Tests/DefaultOnly"

        Variants
        {
        }

        Pass "Main"
        {
            HLSLPROGRAM
                Vertex
                {
                    float4 main() : SV_Position { return 0; }
                }
                Fragment
                {
                    float4 main() : SV_Target { return 1; }
                }
            ENDHLSL
        }
        """;
}
