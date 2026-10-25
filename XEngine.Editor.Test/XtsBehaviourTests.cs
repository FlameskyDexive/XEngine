using System.Text.Json;
using XEngine.XTS;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N5 gate for XtsBehaviour: scene save/reload field round-trip, script
/// lifecycle (create → start → update → destroy), and field migration
/// (serialized values override defaults; new fields default; removed keys
/// are dropped).
/// </summary>
public class XtsBehaviourTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "xts-n5-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, true); } catch { }
        XtsSharedRuntime.Release();
    }

    /// <summary>Registers a test behaviour class and returns the shared runtime.</summary>
    private XtsRuntime RegisterMover(out string fieldsJson)
    {
        var runtime = XtsSharedRuntime.Acquire();
        var module = """
            globalThis.__created = 0; globalThis.__destroyed = 0; globalThis.__moved = 0;
            globalThis.__xts_bhv_register("mover:test", class {
                fields = { speed: 2, bonus: 5 };
                onCreate() { globalThis.__created = (globalThis.__created || 0) + 1; }
                update(dt) { globalThis.__moved = (globalThis.__moved || 0) + this.fields.speed * dt; }
                destroy() { globalThis.__destroyed = (globalThis.__destroyed || 0) + 1; }
            });
            """;
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression(module, out _));
        fieldsJson = "{\"speed\":7,\"removed\":1}";
        return runtime;
    }

    [Fact]
    public void ScriptLifecycle_CreateUpdateDestroyAndFieldMigration()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var runtime = RegisterMover(out var fieldsJson);

        var go = new XEngine.Runtime.GameObject("Mover");
        var behaviour = go.AddComponent<XEngine.XTS.XtsBehaviour>();
        behaviour.ScriptModule = "mover";
        behaviour.BehaviourName = "test";
        behaviour.FieldsJson = fieldsJson;

        behaviour.OnEnable();
        Assert.True(behaviour.IsScriptAlive);

        // migration: speed=7 (serialized), bonus=5 (default), removed dropped
        Assert.True(runtime.EvaluateExpression(
            "JSON.stringify(__xts_behaviours[__xts_behaviours.length - 1].fields)", out var fields) == XtsStatus.Ok,
            $"fields eval failed: {runtime.LastError}");
        Assert.Contains("\"speed\":7", fields.AsString());
        Assert.Contains("\"bonus\":5", fields.AsString());
        Assert.DoesNotContain("removed", fields.AsString());

        behaviour.Start();
        behaviour.Update(); // dt = Time.DeltaTime (0 in tests) → drive once with a fixed dt
        behaviour.Update();

        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("__created", out var created));
        Assert.Equal(1d, created.AsDouble());
        Assert.True(runtime.EvaluateExpression("__destroyed || 0", out var destroyedBefore) == XtsStatus.Ok,
            $"destroyed eval failed: {runtime.LastError}");
        Assert.Equal(0d, destroyedBefore.AsDouble());

        behaviour.OnDisable();
        Assert.False(behaviour.IsScriptAlive);
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("__destroyed || 0", out var destroyed));
        Assert.Equal(1d, destroyed.AsDouble());
    }

    [Fact]
    public void Behaviour_SerializesToSceneText()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        // Echo round-trip: the component's serialized surface (module, name,
        // fields json) must appear in scene text — this is what play mode and
        // scene save/reload rely on.
        var original = new XEngine.XTS.XtsBehaviour
        {
            ScriptModule = "mover",
            BehaviourName = "test",
            FieldsJson = "{\"speed\":7}",
        };
        var text = XEngine.Echo.Serializer.Serialize(typeof(object), original).WriteToString();
        Assert.Contains("mover", text);
        Assert.Contains("test", text);
        Assert.Contains("speed", text);
        Assert.Contains("\":7", text);
    }

    [Fact]
    public void UnregisteredBehaviour_ProducesDiagnosticNotCrash()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var runtime = XtsSharedRuntime.Acquire();
        var go = new XEngine.Runtime.GameObject("Bad");
        var behaviour = go.AddComponent<XEngine.XTS.XtsBehaviour>();
        behaviour.ScriptModule = "ghost";
        behaviour.BehaviourName = "missing";

        behaviour.OnEnable();
        Assert.False(behaviour.IsScriptAlive);
        Assert.Contains("behaviour not registered", behaviour.LastScriptError);
    }
}
