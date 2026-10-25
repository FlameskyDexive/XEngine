using System;
using System.Text.Json;

namespace XEngine.XTS;

/// <summary>
/// Drives pending promise work for a script environment once per frame:
/// fires due timers, then runs up to <paramref name="maxJobs"/> promise jobs
/// under the runtime's execution deadline. Install once per environment;
/// call <see cref="Pump"/> from the frame loop (engine Update timing).
///
/// Cancellation contract: timers created through <c>__xts_delay(ms, signal)</c>
/// reject with "aborted" when the signal is aborted — behaviour destroy
/// aborts its own signal, so late callbacks belonging to a destroyed
/// behaviour never run.
/// </summary>
public sealed class XtsFramePump
{
    private readonly XtsRuntime _runtime;
    private int _maxJobsPerFrame;

    public XtsFramePump(XtsRuntime runtime, int maxJobsPerFrame = 64)
    {
        _runtime = runtime;
        _maxJobsPerFrame = maxJobsPerFrame;
        // install the timer helpers once per environment
        var status = runtime.EvaluateExpression("""
            globalThis.__xts_timers = [];
            globalThis.__xts_delay = function (ms, signal) {
                return new Promise(function (resolve, reject) {
                    globalThis.__xts_timers.push({ due: Date.now() + ms, resolve: resolve, reject: reject, signal: signal || null });
                });
            };
            globalThis.__xts_pump_timers = function () {
                var now = Date.now();
                var remaining = [];
                for (var i = 0; i < globalThis.__xts_timers.length; i++) {
                    var t = globalThis.__xts_timers[i];
                    if (t.signal && t.signal.aborted) { t.reject(new Error('aborted')); continue; }
                    if (now >= t.due) { t.resolve(); continue; }
                    remaining.push(t);
                }
                globalThis.__xts_timers = remaining;
            };
            globalThis.__xts_make_signal = function () { return { aborted: false }; };
            """, out _);
        if (status != XtsStatus.Ok)
            throw new InvalidOperationException($"xts timer prelude failed ({status}): {runtime.LastError}");
    }

    /// <summary>Maximum promise jobs executed per pump.</summary>
    public int MaxJobsPerFrame
    {
        get => _maxJobsPerFrame;
        set => _maxJobsPerFrame = value;
    }

    /// <summary>Fire due timers, then run pending promise jobs. Returns the
    /// number of jobs executed this frame.</summary>
    public XtsStatus Pump(out uint jobsExecuted)
    {
        jobsExecuted = 0;
        var status = _runtime.EvaluateExpression("__xts_pump_timers()", out _);
        if (status != XtsStatus.Ok)
            return status;
        return _runtime.ExecutePendingJobs((uint)Math.Max(1, _maxJobsPerFrame), out jobsExecuted);
    }

    /// <summary>
    /// Abort a behaviour's destroy signal: pending delay timers tied to it
    /// reject and their continuations observe the cancellation.
    /// </summary>
    public static void AbortBehaviourSignal(XtsRuntime runtime, int behaviourHandle)
    {
        runtime.EvaluateExpression(
            $"__xts_bhv_call({behaviourHandle}, '__abort_signal')", out _);
    }
}
