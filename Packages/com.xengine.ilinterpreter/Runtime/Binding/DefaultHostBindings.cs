using System;
using System.Collections.Generic;

using XEngine.ILInterpreter.VM;

namespace XEngine.ILInterpreter.Binding;

/// <summary>
/// Host bindings every module may rely on: System.Nullable heavy patterns
/// (<c>?. </c> / <c>??</c> lower to Nullable&lt;int&gt; boxing + HasValue/
/// GetValueOrDefault calls) and the base marker types. Without these, common
/// C# idioms fail with missing-binding errors even though the VM could run
/// them (N7 gap closure).
/// </summary>
public static class DefaultHostBindings
{
    /// <summary>Registers the default surface onto a domain's registry.</summary>
    public static void Register(HostBindingRegistry host)
    {
        // Semantic base markers (inheritance hierarchy anchors).
        host.Bind("System.ValueType", typeof(ValueType));
        host.Bind("System.Enum", typeof(Enum));
        host.Bind("System.Delegate", typeof(Delegate));
        host.Bind("System.MulticastDelegate", typeof(MulticastDelegate));
        host.Bind("System.Attribute", typeof(Attribute));
        host.Bind("System.Object", typeof(object));
        host.Bind("System.Exception", typeof(Exception));
        host.Bind("System.IDisposable", typeof(IDisposable));
        host.Bind("System.IAsyncDisposable", typeof(IAsyncDisposable));
        host.Bind("System.IAsyncStateMachine", typeof(System.Runtime.CompilerServices.IAsyncStateMachine));
        host.Bind("System.Runtime.CompilerServices.IAsyncStateMachine", typeof(System.Runtime.CompilerServices.IAsyncStateMachine));
        host.Bind("System.Runtime.CompilerServices.ICriticalNotifyCompletion", typeof(System.Runtime.CompilerServices.ICriticalNotifyCompletion));
        host.Bind("System.Runtime.CompilerServices.INotifyCompletion", typeof(System.Runtime.CompilerServices.INotifyCompletion));

        // Common runtime exceptions (catch matching).
        host.Bind("System.NullReferenceException", typeof(NullReferenceException));
        host.Bind("System.IndexOutOfRangeException", typeof(IndexOutOfRangeException));
        host.Bind("System.OverflowException", typeof(OverflowException));
        host.Bind("System.DivideByZeroException", typeof(DivideByZeroException));
        host.Bind("System.InvalidOperationException", typeof(InvalidOperationException));
        host.Bind("System.InvalidCastException", typeof(InvalidCastException));
        host.Bind("System.NotSupportedException", typeof(NotSupportedException));
        host.Bind("System.ArgumentException", typeof(ArgumentException));
        host.Bind("System.ArgumentOutOfRangeException", typeof(ArgumentOutOfRangeException));
        host.Bind("System.OperationCanceledException", typeof(OperationCanceledException));

        // Exception base ctor/message surface.
        var exb = host.Bind("System.Exception", typeof(Exception));
        exb.Methods[(".ctor", 0)] = _ => default;
        exb.Methods[(".ctor", 1)] = _ => default;
        exb.Methods[("get_Message", 0)] = c => Slot.Ref(((Exception)c.This!).Message);
    }

    private static Slot ToSlot<T>(T value) where T : struct => value switch
    {
        int i => Slot.I4(i),
        long l => Slot.I8(l),
        float f => Slot.R4(f),
        double d => Slot.R8(d),
        bool bo => Slot.I4(bo ? 1 : 0),
        _ => Slot.Ref(value),
    };
}
