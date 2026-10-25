using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace XEngine.XTS;

/// <summary>
/// One shared script environment for all <see cref="XtsBehaviour"/> instances
/// in the process. Behaviours acquire it on enable and release on
/// disable/destroy; the environment is created for the first acquirer and
/// destroyed when the last one releases. All users are on the main thread.
/// </summary>
public static class XtsSharedRuntime
{
    private static XtsRuntime? _runtime;
    private static int _refCount;
    private static readonly object Gate = new();

    public static XtsRuntime? Current => _runtime;

    public static XtsRuntime Acquire()
    {
        lock (Gate)
        {
            if (_runtime == null)
                _runtime = XtsRuntime.Create(new XtsRuntimeOptions
                {
                    // gameplay budget defaults; per-content values arrive with
                    // the content manifest (N8)
                    MemoryLimitBytes = 64 << 20,
                });
            Interlocked.Increment(ref _refCount);
            return _runtime;
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (Interlocked.Decrement(ref _refCount) <= 0)
            {
                _runtime?.Dispose();
                _runtime = null;
                _refCount = 0;
            }
        }
    }

    /// <summary>
    /// Create a fresh environment WITHOUT replacing the current one — used by
    /// the safe-point rebuild to prepare and health-check a replacement
    /// before committing (<see cref="SwapTo"/>).
    /// </summary>
    public static XtsRuntime CreateReplacement()
    {
        lock (Gate)
        {
            // XtsRuntime.Create installs the behaviour prelude
            return XtsRuntime.Create(new XtsRuntimeOptions
            {
                MemoryLimitBytes = 64 << 20,
            });
        }
    }

    /// <summary>Commit a replacement environment; returns the replaced one
    /// (the caller disposes it after the swap).</summary>
    public static XtsRuntime? SwapTo(XtsRuntime replacement)
    {
        lock (Gate)
        {
            var old = _runtime;
            _runtime = replacement;
            return old;
        }
    }

    /// <summary>Test isolation: drop the shared environment entirely.</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            _runtime?.Dispose();
            _runtime = null;
            _refCount = 0;
        }
    }
}
