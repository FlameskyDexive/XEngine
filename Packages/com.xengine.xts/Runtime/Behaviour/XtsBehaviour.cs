using System.Runtime.InteropServices;
using System;
using System.Runtime.InteropServices;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices;
using XEngine.Runtime;

namespace XEngine.XTS;

/// <summary>
/// Attaches a TypeScript behaviour to a GameObject. The script module (the
/// project's built bundle) registers a behaviour class through
/// <c>__xts_bhv_register(name, class)</c>; this component instantiates it
/// with <see cref="FieldsJson"/>, drives the update lifecycle, and destroys
/// the instance on disable.
///
/// Field migration contract: the class declares its current fields (with
/// defaults) as `fields`; <see cref="FieldsJson"/> carries the serialized
/// values from the scene. On create, unknown serialized keys are dropped and
/// newly-added fields fall back to their defaults — old scenes keep working
/// as the script evolves.
/// </summary>
[AddComponentMenu("XTS/TypeScript Behaviour")]
[ExecuteAlways]
[Preserve]
public sealed class XtsBehaviour : MonoBehaviour
{
    /// <summary>Module name the behaviour class registered under (e.g. "mover").</summary>
    public string ScriptModule = "main";

    /// <summary>Export/registration name of the behaviour class.</summary>
    public string BehaviourName = "main";

    /// <summary>Serialized TS field values (JSON object, by-value copy).</summary>
    public string FieldsJson = "{}";

    [NonSerialized] private int _handle = -1;
    [NonSerialized] private bool _started;

    public int Handle => _handle;
    public bool IsScriptAlive => _handle >= 0;

    public override void OnEnable()
    {
        base.OnEnable();
        CreateScript();
    }

    public override void Start()
    {
        base.Start();
        if (_handle < 0)
            return;
        CallBehaviour("start");
        _started = true;
    }

    public override void Update()
    {
        base.Update();
        if (_handle < 0)
            return;
        CallBehaviour("update", Time.DeltaTime);
    }

    public override void OnDisable()
    {
        DestroyScript();
        base.OnDisable();
    }

    public override void OnDispose()
    {
        DestroyScript();
        base.OnDispose();
    }

    /// <summary>Create (or re-create) the script instance from FieldsJson.</summary>
    public void CreateScript()
    {
        var runtime = XtsSharedRuntime.Acquire();
        CreateScriptIn(runtime, importStateJson: null);
        XtsSharedRuntime.Release(); // balance the acquire; the shared env outlives us via other refs
    }

    /// <summary>Create the script instance in a SPECIFIC environment, optionally
    /// importing migrated state (used by the safe-point rebuild). When
    /// <paramref name="destroyExisting"/> is false the previously dispatched
    /// instance (in the old domain) is left alone — the caller restores the
    /// old handle if the rebuild aborts. Returns an error text or null on
    /// success.</summary>
    public string? CreateScriptIn(XtsRuntime runtime, string? importStateJson, bool destroyExisting = true)
    {
        if (destroyExisting)
            DestroyScript();
        // JSON string literal built by hand: reflection-based System.Text.Json
        // serialization is unavailable inside the NativeAOT player.
        var stateLiteral = importStateJson == null
            ? "undefined"
            : "\"" + importStateJson.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var status = runtime.EvaluateExpression(
            $"__xts_bhv_create({Quote(ScriptModule ?? string.Empty)}, " +
            $"{Quote(BehaviourName ?? string.Empty)}, " +
            $"{Quote(FieldsJson ?? "{}")}, {stateLiteral})",
            out var handleValue);
        if (status != XtsStatus.Ok || !handleValue.IsNumber)
        {
            _handle = -1;
            LastScriptError = $"create failed ({status}): {runtime.LastError}";
            return LastScriptError;
        }
        _handle = (int)handleValue.AsDouble();
        LastScriptError = null;
        return null;
    }

    /// <summary>Export the instance's migrated state (defaults to its fields).</summary>
    public string ExportStateJson()
    {
        var runtime = XtsSharedRuntime.Current;
        if (runtime == null || _handle < 0)
            return "{}";
        return runtime.EvaluateExpression($"__xts_bhv_export({_handle})", out var v) == XtsStatus.Ok
            && v.Kind == XtsValueKind.String ? v.AsString() : "{}";
    }

    /// <summary>Restore the handle after an aborted rebuild (old domain kept).</summary>
    internal void RestoreHandle(int oldHandle) => _handle = oldHandle;

    /// <summary>Destroy the script instance (idempotent). Aborts the
    /// behaviour's destroy signal first so late timers and callbacks tied to
    /// it reject instead of firing.</summary>
    /// <summary>JS/JSON string literal without System.Text.Json (the NativeAOT
    /// player disables reflection-based serialization).</summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public void DestroyScript()
    {
        if (_handle < 0)
            return;
        XtsSharedRuntime.Current?.EvaluateExpression(
            $"__xts_bhv_call({_handle}, '__abort_signal');", out _);
        CallBehaviour("destroy");
        XtsSharedRuntime.Current?.EvaluateExpression($"__xts_bhv_drop({_handle});", out _);
        _handle = -1;
        _started = false;
    }

    private void CallBehaviour(string method, params double[] numericArgs)
    {
        var runtime = XtsSharedRuntime.Current;
        if (runtime == null)
            return;
        var args = string.Join(",", System.Linq.Enumerable.Select(numericArgs, n =>
            n.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        var status = runtime.EvaluateExpression(
            $"__xts_bhv_call({_handle}, {Quote(method)}, {args})", out _);
        if (status != XtsStatus.Ok)
            LastScriptError = $"{method} failed ({status}): {runtime.LastError}";
    }

    /// <summary>Last script failure (create/call), for the inspector.</summary>
    [NonSerialized] public string? LastScriptError;
}
