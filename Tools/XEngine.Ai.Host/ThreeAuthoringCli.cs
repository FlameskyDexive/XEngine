using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XEngine.Ai.Protocol;
using XEngine.Ai.ThreeHost.Client;

namespace XEngine.Ai.Host;

/// <summary>
/// CLI surface for the shared Windows three.js authoring host (<c>xengine-cli three-authoring</c>).
/// Spawns the same STA WebView2 host process the editor panel uses and speaks the identical
/// three-authoring/1 protocol, which is what makes panel and CLI exports byte-identical.
/// </summary>
internal static class ThreeAuthoringCli
{
    public static object Run(IReadOnlyList<string> args, IServiceProvider services)
    {
        ProjectDescriptor project = services.GetRequiredService<ProjectDescriptor>();
        string projectRoot = project.ProjectRoot;

        string operation = GetRequired(args, "--operation");
        string source = GetRequired(args, "--source");

        // validate is a pure-library pass: parse + recipe validation + dependency existence,
        // without spawning the WebView2 host at all.
        if (operation.Equals("validate", StringComparison.OrdinalIgnoreCase))
            return ValidateOffline(projectRoot, source);

        string? outputDirectory = GetOptional(args, "--out");
        double timeSeconds = GetDouble(args, "--time", 0);
        int width = GetInt(args, "--width", 512);
        int height = GetInt(args, "--height", 512);
        int timeoutMs = GetInt(args, "--timeout-ms", ThreeAuthoringProtocol.DefaultTimeoutMs);
        string? intent = GetOptional(args, "--intent");
        int cancelAfterMs = GetInt(args, "--cancel-after-ms", 0);
        bool showWindow = GetOptional(args, "--window") != "hidden";
        string? engineRoot = GetOptional(args, "--engine-root");

        if (!ThreeHostLauncher.TryResolve(engineRoot, out string hostExe, out string? resolveError))
            throw new InvalidOperationException(resolveError);

        string requestId = "cli-" + Guid.NewGuid().ToString("N")[..12];
        var request = new ThreeAuthoringRequest(
            ProtocolVersion: ThreeAuthoringProtocol.Version,
            RequestId: requestId,
            Operation: operation,
            SourcePath: source,
            OutputDirectory: outputDirectory,
            PreviewId: null,
            TimeSeconds: timeSeconds,
            Width: width,
            Height: height,
            TimeoutMs: timeoutMs,
            Intent: intent,
            Options: BuildOptions(args));

        string pageRoot = Path.Combine(
            Path.GetDirectoryName(hostExe)!, "wwwroot");
        var startInfo = new ProcessStartInfo(hostExe)
        {
            Arguments = ThreeHostLauncher.BuildArguments(
                ThreeAuthoringProtocol.Version, projectRoot, pageRoot,
                ownerHwnd: 0, showWindow, width, height),
            WorkingDirectory = projectRoot,
        };

        using var client = new ThreeHostProcessClient(startInfo);
        client.HostLog += line => Console.Error.WriteLine("host: " + line);
        ThreeAuthoringResponse response;
        if (cancelAfterMs > 0)
        {
            using var cts = new CancellationTokenSource(cancelAfterMs);
            response = client.RoundTrip(request, cts.Token);
        }
        else
        {
            response = client.RoundTrip(request, CancellationToken.None);
        }

        return new ThreeAuthoringCliResult(
            HostExe: hostExe,
            HostProcessId: client.ProcessId,
            Response: response);
    }

    private static object ValidateOffline(string projectRoot, string source)
    {
        string sourceFull = XEngine.Ai.ThreeFx.ThreeAuthoringPath.ResolveBelowRoot(projectRoot, source);
        if (!File.Exists(sourceFull))
            throw new FileNotFoundException($"Source not found: {source}");

        List<ThreeAuthoringDiagnostic> diagnostics = [];
        if (!XEngine.Ai.ThreeFx.ThreeFxDocument.TryParse(
                File.ReadAllText(sourceFull), out XEngine.Ai.ThreeFx.ThreeFxRecipe? recipe, out List<ThreeAuthoringDiagnostic> parseIssues))
        {
            diagnostics.AddRange(parseIssues);
        }
        else
        {
            diagnostics.AddRange(XEngine.Ai.ThreeFx.ThreeFxValidator.Validate(recipe!));
            XEngine.Ai.ThreeFx.ThreeFxValidator.ValidateDependencies(recipe!, projectRoot, diagnostics);
        }

        bool hasError = false;
        foreach (ThreeAuthoringDiagnostic diagnostic in diagnostics)
        {
            if (string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal))
                hasError = true;
        }

        return new ThreeAuthoringValidateResult(
            SourcePath: source,
            Succeeded: !hasError,
            StableId: recipe?.StableId,
            Diagnostics: diagnostics);
    }

    private sealed record ThreeAuthoringValidateResult(
        string SourcePath,
        bool Succeeded,
        string? StableId,
        IReadOnlyList<ThreeAuthoringDiagnostic> Diagnostics);

    private static Dictionary<string, string>? BuildOptions(IReadOnlyList<string> args)
    {
        Dictionary<string, string>? options = null;
        for (int i = 0; i < args.Count; ++i)
        {
            if (!args[i].StartsWith("--opt:", StringComparison.Ordinal))
                continue;
            string pair = args[i]["--opt:".Length..];
            int equals = pair.IndexOf('=');
            if (equals <= 0)
                throw new ArgumentException($"Malformed option (expected --opt:key=value): {args[i]}");
            (options ??= new Dictionary<string, string>())[pair[..equals]] = pair[(equals + 1)..];
        }
        return options;
    }

    private static string GetRequired(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count; ++i)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return args[i + 1];
        }
        throw new ArgumentException($"Missing required option: {name}");
    }

    private static string? GetOptional(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count; ++i)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return args[i + 1];
        }
        return null;
    }

    private static int GetInt(IReadOnlyList<string> args, string name, int fallback)
    {
        string? raw = GetOptional(args, name);
        return raw is null ? fallback : int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double GetDouble(IReadOnlyList<string> args, string name, double fallback)
    {
        string? raw = GetOptional(args, name);
        return raw is null
            ? fallback
            : double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>CLI envelope: host identity plus the raw protocol response.</summary>
internal sealed record ThreeAuthoringCliResult(
    string HostExe,
    int HostProcessId,
    ThreeAuthoringResponse Response);
