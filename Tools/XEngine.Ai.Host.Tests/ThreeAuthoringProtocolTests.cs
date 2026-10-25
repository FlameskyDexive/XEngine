using System.Collections.Generic;
using System.Linq;

using XEngine.Ai.Protocol;

using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class ThreeAuthoringProtocolTests
{
    private static ThreeAuthoringRequest Request(
        string operation = "export",
        string sourcePath = "Assets/Three/scene.threefx",
        string? outputDirectory = "Assets/Generated")
        => new(
            ThreeAuthoringProtocol.Version,
            "request-1",
            operation,
            sourcePath,
            outputDirectory,
            null,
            0,
            1024,
            1024,
            ThreeAuthoringProtocol.DefaultTimeoutMs,
            "test export",
            new Dictionary<string, string>());

    [Fact]
    public void ValidExportRequest_HasNoDiagnostics()
    {
        Assert.Empty(ThreeAuthoringRequestValidator.Validate(Request()));
    }

    [Theory]
    [InlineData("C:/outside/scene.threefx")]
    [InlineData("../outside/scene.threefx")]
    [InlineData("Assets/../outside/scene.threefx")]
    public void RootedOrParentSourcePath_IsRejected(string sourcePath)
    {
        IReadOnlyList<ThreeAuthoringDiagnostic> diagnostics =
            ThreeAuthoringRequestValidator.Validate(Request(sourcePath: sourcePath));

        Assert.Contains(diagnostics, d => d.Code == "source_path" && d.Severity == "error");
    }

    [Fact]
    public void BakeRequiresOutputAndBoundsDimensions()
    {
        ThreeAuthoringRequest request = Request("bake", outputDirectory: null) with { Width = 0, Height = 5000 };

        IReadOnlyList<ThreeAuthoringDiagnostic> diagnostics = ThreeAuthoringRequestValidator.Validate(request);

        Assert.Contains(diagnostics, d => d.Code == "output_directory");
        Assert.Contains(diagnostics, d => d.Code == "width");
        Assert.Contains(diagnostics, d => d.Code == "height");
    }

    [Fact]
    public void UnsupportedProtocolAndOperation_AreReportedTogether()
    {
        ThreeAuthoringRequest request = Request("render") with { ProtocolVersion = "three-authoring/0" };

        IReadOnlyList<ThreeAuthoringDiagnostic> diagnostics = ThreeAuthoringRequestValidator.Validate(request);

        Assert.Contains(diagnostics, d => d.Code == "protocol_version");
        Assert.Contains(diagnostics, d => d.Code == "operation");
    }

    [Fact]
    public void JsonRoundTrip_PreservesManifestStableIds()
    {
        ThreeExportManifest manifest = new(
            ThreeAuthoringProtocol.SchemaVersion,
            ThreeAuthoringProtocol.Version,
            "r180",
            "xengine-three-exporter/1",
            "host/1",
            new string('a', 64),
            new[] { new ThreeArtifactEntry("root/body", "model.glb", "model", "native", new string('b', 64), Array.Empty<string>()) },
            Array.Empty<ThreeAuthoringDiagnostic>());

        string json = System.Text.Json.JsonSerializer.Serialize(manifest, ThreeAuthoringProtocol.JsonOptions);
        ThreeExportManifest roundTrip = System.Text.Json.JsonSerializer.Deserialize<ThreeExportManifest>(json, ThreeAuthoringProtocol.JsonOptions)!;

        Assert.Equal("root/body", Assert.Single(roundTrip.Artifacts).StableId);
        Assert.Equal("model.glb", Assert.Single(roundTrip.Artifacts).RelativePath);
    }
}
