// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ P1-7 — Proves the P0-3 response-size truncation + HasMore signaling, and P0-6 material
/// inspect/patch round-trip. Uses <see cref="EchoFacade"/> directly (the offline core the MCP tools
/// delegate to) so the tests don't need a running engine.</summary>
public class ResponseLimitsAndMaterialTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public ResponseLimitsAndMaterialTests(TestSceneFixture fx) => _fx = fx;

    // ---- P0-3 response limits ----

    [Fact]
    public void ReadHierarchy_TruncatesToMaxObjectsAndSetsHasMore()
    {
        // Build a 25-root scene.
        string dir = Path.Combine(_fx.ProjectRoot, "Big");
        Directory.CreateDirectory(dir);
        string big = Path.Combine(dir, "Big.scene");
        File.WriteAllText(big, MakeScene(25));

        var full = SceneReader.ReadHierarchy(big, maxObjects: 500);
        Assert.Equal(25, full.TotalObjects);
        Assert.False(full.HasMore);

        var truncated = SceneReader.ReadHierarchy(big, maxObjects: 10);
        Assert.Equal(25, truncated.TotalObjects);      // true count still reported
        Assert.Equal(10, truncated.RootObjects.Count); // but only 10 returned
        Assert.True(truncated.HasMore);
    }

    [Fact]
    public void ReadHierarchy_OverHardLimit_ClampsTo2000()
    {
        string dir = Path.Combine(_fx.ProjectRoot, "Clamp");
        Directory.CreateDirectory(dir);
        string scene = Path.Combine(dir, "Clamp.scene");
        File.WriteAllText(scene, MakeScene(5));

        var result = SceneReader.ReadHierarchy(scene, maxObjects: 99999);
        Assert.Equal(ProtocolConstants.Limits.MaxObjectsPerHierarchy, result.MaxObjects);
        Assert.False(result.HasMore);  // 5 < 2000, no truncation
    }

    // ---- P2-1 Tag/Layer name resolution ----

    [Fact]
    public void ReadHierarchy_ResolvesTagAndLayerNamesFromProjectSettings()
    {
        // Write a Tags & Layers.yaml so the resolver has something to read (Player tag at index 2,
        // Water layer at index 3).
        string settingsDir = Path.Combine(_fx.ProjectRoot, "ProjectSettings");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, "Tags & Layers.yaml"),
            "Tags:\n  $id: 2\n  $values:\n    - \"Untagged\"\n    - \"Main Camera\"\n    - \"Player\"\n" +
            "Layers:\n  array:\n    - \"Default\"\n    - \"TransparentFX\"\n    - \"Ignore Raycast\"\n    - \"Water\"\n" +
            "    - \"\"\n    - \"\"\n    - \"\"\n    - \"\"\n  $id: 3\n$id: 1\n" +
            "$type: \"XEngine.Editor.Projects.Settings.TagsAndLayersSettings, XEngine.Editor\"");

        // Build a scene whose Player GO uses tag index 2 (Player) + layer index 3 (Water).
        string dir = Path.Combine(_fx.ProjectRoot, "TagLayer");
        Directory.CreateDirectory(dir);
        string scene = Path.Combine(dir, "T.scene");
        File.WriteAllText(scene, TagLayerScene());

        var resolver = TagLayerResolver.Load(_fx.ProjectRoot);
        var hierarchy = SceneReader.ReadHierarchy(scene, tagLayer: resolver);
        var player = Assert.Single(hierarchy.RootObjects);
        Assert.Equal("Player", player.Tag);          // index 2 -> "Player"
        Assert.Equal("Water", player.LayerName);     // index 3 -> "Water"
        Assert.Equal(2, player.TagIndex);             // raw index preserved
        Assert.Equal(3, player.LayerIndex);
    }

    [Fact]
    public void TagLayerResolver_MissingFile_FallsBackToDefaults()
    {
        // No ProjectSettings dir at all — must not throw, must return defaults.
        var resolver = TagLayerResolver.Load(_fx.ProjectRoot);
        Assert.Equal("Untagged", resolver.GetTag(0));
        Assert.Equal("Default", resolver.GetLayer(0));
        // Out of range is safe.
        Assert.Equal("Untagged", resolver.GetTag(999));
        Assert.Equal("Default", resolver.GetLayer(999));
    }

    /// <summary>A one-GO scene with TagIndex=2, LayerIndex=3 (for the Tag/Layer resolution test).</summary>
    private static string TagLayerScene() =>
        @"{""$id"":1,""$type"":""XEngine.Runtime.Resources.Scene, XEngine.Runtime"",""serializeObj"":{""$id"":2,""array"":[{""$id"":3,""Name"":""Player"",""Identifier"":""aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"",""TagIndex"":2,""LayerIndex"":3,""Transform"":{""$id"":4,""$type"":""XEngine.Vector.Transform, XEngine.Runtime"",""_localPosition"":{""X"":""0F"",""Y"":""0F"",""Z"":""0F""},""_localScale"":{""X"":""1F"",""Y"":""1F"",""Z"":""1F""},""_localRotation"":{""X"":""0F"",""Y"":""0F"",""Z"":""0F"",""W"":""1F""}},""Components"":[],""Children"":[]}]}}";

    // ---- P0-6 material inspect/patch ----

    [Fact]
    public async Task MaterialPatch_ColorChannel_AppliesAndInspectsBack()
    {
        string matDir = Path.Combine(_fx.ProjectRoot, "Mats");
        Directory.CreateDirectory(matDir);
        string mat = Path.Combine(matDir, "Test.mat");
        File.WriteAllText(mat, MinimalMaterial);

        string hashBefore = XEngine.Data.Provenance.ProvenancedDelta.ContentHash(
            XEngine.Echo.EchoObject.ReadFromString(MinimalMaterial));

        await _fx.SceneWriter.PatchMaterialAsync(mat, "Mats/Test.mat",
            new[] { new SceneFieldPatch("_properties/_colors/_MainColor/R", "0.5") },
            TestSceneFixture.Provenance("test.mat", "patch-color-1"), dryRun: false,
            expectedBeforeHash: hashBefore);

        var detail = SceneReader.ReadMaterial(mat, "Mats/Test.mat");
        var mainColor = Assert.Single(detail.Properties, p => p.Name == "_MainColor");
        Assert.Contains("R=0.5", mainColor.Value);
    }

    [Fact]
    public async Task MaterialPatch_FloatProperty_Applies()
    {
        string matDir = Path.Combine(_fx.ProjectRoot, "Mats2");
        Directory.CreateDirectory(matDir);
        string mat = Path.Combine(matDir, "F.mat");
        File.WriteAllText(mat, MinimalMaterial);

        await _fx.SceneWriter.PatchMaterialAsync(mat, "Mats2/F.mat",
            new[] { new SceneFieldPatch("_properties/_floats/_EmissionIntensity", "3.5") },
            TestSceneFixture.Provenance("test.mat", "patch-float-1"), dryRun: false);

        var detail = SceneReader.ReadMaterial(mat, "Mats2/F.mat");
        var em = Assert.Single(detail.Properties, p => p.Name == "_EmissionIntensity");
        Assert.Equal("3.5", em.Value);
    }

    // ---- P2-2 scene settings (fog/ambient/skybox) ----

    [Fact]
    public async Task SceneSettings_PatchFogDensity_AppliesAndQueriesBack()
    {
        string dir = Path.Combine(_fx.ProjectRoot, "Settings");
        Directory.CreateDirectory(dir);
        string scene = Path.Combine(dir, "Lit.scene");
        File.WriteAllText(scene, LitScene);

        var before = SceneReader.ReadSceneSettings(scene, "Settings/Lit.scene");
        string hashBefore = before.ContentHash;
        Assert.NotEmpty(before.Categories);

        await _fx.SceneWriter.PatchSceneSettingsAsync(scene, "Settings/Lit.scene",
            new[] { new SceneFieldPatch("Fog/Density", "0.05") },
            TestSceneFixture.Provenance("test.settings", "patch-fog-1"), dryRun: false,
            expectedBeforeHash: hashBefore);

        var after = SceneReader.ReadSceneSettings(scene, "Settings/Lit.scene");
        var density = Assert.Single(after.Categories["fog"], e => e.Path == "Fog/Density");
        Assert.Equal("0.05", density.Value);
    }

    [Fact]
    public async Task SceneSettings_StaleHash_IsRejected()
    {
        string dir = Path.Combine(_fx.ProjectRoot, "Settings2");
        Directory.CreateDirectory(dir);
        string scene = Path.Combine(dir, "Lit.scene");
        File.WriteAllText(scene, LitScene);

        // Make one valid change first so the original hash becomes stale.
        var first = await _fx.SceneWriter.PatchSceneSettingsAsync(scene, "Settings2/Lit.scene",
            new[] { new SceneFieldPatch("Fog/Start", "10") },
            TestSceneFixture.Provenance("test.settings", "first-1"), dryRun: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fx.SceneWriter.PatchSceneSettingsAsync(scene, "Settings2/Lit.scene",
                new[] { new SceneFieldPatch("Fog/End", "200") },
                TestSceneFixture.Provenance("test.settings", "stale-1"), dryRun: false,
                expectedBeforeHash: first.BeforeHash));
    }

    /// <summary>A scene with empty GameObject array + Fog/Ambient/Skybox root fields (P2-2 fixture).</summary>
    private const string LitScene = @"{
  ""$id"": 1,
  ""$type"": ""XEngine.Runtime.Resources.Scene, XEngine.Runtime"",
  ""serializeObj"": { ""$id"": 2, ""array"": [] },
  ""Fog"": { ""$id"": 3, ""Mode"": 0, ""Color"": { ""R"": ""0.5F"", ""G"": ""0.5F"", ""B"": ""0.5F"", ""A"": ""1F"" }, ""Start"": ""20F"", ""End"": ""100F"", ""Density"": ""0.01F"" },
  ""Ambient"": { ""$id"": 4, ""Mode"": 1, ""Strength"": ""0.55F"", ""Color"": { ""X"": ""0.43F"", ""Y"": ""0.55F"", ""Z"": ""0.65F"", ""W"": ""1F"" } },
  ""Skybox"": { ""$id"": 5, ""Mode"": 0, ""SolidColor"": { ""R"": ""0.2F"", ""G"": ""0.3F"", ""B"": ""0.5F"", ""A"": ""1F"" } }
}";

    /// <summary>A minimal .mat with one color and one float property (no textures), for patch tests.</summary>
    private const string MinimalMaterial = @"{
  ""$id"": 1,
  ""$type"": ""XEngine.Runtime.Resources.Material, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"",
  ""_shader"": { ""AssetID"": ""aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"" },
  ""_properties"": {
    ""$id"": 2,
    ""_colors"": { ""_MainColor"": { ""R"": 1F, ""G"": 1F, ""B"": 1F, ""A"": 1F } },
    ""_vectors2"": {},
    ""_vectors3"": {},
    ""_vectors4"": {},
    ""_floats"": { ""_EmissionIntensity"": 1F },
    ""_ints"": {},
    ""_matrices"": {},
    ""_matrixArr"": {},
    ""_textures"": {},
    ""_textures3D"": {},
    ""_buffers"": {},
    ""_bufferBindings"": {}
  },
  ""AssetPath"": """",
  ""AssetID"": ""00000000-0000-0000-0000-000000000000"",
  ""Name"": ""Test""
}";

    /// <summary>Generate a scene JSON string with N root GameObjects (for the truncation tests).</summary>
    private static string MakeScene(int n)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(@"{""$id"":1,""$type"":""XEngine.Runtime.Resources.Scene, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"",""serializeObj"":{""$id"":2,""array"":[");
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(',');
            string id = (0x10000000000000 + i).ToString("x");  // deterministic unique id per index
            sb.Append($@"{{""$id"":{10+i},""Name"":""Obj_{i}"",""AssetPath"":"""",""Identifier"":""00000000-0000-0000-0000-{id}"",""Static"":0B,""Enabled"":1B,""EnabledInHierarchy"":1B,""TagIndex"":0,""LayerIndex"":0,""HideFlags"":0,""Transform"":{{""$id"":{1000+i},""$type"":""XEngine.Vector.Transform, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"",""_localPosition"":{{""X"":""{i}F"",""Y"":""0F"",""Z"":""0F""}},""_localScale"":{{""X"":""1F"",""Y"":""1F"",""Z"":""1F""}},""_localRotation"":{{""X"":""0F"",""Y"":""0F"",""Z"":""0F"",""W"":""1F""}}}},""Components"":[],""Children"":[]}}");
        }
        sb.Append("]}}");
        return sb.ToString();
    }
}
