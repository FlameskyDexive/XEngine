// This file is part of the XEngine AI tooling.
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;

namespace XEngine.Ai.Host;

/// <summary>
/// NodePlay graph tools (M20) — offline inspect/patch/compile over .nodeplay Echo text assets,
/// mirroring the shadergraph/vfxgraph tool family: drift-guarded writes, journal provenance,
/// and a pure validation pass for compile.
/// </summary>
public static class NodePlayEcho
{
    internal static void RequireNodePlayPath(string graphPath)
    {
        if (string.IsNullOrWhiteSpace(graphPath)
            || !graphPath.EndsWith(".nodeplay", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("nodeplay_graph_* tools require a .nodeplay path.");
        }
    }

    [McpServerTool(Name = "nodeplay_graph_inspect", UseStructuredContent = true)]
    [Description("Read a NodePlay graph (.nodeplay) offline: $type envelope, a flat path/value list (GraphName/Module/Nodes/Connections/Groups/Stickies/LocalBlackboard), and a SHA-256 content hash. Pass the hash to nodeplay_graph_patch.")]
    public static AssetDataDetail InspectNodePlayGraph(
        IServiceProvider services,
        [Description("Project-relative .nodeplay path (e.g. Assets/NodePlay/Soldier.nodeplay) or absolute.")] string graphPath)
    {
        RequireNodePlayPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(graphPath);
        return SceneReader.ReadAssetData(absolute, graphPath);
    }

    [McpServerTool(Name = "nodeplay_graph_patch", UseStructuredContent = true)]
    [Description("Patch a NodePlay graph (.nodeplay). Paths are slash-separated into the Echo root (e.g. GraphName, PrimeNodeId, Nodes/$values/0/Name, Connections/$values/0/ToNodeId). Same drift-protected + journaled mechanism as asset_data_patch.")]
    public static Task<SceneWriteResult> PatchNodePlayGraph(
        IServiceProvider services,
        [Description("Project-relative .nodeplay path or absolute.")] string graphPath,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from nodeplay_graph_inspect. Rejects the write if the graph changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        RequireNodePlayPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchAssetDataAsync(
            project.ResolveAssetPath(graphPath), graphPath, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.nodeplay-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    [McpServerTool(Name = "nodeplay_graph_compile", UseStructuredContent = true)]
    [Description("Validate a NodePlay graph (.nodeplay) without side effects: wire endpoints resolve, prime/start ids exist, duplicate node ids, dangling connections, empty ids. Returns an issues list plus node/connection counts.")]
    public static NodePlayGraphValidator.NodePlayValidateResult CompileNodePlayGraph(
        IServiceProvider services,
        [Description("Project-relative .nodeplay path or absolute.")] string graphPath)
    {
        RequireNodePlayPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(graphPath);
        return NodePlayGraphValidator.ValidateFile(absolute, graphPath);
    }
}
