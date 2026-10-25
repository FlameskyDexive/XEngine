using System;
using System.Collections.Generic;
using System.Globalization;
using System;
using System.Collections.Generic;
using System.Text;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System;
using System.Collections.Generic;
using XEngine.XTS.Binding;

namespace XEngine.XTS;

/// <summary>Thrown when a host→script function invocation fails.</summary>
public sealed class XtsBindingException(string message) : Exception(message);

/// <summary>Handler invoked when script calls the bound function.</summary>
public delegate XtsValue XtsBindingHandler(ReadOnlySpan<XtsValue> args);

public unsafe partial class XtsRuntime
{
    private readonly Dictionary<string, XtsBindingHandler> _callbacks = new(StringComparer.Ordinal);
    private readonly List<GCHandle> _callbackHandles = new();
    private readonly List<IntPtr> _trackedAllocs = new();

    /// <summary>
    /// Expose a host function as a global <c>name(...)</c> callable from JS.
    /// Arguments arrive as <see cref="XtsValue"/> primitives; functions the
    /// script passes cross as handle indices (see <see cref="CreateFunctionHandle"/>).
    /// </summary>
    public void RegisterCallback(string name, XtsBindingHandler handler)
    {
        ThrowIfDisposed();
        _callbacks[name] = handler;
        var record = new CallbackRecord(this, name);
        var handle = GCHandle.Alloc(record);
        _callbackHandles.Add(handle);
        var status = (XtsStatus)XtsNativeAbi.xts_env_register_callback(
            _env, XtsNativeAbi.Utf8(name), Trampoline, GCHandle.ToIntPtr(handle));
        if (status != XtsStatus.Ok)
        {
            handle.Free();
            _callbackHandles.RemoveAt(_callbackHandles.Count - 1);
            throw new InvalidOperationException($"xts_env_register_callback('{name}') -> {status}");
        }
    }

    /// <summary>
    /// Wrap a function index (handed over by a script through the
    /// <c>__xts_hold</c> prelude) as an invocable handle.
    /// </summary>
    public XtsFunctionHandle CreateFunctionHandle(int index)
    {
        ThrowIfDisposed();
        return new XtsFunctionHandle(this, index);
    }

    /// <summary>
    /// Invoke the script function stored at <c>__xts_handles[index]</c>.
    /// Arguments render as JS literals (numbers, bigint, string, bool, null);
    /// the function's return value converts back to an <see cref="XtsValue"/>.
    /// </summary>
    public XtsValue InvokeFunction(int index, params XtsValue[] args)
    {
        ThrowIfDisposed();
        var rendered = new string[args?.Length ?? 0];
        for (var i = 0; i < rendered.Length; i++)
            rendered[i] = RenderArgument(args![i]);
        var status = EvaluateExpression(
            $"(__xts_result = undefined, __xts_handles[{index}]({string.Join(", ", rendered)}))",
            out var result);
        if (status != XtsStatus.Ok)
            throw new XtsBindingException($"script function invocation failed ({status}): {LastError}");
        return result;
    }

    /// <summary>Release a script function slot early (otherwise freed with the env).</summary>
    public void ReleaseFunction(int index) =>
        EvaluateExpression($"__xts_handles[{index}] = null;", out _);

