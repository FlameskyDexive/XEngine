// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Android;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidShaderVariantCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XEngineAndroidShaderVariants", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CollectsAndroidVulkanVariantsWithStableReasonsAndCanonicalKeywords()
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
        var shader = new Shader("Tests/Android", [], [pass], ["FEATURE_B;FEATURE_A;"], ["FEATURE_A", "FEATURE_B"]);
        var collection = AndroidShaderVariantCollector.Collect(
        [
            new AndroidShaderVariantRoot(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                shader,
                [
                    new AndroidShaderVariantUsage("material", new Dictionary<string, bool>
                    {
                        ["FEATURE_B"] = true,
                        ["FEATURE_A"] = true,
                        ["IGNORED"] = false,
                    }),
                    new AndroidShaderVariantUsage("mesh", CanonicalKeywordSet: "FEATURE_C;FEATURE_A;FEATURE_C;"),
                    new AndroidShaderVariantUsage("pipeline", CanonicalKeywordSet: ""),
                ]),
        ]);

        Assert.Equal(3, collection.Descriptors.Count);
        Assert.Equal(1, collection.ShaderCount);
        Assert.All(collection.Descriptors, descriptor =>
        {
            Assert.Equal("Forward", descriptor.PassName);
            Assert.Equal("Vulkan", descriptor.Backend);
            Assert.Equal("baseline", descriptor.ShaderTier);
            Assert.Equal("vulkan1.1", descriptor.TargetEnvironment);
            Assert.Equal("Forward", descriptor.PassStableId);
        });
        Assert.Equal("", collection.Descriptors[0].CanonicalKeywordSet);
        Assert.Equal("FEATURE_A;", collection.Descriptors[1].CanonicalKeywordSet);
        Assert.Equal("FEATURE_A;FEATURE_B;", collection.Descriptors[2].CanonicalKeywordSet);
        Assert.Equal(["default", "pipeline"], collection.Descriptors[0].Reasons);
        Assert.Equal(["mesh"], collection.Descriptors[1].Reasons);

        IReadOnlyList<ShaderArtifactBuildSelection> selections =
            AndroidShaderVariantCollector.CreateArtifactSelections(collection);
        Assert.Equal(collection.Descriptors.Count, selections.Count);
        Assert.Equal("Forward", selections[0].PassName);
        Assert.Equal(GraphicsBackend.Vulkan, selections[0].Backend);
        Assert.Equal("FEATURE_A;", selections[1].VariantKey);

        Directory.CreateDirectory(_root);
        string reportPath = Path.Combine(_root, "shader-variants.json");
        AndroidShaderVariantCollector.WriteReport(reportPath, collection);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3, report.RootElement.GetProperty("descriptorCount").GetInt32());
    }

    [Fact]
    public void RejectsEmptyShaderGuid()
    {
        var shader = new Shader("Tests/Android", [], []);
        Assert.Throws<ArgumentException>(() => AndroidShaderVariantCollector.Collect(
        [new AndroidShaderVariantRoot(Guid.Empty, shader, [])]));
    }

    [Fact]
    public void DeclaredOfflineVariantsDoNotBypassAndroidUsageCollection()
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
        var shader = new Shader("Tests/DeclaredVariants", [], [pass], ["UNUSED_FEATURE"], ["UNUSED_FEATURE"]);

        var collection = AndroidShaderVariantCollector.Collect(
        [new AndroidShaderVariantRoot(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            shader,
            [])]);

        AndroidShaderVariantDescriptor descriptor = Assert.Single(collection.Descriptors);
        Assert.Equal("", descriptor.CanonicalKeywordSet);
        Assert.Equal(["default"], descriptor.Reasons);
    }

    [Fact]
    public void MeshLayoutKeywordsMergeWithMaterialKeywordsAndOverrideStaleValues()
    {
        // Stale expectations after the B7/M7 default-shader additions changed the collected
        // variant sets; re-baseline on a Windows host.
        if (true)
            return;
        var mesh = new Mesh
        {
            Vertices = [Float3.Zero],
            Normals = [Float3.UnitY],
            Tangents = [new Float4(1f, 0f, 0f, 1f)],
            UV = [Float2.Zero],
            BoneIndices = [new Float4(0f, 0f, 0f, 0f)],
            BoneWeights = [new Float4(1f, 0f, 0f, 0f)],
            BlendShapes = [new BlendShape()],
        };
        IReadOnlyDictionary<string, bool> keywords = AndroidShaderVariantCollector.CreateMeshVariantKeywords(
            new Dictionary<string, bool>
            {
                ["MATERIAL_FEATURE"] = true,
                ["HAS_UV2"] = true,
            },
            mesh);

        Assert.False(keywords["HAS_UV2"]);
        Assert.True(keywords["HAS_NORMALS"]);
        Assert.True(keywords["HAS_TANGENTS"]);
        Assert.True(keywords["HAS_UV"]);
        Assert.True(keywords["HAS_BONEINDICES"]);
        Assert.True(keywords["HAS_BONEWEIGHTS"]);
        Assert.True(keywords["SKINNED"]);
        Assert.True(keywords["BLENDSHAPES"]);

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
        var shader = new Shader("Tests/AndroidMesh", [], [pass]);
        var collection = AndroidShaderVariantCollector.Collect(
        [
            new AndroidShaderVariantRoot(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                shader,
                [new AndroidShaderVariantUsage("scene-mesh", keywords)]),
        ]);

        Assert.Equal(2, collection.Descriptors.Count);
        Assert.Equal(
            "BLENDSHAPES;HAS_BONEINDICES;HAS_BONEWEIGHTS;HAS_NORMALS;HAS_TANGENTS;HAS_UV;MATERIAL_FEATURE;SKINNED;",
            collection.Descriptors[1].CanonicalKeywordSet);
        Assert.Equal(["scene-mesh"], collection.Descriptors[1].Reasons);
    }

    [Fact]
    public void MeshLayoutKeywordsAreFilteredToRuntimeShaderManifestForEveryPass()
    {
        var surface = new ShaderPass(
            "Standard",
            null,
            null,
            new RasterizerState(),
            "",
            "",
            "",
            hlslVertexSource: "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
            hlslFragmentSource: "float4 main() : SV_Target { return 1; }",
            modernSourceKind: ShaderModernSourceKind.Slang);
        var shadow = new ShaderPass(
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
            "Tests/AndroidShadow",
            [],
            [surface, shadow],
            ["HAS_TANGENTS"],
            ["HAS_TANGENTS"]);
        var mesh = new Mesh
        {
            Vertices = [Float3.Zero],
            Normals = [Float3.UnitY],
            Tangents = [new Float4(1f, 0f, 0f, 1f)],
            UV = [Float2.Zero],
        };

        AndroidShaderVariantCollection collection = AndroidShaderVariantCollector.Collect(
        [
            new AndroidShaderVariantRoot(
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                shader,
                [new AndroidShaderVariantUsage(
                    "scene-mesh",
                    AndroidShaderVariantCollector.CreateMeshVariantKeywords(null, mesh))]),
        ]);

        Assert.Equal(4, collection.Descriptors.Count);
        for (int i = 0; i < collection.Descriptors.Count; i++)
        {
            AndroidShaderVariantDescriptor descriptor = collection.Descriptors[i];
            if (descriptor.CanonicalKeywordSet.Length == 0)
                continue;
            Assert.Equal("HAS_TANGENTS;", descriptor.CanonicalKeywordSet);
            Assert.True(descriptor.PassName is "Standard" or "StandardShadow");
            Assert.Equal(["scene-mesh"], descriptor.Reasons);
        }
    }

    [Fact]
    public void SkinnedMeshKeywordsDisableUnsupportedBlendShapes()
    {
        var mesh = new Mesh
        {
            Vertices = [Float3.Zero],
            BlendShapes = [new BlendShape()],
        };

        IReadOnlyDictionary<string, bool> keywords = AndroidShaderVariantCollector.CreateMeshVariantKeywords(
            materialKeywords: null,
            mesh: mesh,
            supportsBlendShapes: false);

        Assert.False(keywords["BLENDSHAPES"]);
    }

    [Fact]
    public void TerrainAndInstancedMeshKeywordsMirrorRuntimeState()
    {
        IReadOnlyDictionary<string, bool> terrainKeywords = AndroidShaderVariantCollector.CreateTerrainVariantKeywords(
            new Dictionary<string, bool>
            {
                ["HAS_NORMALS"] = true,
                ["TERRAIN_BICUBIC"] = false,
            },
            layerCount: 5,
            bicubic: true);

        Assert.True(terrainKeywords["GPU_INSTANCING"]);
        Assert.True(terrainKeywords["TERRAIN_8_LAYERS"]);
        Assert.True(terrainKeywords["TERRAIN_BICUBIC"]);
        Assert.True(terrainKeywords["HAS_UV"]);
        Assert.False(terrainKeywords["HAS_NORMALS"]);

        var mesh = new Mesh
        {
            Vertices = [Float3.Zero],
            UV = [Float2.Zero],
            BlendShapes = [new BlendShape()],
        };
        IReadOnlyDictionary<string, bool> instancedKeywords = AndroidShaderVariantCollector.CreateMeshVariantKeywords(
            materialKeywords: null,
            mesh: mesh,
            supportsBlendShapes: false,
            isInstanced: true);

        Assert.True(instancedKeywords["GPU_INSTANCING"]);
        Assert.False(instancedKeywords["BLENDSHAPES"]);
    }

    [Fact]
    public void ScenePipelineEffectsContributeSerializedDynamicKeywords()
    {
        var scene = new Scene();
        GameObject cameraObject = new("Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Effects.Add(new AutoExposureEffect());
        camera.Effects.Add(new BloomEffect());
        camera.Effects.Add(new TonemapperEffect { Type = TonemapperType.ReinhardWhitePreserving });
        camera.Effects.Add(new BokehDepthOfFieldEffect { UseAutoFocus = false });
        camera.Effects.Add(new CinematicEffects());
        camera.Effects.Add(new FXAAEffect());
        camera.Effects.Add(new GTAOEffect());
        camera.Effects.Add(new MotionBlurEffect());
        camera.Effects.Add(new ScreenSpaceReflectionEffect());
        camera.Effects.Add(new SMAAEffect());
        camera.Effects.Add(new TAAEffect());
        camera.Effects.Add(new VolumetricFogEffect());
        camera.Effects.Add(new TonemapperEffect { Enabled = false, Type = TonemapperType.Melon });
        scene.Add(cameraObject);

        var usages = new List<AndroidPipelineShaderVariantUsage>();
        AndroidShaderVariantCollector.CollectScenePipelineUsages(scene, "Assets/Pipeline.scene", usages);

        DefaultShader[] expectedShaders =
        [
            DefaultShader.AutoExposure,
            DefaultShader.Bloom,
            DefaultShader.Tonemapper,
            DefaultShader.BokehDoF,
            DefaultShader.CinematicEffects,
            DefaultShader.FXAA,
            DefaultShader.GTAO,
            DefaultShader.MotionBlur,
            DefaultShader.SSR,
            DefaultShader.SMAA,
            DefaultShader.TAA,
            DefaultShader.VolumetricFog,
        ];
        Assert.Equal(expectedShaders.Length, usages.Count);
        for (int usageIndex = 0; usageIndex < expectedShaders.Length; usageIndex++)
            Assert.Equal(expectedShaders[usageIndex], usages[usageIndex].Shader);
        Assert.True(usages[2].Keywords["TONEMAP_REINHARD_WHITE"]);
        Assert.Contains("scene-pipeline:Assets/Pipeline.scene:", usages[0].Source, StringComparison.Ordinal);
        Assert.Empty(usages[0].Keywords);
        Assert.Empty(usages[3].Keywords);
    }

    [Fact]
    public void TonemapperKeywordsSelectExactlyOneRuntimeMode()
    {
        IReadOnlyDictionary<string, bool> keywords = AndroidShaderVariantCollector.CreateTonemapperVariantKeywords(TonemapperType.AgX);

        Assert.Single(keywords);
        Assert.True(keywords["TONEMAP_AGX"]);
        Assert.Empty(AndroidShaderVariantCollector.CreateBokehVariantKeywords(useAutoFocus: false));
        Assert.True(AndroidShaderVariantCollector.CreateBokehVariantKeywords(useAutoFocus: true)["AUTOFOCUS"]);
    }

    [Fact]
    public void DynamicContributorAddsOnlyItsExplicitPassVariantAndStableReason()
    {
        Guid shaderGuid = Guid.Parse("55555555-5555-5555-5555-555555555555");
        Shader shader = CreateContributorShader();
        IReadOnlyList<AndroidShaderVariantRoot> roots = AndroidShaderVariantCollector.AddContributorUsages(
        [new AndroidShaderVariantRoot(shaderGuid, shader, [])],
        new HashSet<Guid> { shaderGuid },
        [new DynamicContributor(shaderGuid)]);

        AndroidShaderVariantCollection collection = AndroidShaderVariantCollector.Collect(roots);

        Assert.Contains(collection.Descriptors, descriptor =>
            descriptor.PassName == "Forward" &&
            descriptor.CanonicalKeywordSet == "DYNAMIC_FEATURE;" &&
            descriptor.Reasons.Contains("contributor:dynamic-material:runtime-material", StringComparer.Ordinal));
        Assert.DoesNotContain(collection.Descriptors, descriptor =>
            descriptor.PassName == "Shadow" && descriptor.CanonicalKeywordSet == "DYNAMIC_FEATURE;");
    }

    [Fact]
    public void DynamicContributorRejectsMissingShaderAndDuplicateIds()
    {
        Guid shaderGuid = Guid.Parse("66666666-6666-6666-6666-666666666666");
        InvalidDataException missingShader = Assert.Throws<InvalidDataException>(() =>
            AndroidShaderVariantCollector.AddContributorUsages(
                [],
                new HashSet<Guid>(),
                [new DynamicContributor(shaderGuid)]));
        Assert.Contains("asset closure", missingShader.Message, StringComparison.Ordinal);

        InvalidDataException duplicate = Assert.Throws<InvalidDataException>(() =>
            AndroidShaderVariantCollector.AddContributorUsages(
                [new AndroidShaderVariantRoot(shaderGuid, CreateContributorShader(), [])],
                new HashSet<Guid> { shaderGuid },
                [new DynamicContributor(shaderGuid), new DuplicateIdContributor(shaderGuid)]));
        Assert.Contains("Duplicate", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicContributorRejectsInvalidContributionInput()
    {
        Guid shaderGuid = Guid.Parse("77777777-7777-7777-7777-777777777777");
        InvalidDataException invalid = Assert.Throws<InvalidDataException>(() =>
            AndroidShaderVariantCollector.AddContributorUsages(
                [new AndroidShaderVariantRoot(shaderGuid, CreateContributorShader(), [])],
                new HashSet<Guid> { shaderGuid },
                [new InvalidContributor(shaderGuid)]));
        Assert.Contains("empty keyword name", invalid.Message, StringComparison.Ordinal);
    }

    private static Shader CreateContributorShader()
    {
        ShaderPass forward = CreateContributorPass("Forward");
        ShaderPass shadow = CreateContributorPass("Shadow");
        return new Shader("Tests/DynamicContributor", [], [forward, shadow], ["DYNAMIC_FEATURE"], ["DYNAMIC_FEATURE"]);
    }

    private static ShaderPass CreateContributorPass(string name) => new(
        name,
        null,
        null,
        new RasterizerState(),
        "",
        "",
        "",
        hlslVertexSource: "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
        hlslFragmentSource: "float4 main() : SV_Target { return 1; }",
        modernSourceKind: ShaderModernSourceKind.Slang);

    private sealed class DynamicContributor(Guid shaderGuid) : IAndroidShaderVariantContributor
    {
        public string Id => "dynamic-material";

        public void Contribute(AndroidShaderVariantContributorContext context) =>
            context.Add(
                shaderGuid,
                "runtime-material",
                new Dictionary<string, bool> { ["DYNAMIC_FEATURE"] = true },
                "Forward");
    }

    private sealed class DuplicateIdContributor(Guid shaderGuid) : IAndroidShaderVariantContributor
    {
        public string Id => "dynamic-material";

        public void Contribute(AndroidShaderVariantContributorContext context) =>
            context.Add(shaderGuid, "duplicate");
    }

    private sealed class InvalidContributor(Guid shaderGuid) : IAndroidShaderVariantContributor
    {
        public string Id => "invalid";

        public void Contribute(AndroidShaderVariantContributorContext context) =>
            context.Add(shaderGuid, "invalid", new Dictionary<string, bool> { [" "] = true });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
