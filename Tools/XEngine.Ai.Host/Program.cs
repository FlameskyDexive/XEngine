using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// xengine-cli entry point. Dual-mode single binary (XMcp pattern):
///   - <c>serve</c>           : runs the MCP stdio server (AI client connects here).
///   - any other command       : CLI that prints structured JSON to stdout and exits.
///
/// Contract: stdout is reserved for JSON results; all logging goes to stderr so the MCP
/// stdio transport stays clean. Exit codes: 0 success / 1 error / 130 canceled.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        HostCommand? command = null;
        try
        {
            HostOptions options = HostOptions.Parse(args);
            command = HostCommandParser.Parse(args);

            HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

            // stderr-only logging: stdout must stay clean for MCP stdio + CLI JSON.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(console =>
            {
                console.LogToStandardErrorThreshold = LogLevel.Trace;
            });
            if (command != HostCommand.Serve)
            {
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
            }

            ProjectDescriptor project = ProjectIdentity.Inspect(options.ProjectRoot);

            // Edition: explicit --edition flag wins; otherwise auto-detect from the project layout
            // (engine repository → engine-dev, user game project → user).
            AiEdition edition = options.Edition ?? AiEditionDetector.Detect(project.ProjectRoot);

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(project);
            // Non-generic overload: AiEdition is an enum (value type), the generic AddSingleton<T>
            // requires a reference type.
            builder.Services.AddSingleton(typeof(AiEdition), edition);
            builder.Services.AddSingleton<HealthService>();
            builder.Services.AddSingleton<CapabilityCatalog>();
            builder.Services.AddSingleton<DoctorService>();
            builder.Services.AddSingleton<CodeIndexService>();
            builder.Services.AddSingleton(sp => new WorkflowCatalog(sp.GetRequiredService<ProjectDescriptor>().ProjectRoot));
            builder.Services.AddSingleton<HostToolDispatcher>();
            builder.Services.AddSingleton<WorkflowToolDispatcher>(sp => sp.GetRequiredService<HostToolDispatcher>());
            builder.Services.AddSingleton<WorkflowRunner>();

            // ★ Layer 0 (A2) journal, owned by the Host. scene-write tools (B2) emit provenance-bearing
            // changes here — the SAME journal the editor's ChangeJournalService writes.
            builder.Services.AddSingleton(sp =>
            {
                var p = sp.GetRequiredService<ProjectDescriptor>();
                string journalRoot = Path.Combine(p.ProjectRoot, ".xengine", "journal");
                return new XEngine.Data.Provenance.ChangeJournal(journalRoot, p.ProjectId);
            });
            builder.Services.AddSingleton<SceneWriter>();
            // ★ P0-5 — asset file ops (import/move/delete + .meta minting), same journal as SceneWriter.
            builder.Services.AddSingleton<AssetWriter>();
            // ★ Script ops (create/delete/apply_edits), same journal. Engine AssetWatcher auto-recompiles.
            builder.Services.AddSingleton<ScriptWriter>();
            // ★ Gen-P1.2 — generation gateway: provider registry (mock always; meshy et al resolve
            // keys lazily), the durable per-project task ledger, and the submit/status/fetch service.
            builder.Services.AddSingleton(sp => XEngine.Ai.Gateway.GenProviderRegistry.CreateDefault(
                sp.GetRequiredService<ProjectDescriptor>().ProjectRoot));
            builder.Services.AddSingleton(sp => new XEngine.Ai.Gateway.GenTaskStore(
                sp.GetRequiredService<ProjectDescriptor>().ProjectRoot));
            builder.Services.AddSingleton<GenService>();
            // ★ XUI (M3) — offline .xui validate/inspect/patch. Shares the same journal as SceneWriter
            // so UI edits land next to scene and asset edits rather than in a second ledger.
            builder.Services.AddSingleton(sp => new XuiService(
                sp.GetRequiredService<ProjectDescriptor>(),
                sp.GetRequiredService<XEngine.Data.Provenance.ChangeJournal>()));
            builder.Services.AddSingleton(sp => new PxlService(sp.GetRequiredService<ProjectDescriptor>()));
            builder.Services.AddSingleton(sp => new XuiI18nService(sp.GetRequiredService<ProjectDescriptor>()));
            builder.Services.AddSingleton(sp => new PsdService(sp.GetRequiredService<ProjectDescriptor>()));
            builder.Services.AddSingleton(sp => new XuiFromPsdService(sp.GetRequiredService<ProjectDescriptor>()));
            builder.Services.AddSingleton<PlayablePropService>();
            builder.Services.AddSingleton<CharacterWireService>();
            builder.Services.AddSingleton<SpriteWireService>();
            // ★ SpineGen pipeline services (2026-08-27 spec) — slice/fill/rig/skin/animate stages.
            builder.Services.AddSingleton<SpineGenSliceService>();
            builder.Services.AddSingleton<SpineGenFillService>();
            builder.Services.AddSingleton<SpineGenRigService>();
            builder.Services.AddSingleton<SpineGenSkinService>();
            builder.Services.AddSingleton<SpineGenAnimateService>();

            // Register the MCP stdio server + reflect [McpServerTool] methods in this assembly,
            // FILTERED BY EDITION (engine-dev = full surface; user = game-dev tools only,
            // engine-source tools never register). WithEditionTools keeps the SDK's own
            // per-method tool construction, so behavior matches the old WithToolsFromAssembly
            // for every type it includes.
            // ★ Resources: URI-addressable project/capabilities/journal/workflows (MCP resources primitive).
            // ★ Prompts (P2-3): reusable prompt templates clients can fetch as bootstrap guidance.
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithEditionTools(typeof(AiTools).Assembly, edition)
                .WithResourcesFromAssembly()
                .WithPromptsFromAssembly();

            using IHost host = builder.Build();

            if (command != HostCommand.Serve)
            {
                AiEdition? requiredEdition = HostCommandEdition.RequiredEdition(command.Value);
                if (requiredEdition.HasValue && edition != requiredEdition.Value)
                {
                    WriteErrorEnvelope(
                        AiError.Codes.EditionForbidden,
                        $"'{command.Value}' is an engine-repository tool and requires edition " +
                        $"'{requiredEdition.Value.ToWireName()}', but this host resolved " +
                        $"'{edition.ToWireName()}' for '{project.ProjectRoot}'. Run against the engine " +
                        "repository or pass --edition engine-dev explicitly.",
                        retryable: false);
                    return ProtocolConstants.ExitCodes.Error;
                }

                await CliRunner.RunAsync(command.Value, args, host.Services).ConfigureAwait(false);
                return ProtocolConstants.ExitCodes.Success;
            }

            await host.RunAsync().ConfigureAwait(false);
            return ProtocolConstants.ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return ProtocolConstants.ExitCodes.Canceled;
        }
        catch (Exception exception) when (command != HostCommand.Serve)
        {
            // Structured error envelope for CLI mode. (serve mode lets the SDK own error framing.)
            WriteErrorEnvelope(GetErrorCode(exception), exception.Message,
                retryable: exception is TimeoutException or IOException);
            return ProtocolConstants.ExitCodes.Error;
        }
    }

    /// <summary>Maps a caught exception to a stable error code (mirrors AiError.Codes).</summary>
    private static string GetErrorCode(Exception exception)
    {
        return exception switch
        {
            ArgumentException or JsonException => AiError.Codes.InvalidArguments,
            KeyNotFoundException => AiError.Codes.NotFound,
            OperationCanceledException => AiError.Codes.Timeout,
            TimeoutException => AiError.Codes.Timeout,
            IOException => AiError.Codes.IoError,
            InvalidOperationException => AiError.Codes.OperationFailed,
            NotImplementedException => AiError.Codes.ToolNotFound,
            _ => AiError.Codes.InternalError,
        };
    }

    /// <summary>Structured CLI-mode error envelope on stderr (stdout stays clean for JSON results).</summary>
    private static void WriteErrorEnvelope(string code, string message, bool retryable)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(
            new
            {
                status = "Failed",
                error = new { code, message, retryable },
            },
            ErrorJsonOptions));
    }
}
