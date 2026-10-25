using XEngine.XTS;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N9 gate: safe-point rebuild migrates behaviour state to a fresh
/// environment, verifies health before committing, keeps the old domain
/// alive on any migration failure, and guarantees old-domain late callbacks
/// cannot touch the new domain.
/// </summary>
public class XtsSafePointTests
{
    public XtsSafePointTests() => XtsSharedRuntime.ResetForTests();

    private const string AccScript = """
        globalThis.__total = 0;
        __xts_bhv_register("acc:test", class {
            fields = { step: 3 };
            total = 0;
            exportState() { return { total: this.total }; }
            importState(s) { this.total = s.total; }
            update(dt) { this.total += this.fields.step; globalThis.__total = this.total; }
        });
        """;

    private const string FragileScript = """
        __xts_bhv_register("fragile:test", class {
            fields = { step: 5 };
            total = 0;
            importState(s) { throw new Error('corrupt state'); }
            update(dt) { this.total += this.fields.step; globalThis.__total = this.total; }
        });
        """;

    private static XtsRuntime RegisterAccumulator(XtsRuntime runtime)
    {
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression(AccScript, out _));
        return runtime;
    }

    private XtsBehaviour Attach(XtsRuntime runtime, string module = "acc", string name = "test",
        string fieldsJson = "{\"step\":3}")
    {
        var go = new XEngine.Runtime.GameObject("Acc");
        var behaviour = go.AddComponent<XEngine.XTS.XtsBehaviour>();
        behaviour.ScriptModule = module;
        behaviour.BehaviourName = name;
        behaviour.FieldsJson = fieldsJson;
        behaviour.CreateScriptIn(runtime, importStateJson: null);
        return behaviour;
    }

    [Fact]
    public void Rebuild_PreservesStateAcrossEnvironments()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var runtime = XtsSharedRuntime.Acquire();
        RegisterAccumulator(runtime);
        var behaviour = Attach(runtime);

        behaviour.Update(); // total = 3
        behaviour.Update(); // total = 6
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("__total", out var before));
        Assert.Equal(6d, before.AsDouble());

        var result = XtsSafePoint.Rebuild(new[] { behaviour }, new[] { AccScript });
        Assert.True(result.Rebuilt, string.Join("; ", result.Errors));
        Assert.NotSame(runtime, result.Runtime);
        Assert.True(runtime.IsDisposed); // the old domain is disposed by the swap

        // the new domain continues from the migrated instance state
        Assert.Equal(XtsStatus.Ok, result.Runtime!.EvaluateExpression(
            $"__xts_behaviours[{behaviour.Handle}].total", out var migrated));
        Assert.Equal(6d, migrated.AsDouble());

        behaviour.Update(); // total = 9 in the NEW domain
        Assert.Equal(XtsStatus.Ok, result.Runtime.EvaluateExpression("__total", out var after));
        Assert.Equal(9d, after.AsDouble());

        XtsSharedRuntime.Release();
    }

    [Fact]
    public void Rebuild_OldDomainLateCallbacksDieWithIt()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var oldRuntime = XtsSharedRuntime.Acquire();
        RegisterAccumulator(oldRuntime);
        var behaviour = Attach(oldRuntime);

        // the OLD domain queues a late mutation (installs the timer helpers)
        var pump = new XtsFramePump(oldRuntime);
        Assert.Equal(XtsStatus.Ok, oldRuntime.EvaluateExpression(
            "globalThis.__late = 0; __xts_delay(10).then(function () { globalThis.__late = 999; });", out _));

        var result = XtsSafePoint.Rebuild(new[] { behaviour }, new[] { AccScript });
        Assert.True(result.Rebuilt);

        // the new domain has no __late value at all
        Assert.Equal(XtsStatus.Ok, result.Runtime!.EvaluateExpression(
            "globalThis.__late === undefined", out var lateUndefined));
        Assert.True(lateUndefined.AsBool());

        // the old environment is disposed: its late callback can never run
        Assert.True(oldRuntime.IsDisposed);
        Assert.Equal(XtsStatus.BadArg, oldRuntime.EvaluateExpression("__late = 999", out _));
    }

    [Fact]
    public void Rebuild_MigrationFailure_KeepsOldDomainAlive()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var runtime = XtsSharedRuntime.Acquire();
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression(FragileScript, out _));
        var behaviour = Attach(runtime, module: "fragile", name: "test", fieldsJson: "{}");
        behaviour.Update();
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("__total", out var before));
        Assert.Equal(5d, before.AsDouble());

        // migration fails: importState throws inside the replacement env
        var result = XtsSafePoint.Rebuild(new[] { behaviour }, new[] { FragileScript });
        Assert.False(result.Rebuilt);
        Assert.Contains(result.Errors, e => e.Contains("corrupt state"));

        // the old domain is untouched and still drives the behaviour (total
        // continued 5 → 10, nothing was lost by the aborted rebuild)
        Assert.Same(runtime, result.Runtime);
        Assert.True(behaviour.IsScriptAlive);
        behaviour.Update();
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("__total", out var after));
        Assert.Equal(10d, after.AsDouble());

        XtsSharedRuntime.Release();
    }
}
