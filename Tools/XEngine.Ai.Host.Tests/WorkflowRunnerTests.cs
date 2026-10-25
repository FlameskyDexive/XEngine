// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using XEngine.Ai.Protocol;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Gen-P1.5 — Workflow templating that the text-to-prop DAG depends on: array indexing
/// via numeric path segments (gen_fetch's assets[i].subAssets[j].guid) and chaining a created
/// GameObject's identifier between steps.</summary>
public sealed class WorkflowRunnerTests
{
    private sealed class RecordingDispatcher : WorkflowToolDispatcher
    {
        public List<(string Tool, JsonElement Args)> Calls { get; } = new();
        public Dictionary<string, object> Results { get; } = new(StringComparer.Ordinal);

        public Task<object> InvokeAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls.Add((tool, arguments.Clone()));
            return Task.FromResult(Results.TryGetValue(tool, out object? result) ? result : new { ok = true });
        }
    }

    private static WorkflowStep Step(string name, string tool, object args, params string[] dependsOn)
        => new(name, tool, JsonSerializer.SerializeToElement(args), dependsOn, "Stop");

    private static JsonElement Inputs(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task StepTemplating_IndexesIntoArrays_WithNumericSegments()
    {
        var dispatcher = new RecordingDispatcher();
        // Shape mirrors GenFetchToolResult: assets[0].subAssets[{0,1}].guid.
        dispatcher.Results["gen_fetch"] = new
        {
            taskId = "mock-1",
            assets = new[]
            {
                new
                {
                    assetPath = "Assets/GenArt/sword.glb",
                    subAssets = new[]
                    {
                        new { stableId = "Blade", kind = "Mesh", guid = "mesh-guid-000" },
                        new { stableId = "Iron", kind = "Material", guid = "mat-guid-111" },
                    },
                },
            },
        };

        var definition = new WorkflowDefinition("t", "test", Inputs(new { }), new[]
        {
            Step("fetch", "gen_fetch", new { taskId = "mock-1" }),
            Step("wire", "scene_component_add", new
            {
                objectIdentifier = "some-go",
                initialFields = new[]
                {
                    new { path = "Mesh/AssetID", value = "${steps.fetch.assets.0.subAssets.0.guid}" },
                    new { path = "Materials/$values/0/AssetID", value = "${steps.fetch.assets.0.subAssets.1.guid}" },
                },
            }, "fetch"),
        });

        var runner = new WorkflowRunner(dispatcher);
        WorkflowRunResult result = await runner.RunAsync(definition, Inputs(new { }), dryRun: false);

        Assert.True(result.Succeeded);
        JsonElement wireArgs = dispatcher.Calls[1].Args;
        JsonElement fields = wireArgs.GetProperty("initialFields");
        Assert.Equal("mesh-guid-000", fields[0].GetProperty("value").GetString());
        Assert.Equal("mat-guid-111", fields[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task StepTemplating_OutOfRangeIndex_LeavesTokenUnresolved()
    {
        var dispatcher = new RecordingDispatcher();
        dispatcher.Results["gen_fetch"] = new { assets = new[] { new { guid = "g0" } } };

        var definition = new WorkflowDefinition("t", "test", Inputs(new { }), new[]
        {
            Step("fetch", "gen_fetch", new { }),
            Step("wire", "noop", new { value = "${steps.fetch.assets.5.guid}" }, "fetch"),
        });

        WorkflowRunResult result = await new WorkflowRunner(dispatcher).RunAsync(definition, Inputs(new { }), dryRun: false);

        Assert.True(result.Succeeded); // unresolved token passes through literally, tool decides
        Assert.Equal("${steps.fetch.assets.5.guid}", dispatcher.Calls[1].Args.GetProperty("value").GetString());
    }

    [Fact]
    public async Task StepTemplating_ChainsCreatedIdentifier_FromSceneWriteResult()
    {
        var dispatcher = new RecordingDispatcher();
        dispatcher.Results["scene_object_create"] = new SceneWriteResult(
            "change-1", "Assets/S.scene", "h0", "h1", DryRun: false,
            Summary: "Created GameObject 'Sword' (Identifier abc-123) at scene root.",
            CreatedIdentifier: "abc-123");

        var definition = new WorkflowDefinition("t", "test", Inputs(new { }), new[]
        {
            Step("create-go", "scene_object_create", new { name = "Sword" }),
            Step("add", "scene_component_add", new { objectIdentifier = "${steps.create-go.createdIdentifier}" }, "create-go"),
        });

        WorkflowRunResult result = await new WorkflowRunner(dispatcher).RunAsync(definition, Inputs(new { }), dryRun: false);

        Assert.True(result.Succeeded);
        Assert.Equal("abc-123", dispatcher.Calls[1].Args.GetProperty("objectIdentifier").GetString());
    }
}
