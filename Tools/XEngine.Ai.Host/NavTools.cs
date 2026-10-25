using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>★ M6 — Offline navmesh MCP tools (Echo .navmesh + Detour, no engine).</summary>
[McpServerToolType]
public static class NavTools
{
    [McpServerTool(Name = "nav_inspect", UseStructuredContent = true)]
    [Description("Read a baked .navmesh offline: settings, tile summaries, bounds, payload sizes, and contentHash. Does not start the engine.")]
    public static NavInspectResult Inspect(
        IServiceProvider services,
        [Description("Project-relative .navmesh path (e.g. Assets/Scenes/Demo_navmesh/Demo.navmesh) or absolute.")] string navmeshPath)
    {
        string abs = Resolve(services, navmeshPath);
        return NavOps.Inspect(File.ReadAllText(abs), navmeshPath);
    }

    [McpServerTool(Name = "nav_query_path", UseStructuredContent = true)]
    [Description("Find a straight path on a .navmesh by loading its Detour TileData. from/to are world X,Y,Z. No engine required.")]
    public static NavQueryPathResult QueryPath(
        IServiceProvider services,
        [Description("Project-relative .navmesh path or absolute.")] string navmeshPath,
        [Description("Start X.")] float fromX,
        [Description("Start Y.")] float fromY,
        [Description("Start Z.")] float fromZ,
        [Description("End X.")] float toX,
        [Description("End Y.")] float toY,
        [Description("End Z.")] float toZ)
    {
        string abs = Resolve(services, navmeshPath);
        return NavOps.QueryPath(File.ReadAllText(abs), navmeshPath,
            new NavVec3(fromX, fromY, fromZ), new NavVec3(toX, toY, toZ));
    }

    [McpServerTool(Name = "nav_export", UseStructuredContent = true)]
    [Description("Export a .navmesh to OBJ (triangles), JSON (verts/indices), or BIN (Detour mesh-set bytes).")]
    public static NavExportResult Export(
        IServiceProvider services,
        [Description("Project-relative .navmesh path or absolute.")] string navmeshPath,
        [Description("Output format: obj, json, or bin.")] string format,
        [Description("Destination file path (project-relative or absolute).")] string outputPath)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string abs = Resolve(services, navmeshPath);
        string outAbs = project.ResolveAssetPath(outputPath);
        return NavOps.Export(File.ReadAllText(abs), navmeshPath, format, outAbs);
    }

    [McpServerTool(Name = "nav_settings_patch", UseStructuredContent = true)]
    [Description("Patch bake Settings (or other Echo fields) on a .navmesh. Paths are slash-separated (e.g. Settings/AgentRadius). Journaled; pass contentHash from nav_inspect.")]
    public static Task<SceneWriteResult> PatchSettings(
        IServiceProvider services,
        [Description("Project-relative .navmesh path or absolute.")] string navmeshPath,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from nav_inspect.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        RequireNavMeshPath(navmeshPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchAssetDataAsync(
            project.ResolveAssetPath(navmeshPath), navmeshPath, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.nav-settings-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    internal static void RequireNavMeshPath(string navmeshPath)
    {
        if (string.IsNullOrWhiteSpace(navmeshPath)
            || !navmeshPath.EndsWith(".navmesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("nav_* tools require a .navmesh path.");
        }
    }

    static string Resolve(IServiceProvider services, string navmeshPath)
    {
        RequireNavMeshPath(navmeshPath);
        string abs = services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(navmeshPath);
        if (!File.Exists(abs))
            throw new FileNotFoundException($"NavMesh not found: {navmeshPath}", abs);
        return abs;
    }

    internal static NavVec3 ParseVec3(string value, string name)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            throw new ArgumentException($"{name} must be x,y,z.");
        return new NavVec3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }
}
