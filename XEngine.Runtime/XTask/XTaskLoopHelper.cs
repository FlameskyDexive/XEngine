// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Async.Internal;
using XEngine.Runtime;

namespace XEngine.Async;

// NOTE: XTaskLoopHelper is the bridge between Game.SimulationStep and the frame-driven XTask
// operators. It owns one ContinuationQueue (one-shot Action continuations) and one FrameLoopRunner
// (polling IFrameLoopItems) per FrameTiming phase. Game calls Initialize once on the main thread at
// startup, then Tick(timing) at each phase. The Tick path must be allocation-free on the steady
// state (empty queues early-out; the only allocation is buffer growth in Add/Enqueue, which is on
// the user/operator path, not the per-frame pump).

/// <summary>
/// Static hub that pumps XTask continuations and polling items once per <see cref="FrameTiming"/>
/// phase. Wired into <c>Game.SimulationStep</c>; not for direct use by gameplay code.
/// </summary>
public static class XTaskLoopHelper
{
    private const int PhaseCount = 4; // FixedUpdate, Update, LateUpdate, EndOfFrame

    private static readonly ContinuationQueue[] s_continuationQueues = new ContinuationQueue[PhaseCount];
    private static readonly FrameLoopRunner[] s_frameLoopRunners = new FrameLoopRunner[PhaseCount];

    public static int MainThreadId { get; private set; }

    /// <summary>True when <see cref="Initialize"/> has captured the main thread id.</summary>
    public static bool IsInitialized { get; private set; }

    static XTaskLoopHelper()
    {
        for (int i = 0; i < PhaseCount; i++)
        {
            s_continuationQueues[i] = new ContinuationQueue();
            s_frameLoopRunners[i] = new FrameLoopRunner();
        }
    }

    /// <summary>Capture the main-thread identity. Call once from the main thread during engine startup.</summary>
    public static void Initialize(int mainThreadId)
    {
        MainThreadId = mainThreadId;
        IsInitialized = true;
    }

    /// <summary>Enqueue a one-shot continuation to fire on the next <see cref="Tick"/> of <paramref name="timing"/>.</summary>
    internal static void AddContinuation(FrameTiming timing, Action continuation)
        => s_continuationQueues[(int)timing].Enqueue(continuation);

    /// <summary>Register a polling item that runs <see cref="IFrameLoopItem.MoveNext"/> each frame at <paramref name="timing"/>.</summary>
    internal static void AddAction(FrameTiming timing, IFrameLoopItem item)
        => s_frameLoopRunners[(int)timing].Add(item);

    /// <summary>
    /// Advance one phase: drain one-shot continuations then poll all active items. Called from
    /// <c>Game.SimulationStep</c> at the matching point in the frame. Cheap no-op when both buffers
    /// for the phase are empty.
    /// </summary>
    public static void Tick(FrameTiming timing)
    {
        int i = (int)timing;
        s_continuationQueues[i].Run();
        s_frameLoopRunners[i].Run();
    }

    /// <summary>
    /// Hot-reload: drop every pending continuation and polling item that references the collectible
    /// script assembly context. Without this, any gameplay coroutine that was suspended at
    /// <c>await XTask.Delay/NextFrame/WaitUntil/...</c> pins the whole AssemblyLoadContext and the
    /// unload verification fails, forcing an editor restart on every reload.
    /// </summary>
    [OnAssemblyUnload]
    internal static void ClearForReload()
    {
        int dropped = 0;
        for (int i = 0; i < PhaseCount; i++)
        {
            dropped += s_continuationQueues[i].RemoveCollectibleBound();
            dropped += s_frameLoopRunners[i].RemoveCollectibleBound();
        }

        if (dropped > 0)
        {
            Debug.Log($"[XTaskLoopHelper] Dropped {dropped} pending XTask operators bound to unloaded script assemblies.");
        }
    }

    /// <summary>Total queued continuations and polling items across all phases (diagnostics/tests only).</summary>
    internal static int PendingItemCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < PhaseCount; i++)
            {
                total += s_continuationQueues[i].Count + s_frameLoopRunners[i].Count;
            }
            return total;
        }
    }
}
