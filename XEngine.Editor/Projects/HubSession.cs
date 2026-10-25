using System;
using System.IO;
using System.Text.Json;

namespace XEngine.Editor.Projects;

/// <summary>
/// Editor-side half of the XHub session protocol (XHub plan §4.3): when launched with
/// <c>--hub-session &lt;id&gt;</c>, lifecycle events are appended as NDJSON lines to
/// <c>%LOCALAPPDATA%\XEngine\XHub\sessions\&lt;id&gt;.ndjson</c> — the file XHub watches.
/// Every event carries the protocol version, session id, type and a data payload; emission is
/// best-effort and never throws (a hub-less launch must not be affected in any way).
/// </summary>
public static class HubSession
{
    /// <summary>Bumped on breaking protocol changes; XHub gates on this value.</summary>
    public const int ProtocolVersion = 1;

    public const string SessionStarted = "sessionStarted";
    public const string ProjectReady = "projectReady";
    public const string OpenFailed = "openFailed";
    public const string LockConflict = "lockConflict";
    public const string Closing = "closing";

    /// <summary>Stable error codes carried in failure events (§4.3).</summary>
    public const string CodeProjectLocked = "PROJECT_LOCKED";
    public const string CodeProjectOpenFailed = "PROJECT_OPEN_FAILED";

    private static string? _sessionId;
    private static int _pid = Environment.ProcessId;

    /// <summary>Activates event emission for this process. No-op when already active.</summary>
    public static void Activate(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        _sessionId = sessionId;
        Emit(SessionStarted, new { pid = _pid });
    }

    public static bool IsActive => _sessionId != null;

    public static string EventsFilePathFor(string sessionId)
    {
        // XHub passes its (possibly sandboxed) data root per launch; production leaves it unset
        // and the documented %LOCALAPPDATA% location is used.
        string hubRoot = Environment.GetEnvironmentVariable("XENGINE_XHUB_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XEngine", "XHub");
        string root = Path.Combine(hubRoot, "sessions");
        return Path.Combine(root, $"{sessionId}.ndjson");
    }

    /// <summary>Appends one event. Never throws; a failing write is silently dropped.</summary>
    public static void Emit(string type, object? data = null, string code = "")
    {
        string? sessionId = _sessionId;
        if (sessionId == null) return;
        try
        {
            string path = EventsFilePathFor(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var envelope = new
            {
                protocolVersion = ProtocolVersion,
                sessionId,
                pid = _pid,
                type,
                code,
                data,
                ts = DateTime.UtcNow.ToString("o"),
            };
            File.AppendAllText(path, JsonSerializer.Serialize(envelope) + Environment.NewLine);
        }
        catch
        {
            // Hub feedback is advisory — disk/permission problems must never break the editor.
        }
    }

    /// <summary>True when the exception is the project-directory lease conflict (§4.4).</summary>
    public static bool IsLeaseConflict(Exception ex) =>
        ex.Message.Contains("项目目录已被另一个编辑器", StringComparison.Ordinal);
}
