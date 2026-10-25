using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using XHub.Platform;

namespace XHub.Infrastructure;

public enum HubLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// File logger for XHub: one append-only <c>hub-YYYYMMDD.log</c> per local day under
/// <c>logs/</c>, safe to call from any thread, capped retention. Never throws — logging
/// must not take the hub down.
/// </summary>
public static class HubLog
{
    private const int MaxLogFiles = 10;
    private static readonly BlockingCollection<PendingEntry>? _queue = StartWorker();

    private static HubLogLevel _minLevel = HubLogLevel.Info;

    private readonly record struct PendingEntry(HubLogLevel Level, string Message);

    /// <summary>Enable Debug lines. Call before the first log write for it to matter.</summary>
    public static void SetVerbose(bool verbose) => _minLevel = verbose ? HubLogLevel.Debug : HubLogLevel.Info;

    public static void Debug(string message) => Write(HubLogLevel.Debug, message);
    public static void Info(string message) => Write(HubLogLevel.Info, message);
    public static void Warning(string message) => Write(HubLogLevel.Warning, message);
    public static void Error(string message, Exception? exception = null)
    {
        if (exception == null)
            Write(HubLogLevel.Error, message);
        else
            Write(HubLogLevel.Error, $"{message}{Environment.NewLine}{exception}");
    }

    public static void Write(HubLogLevel level, string message)
    {
        if (_queue == null || level < _minLevel) return;
        try { _queue.TryAdd(new PendingEntry(level, message), millisecondsTimeout: 100); }
        catch { /* queue completed during shutdown — drop the line */ }
    }

    private static BlockingCollection<PendingEntry> StartWorker()
    {
        var queue = new BlockingCollection<PendingEntry>(boundedCapacity: 4096);
        var worker = new Thread(() => Drain(queue))
        {
            IsBackground = true,
            Name = "XHubLog",
        };
        worker.Start();
        return queue;
    }

    private static void Drain(BlockingCollection<PendingEntry> queue)
    {
        foreach (PendingEntry entry in queue.GetConsumingEnumerable())
        {
            try
            {
                string dir = AppPaths.LogsDir;
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"hub-{DateTime.Now:yyyyMMdd}.log");
                string line = $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [{entry.Level.ToString().ToUpperInvariant()}] {entry.Message}{Environment.NewLine}";
                File.AppendAllText(file, line);
            }
            catch { /* a failed log write is never fatal */ }
        }
    }

    /// <summary>Best-effort retention: delete the oldest hub logs beyond the cap.</summary>
    public static void PruneOldLogs(string? directory = null)
    {
        try
        {
            string dir = directory ?? AppPaths.LogsDir;
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir, "hub-*.log");
            if (files.Length <= MaxLogFiles) return;
            Array.Sort(files);
            for (int i = 0; i < files.Length - MaxLogFiles; i++)
                File.Delete(files[i]);
        }
        catch { /* retention is advisory */ }
    }
}
