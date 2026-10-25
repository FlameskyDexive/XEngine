// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text;

using XEngine.Runtime;
namespace XEngine.Editor.Core;

/// <summary>
/// Persistent editor log — XEngine's counterpart of Unity's <c>Editor.log</c>. Every
/// <see cref="Debug.OnLog"/> entry (info/warning/error, with stack trace) is appended to a
/// well-known file on disk so an AI agent (or a human) can read it after the fact without a
/// live MCP connection — including after a crash, where the in-memory ring buffer is gone.
///
/// <para>File locations (in priority order):</para>
/// <list type="bullet">
/// <item><c>{project}/Logs/Editor.log</c> — per-project session log (preferred; the project's
/// Logs/ directory already holds Crashes/, so all diagnostic artifacts live together).</item>
/// <item><c>%LOCALAPPDATA%/XEngine/Editor.log</c> — global fallback when no project is open.</item>
/// </list>
///
/// <para>Rotation: on editor start, a previous session's log is renamed to
/// <c>Editor-prev.log</c> (only the immediately-previous session is kept, matching Unity's
/// <c>Editor-prev.log</c> convention). The file is capped at <see cref="MaxLogBytes"/> — when
/// exceeded, the oldest half is truncated to keep the file bounded.</para>
///
/// <para>AI access:</para>
/// <list type="bullet">
/// <item>Offline: <c>GetLastLines(count)</c> / <c>GetLastErrorLines(count)</c> from any process,
/// or simply <c>type {project}\Logs\Editor.log</c> on the command line.</item>
/// <item>Live MCP: the <c>runtime_logs</c> tool reads from the same ring buffer this class
/// feeds, so the file and the tool always agree.</item>
/// </list>
/// </summary>
public static class EditorLog
{
    /// <summary>Hard cap on the log file size; the oldest half is dropped when exceeded.</summary>
    private const long MaxLogBytes = 16 * 1024 * 1024; // 16 MB

    /// <summary>In-memory ring buffer for the live <c>runtime_logs</c> MCP tool. Keeps the most
    /// recent entries so the tool can filter by level without touching the file.</summary>
    private static readonly LogRingBuffer _ring = new(capacity: 2000);

    private static readonly object _fileLock = new();
    private static string? _logPath;
    private static StreamWriter? _writer;
    private static bool _initialized;

    /// <summary>The active log file path, or null before Initialize / after Shutdown.</summary>
    public static string? LogPath => _logPath;

    /// <summary>Wire <see cref="Debug.OnLog"/> to the file writer. Call once at editor startup
    /// (before any subsystem that logs). Safe to call multiple times.</summary>
    public static void Initialize(string? projectRootPath)
    {
        if (_initialized) return;
        _initialized = true;
        lock (_fileLock) { _ring.Clear(); }

        try
        {
            // --project accepts either a root directory or its .xengine marker.
            string? root = projectRootPath;
            if (!string.IsNullOrWhiteSpace(root) && File.Exists(root) &&
                root.EndsWith(".xengine", StringComparison.OrdinalIgnoreCase))
                root = Path.GetDirectoryName(Path.GetFullPath(root));
            string dir = !string.IsNullOrWhiteSpace(root)
                ? Path.Combine(Path.GetFullPath(root), "Logs")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "XEngine");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "Editor.log");

            // Rotate: keep the previous session as Editor-prev.log.
            string prev = Path.Combine(dir, "Editor-prev.log");
            if (File.Exists(_logPath))
            {
                File.Delete(prev);
                File.Move(_logPath, prev);
            }

            _writer = new StreamWriter(new FileStream(_logPath, FileMode.Create, FileAccess.Write,
                FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };

            WriteLine("info", $"Editor log started — {_logPath}");
            WriteLine("info", $"Process: {Environment.ProcessPath}");
            WriteLine("info", $"OS: {Environment.OSVersion} — .NET {Environment.Version}");
            WriteLine("info", $"Project: {root ?? "(none)"}");
        }
        catch
        {
            // Logging must never take the editor down; a failed file just means no log.
            try { _writer?.Dispose(); } catch { /* best-effort */ }
            _writer = null;
            _logPath = null;
        }