    private static readonly string Prelude =
        "globalThis.__xts_handles = [];\n" +
        "globalThis.__xts_hold = function (fn) { return globalThis.__xts_handles.push(fn) - 1; };\n" +
        "globalThis.__xts_result = undefined;\n" +
        // behaviour helpers (see XtsBehaviour): classes self-register under a
        // name; create merges serialized fields with the class defaults so
        // old scenes keep working as scripts gain or drop fields
        "globalThis.__xts_bhv_classes = {};\n" +
        "globalThis.__xts_bhv_register = function (name, cls) { globalThis.__xts_bhv_classes[name] = cls; };\n" +
        "globalThis.__xts_bhv_create = function (moduleName, exportName, fieldsJson, stateJson) {\n" +
        "    var key = moduleName + ':' + exportName;\n" +
        "    var cls = globalThis.__xts_bhv_classes[key];\n" +
        "    if (cls === undefined) throw new Error('behaviour not registered: ' + key);\n" +
        "    var inst = new cls();\n" +
        "    var defaults = (inst.fields && typeof inst.fields === 'object') ? inst.fields : {};\n" +
        "    var serialized = fieldsJson ? JSON.parse(fieldsJson) : {};\n" +
        "    var merged = {};\n" +
        "    for (var k in defaults) merged[k] = (k in serialized) ? serialized[k] : defaults[k];\n" +
        "    inst.fields = merged;\n" +
        "    inst.destroySignal = { aborted: false };\n" +
        "    inst.__abort_signal = function () { if (inst.destroySignal) inst.destroySignal.aborted = true; };\n" +
        "    var index = globalThis.__xts_behaviours.push(inst) - 1;\n" +
        "    if (typeof inst.onCreate === 'function') inst.onCreate();\n" +
        "    if (stateJson && typeof inst.importState === 'function') inst.importState(JSON.parse(stateJson));\n" +
        "    return index;\n" +
        "};\n" +
        "globalThis.__xts_bhv_export = function (index) {\n" +
        "    var inst = globalThis.__xts_behaviours[index];\n" +
        "    if (inst === null || inst === undefined) return '{}';\n" +
        "    return (typeof inst.exportState === 'function') ? JSON.stringify(inst.exportState()) : JSON.stringify(inst.fields || {});\n" +
        "};\n" +
        "globalThis.__xts_bhv_import = function (index, json) {\n" +
        "    var inst = globalThis.__xts_behaviours[index];\n" +
        "    if (inst && json && typeof inst.importState === 'function') inst.importState(JSON.parse(json));\n" +
        "};\n" +
        "globalThis.__xts_bhv_alive = function (index) {\n" +
        "    var inst = globalThis.__xts_behaviours[index];\n" +
        "    return !!inst && typeof inst.update === 'function';\n" +
        "};\n" +
        "globalThis.__xts_bhv_call = function (index, method, a, b, c) {\n" +
        "    var inst = globalThis.__xts_behaviours[index];\n" +
        "    if (inst === null || inst === undefined) return;\n" +
        "    if (typeof inst[method] === 'function') inst[method](a, b, c);\n" +
        "};\n" +
        "globalThis.__xts_bhv_drop = function (index) {\n" +
        "    globalThis.__xts_behaviours[index] = null;\n" +
        "};\n" +
        "globalThis.__xts_behaviours = [];\n";

    internal void EnsurePrelude()
    {
        if (_preludeInstalled)
            return;
        XtsNativeAbi.XtsValue ignored = default;
        var status = (XtsStatus)XtsNativeAbi.xts_env_eval_expression(
            _env, XtsNativeAbi.Utf8(Prelude), &ignored);
        if (status != XtsStatus.Ok)
            throw new InvalidOperationException($"xts prelude failed ({status}): {LastError}");
        _preludeInstalled = true;
    }
    private bool _preludeInstalled;

    /// <summary>Render an XtsValue as a JS expression literal.</summary>
    internal static string RenderArgument(XtsValue value) => value.Kind switch
    {
        XtsValueKind.Bool => value.AsBool() ? "true" : "false",
        XtsValueKind.Int32 => value.AsInt32().ToString(CultureInfo.InvariantCulture),
        XtsValueKind.Float64 => value.AsDouble().ToString("R", CultureInfo.InvariantCulture),
        XtsValueKind.BigInt => value.AsBigInt().ToString(CultureInfo.InvariantCulture) + "n",
        XtsValueKind.String => JsonSerializer.Serialize(value.AsString()),
        XtsValueKind.Null => "null",
        _ => "undefined",
    };

