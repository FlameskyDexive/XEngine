using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.ThreeHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ThreeHostOptions options;
        try
        {
            options = ThreeHostOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("argument_error: " + ex.Message);
            WriteEnvelope(new ThreeAuthoringResponse(
                ThreeAuthoringProtocol.Version,
                string.Empty,
                string.Empty,
                Succeeded: false,
                ErrorCode: "argument_error",
                Message: ex.Message,
                Manifest: null,
                Diagnostics: [new ThreeAuthoringDiagnostic("argument_error", "error", ex.Message)]));
            return 2;
        }

        // One host process per project root. A second spawn (editor + CLI colliding) fails fast
        // with a structured error instead of silently sharing someone else's stdio.
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            "xengine-three-host-" + Sha256Hex(options.ProjectRoot.ToLowerInvariant()),
            out bool createdNew);
        if (!createdNew)
        {
            WriteEnvelope(new ThreeAuthoringResponse(
                ThreeAuthoringProtocol.Version,
                string.Empty,
                string.Empty,
                Succeeded: false,
                ErrorCode: "host_already_running",
                Message: "An authoring host is already running for this project root.",
                Manifest: null,
                Diagnostics: [new ThreeAuthoringDiagnostic("host_already_running", "error",
                    "An authoring host is already running for this project root.")]));
            return 3;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Console.Error.WriteLine("host-trace: starting message pump");

        using var server = new ThreeHostServer(options);
        int exitCode = server.Run();
        Console.Error.WriteLine($"host-trace: pump returned, exit {exitCode}");
        return exitCode;
    }

    private static void WriteEnvelope(ThreeAuthoringResponse response) =>
        Console.Out.WriteLine(JsonSerialization.Serialize(response));

    internal static string Sha256Hex(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>Parsed command line for one host process lifetime.</summary>
internal sealed record ThreeHostOptions(
    string ProjectRoot,
    string PageRoot,
    long OwnerHwnd,
    bool ShowWindow,
    int Width,
    int Height)
{
    public static ThreeHostOptions Parse(string[] args)
    {
        string projectRoot = Environment.CurrentDirectory;
        string pageRoot = DefaultPageRoot();
        string? protocolVersion = null;
        long ownerHwnd = 0;
        bool showWindow = true;
        int width = 960;
        int height = 640;

        for (int i = 0; i < args.Length; ++i)
        {
            string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            switch (args[i].ToLowerInvariant())
            {
                case "--protocol":
                    RequireValue(args, i, value);
                    protocolVersion = value;
                    ++i;
                    break;
                case "--project-root":
                    RequireValue(args, i, value);
                    projectRoot = value;
                    ++i;
                    break;
                case "--page-root":
                    RequireValue(args, i, value);
                    pageRoot = value;
                    ++i;
                    break;
                case "--owner-hwnd":
                    RequireValue(args, i, value);
                    ownerHwnd = long.Parse(value, CultureInfo.InvariantCulture);
                    ++i;
                    break;
                case "--window":
                    RequireValue(args, i, value);
                    showWindow = !value.Equals("hidden", StringComparison.OrdinalIgnoreCase);
                    ++i;
                    break;
                case "--width":
                    RequireValue(args, i, value);
                    width = int.Parse(value, CultureInfo.InvariantCulture);
                    ++i;
                    break;
                case "--height":
                    RequireValue(args, i, value);
                    height = int.Parse(value, CultureInfo.InvariantCulture);
                    ++i;
                    break;
                default:
                    throw new ArgumentException($"Unknown authoring host argument: {args[i]}");
            }
        }

        if (!Directory.Exists(projectRoot))
            throw new ArgumentException($"Project root does not exist: {projectRoot}");
        if (!Directory.Exists(pageRoot))
            throw new ArgumentException($"Authoring page root does not exist: {pageRoot}");
        if (protocolVersion is not null
            && !string.Equals(protocolVersion, ThreeAuthoringProtocol.Version, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported protocol '{protocolVersion}', expected '{ThreeAuthoringProtocol.Version}'.");
        }

        return new ThreeHostOptions(
            Path.GetFullPath(projectRoot),
            Path.GetFullPath(pageRoot),
            ownerHwnd,
            showWindow,
            width,
            height);
    }

    private static void RequireValue(string[] args, int index, string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"Argument {args[index]} requires a value.");
    }

    /// <summary>Authoring pages live next to the executable (wwwroot copied at build/publish).</summary>
    public static string DefaultPageRoot()
    {
        string exeDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(exeDir, "wwwroot");
        return Directory.Exists(candidate) ? candidate : exeDir;
    }
}

internal static class JsonSerialization
{
    public static string Serialize<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, ThreeAuthoringProtocol.JsonOptions);

    public static T? Deserialize<T>(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(json, ThreeAuthoringProtocol.JsonOptions);
}
