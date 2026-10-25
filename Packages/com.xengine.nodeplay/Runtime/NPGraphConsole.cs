using System;
using System.Collections.Generic;

namespace XEngine.NodePlay;

/// <summary>
/// Graph-scoped console ring (the reference design's Graph Console): captures runner lifecycle,
/// task bind errors and explicit Warn/Error calls with the offending node id so a click can focus
/// the broken node. Runtime-owned ring; the editor panel renders the tail.
/// </summary>
public static class NPGraphConsole
{
    public sealed record Entry(DateTime Time, string GraphName, string Severity, string Message, string? NodeId);

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static bool _hooked;

    public static IReadOnlyList<Entry> All
    {
        get { lock (Gate) return Entries.ToArray(); }
    }

    public static event Action<Entry>? OnEntry;

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
    }

    public static void Log(NPGraphAsset? graph, string severity, string message, string? nodeId = null)
    {
        var entry = new Entry(DateTime.Now, graph?.GraphName ?? "?", severity, message, nodeId);
        lock (Gate)
        {
            Entries.Add(entry);
            if (Entries.Count > 512)
                Entries.RemoveRange(0, Entries.Count - 512);
        }

        OnEntry?.Invoke(entry);
    }

    public static void Warn(NPGraphAsset? graph, string message, string? nodeId = null)
        => Log(graph, "warn", message, nodeId);

    /// <summary>Wire automatic sources (idempotent): event dispatch feed.</summary>
    public static void Install()
    {
        if (_hooked)
            return;
        _hooked = true;
        NPEvents.OnEventDispatched += (runner, name, value) =>
            Log(runner?.RunningGraph, "event", $"event '{name}'" + (value == null ? "" : " = " + value), null);
    }
}
