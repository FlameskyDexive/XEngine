using System.Runtime.InteropServices;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.InteropServices;
using XEngine.XTS.Binding;

namespace XEngine.XTS;

/// <summary>
/// One isolated TypeScript/JS script environment over the XTS native ABI.
/// Create, use, and dispose it on the engine main thread; the native layer
/// performs no locking. Every evaluation is bounded by the heap limit from
/// <see cref="XtsRuntimeOptions"/> and (per call) the execution deadline —
/// a runaway loop or unbounded recursion returns instead of killing the
/// player process.
/// </summary>
public sealed unsafe partial class XtsRuntime : IDisposable
{
    private IntPtr _env;

    private XtsRuntime(IntPtr env) => _env = env;

    public bool IsDisposed => _env == IntPtr.Zero;

    public static XtsRuntime Create(XtsRuntimeOptions? options = null)
    {
        options ??= XtsRuntimeOptions.Default;
        var native = new XtsNativeAbi.XtsOptions
        {
            StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<XtsNativeAbi.XtsOptions>(),
            AbiVersion = XtsNativeAbi.AbiVersion,
            MemoryLimitBytes = options.MemoryLimitBytes,
            MaxStackBytes = options.MaxStackBytes,
        };
        var env = XtsNativeAbi.xts_env_create(ref native);
        if (env == IntPtr.Zero)
            throw new InvalidOperationException("xts_env_create failed (options rejected by the native ABI)");
        var runtime = new XtsRuntime(env);
        runtime.EnsurePrelude();
        return runtime;
    }

    /// <summary>ABI version of the loaded native library.</summary>
    public static int NativeAbiVersion => XtsNativeAbi.xts_abi_version();

    /// <summary>
    /// Register (or replace) the ES module source for <paramref name="name"/>.
    /// Import specifiers are resolved by the native layer; register the
    /// resolved name (e.g. "lib.js" for "./lib.js" imported from a sibling).
    /// </summary>
    public XtsStatus AddModuleSource(string name, string source) =>
        (XtsStatus)XtsNativeAbi.xts_env_add_module_source(_env, XtsNativeAbi.Utf8(name), XtsNativeAbi.Utf8(source));

    /// <summary>
    /// Compile, link and evaluate the named module (and its imports). The
    /// failure kind is classified into <see cref="XtsStatus"/>; text for the
    /// failure is available from <see cref="LastError"/>.
    /// </summary>
    public XtsStatus EvalModule(string name) =>
        (XtsStatus)XtsNativeAbi.xts_env_eval_module(_env, XtsNativeAbi.Utf8(name));

    /// <summary>
    /// Evaluate a global (non-module) script and convert the completion value
    /// to a primitive <see cref="XtsValue"/>. Objects, arrays, functions and
    /// symbols surface as <see cref="XtsValueKind.Undefined"/>. On failure the
    /// value is undefined and <see cref="LastError"/> carries the cause.
    /// </summary>
    public unsafe XtsStatus EvaluateExpression(string code, out XtsValue value)
    {
        value = default;
        XtsNativeAbi.XtsValue native = default;
        var status = (XtsStatus)XtsNativeAbi.xts_env_eval_expression(_env, XtsNativeAbi.Utf8(code), &native);
        if (status != XtsStatus.Ok)
            return status;
        value = FromNative(&native);
        return status;
    }

    /// <summary>
    /// Run pending promise jobs (module bodies, continuations). Stop after
    /// <paramref name="maxJobs"/> jobs (0 = unbounded) or on the first
    /// failure; the deadline set via <see cref="SetExecutionDeadlineMs"/>
    /// bounds the whole batch.
    /// </summary>
    public XtsStatus ExecutePendingJobs(uint maxJobs, out uint executed) =>
        (XtsStatus)XtsNativeAbi.xts_env_execute_pending_jobs(_env, maxJobs, out executed);

    /// <summary>0 disables the limit. Applies to the QuickJS heap.</summary>
    public void SetMemoryLimit(ulong bytes) => XtsNativeAbi.xts_env_set_memory_limit(_env, bytes);

    /// <summary>
    /// 0 disables it. The deadline is measured from the start of the next
    /// eval or job batch; an overrun raises an interrupt inside the script
    /// and the call returns <see cref="XtsStatus.Limit"/> with the
    /// environment still usable.
    /// </summary>
    public void SetExecutionDeadlineMs(ulong ms) => XtsNativeAbi.xts_env_set_execution_deadline_ms(_env, ms);

    public ulong MemoryUsedBytes() =>
        XtsNativeAbi.xts_env_memory_used_bytes(_env, out var used) == 0 ? used : 0;

    /// <summary>
    /// UTF-8 text of the last failure (exception message, interrupted notice,
    /// out-of-memory). Empty when the last call succeeded.
    /// </summary>
    public string LastError
    {
        get
        {
            if (_env == IntPtr.Zero)
                return string.Empty;
            var buffer = new byte[512];
            var written = XtsNativeAbi.xts_env_get_last_exception(_env, buffer, (uint)buffer.Length);
            return written <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, written);
        }
    }

    /// <summary>
    /// Message of the last managed exception thrown by a binding handler
    /// (never unwound through the native boundary). Null when none.
    /// </summary>
    public string? LastManagedError { get; internal set; }

    public void Dispose()
    {
        if (_env != IntPtr.Zero)
        {
            XtsNativeAbi.xts_env_destroy(_env);
            _env = IntPtr.Zero;
        }
        _callbacks.Clear();
        foreach (var handle in _callbackHandles)
        {
            if (handle.IsAllocated)
                handle.Free();
        }
        _callbackHandles.Clear();
        FreeTracked();
    }
}