        Debug.OnLog += OnLog;
    }

    /// <summary>
    /// Hot-reload: the engine's <c>[OnAssemblyUnload]</c> nulls <see cref="Debug.OnLog"/> so no
    /// script-bound subscriber can pin the collectible context, which also drops this file
    /// writer. Without resubscribing, <c>Logs/Editor.log</c> stopped at "Stopping AI server for
    /// script reload" after the first successful hot-reload and every later message (including
    /// "Hot-reload successful!") only reached stdout. Same pattern as the Console panel.
    /// </summary>
    [OnAssemblyLoad]
    private static void ResubscribeAfterReload()
    {
        if (!_initialized) return;
        Debug.OnLog -= OnLog;
        Debug.OnLog += OnLog;
    }

    /// <summary>Flush and close the log file. Call on editor exit (after the last log entry).</summary>
    public static void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;
        Debug.OnLog -= OnLog;
        lock (_fileLock)
        {
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [info] Editor log closed.");
            _writer?.Dispose();
            _writer = null;
            _logPath = null;
        }
    }

    /// <summary>The most recent entries from the in-memory ring buffer, optionally filtered by
    /// the minimum severity. Used by the <c>runtime_logs</c> MCP tool.</summary>
    public static (int returned, int totalBuffered, string[] lines) GetRecent(
        int count = 50, string? level = null)
    {
        var (returned, total, entries) = GetRecentEntries(count, level);
        var lines = new string[returned];
        for (int i = 0; i < returned; i++)
        {
            var entry = entries[i];
            lines[i] = $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff} [{LevelName(entry.Severity)}] {entry.Message}";
            if (!string.IsNullOrEmpty(entry.StackTrace))
                lines[i] += $"\n  at {entry.StackTrace}";
        }
        return (returned, total, lines);
    }

    internal readonly record struct RecentEntry(
        LogSeverity Severity, string Message, string? StackTrace, DateTimeOffset TimestampUtc);

    internal static (int returned, int totalBuffered, RecentEntry[] entries) GetRecentEntries(
        int count = 50, string? level = null)
    {
        lock (_fileLock) { return _ring.GetRecent(Math.Max(0, count), level); }
    }

    private static string LevelName(LogSeverity severity) => severity switch
    {
        LogSeverity.Warning => "warning",
        LogSeverity.Error => "error",
        LogSeverity.Exception => "exception",
        LogSeverity.Success => "success",
        _ => "info",
    };

    private static void OnLog(string message, DebugStackTrace? stackTrace, LogSeverity severity)
    {
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        string line = $"{timestamp.ToLocalTime():HH:mm:ss.fff} [{LevelName(severity)}] {message}";
        string frame0 = stackTrace?.StackFrames is { Length: > 0 } frames
            ? (frames[0].Method ?? "?") + " (" + frames[0].FileName + ":" + frames[0].Line + ")"
            : "";
        if (!string.IsNullOrEmpty(frame0) && severity >= LogSeverity.Warning)
            line += $"\n  at {frame0}";

        lock (_fileLock)
        {
            _writer?.WriteLine(line);
            _ring.Add(new RecentEntry(severity, message,
                severity >= LogSeverity.Warning && !string.IsNullOrEmpty(frame0) ? frame0 : null, timestamp));
        }

        // Bound the file: if it exceeds the cap, drop the oldest half.
        if (_writer != null && (_writer.BaseStream.Length & 0xFFF) == 0) // check every ~4KB
        {
            try
            {
                if (_writer.BaseStream.Length > MaxLogBytes)
                    TruncateOldestHalf();
            }
            catch { /* best-effort */ }
        }
    }

    private static void WriteLine(string level, string message)
    {
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        string line = $"{timestamp.ToLocalTime():HH:mm:ss.fff} [{level}] {message}";
        lock (_fileLock)
        {
            _writer?.WriteLine(line);
            _ring.Add(new RecentEntry(LogSeverity.Normal, message, null, timestamp));
        }
    }

    private static void TruncateOldestHalf()
    {
        // Read the file, keep the second half, rewrite. Inefficient but bounded (runs at most
        // once per ~8MB of log output).
        _writer?.Flush();
        string? path = _logPath;
        if (path == null || !File.Exists(path)) return;

        var lines = File.ReadAllLines(path);
        int keepFrom = lines.Length / 2;
        _writer?.Dispose();
        _writer = null;

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            for (int i = keepFrom; i < lines.Length; i++)
                sw.WriteLine(lines[i]);

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [info] --- log truncated (oldest half dropped) ---");
    }

    /// <summary>Fixed-capacity ring. Access is serialized with the file writer.</summary>
    private sealed class LogRingBuffer(int capacity)
    {
        private readonly RecentEntry[] _entries = new RecentEntry[capacity];
        private int _head; // next write position
        private int _count;

        public void Clear()
        {
            Array.Clear(_entries);
            _head = 0;
            _count = 0;
        }

        public void Add(RecentEntry entry)
        {
            _entries[_head] = entry;
            _head = (_head + 1) % _entries.Length;
            if (_count < _entries.Length) _count++;
        }

        public (int returned, int total, RecentEntry[] entries) GetRecent(int count, string? levelFilter)
        {
            int minimum = levelFilter?.Trim().ToLowerInvariant() switch
            {
                "success" => (int)LogSeverity.Success,
                "normal" or "info" => (int)LogSeverity.Normal,
                "warning" or "warn" => (int)LogSeverity.Warning,
                "error" => (int)LogSeverity.Error,
                "exception" => (int)LogSeverity.Exception,
                _ => 0,
            };

            var result = new System.Collections.Generic.List<RecentEntry>(Math.Min(count, _count));
            for (int i = 0; i < _count && result.Count < count; i++)
            {
                int idx = (_head - 1 - i + _entries.Length * 2) % _entries.Length;
                var e = _entries[idx];
                if ((int)e.Severity < minimum) continue;
                result.Add(e);
            }
            return (result.Count, _count, result.ToArray());
        }
    }
}
