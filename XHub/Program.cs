using System;
using Avalonia;
using XHub.Infrastructure;
using XHub.Platform;
using XHub.Services;

namespace XHub;

internal static class Program
{
    /// <summary>Parsed command line; read by App/MainWindow during startup.</summary>
    public static CliOptions? Options { get; private set; }

    [STAThread]
    public static int Main(string[]? args)
    {
        string[] argv = args ?? [];
        Options = CliOptions.Parse(argv);
        if (Options.RegisterFileAssociation)
        {
            // §5: zip/manual installs register the .xengine association per-user (HKCU) without
            // the Inno installer; the installer performs the same registration itself.
            if (OperatingSystem.IsWindows())
            {
                string command = Platform.FileAssociation.Register();
                Console.WriteLine($".xengine -> {command}");
            }
            else
            {
                Console.Error.WriteLine("--register-file-association is Windows-only.");
                return 1;
            }
            return 0;
        }

        AppPaths.Initialize(Options.DataDir);
        AppPaths.EnsureDirectories();
        HubLog.SetVerbose(Options.Verbose);
        HubLog.PruneOldLogs();
        HubLog.Info($"{VersionInfo.DisplayVersion} starting (args: {(argv.Length == 0 ? "<none>" : string.Join(' ', argv))})");

        // §4.4 single instance: later launches forward their request to the running instance.
        if (SingleInstanceGuard.TryAcquire() is not { } guard)
        {
            HubLog.Info("Another XHub instance owns the mutex; forwarding and exiting.");
            string[] forward = argv.Length > 0 ? argv : ["--activate"];
            SingleInstanceGuard.TryForward(forward);
            return 0;
        }

        using (guard)
        {
            var server = new SingleInstanceServer(message => App.DispatchIpc(message.Args));
            server.Start();
            HubLog.Info("Primary instance acquired; launching UI.");

            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
            }
            finally
            {
                server.Dispose();
                HubLog.Info("XHub exited.");
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
