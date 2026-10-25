using System;
using System.Collections.Generic;

using XEngine.Async;
using XEngine.ILInterpreter.Binding;
using XEngine.ILInterpreter.VM;

namespace XEngine.ILInterpreter.Hosting;

/// <summary>
/// <see cref="ILAsyncSupport"/> over the engine's real XTask surface. One closed
/// instance per supported result type — every generic instantiation the VM needs
/// is explicit here, which is what NativeAOT can compile (plan §5.4).
/// Completion flows through <see cref="XTaskCompletionSource{T}"/>, so host code
/// completes pending tasks and the VM continuation resumes without any frame
/// pump (pump-free semantics equal to the engine's own awaiter contract).
/// </summary>
public sealed class XTaskAsyncSupport<T> : ILAsyncSupport
{
    public override string ResultTypeName => ResultKey;

    /// <summary>Domain-level key for the closed result type.</summary>
    public static string ResultKey => typeof(T).FullName ?? typeof(T).Name!;

    public override object CreateSource() => new XTaskCompletionSource<T>();

    public override object CreateTask(object source) => ((XTaskCompletionSource<T>)source).Task;

    public override object MakeAwaiter(object task) => ((XTask<T>)task).GetAwaiter();

    public override void Complete(object source, Slot value)
        => ((XTaskCompletionSource<T>)source).TrySetResult(FromSlot(value));

    public override void Fail(object source, Exception error)
        => ((XTaskCompletionSource<T>)source).TrySetException(error);

    public override bool IsCompleted(object o) => o switch
    {
        XTask<T> t => t.IsCompleted,
        XTaskCompletionSource<T> s => s.UnsafeGetStatus() != XTaskStatus.Pending,
        XTaskAwaiter<T> a => a.IsCompleted,
        _ => throw new ILError($"bad async state type {o?.GetType().FullName}"),
    };

    public override Slot GetResult(object o) => o switch
    {
        XTask<T> t => ToSlot(t.GetAwaiter().GetResult()),
        XTaskAwaiter<T> a => ToSlot(a.GetResult()),
        XTaskCompletionSource<T> s => ToSlot(s.Task.GetAwaiter().GetResult()),
        _ => throw new ILError($"bad async result type {o?.GetType().FullName}"),
    };

    public override bool Owns(object o)
        => o is XTask<T> or XTaskAwaiter<T> or XTaskCompletionSource<T>;

    public override Exception? ErrorOf(object task)
    {
        if (task is XTask<T> t && t.IsCompleted)
        {
            try
            {
                _ = t.GetAwaiter().GetResult();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
        return null;
    }

    private readonly Func<Slot, T> _fromSlot;
    private readonly Func<T, Slot> _toSlot;

    public XTaskAsyncSupport(Func<Slot, T> fromSlot, Func<T, Slot> toSlot)
    {
        _fromSlot = fromSlot;
        _toSlot = toSlot;
    }

    private T FromSlot(Slot s) => s.Kind switch
    {
        ILSlotKind.I4 => _fromSlot(Slot.I4((int)s.I)),
        ILSlotKind.I8 => _fromSlot(Slot.I8(s.I)),
        ILSlotKind.R4 => _fromSlot(Slot.R4((float)s.F)),
        ILSlotKind.R8 => _fromSlot(Slot.R8(s.F)),
        _ => _fromSlot(s),
    };

    private Slot ToSlot(T v) => _toSlot(v);
}

/// <summary>Registers the closed XTask supports a project allows, plus the
/// interpreter-side key helpers.</summary>
public static class XTaskAsyncSupportRegistration
{
    public static void RegisterInt32(HostBindingRegistry host)
        => host.AsyncSupport[typeof(int).FullName!] = new XTaskAsyncSupport<int>(
            s => (int)s.I, v => Slot.I4(v));

    public static void RegisterSingle(HostBindingRegistry host)
        => host.AsyncSupport[typeof(float).FullName!] = new XTaskAsyncSupport<float>(
            s => (float)s.F, v => Slot.R4(v));

    public static void RegisterDouble(HostBindingRegistry host)
        => host.AsyncSupport[typeof(double).FullName!] = new XTaskAsyncSupport<double>(
            s => s.F, v => Slot.R8(v));

    public static void RegisterString(HostBindingRegistry host)
        => host.AsyncSupport[typeof(string).FullName!] = new XTaskAsyncSupport<string>(
            s => (string)(s.O ?? ""), v => Slot.Ref(v));
}
