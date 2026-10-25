using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Core.BugReport;

/// <summary>Everything the reporter dialog collected, ready to be bundled and mailed.</summary>
public sealed class BugReportPayload
{
    public string Title = "";
    public string Category = "";
    public string Description = "";
    public string UserEmail = "";
    public bool AttachLogs = true;
    public bool AttachScreenshot = true;

    /// <summary>Crash record attached when the report was prefilled by <see cref="CrashHandler"/>.</summary>
    public string? CrashFilePath;

    /// <summary>Filled by <see cref="BugReportBuilder.WriteBundle"/> on the main thread before the send.</summary>
    public string SystemInfoText = "";
}

/// <summary>
/// Gathers the data a bug report carries: system info, recent editor logs, and a game-view
/// screenshot. Capture methods must run on the main thread (GPU readback, device queries);
/// the mailer then does the network half off-thread.
/// </summary>
public static class BugReportBuilder
{
    private static string? _cachedSystemInfo;

    /// <summary>System info snapshot, cached for the editor session (version/GPU don't change at runtime).</summary>
    public static string BuildSystemInfo()
    {
        if (_cachedSystemInfo != null) return _cachedSystemInfo;

        var sb = new StringBuilder();
        sb.AppendLine($"Editor:  XEngine {EditorVersion()}");
        sb.AppendLine($"Git:     {GitSummary()}");
        sb.AppendLine($"OS:      {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET:    {Environment.Version}");
        sb.AppendLine($"GPU:     {Graphics.Device.Capabilities.Baseline?.ToStableLogString() ?? "unknown"}");
        sb.AppendLine($"Project: {(Project.Current != null ? $"{Project.Current.Name} ({Project.Current.RootPath})" : "(none)")}");
        sb.AppendLine($"Scene:   {EditorSceneManager.CurrentScenePath ?? "(untitled)"}");
        sb.AppendLine($"Playmode:{(Application.IsPlaying ? (Application.IsPaused ? " paused" : " playing") : " stopped")}");
        sb.AppendLine($"Memory:  {GC.GetTotalMemory(false) / (1024 * 1024)} MB managed");

        _cachedSystemInfo = sb.ToString();
        return _cachedSystemInfo;
    }

    private static string EditorVersion()
    {
        string version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.1";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        return $"v{version}";
    }

    private static string GitSummary()
    {
        if (!GitInfo.GitInstalled) return "git not installed";
        if (!GitInfo.IsRepository) return "not a repository";
        return GitInfo.HasChanges ? $"branch {GitInfo.Branch}, uncommitted changes" : $"branch {GitInfo.Branch}, clean";
    }

    /// <summary>
    /// Captures the live GameView to <paramref name="directory"/>/screenshot.png (same RGBA→PNG
    /// path as the runtime_screenshot MCP tool). Returns null when no GameView panel is open.
    /// Main thread only (synchronous GPU readback).
    /// </summary>
    public static string? CaptureGameViewScreenshot(string directory)
    {
        var capture = GameViewPanel.ActiveInstance?.TryCapture();
        if (capture is null) return null;

        (byte[] rgba, int width, int height) = capture.Value;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "screenshot.png");

        var settings = new ImageMagick.PixelReadSettings((uint)width, (uint)height,
            ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA);
        GameViewScreenshotColorSpace.PreserveDisplayReferredRgbAndMakeOpaqueInPlace(rgba);
        using var img = new ImageMagick.MagickImage();
        img.ReadPixels(rgba, settings);
        img.Flip(); // GPU readback is bottom-up (LightmapBakeService / runtime_screenshot pattern)
        img.Write(path, ImageMagick.MagickFormat.Png32);
        return path;
    }

    /// <summary>
    /// Writes the local report bundle under Logs/BugReports/bugreport-&lt;timestamp&gt;/ —
    /// report.txt, logs.txt, screenshot.png — so a copy always survives even when the mail
    /// fails. Main thread only (screenshot readback). Returns the bundle directory.
    /// </summary>
    public static string WriteBundle(BugReportPayload payload, out string logsPath, out string? screenshotPath)
    {
        string root = Project.Current?.LogsPath is { } logs
            ? Path.Combine(logs, "BugReports")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XEngine", "BugReports");
        string dir = Path.Combine(root, $"bugreport-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);

        payload.SystemInfoText = BuildSystemInfo();

        File.WriteAllText(Path.Combine(dir, "report.txt"), BuildReportText(payload), new UTF8Encoding(false));

        logsPath = Path.Combine(dir, "logs.txt");
        File.WriteAllText(logsPath, ConsolePanel.DumpRecentLogs(), new UTF8Encoding(false));

        screenshotPath = payload.AttachScreenshot ? CaptureGameViewScreenshot(dir) : null;
        return dir;
    }

    /// <summary>The report.txt / email body text: user description plus everything we know.</summary>
    public static string BuildReportText(BugReportPayload payload)
    {
        var sb = new StringBuilder();
        sb.AppendLine(payload.Description.Trim());
        sb.AppendLine();
        sb.AppendLine($"Reporter: {(string.IsNullOrWhiteSpace(payload.UserEmail) ? "(none)" : payload.UserEmail.Trim())}");
        sb.AppendLine();
        sb.AppendLine("---- System info ----");
        sb.AppendLine(payload.SystemInfoText);

        if (payload.CrashFilePath != null && File.Exists(payload.CrashFilePath))
        {
            sb.AppendLine("---- Crash record ----");
            sb.AppendLine(File.ReadAllText(payload.CrashFilePath));
        }
        return sb.ToString();
    }
}
