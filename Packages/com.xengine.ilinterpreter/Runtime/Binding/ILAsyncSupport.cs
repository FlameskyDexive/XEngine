using System;
using System.Collections.Generic;

using XEngine.ILInterpreter.VM;

namespace XEngine.ILInterpreter.Binding;

/// <summary>
/// Host-side async support for one closed task result type. The interpreter
/// routes builder calls and task/awaiter member refs through this; the host
/// registers one instance per supported result type so every generic
/// instantiation is explicit and AOT-visible (plan §5.4).
/// </summary>
public abstract class ILAsyncSupport
{
    /// <summary>Full name of the result type this support handles (e.g. "System.Int32").</summary>
    public abstract string ResultTypeName { get; }

    public abstract object CreateSource();
    public abstract object CreateTask(object source);
    public abstract object MakeAwaiter(object task);
    public abstract void Complete(object source, Slot value);
    public abstract void Fail(object source, Exception error);
    public abstract bool IsCompleted(object sourceOrTaskOrAwaiter);
    public abstract Slot GetResult(object sourceOrAwaiter);
    /// <summary>True when the object is a task/awaiter/source of this result type.</summary>
    public abstract bool Owns(object o);
    /// <summary>Extracts the fault of a completed-with-error task, or null.</summary>
    public abstract Exception? ErrorOf(object task);
}
