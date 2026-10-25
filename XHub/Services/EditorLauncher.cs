using System;
using System.Diagnostics;
using System.IO;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;

namespace XHub.Services;

/// <summary>A launched editor session tracked until project-ready or failure (§4.4).</summary>
public sealed record EditorSession(
    string SessionId,
    string ProjectPath,
    InstallationRecord Installation,
    int ProcessId,
    DateTime ProcessStartTime,
    string EventsFilePath,
    bool HubAware);

/// <summary>
/// Launches an editor installation against a project (§4.4): hub-aware payloads get
/// <c>--hub-session &lt;id&gt;</c> so XHub receives lifecycle events; legacy payloads enter
/// compat mode (<c>--project</c> only, success can never be confirmed). Pure arg composition is
/// exposed separately for tests.
/// </summary>
public static class EditorLauncher
{
    public static string[] BuildArgs(InstallationRecord install, string projectPath, string sessionId)
    {
        return install.HubProtocolVersion >= 1
            ? [$"--project", projectPath, "--hub-session", sessionId]
            : ["--project", projectPath]; // legacy: unknown args would abort the editor
    }

    /// <summary>
    /// Starts the editor. Returns null (with <paramref name="error"/> set) when the payload is
    /// broken or the process cannot be started. Never treats process start as project success.
    /// </summary>
    public static EditorSession? Launch(InstallationRecord install, string projectPath, out string? error)
    {
        error = null;
        if (install.IsBroken || !File.Exists(install.EditorExePath))
        {
            error = "launch.error.brokenInstall";
            return null;
        }

        bool hubAware = install.HubProtocolVersion >= 1;
        string sessionId = Guid.NewGuid().ToString("N");
        string eventsFile = HubSessionProxy.EventsFilePathFor(sessionId);

        try
        {
            // Reset any stale event file from a recycled session id (128-bit — practically impossible).
            if (File.Exists(eventsFile)) File.Delete(eventsFile);
            if (hubAware)
                Directory.CreateDirectory(Path.GetDirectoryName(eventsFile)!);

            var startInfo = new ProcessStartInfo
            {
                FileName = install.EditorExePath,
                UseShellExecute = false,
                WorkingDirectory = projectPath,
            };
            // Sandbox seam: keep the editor's session events inside the same data root XHub
            // watches (--data-dir acceptance runs never read/write the real profile).
            startInfo.EnvironmentVariables["XENGINE_XHUB_DATA_DIR"] = AppPaths.Root;
            foreach (string arg in BuildArgs(install, projectPath, sessionId))
                startInfo.ArgumentList.Add(arg);

            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "launch.error.startFailed";
                return null;
            }

            DateTime startTime = TryGetStartTime(process) ?? DateTime.UtcNow;
            HubLog.Info(
                $"Launched editor session {sessionId} (pid {process.Id}, hub {(hubAware ? install.HubProtocolVersion : 0)}) for {projectPath}");
            return new EditorSession(sessionId, projectPath, install, process.Id, startTime, eventsFile, hubAware);
        }
        catch (Exception ex)
        {
            HubLog.Error("Editor launch failed.", ex);
            error = "launch.error.startFailed";
            return null;
        }
    }

    /// <summary>Process start time guards against PID reuse (§4.4).</summary>
    public static DateTime? TryGetStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }
}

/// <summary>
/// Isolation shim over the editor's event-file naming so XHub tests never need the editor
/// assembly: identical path scheme as XEngine.Editor.Projects.HubSession (documented contract).
/// </summary>
public static class HubSessionProxy
{
    public static string SessionsDirectory =>
        Path.Combine(AppPaths.Root, "sessions");

    public static string EventsFilePathFor(string sessionId) =>
        Path.Combine(SessionsDirectory, $"{sessionId}.ndjson");
}
