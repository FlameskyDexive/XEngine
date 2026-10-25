using System;
using System.Collections.Generic;

using XEngine.ILInterpreter.VM;

namespace XEngine.ILInterpreter.Binding;

public readonly struct HostBindingCall
{
    public HostBindingCall(ILDomain d, Slot[] args, object? self)
    {
        D = d;
        Args = args;
        This = self;
    }

    public ILDomain D { get; }
    public Slot[] Args { get; }
    public object? This { get; }
}

public sealed class HostTypeBinding
{
    public Type ClrType = default!;
    public Dictionary<(string Name, int ParamCount), Func<HostBindingCall, Slot>> Methods = new();
    public Func<HostBindingCall, Slot>? Ctor;
    public Dictionary<string, Func<object?, Slot>> FieldGet = new();
    public Dictionary<string, Action<object?, Slot>> FieldSet = new();
}

/// <summary>
/// Explicit static host bindings. Registration is done with concrete lambdas —
/// no reflection scanning — which keeps the binding surface auditable and
/// AOT-safe: everything registered is everything that exists.
/// </summary>
public sealed class HostBindingRegistry
{
    private readonly Dictionary<string, HostTypeBinding> _bindings = new();
    private readonly Dictionary<string, Type> _clrTypes = new();

    /// <summary>Closed-generic async support: key = result type full name. The host
    /// registers one support object per task type it is willing to run (AOT-visible).</summary>
    public Dictionary<string, ILAsyncSupport> AsyncSupport = new();

    /// <summary>Open generic builder/task/awaiter type names the VM intercepts as
    /// async carriers. Hosts extend this set for their own task families.</summary>
    public HashSet<string> BuilderTypeNames { get; } = new(StringComparer.Ordinal)
    {
        "XEngine.Async.AsyncXTaskMethodBuilder`1",
        "XEngine.Async.AsyncXTaskMethodBuilder",
        "Probe.Contracts.ProbeTaskBuilder`1",
    };

    /// <summary>Open generic task-family names dispatched through AsyncSupport.</summary>
    public HashSet<string> TaskFamilyTypeNames { get; } = new(StringComparer.Ordinal)
    {
        "XEngine.Async.XTask`1",
        "XEngine.Async.XTaskAwaiter`1",
        "Probe.Contracts.ProbeTask`1",
        "Probe.Contracts.ProbeTaskAwaiter`1",
        "Probe.Contracts.ProbeTaskSource`1",
    };

    public void Type(string fullName, Type clr)
    {
        _clrTypes[fullName] = clr;
        if (!_bindings.TryGetValue(fullName, out var b))
        {
            b = new HostTypeBinding { ClrType = clr };
            _bindings[fullName] = b;
        }
    }

    public HostTypeBinding Bind(string fullName, Type clr)
    {
        _clrTypes[fullName] = clr;
        if (!_bindings.TryGetValue(fullName, out var b))
        {
            b = new HostTypeBinding { ClrType = clr };
            _bindings[fullName] = b;
        }
        return b;
    }

    public Type? FindClrType(string fullName) => _clrTypes.TryGetValue(fullName, out var t) ? t : null;

    public HostTypeBinding? Find(string fullName) => _bindings.TryGetValue(fullName, out var b) ? b : null;

    public bool TryInvoke(string typeName, string method, int paramCount, HostBindingCall call, out Slot result)
    {
        result = default;
        if (!_bindings.TryGetValue(typeName, out var b)) return false;
        if (!b.Methods.TryGetValue((method, paramCount), out var f)) return false;
        result = f(call);
        return true;
    }

    public bool TryCtor(string typeName, HostBindingCall call, out Slot result)
    {
        result = default;
        if (!_bindings.TryGetValue(typeName, out var b) || b.Ctor is null) return false;
        result = b.Ctor(call);
        return true;
    }

    public bool TryFieldGet(string typeName, string field, object? instance, out Slot result)
    {
        result = default;
        if (!_bindings.TryGetValue(typeName, out var b) || !b.FieldGet.TryGetValue(field, out var f)) return false;
        result = f(instance);
        return true;
    }

    public bool TryFieldSet(string typeName, string field, object? instance, Slot value)
    {
        if (!_bindings.TryGetValue(typeName, out var b) || !b.FieldSet.TryGetValue(field, out var f)) return false;
        f(instance, value);
        return true;
    }

    public static string MissingSignatureError(string typeName, string member, int paramCount)
    {
        return $"missing host binding: {typeName}::{member} ({paramCount} params) — " +
               "hotfix requires a newer host or an explicit registration";
    }
}