    private static readonly XtsNativeAbi.XtsNativeCallback Trampoline = NativeTrampoline;

    private static void NativeTrampoline(IntPtr userData, int argc, XtsNativeAbi.XtsValue* argv, XtsNativeAbi.XtsValue* result)
    {
        if (GCHandle.FromIntPtr(userData).Target is not CallbackRecord record ||
            record.Runtime is not { } runtime ||
            !runtime._callbacks.TryGetValue(record.Name, out var handler))
            return;

        try
        {
            var args = new XtsValue[argc];
            for (var i = 0; i < argc; i++)
                args[i] = runtime.FromNative(argv + i);

            var outcome = handler(args);
            runtime.ToNative(outcome, out *result);
        }
        catch (Exception e)
        {
            // a managed handler failure must never unwind through the native
            // boundary; surface undefined and keep the reason for diagnostics
            runtime.LastManagedError = $"{record.Name}: {e.Message}";
            *result = default;
        }
        runtime.FreeTracked();
    }

    private void FreeTracked()
    {
        if (_trackedAllocs.Count == 0)
            return;
        foreach (var ptr in _trackedAllocs)
            Marshal.FreeHGlobal(ptr);
        _trackedAllocs.Clear();
    }

    internal void ThrowIfDisposed()
    {
        if (_env == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(XtsRuntime));
    }

    private readonly struct CallbackRecord(XtsRuntime runtime, string name)
    {
        public readonly XtsRuntime Runtime = runtime;
        public readonly string Name = name;
    }

    internal XtsValue FromNative(XtsNativeAbi.XtsValue* native)
    {
        return native->Kind switch
        {
            XtsNativeAbi.XtsValueKind.Bool => XtsValue.From(native->Int32Value != 0),
            XtsNativeAbi.XtsValueKind.Int32 => XtsValue.From(native->Int32Value),
            XtsNativeAbi.XtsValueKind.Float64 => XtsValue.From(native->Float64Value),
            XtsNativeAbi.XtsValueKind.Null => XtsValue.Null,
            XtsNativeAbi.XtsValueKind.BigInt => XtsValue.From(native->BigIntValue),
            XtsNativeAbi.XtsValueKind.String => XtsValue.From(
                Marshal.PtrToStringUTF8(native->StringData, (int)native->StringLength)),
            _ => XtsValue.Undefined,
        };
    }

    private void ToNative(XtsValue value, out XtsNativeAbi.XtsValue native)
    {
        native = default;
        switch (value.Kind)
        {
            case XtsValueKind.Bool:
                native.Kind = XtsNativeAbi.XtsValueKind.Bool;
                native.Int32Value = value.AsBool() ? 1 : 0;
                break;
            case XtsValueKind.Int32:
                native.Kind = XtsNativeAbi.XtsValueKind.Int32;
                native.Int32Value = value.AsInt32();
                break;
            case XtsValueKind.Float64:
                native.Kind = XtsNativeAbi.XtsValueKind.Float64;
                native.Float64Value = value.AsDouble();
                break;
            case XtsValueKind.BigInt:
                native.Kind = XtsNativeAbi.XtsValueKind.BigInt;
                native.BigIntValue = value.AsBigInt();
                break;
            case XtsValueKind.String:
                var text = value.AsString();
                native.Kind = XtsNativeAbi.XtsValueKind.String;
                // the native side copies synchronously inside the call; the
                // pointer is released right after the call returns
                native.StringData = XtsNativeAbi.AllocUtf8(text);
                native.StringLength = (uint)Encoding.UTF8.GetByteCount(text);
                _trackedAllocs.Add(native.StringData);
                break;
            case XtsValueKind.Null:
                native.Kind = XtsNativeAbi.XtsValueKind.Null;
                break;
            default:
                native.Kind = XtsNativeAbi.XtsValueKind.Undefined;
                break;
        }
    }
}
