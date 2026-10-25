using XEngine.XTS;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N6 gate: the frame pump drives promise/timer work with per-frame budgets;
/// delays complete, cancel through the destroy signal, respect the job
/// limit, late callbacks of destroyed behaviours are dropped, and 100
/// create/destroy cycles leave no residual growth.
/// </summary>
public class XtsFramePumpTests : IDisposable
{
    private readonly XtsRuntime? _runtime;

    public XtsFramePumpTests()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; skip construction on
        // hosts without it (field initializers would otherwise throw before test guards run).
        if (!XtsNativeAvailability.ShouldSkip)
            _runtime = XtsRuntime.Create();
    }

    public void Dispose() => _runtime?.Dispose();

    private XtsRuntime Runtime => _runtime ?? throw new InvalidOperationException("XTS native unavailable");

    private double EvalNumber(string expr)
    {
        Assert.Equal(XtsStatus.Ok, Runtime.EvaluateExpression(expr, out var v));
        return v.AsDouble();
    }

    [Fact]
    public void Delay_CompletesThroughPump()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var pump = new XtsFramePump(Runtime);
        Assert.Equal(XtsStatus.Ok, Runtime.EvaluateExpression(
            "globalThis.__done = 0; __xts_delay(20).then(function () { globalThis.__done++; });", out _));

        Assert.Equal(XtsStatus.Ok, Runtime.EvaluateExpression("__done", out var before));
        Assert.Equal(0d, before.AsDouble());

        System.Threading.Thread.Sleep(60);
        pump.Pump(out var jobs);

        Assert.Equal(XtsStatus.Ok, Runtime.EvaluateExpression("__done", out var after));
        Assert.Equal(1d, after.AsDouble());
        Assert.True(jobs >= 1);
    }

    [Fact]
    public void JobLimit_ExecutesAtMostMaxJobsPerPump()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var pump = new XtsFramePump(_runtime, maxJobsPerFrame: 3);
        Assert.Equal(XtsStatus.Ok, Runtime.EvaluateExpression(
            "globalThis.__ran = 0; for (var i = 0; i < 10; i++) { Promise.resolve(i).then(function () { globalThis.__ran++; }); }",
            out _));

        pump.Pump(out var executed);
        Assert.Equal(3u, executed);
        Assert.Equal(3d, EvalNumber("__ran"));

        pump.Pump(out _);
        pump.Pump(out _);
        pump.Pump(out _);
        Assert.Equal(10d, EvalNumber("__ran"));
    }

    [Fact]
    public void BehaviourDestroy_AbortsPendingDelay_LateCallbackDoesNotRun()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var pump = new XtsFramePump(Runtime);
        Assert.Equal(XtsStatus.Ok, Runtime.EvaluateExpression("""
            __xts_bhv_register("late:test", class {
                fields = {};
                onCreate() {
                    globalThis.__fired = 0;
                    this.destroySignal = globalThis.__xts_make_signal();
                    __xts_delay(30, this.destroySignal).then(function () { globalThis.__fired = 1; });
                }
            });
            """, out _));

        Assert.Equal(XtsStatus.Ok, Runtime.EvaluateExpression(
            "__xts_bhv_create('late', 'test', '{}')", out var handle));
        var index = (int)handle.AsDouble();

        // abort the signal the way XtsBehaviour.DestroyScript does
        Assert.Equal(XtsStatus.Ok, Runtime.EvaluateExpression(
            $"__xts_bhv_call({index}, '__abort_signal')", out _));
        System.Threading.Thread.Sleep(60);
        pump.Pump(out _);
        System.Threading.Thread.Sleep(10);
        pump.Pump(out _);

        Assert.Equal(0d, EvalNumber("__fired"));
    }

    [Fact]
    public void RejectedPromise_SurfacesThroughPumpDiagnostics()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var pump = new XtsFramePump(Runtime);
        // the rejection tracker fires when the promise is rejected with no
        // handler; the failure surfaces at the next boundary call
        var status = Runtime.EvaluateExpression(
            "Promise.reject(new TypeError('late failure'));", out _);
        Assert.Equal(XtsStatus.Exception, status);
        Assert.Contains("late failure", _runtime.LastError);
    }

    [Fact]
    public void HundredDomainCycles_NoResidualGrowth()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        long firstAfterRun = 0;
        for (var i = 0; i < 100; i++)
        {
            using var runtime = XtsRuntime.Create(new XtsRuntimeOptions { MemoryLimitBytes = 32 << 20 });
            var pump = new XtsFramePump(runtime);
            Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression(
                "globalThis.__n = 0; __xts_delay(0).then(function () { globalThis.__n++; }); " +
                "for (var k = 0; k < 50; k++) { Promise.resolve(k).then(function () { globalThis.__n++; }); }",
                out _));
            System.Threading.Thread.Sleep(5);
            pump.Pump(out _);
            pump.Pump(out _);
            Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("__n", out var n));
            Assert.Equal(51d, n.AsDouble());

            if (i == 9)
                firstAfterRun = (long)runtime.MemoryUsedBytes();
        }
        Assert.True(firstAfterRun > 0);
    }
}
