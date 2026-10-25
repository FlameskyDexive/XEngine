using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using XEngine.Runtime;

namespace XEngine.Editor.Core;

/// <summary>
/// Records named editor-startup phase durations on a process-start-relative timeline and logs
/// one summary line after the first presented frame. Phases self-report from inside the phase
/// methods so every entry path (GUI, --serve, build mode, --refresh-assets) is covered.
/// </summary>
public static class StartupTrace
{
    private readonly record struct Phase(string Name, double StartMs, double DurationMs);

    private static readonly List<Phase> s_phases = new(16);
    private static readonly object s_lock = new();
    private static readonly long s_processStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double s_managedStartOffsetMs = ReadManagedStartOffsetMs();
    private static bool s_summaryLogged;

    private static double ReadManagedStartOffsetMs()
    {
        try
        {
            return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        }
        catch
        {
            return 0;
        }
    }

    private static double NowMs()
        => s_managedStartOffsetMs
           + (Stopwatch.GetTimestamp() - s_processStartTimestamp) * 1_000.0 / Stopwatch.Frequency;

    /// <summary>Times a startup phase; safe to call from any entry path, including nested phases.</summary>
    public static T Time<T>(string name, Func<T> phase)
    {
        double start = NowMs();
        long t0 = Stopwatch.GetTimestamp();
        T result = phase();
        Record(name, start, (Stopwatch.GetTimestamp() - t0) * 1_000.0 / Stopwatch.Frequency);
        return result;
    }

    public static void Time(string name, Action phase)
    {
        double start = NowMs();
        long t0 = Stopwatch.GetTimestamp();
        phase();
        Record(name, start, (Stopwatch.GetTimestamp() - t0) * 1_000.0 / Stopwatch.Frequency);
    }

    public static void Record(string name, double startMs, double durationMs)
    {
        lock (s_lock)
        {
            if (!s_summaryLogged)
                s_phases.Add(new Phase(name, startMs, durationMs));
        }
    }

    /// <summary>
    /// Logs the phase summary once, stamped with the first-present time. Call after the first
    /// frame reaches the screen; later calls are no-ops.
    /// </summary>
    public static void LogSummaryOnce()
    {
        lock (s_lock)
        {
            if (s_summaryLogged)
                return;
            s_summaryLogged = true;

            var sb = new StringBuilder(256);
            sb.Append("[Startup] first frame at ")
                .Append(NowMs().ToString("F0"))
                .Append(" ms (managed entry +")
                .Append(s_managedStartOffsetMs.ToString("F0"))
                .Append(" ms)");
            foreach (Phase phase in s_phases)
            {
                sb.Append("; ").Append(phase.Name)
                    .Append(' ').Append(phase.DurationMs.ToString("F0"))
                    .Append(" ms @").Append(phase.StartMs.ToString("F0"));
            }
            XEngine.Runtime.Debug.Log(sb.ToString());
        }
    }
}
