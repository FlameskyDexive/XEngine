// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;

using XEngine.Editor.Core;
using XEngine.Runtime;

namespace XEngine.Editor.Core;

/// <summary>
/// ★ Layer 0 (A4) — A single in-memory change stream that aggregates editor mutation signals, so a
/// subscriber (the in-process MCP server's "scene changed" tool in B3) doesn't have to wire each
/// source individually. Today it re-publishes <see cref="Undo.OnPropertyChanged"/> (the property-edit
/// path with structured deltas). Structural create/delete deltas will be added here once those paths
/// emit serializable deltas.
///
/// <para>Subscribers receive events on the thread that raised them (the editor main loop); off-thread
/// consumers must marshal themselves. The queue is for optional buffered consumption.</para>
/// </summary>
public static class ChangeBus
{
    /// <summary>Raised for every editor change (property edit today; structural later).</summary>
    public static event Action<Undo.PropertyChangedEventArgs>? OnChange;

    [OnAssemblyUnload]
    internal static void ClearForReload() => OnChange = null;

    private static readonly ConcurrentQueue<Undo.PropertyChangedEventArgs> _recent = new();
    private const int MaxRecent = 256;

    static ChangeBus()
    {
        // Re-publish the Undo property-change stream. One wiring point; B3 subscribes to ChangeBus,
        // not to Undo directly, so future sources only need to feed ChangeBus.
        Undo.OnPropertyChanged += Relay;
    }

    private static void Relay(Undo.PropertyChangedEventArgs e)
    {
        _recent.Enqueue(e);
        while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }
        OnChange?.Invoke(e);
    }

    /// <summary>Drain a snapshot of the most recent changes (newest last), without subscribing.</summary>
    public static System.Collections.Generic.IReadOnlyList<Undo.PropertyChangedEventArgs> DrainRecent()
        => _recent.ToArray();
}
