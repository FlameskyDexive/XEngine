// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.Editor.Build.Android;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidShaderVariantTraceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineAndroidShaderTrace",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApprovedTraceIsStableSortedAndReported()
    {
        string tracePath = WriteTrace(
            "Assets/ShaderTraces/approved.jsonl",
            TraceLine("ZShader", "PassZ", "FEATURE_Z;", found: true),
            TraceLine("AShader", "PassB", "FEATURE_B;", found: false),
            TraceLine("AShader", "PassA", string.Empty, found: true));

        AndroidShaderVariantTraceCollection trace = AndroidShaderVariantTrace.ReadApproved(
            _root,
            Path.GetRelativePath(_root, tracePath));

        Assert.Equal(3, trace.Entries.Count);
        Assert.Equal("AShader", trace.Entries[0].ShaderName);
        Assert.Equal("PassA", trace.Entries[0].PassName);
        Assert.Equal("PassB", trace.Entries[1].PassName);
        Assert.False(trace.Entries[1].Found);
        Assert.Equal("ZShader", trace.Entries[2].ShaderName);

        string reportPath = Path.Combine(_root, "Reports", "approved-runtime-trace.json");
        AndroidShaderVariantTrace.WriteReport(reportPath, trace);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3, report.RootElement.GetProperty("entryCount").GetInt32());
        JsonElement entries = report.RootElement.GetProperty("entries");
        Assert.Equal("AShader", entries[0].GetProperty("shaderName").GetString());
        Assert.Equal("PassA", entries[0].GetProperty("passName").GetString());
    }

    [Fact]
    public void ApprovedTraceRejectsMalformedDuplicateAndNonVulkanRecords()
    {
        string malformed = WriteTrace("Assets/ShaderTraces/malformed.jsonl", "not-json");
        InvalidDataException malformedError = Assert.Throws<InvalidDataException>(() =>
            AndroidShaderVariantTrace.ReadApproved(_root, Path.GetRelativePath(_root, malformed)));
        Assert.Contains("malformed", malformedError.Message, StringComparison.OrdinalIgnoreCase);

        string duplicate = WriteTrace(
            "Assets/ShaderTraces/duplicate.jsonl",
            TraceLine("TraceShader", "Forward", "FEATURE_A;", found: true),
            TraceLine("TraceShader", "Forward", "FEATURE_A;", found: false));
        InvalidDataException duplicateError = Assert.Throws<InvalidDataException>(() =>
            AndroidShaderVariantTrace.ReadApproved(_root, Path.GetRelativePath(_root, duplicate)));
        Assert.Contains("duplicate", duplicateError.Message, StringComparison.OrdinalIgnoreCase);

        string wrongBackend = WriteTrace(
            "Assets/ShaderTraces/wrong-backend.jsonl",
            TraceLine("TraceShader", "Forward", string.Empty, found: true, backend: "Direct3D12"));
        InvalidDataException backendError = Assert.Throws<InvalidDataException>(() =>
            AndroidShaderVariantTrace.ReadApproved(_root, Path.GetRelativePath(_root, wrongBackend)));
        Assert.Contains("Vulkan", backendError.Message, StringComparison.Ordinal);

        string malformedKeywords = WriteTrace(
            "Assets/ShaderTraces/malformed-keywords.jsonl",
            TraceLine("TraceShader", "Forward", "FEATURE_A;;", found: true));
        InvalidDataException keywordError = Assert.Throws<InvalidDataException>(() =>
            AndroidShaderVariantTrace.ReadApproved(_root, Path.GetRelativePath(_root, malformedKeywords)));
        Assert.Contains("invalid keyword", keywordError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingArtifactTraceAddsOnlyItsRecordedPassVariant()
    {
        var forward = new ShaderPass(
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
        var shadow = new ShaderPass(
            "Shadow",
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
            "TraceShader",
            [],
            [forward, shadow],
            ["FEATURE_A"],
            ["FEATURE_A"]);

        AndroidShaderVariantTraceCollection trace = new(
        [
            new AndroidShaderVariantTraceUsage(
                "TraceShader",
                "Forward",
                GraphicsBackend.Vulkan,
                "FEATURE_A;",
                Found: false,
                Source: "Assets/ShaderTraces/missing.jsonl:1"),
        ]);
        IReadOnlyList<AndroidShaderVariantRoot> roots =
        [
            new AndroidShaderVariantRoot(
                Guid.Parse("6e7eaa42-4e76-4a5d-a0c6-e0362a758f7a"),
                shader,
                []),
        ];
        roots = AndroidShaderVariantCollector.AddApprovedTraceUsages(roots, trace);
        var collection = AndroidShaderVariantCollector.Collect(roots);

        Assert.Equal(3, collection.Descriptors.Count);
        Assert.Contains(collection.Descriptors, descriptor =>
            descriptor.PassName == "Forward" && descriptor.CanonicalKeywordSet == "FEATURE_A;");
        Assert.DoesNotContain(collection.Descriptors, descriptor =>
            descriptor.PassName == "Shadow" && descriptor.CanonicalKeywordSet == "FEATURE_A;");
    }

    [Fact]
    public void ApprovedTraceUsesBuiltInArtifactIdentityInsteadOfDisplayName()
    {
        Shader blit = Shader.LoadDefault(DefaultShader.Blit);
        Shader tonemapper = Shader.LoadDefault(DefaultShader.Tonemapper);
        Shader skybox = Shader.LoadDefault(DefaultShader.GradientSkybox);

        // BuiltInAssets names objects after the enum value for editor presentation, while
        // runtime artifact lookup continues to use the authored shader name.
        Assert.Equal("Blit", blit.Name);
        Assert.Equal("Default/Blit", blit.ArtifactName);

        IReadOnlyList<AndroidShaderVariantRoot> roots =
        [
            new AndroidShaderVariantRoot(BuiltInAssets.GuidFor(DefaultShader.Blit), blit, []),
            new AndroidShaderVariantRoot(BuiltInAssets.GuidFor(DefaultShader.Tonemapper), tonemapper, []),
            new AndroidShaderVariantRoot(BuiltInAssets.GuidFor(DefaultShader.GradientSkybox), skybox, []),
        ];
        AndroidShaderVariantTraceCollection trace = new(
        [
            new AndroidShaderVariantTraceUsage("Default/Blit", "Blit", GraphicsBackend.Vulkan, string.Empty, true, "trace:1"),
            new AndroidShaderVariantTraceUsage("Default/Tonemapper", "Tonemapper", GraphicsBackend.Vulkan, "TONEMAP_AGX;", true, "trace:2"),
            new AndroidShaderVariantTraceUsage("Default/GradientSkybox", "GradientSkybox", GraphicsBackend.Vulkan, string.Empty, true, "trace:3"),
        ]);

        AndroidShaderVariantCollection collection = AndroidShaderVariantCollector.Collect(
            AndroidShaderVariantCollector.AddApprovedTraceUsages(roots, trace));

        Assert.Contains(collection.Descriptors, descriptor =>
            descriptor.ShaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.Blit) &&
            descriptor.PassName == "Blit");
        Assert.Contains(collection.Descriptors, descriptor =>
            descriptor.ShaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.Tonemapper) &&
            descriptor.PassName == "Tonemapper" &&
            descriptor.CanonicalKeywordSet == "TONEMAP_AGX;");
        Assert.Contains(collection.Descriptors, descriptor =>
            descriptor.ShaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.GradientSkybox) &&
            descriptor.PassName == "GradientSkybox");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string WriteTrace(string relativePath, params string[] lines)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static string TraceLine(
        string shaderName,
        string passName,
        string variantKey,
        bool found,
        string backend = "Vulkan") =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = AndroidShaderVariantTraceCollection.SchemaVersion,
            shaderName,
            passName,
            backend,
            variantKey,
            found,
        });
}
