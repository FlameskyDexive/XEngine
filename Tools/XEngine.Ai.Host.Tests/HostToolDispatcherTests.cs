// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ Gen-P0.4 — Workflows chain tools through <see cref="HostToolDispatcher"/>; a tool missing from
/// the dispatch switch silently breaks the DAG. These tests pin the routes the generated-asset
/// pipeline depends on: asset_import (download → project), material_patch (bind generated textures)
/// and scene_component_patch (wire GUIDs onto a freshly added renderer).
/// </summary>
public class HostToolDispatcherTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public HostToolDispatcherTests(TestSceneFixture fx) => _fx = fx;

    private ServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddSingleton(new ProjectDescriptor("test-project", _fx.ProjectRoot, null, null, null, "fp"))
            .AddSingleton(_fx.Journal)
            .AddSingleton(_fx.SceneWriter)
            .AddSingleton(_fx.AssetWriter)
            .BuildServiceProvider();

    private static JsonElement Args(object anonymous) => JsonSerializer.SerializeToElement(anonymous);

    [Fact]
    public async Task Dispatches_AssetImport_CopiesFileAndMintsMeta()
    {
        using ServiceProvider services = BuildServices();
        var dispatcher = new HostToolDispatcher(services);

        string source = Path.Combine(_fx.ProjectRoot, "external-download.png");
        await File.WriteAllBytesAsync(source, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        object result = await dispatcher.InvokeAsync("asset_import", Args(new
        {
            sourcePath = source,
            destinationPath = "Assets/GenArt/tex.png",
            idempotencyKey = "wf-import-1",
            intent = "workflow import test",
        }), CancellationToken.None);

        var import = Assert.IsType<AssetImportResult>(result);
        Assert.False(string.IsNullOrEmpty(import.Guid));
        Assert.Equal("TextureImporter", import.ImporterType);
        Assert.True(File.Exists(Path.Combine(_fx.ProjectRoot, "Assets", "GenArt", "tex.png")));
        Assert.True(File.Exists(Path.Combine(_fx.ProjectRoot, "Assets", "GenArt", "tex.png.meta")));
    }

    [Fact]
    public async Task Dispatches_SceneComponentPatch_ByComponentType()
    {
        _fx.ResetScene();
        using ServiceProvider services = BuildServices();
        var dispatcher = new HostToolDispatcher(services);

        await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "Camera", initialFields: null,
            TestSceneFixture.Provenance("test.comp", "wf-add-cam-1"), dryRun: false);

        object result = await dispatcher.InvokeAsync("scene_component_patch", Args(new
        {
            scenePath = _fx.ScenePath,
            objectIdentifier = TestSceneFixture.PlayerIdentifier,
            componentType = "Camera",
            patches = new[] { new { path = "FieldOfView", value = "42" } },
            idempotencyKey = "wf-patch-cam-1",
        }), CancellationToken.None);

        Assert.IsType<SceneWriteResult>(result);
        var hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        var camera = Assert.Single(Assert.Single(hierarchy.RootObjects).Components);
        Assert.Equal("42", camera.Fields["FieldOfView"]);
    }

    [Fact]
    public async Task Dispatches_MaterialPatch()
    {
        using ServiceProvider services = BuildServices();
        var dispatcher = new HostToolDispatcher(services);

        string matDir = Path.Combine(_fx.ProjectRoot, "Assets", "Materials");
        Directory.CreateDirectory(matDir);
        string matPath = Path.Combine(matDir, "Gen.mat");
        await File.WriteAllTextAsync(matPath, """
            {
              "$id": 1,
              "$type": "XEngine.Runtime.Material, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
              "Name": "Gen",
              "_shader": { "AssetID": "00000000-0000-0000-0000-000000000000" },
              "_properties": { "_floats": { "_Roughness": 1F } }
            }
            """);

        object result = await dispatcher.InvokeAsync("material_patch", Args(new
        {
            materialPath = "Assets/Materials/Gen.mat",
            patches = new[] { new { path = "_properties/_floats/_Roughness", value = "0.25" } },
            idempotencyKey = "wf-mat-1",
            intent = "bind generated roughness",
        }), CancellationToken.None);

        Assert.IsType<SceneWriteResult>(result);
        string written = await File.ReadAllTextAsync(matPath);
        Assert.Contains("0.25", written);
    }
}
