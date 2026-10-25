// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

namespace XEngine.Async;

// NOTE: XEngine's main thread installs no ambient SynchronizationContext by default, so standard
// `await someTask` continuations run on whatever thread completed the task (often the threadpool).
// This class is an opt-in marshaler: Install() sets it as Current, after which Post (and therefore
// the ambient continuation posting done by Task/async machinery) lands work on the next Tick of the
// chosen FrameTiming — i.e. back on the main thread. Uninstall() restores the previous context.
//
// XTask's own operators (Delay/WaitUntil/etc.) do NOT depend on the ambient context — they route
// through the explicit FrameTiming queues — so Install() is strictly opt-in and does not change the
// behavior of existing Task-based code unless that code captures SynchronizationContext.Current.

/// <summary>
/// A <see cref="SynchronizationContext"/> that marshals <see cref="Post"/> onto the engine's frame
/// loop. Opt-in via <see cref="Install(FrameTiming)"/>; see the type-level note for caveats.
/// </summary>
public sealed class XTaskSynchronizationContext : SynchronizationContext
{
    /// <summary>The currently installed instance, or null if none.</summary>
    public static XTaskSynchronizationContext? Installed { get; private set; }

    private readonly FrameTiming _timing;

    public XTaskSynchronizationContext(FrameTiming timing = FrameTiming.Update)
    {
        _timing = timing;
    }

    /// <summary>The phase at which posted work runs.</summary>
    public FrameTiming Timing => _timing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (d == null)
        {
            throw new ArgumentNullException(nameof(d));
        }

        // Box the callback+state pair once into an Action and hand it to the continuation queue. The
        // closure allocates here, but Post is the marshaling path (not a per-frame hot path).
        XTaskLoopHelper.AddContinuation(_timing, () => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        // Synchronous Send can only run inline if already on the main thread; blocking the loop to
        // wait for a frame would deadlock, so we refuse otherwise.
        if (XTaskLoopHelper.IsInitialized && Environment.CurrentManagedThreadId == XTaskLoopHelper.MainThreadId)
        {
            d(state);
            return;
        }

        throw new InvalidOperationException(
            "XTaskSynchronizationContext.Send cannot block off the main thread; use Post instead.");
    }

    /// <summary>Install this context as <see cref="SynchronizationContext.Current"/> on the calling
    /// (main) thread. Returns the previously-installed context (typically null) for restoration.</summary>
    public static SynchronizationContext? Install(FrameTiming timing = FrameTiming.Update)
    {
        SynchronizationContext? previous = Current;
        var ctx = new XTaskSynchronizationContext(timing);
        SetSynchronizationContext(ctx);
        Installed = ctx;
        return previous;
    }

    /// <summary>Remove a previously-installed context (restores a null ambient context).</summary>
    public static void Uninstall()
    {
        SetSynchronizationContext(null);
        Installed = null;
    }
}
