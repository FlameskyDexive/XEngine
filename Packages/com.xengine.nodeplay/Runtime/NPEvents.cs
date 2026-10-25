using System;
using System.Collections.Generic;

namespace XEngine.NodePlay;

/// <summary>
/// Minimal named-event plumbing between graphs/owners — the equivalent of the reference design's
/// custom-event surface. Per-runner delivery is routed through <see cref="NPRunner.SendEvent"/>;
/// broadcast goes through <see cref="SendGlobalEvent"/> and fans out over every running owner.
/// </summary>
public static class NPEvents
{
    /// <summary>Observation hook for editors/console (M13): every event that was dispatched.</summary>
    public static event Action<NPRunner?, string, object?>? OnEventDispatched;

    /// <summary>Broadcast to all running owners.</summary>
    public static void SendGlobalEvent(string name, object? value = null)
    {
        OnEventDispatched?.Invoke(null, name, value);
        NPRunner.Broadcast(name, value);
    }

    internal static void Notify(NPRunner sender, string name, object? value)
        => OnEventDispatched?.Invoke(sender, name, value);
}
