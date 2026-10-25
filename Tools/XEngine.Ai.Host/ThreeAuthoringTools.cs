using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;
using XEngine.Ai.ThreeFx;
using XEngine.Ai.ThreeHost.Client;
using XEngine.Data.Provenance;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ A2 — MCP face of the three.js authoring chain. <c>three_validate</c> is a pure-library
/// pass; <c>three_preview</c>/<c>three_export</c> drive the shared STA WebView2 host through the
/// same client the CLI and the editor panel use. Exports append to the on-disk change journal
/// (<c>.xengine/journal</c>) so provenance stays queryable via <c>journal_query</c>.
/// </summary>
[McpServerToolType]
public static class ThreeAuthoringTools
{
    [McpServerTool(Name = "three_validate", UseStructuredContent = true)]
    [Description("Validate a .threefx recipe offline (Echo parse + schema + dependency existence) without spawning the WebView2 authoring host.")]
    public static ThreeValidateResult Validate(
        IServiceProvider services,
        [Description("Project-relative .threefx path (e.g. Three/sample.threefx).")] string sourcePath)
    {
        ProjectDescriptor project = services.GetRequiredService<ProjectDescriptor>();
        string sourceFull = ThreeAuthoringPath.ResolveBelowRoot(project.ProjectRoot, sourcePath);
        if (!File.Exists(sourceFull))
            throw new FileNotFoundException($"Source not found: {sourcePath}");

        List<ThreeAuthoringDiagnostic> diagnostics = [];
        ThreeFxRecipe? recipe = null;
        if (!ThreeFxDocument.TryParse(File.ReadAllText(sourceFull), out recipe, out List<ThreeAuthoringDiagnostic> parseIssues))
            diagnostics.AddRange(parseIssues);
        else
        {
            diagnostics.AddRange(ThreeFxValidator.Validate(recipe!));
            ThreeFxValidator.ValidateDependencies(recipe!, project.ProjectRoot, diagnostics);
        }

        bool hasError = false;
        foreach (ThreeAuthoringDiagnostic diagnostic in diagnostics)
        {
            if (string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal))
                hasError = true;
        }
        return new ThreeValidateResult(sourcePath, !hasError, recipe?.StableId, diagnostics);
    }

    [McpServerTool(Name = "three_preview", UseStructuredContent = true)]
    [Description("Render a .threefx recipe to a preview PNG through the Windows authoring host (spawns the STA WebView2 process). Returns the manifest with the artifact path + content hash.")]
    public static ThreeAuthoringResponse Preview(
        IServiceProvider services,
        [Description("Project-relative .threefx path.")] string sourcePath,
        [Description("Animation time in seconds.")] double timeSeconds = 0,
        [Description("Preview width in pixels (1-4096).")] int width = 512,
        [Description("Preview height in pixels (1-4096).")] int height = 512,
        [Description("Operation timeout in milliseconds.")] int timeoutMs = 120000,
        [Description("Optional extra host options, e.g. seed.")] Dictionary<string, string>? options = null)
        => RunHost(services, new ThreeAuthoringRequest(
            ThreeAuthoringProtocol.Version, NewRequestId("preview"), "Preview", sourcePath,
            null, null, timeSeconds, width, height, timeoutMs, null, options));

    [McpServerTool(Name = "three_export", UseStructuredContent = true)]
    [Description("Export a .threefx recipe to a staged PNG + manifest through the Windows authoring host. Idempotent: an unchanged recipe+seed+params+tools identity reuses existing artifacts and skips re-render. Appends a provenance journal record.")]
    public static Task<ThreeAuthoringResponse> Export(
        IServiceProvider services,
        [Description("Project-relative .threefx path.")] string sourcePath,
        [Description("Project-relative output directory.")] string outputDirectory,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent, recorded in the journal.")] string? intent = null,
        [Description("Animation time in seconds.")] double timeSeconds = 0,
        [Description("Operation timeout in milliseconds.")] int timeoutMs = 120000,
        [Description("If true, validate and report without rendering or writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => ExportCoreAsync(services, sourcePath, outputDirectory, idempotencyKey, intent,
            timeSeconds, timeoutMs, dryRun, cancellationToken);

    internal static async Task<ThreeAuthoringResponse> ExportCoreAsync(
        IServiceProvider services,
        string sourcePath,
        string outputDirectory,
        string idempotencyKey,
        string? intent,
        double timeSeconds,
        int timeoutMs,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ProjectDescriptor project = services.GetRequiredService<ProjectDescriptor>();

        var validate = Validate(services, sourcePath);
        if (!validate.Succeeded)
            return new ThreeAuthoringResponse(
                ThreeAuthoringProtocol.Version, NewRequestId("export"), "Export",
                Succeeded: false, ErrorCode: "recipe_invalid",
                Message: "Recipe failed validation; nothing was written.",
                Manifest: null, Diagnostics: validate.Diagnostics);

        if (dryRun)
            return new ThreeAuthoringResponse(
                ThreeAuthoringProtocol.Version, NewRequestId("export"), "Export",
                Succeeded: true, ErrorCode: null,
                Message: "Dry run: recipe valid; no render, no files written.",
                Manifest: null, Diagnostics: validate.Diagnostics);

        ThreeAuthoringRequest request = new(
            ThreeAuthoringProtocol.Version, NewRequestId("export"), "Export", sourcePath,
            outputDirectory, null, timeSeconds, 0, 0, timeoutMs, intent,
            Options: new Dictionary<string, string> { ["idempotencyKey"] = idempotencyKey });
        ThreeAuthoringResponse response = RunHost(services, request);

        if (response.Succeeded)
            await AppendExportJournalAsync(services, sourcePath, outputDirectory,
                idempotencyKey, intent, response, cancellationToken).ConfigureAwait(false);

        return response;
    }

    private static async Task AppendExportJournalAsync(
        IServiceProvider services,
        string sourcePath,
        string outputDirectory,
        string idempotencyKey,
        string? intent,
        ThreeAuthoringResponse response,
        CancellationToken cancellationToken)
    {
        string artifactHash = response.Manifest?.Artifacts.Count > 0
            ? response.Manifest.Artifacts[0].ContentHash
            : "unknown";
        string identity = "";
        foreach (ThreeAuthoringDiagnostic diagnostic in response.Diagnostics)
        {
            if (diagnostic.Code == "cache_identity")
                identity = diagnostic.Message;
        }

        EchoObject delta = EchoObject.NewCompound();
        delta["tool"] = new EchoObject("three_export");
        delta["source"] = new EchoObject(sourcePath);
        delta["output"] = new EchoObject(outputDirectory);
        delta["artifactSha256"] = new EchoObject(artifactHash);
        delta["cacheIdentity"] = new EchoObject(identity);

        string journalRoot = Path.Combine(services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
            ".xengine", "journal");
        using var journal = new ChangeJournal(journalRoot, "three-authoring");
            XEngine.Data.Provenance.ChangeProvenance provenance = XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "mcp.three-export", intent, idempotencyKey);
        await journal.AppendAsync(
            targetAsset: outputDirectory,
            delta,
            provenance,
            beforeHash: string.Empty,
            afterHash: artifactHash,
            cancellationToken).ConfigureAwait(false);
    }

    internal static ThreeAuthoringResponse RunHost(IServiceProvider services, ThreeAuthoringRequest request)
    {
        ProjectDescriptor project = services.GetRequiredService<ProjectDescriptor>();
        if (!ThreeHostLauncher.TryResolve(null, out string hostExe, out string? error))
            throw new InvalidOperationException(error);

        IReadOnlyList<ThreeAuthoringDiagnostic> issues = ThreeAuthoringRequestValidator.Validate(request);
        foreach (ThreeAuthoringDiagnostic diagnostic in issues)
        {
            if (string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal))
                return new ThreeAuthoringResponse(
                    ThreeAuthoringProtocol.Version, request.RequestId, request.Operation,
                    Succeeded: false, ErrorCode: "invalid_request", Message: diagnostic.Message,
                    Manifest: null, Diagnostics: issues);
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo(hostExe)
        {
            Arguments = ThreeHostLauncher.BuildArguments(
                ThreeAuthoringProtocol.Version,
                project.ProjectRoot,
                Path.Combine(Path.GetDirectoryName(hostExe)!, "wwwroot"),
                ownerHwnd: 0,
                showWindow: false,
                width: 960,
                height: 640),
            WorkingDirectory = project.ProjectRoot,
        };

        using var client = new ThreeHostProcessClient(startInfo);
        return client.RoundTrip(request, CancellationToken.None);
    }

    private static string NewRequestId(string prefix) =>
        prefix + "-" + Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Result envelope for the offline recipe validation.</summary>
public sealed record ThreeValidateResult(
    string SourcePath,
    bool Succeeded,
    string? StableId,
    IReadOnlyList<ThreeAuthoringDiagnostic> Diagnostics);
