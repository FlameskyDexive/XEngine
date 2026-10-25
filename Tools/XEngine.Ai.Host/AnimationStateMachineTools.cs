using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>★ M32 — Nested state-machine MCP tools.</summary>
[McpServerToolType]
public static class AnimationStateMachineTools
{
    [McpServerTool(Name = "animator_statemachine_create", UseStructuredContent = true)]
    [Description("Create a nested state machine on a .controller. parentPath is slash-separated from the layer root (empty = layer root). Journaled.")]
    public static Task<SceneWriteResult> CreateStateMachine(
        IServiceProvider services,
        string controllerPath, string name, string idempotencyKey,
        string? intent = null, string? layerName = null, string? parentPath = null,
        float posX = 200f, float posY = 80f,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-statemachine-create", $"Created state machine '{name}'.",
            root => AnimationAssetOps.CreateStateMachine(root, name, layerName, parentPath, posX, posY), cancellationToken);

    [McpServerTool(Name = "animator_statemachine_delete", UseStructuredContent = true)]
    [Description("Delete a nested state machine. parentPath is slash-separated from the layer root. Journaled.")]
    public static Task<SceneWriteResult> DeleteStateMachine(
        IServiceProvider services,
        string controllerPath, string name, string idempotencyKey,
        string? intent = null, string? layerName = null, string? parentPath = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-statemachine-delete", $"Deleted state machine '{name}'.",
            root => AnimationAssetOps.DeleteStateMachine(root, name, layerName, parentPath), cancellationToken);

    [McpServerTool(Name = "animator_statemachine_move", UseStructuredContent = true)]
    [Description("Move a state into a child state machine on the same parent. sourcePath is slash-separated from the layer root. Journaled.")]
    public static Task<SceneWriteResult> MoveStateIntoMachine(
        IServiceProvider services,
        string controllerPath, string stateName, string targetStateMachine, string idempotencyKey,
        string? intent = null, string? layerName = null, string? sourcePath = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-statemachine-move", $"Moved state '{stateName}' into '{targetStateMachine}'.",
            root => AnimationAssetOps.MoveState(root, stateName, targetStateMachine, layerName, sourcePath), cancellationToken);

    static Task<SceneWriteResult> Mutate(
        IServiceProvider services, string assetPath, string idempotencyKey, string? intent,
        string? expectedBeforeHash, bool dryRun, string tool, string summary,
        Action<EchoObject> mutate, CancellationToken cancellationToken)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.MutateAssetDataAsync(
            project.ResolveAssetPath(assetPath), assetPath, mutate, summary,
            XEngine.Data.Provenance.ChangeProvenance.ForTool(tool, intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }
}
