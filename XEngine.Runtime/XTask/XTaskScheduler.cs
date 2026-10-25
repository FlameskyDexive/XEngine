// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;

namespace XEngine.Async;

/// <summary>
/// Routes unobserved XTask exceptions. Sources that capture an exception whose result is never
/// fetched (never awaited, or pooled-and-recycled before observation) publish through here so the
/// exception is not silently swallowed. The host should subscribe to surface these in its logger.
/// </summary>
public static class XTaskScheduler
{
    /// <summary>
    /// Invoked when an XTask exception goes unobserved. Default handler is a no-op; the host
    /// application should reassign this to route to its logger.
    /// </summary>
    public static Action<Exception>? UnobservedTaskException;

    static XTaskScheduler()
    {
        UnobservedTaskException = DefaultUnobservedTaskException;
    }

    private static void DefaultUnobservedTaskException(Exception ex)
    {
        // Intentionally a no-op. This layer keeps no hard dependency on a logging subsystem; the
        // host reassigns UnobservedTaskException to route unobserved exceptions to its logger.
    }

    /// <summary>Internal: publish an unobserved exception to <see cref="UnobservedTaskException"/>.</summary>
    internal static void PublishUnobservedTaskException(Exception ex)
    {
        try { UnobservedTaskException?.Invoke(ex); }
        catch { /* swallow handler faults so they never mask the original exception */ }
    }

    /// <summary>
    /// Hot-reload: if the host (or user script) replaced the handler with a delegate bound to the
    /// collectible script context, that delegate pins the whole AssemblyLoadContext. Reset only
    /// collectible-bound handlers; engine/editor handlers survive the reload.
    /// </summary>
    [OnAssemblyUnload]
    internal static void ClearForReload()
    {
        var handler = UnobservedTaskException;
        if (handler != null && IsCollectibleBound(handler))
            UnobservedTaskException = DefaultUnobservedTaskException;
    }

    private static bool IsCollectibleBound(Delegate d)
    {
        if (d.Target != null && d.Target.GetType().Assembly.IsCollectible)
            return true;
        return d.Method.DeclaringType?.Assembly.IsCollectible == true;
    }
}
