using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using XEngine.ILInterpreter.Binding;
using XEngine.ILInterpreter.Hosting;
using XEngine.ILInterpreter.VM;

namespace XEngine.ILInterpreter.Integration;

/// <summary>
/// Owns the active <see cref="ILDomain"/> for a player/editor session: loads the
/// interpreted module set, creates behaviour instances, forwards lifecycle
/// calls, and recycles domains with residue accounting (live handles + queued
/// continuations). Continuations captured against an old generation are dropped
/// by the domain — never delivered into a recycled domain (plan §5.4).
/// </summary>
public sealed class ILInterpreterRuntimeService
{
    public static ILInterpreterRuntimeService Shared { get; } = new();

    public ILDomain? ActiveDomain { get; private set; }
    public string ModuleRoot { get; set; } = "";
    public string PrimaryModuleName { get; set; } = "Game.Hotfix";

    /// <summary>Live behaviour instances in the active domain.</summary>
    public int LiveInstances => _live.Count;
    public long DomainsCreated { get; private set; }
    public long LateContinuationsDropped { get; private set; }

    private readonly Dictionary<ILObject, string> _live = new();
    private readonly List<Action> _pendingContinuations = new();

    /// <summary>Bindings every module may use; extend per project.</summary>
    public HostBindingRegistry Host { get; } = new();

    /// <summary>Common BCL interfaces/types referenced by module metadata but never
    /// executed directly. Registered so signature resolution succeeds.</summary>
    private static void RegisterCommonMarkers(HostBindingRegistry host)
    {
        host.Bind("System.IDisposable", typeof(IDisposable));
        host.Bind("System.IAsyncDisposable", typeof(IAsyncDisposable));
        host.Bind("System.IAsyncStateMachine", typeof(System.Runtime.CompilerServices.IAsyncStateMachine));
        host.Bind("System.Runtime.CompilerServices.IAsyncStateMachine", typeof(System.Runtime.CompilerServices.IAsyncStateMachine));
        host.Bind("System.Runtime.CompilerServices.ICriticalNotifyCompletion", typeof(System.Runtime.CompilerServices.ICriticalNotifyCompletion));
        host.Bind("System.Runtime.CompilerServices.INotifyCompletion", typeof(System.Runtime.CompilerServices.INotifyCompletion));
        host.Bind("System.IEquatable`1", typeof(IEquatable<>));
        host.Bind("System.IComparable`1", typeof(IComparable<>));
        host.Bind("System.IComparable", typeof(IComparable));
        host.Bind("System.Collections.Generic.IEnumerable`1", typeof(IEnumerable<>));
        host.Bind("System.IFormatProvider", typeof(IFormatProvider));        host.Bind("System.ValueType", typeof(ValueType));
        host.Bind("System.Enum", typeof(Enum));
        host.Bind("System.Delegate", typeof(Delegate));
        host.Bind("System.MulticastDelegate", typeof(MulticastDelegate));
        host.Bind("System.Attribute", typeof(Attribute));
        host.Bind("System.Object", typeof(object));
        host.Bind("System.String", typeof(string));
        host.Bind("System.Exception", typeof(Exception));
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
        var exb = host.Bind("System.Exception", typeof(Exception));
        exb.Methods[(".ctor", 0)] = _ => default;
        exb.Methods[(".ctor", 1)] = c => default;
        exb.Methods[("get_Message", 0)] = c => Slot.Ref(((Exception)c.This!).Message);
        var strb = host.Bind("System.String", typeof(string));
        strb.Methods[("Concat", 2)] = c => Slot.Ref(string.Concat((string?)c.Args[0].O, c.Args[1].Boxed()?.ToString()));
        strb.Methods[("get_Length", 0)] = c => Slot.I4(((string)c.This!).Length);
        strb.Methods[("op_Equality", 2)] = c => Slot.I4(string.Equals((string?)c.Args[0].O, (string?)c.Args[1].O, StringComparison.Ordinal) ? 1 : 0);
        var i32b = host.Bind("System.Int32", typeof(int));
        i32b.Methods[("ToString", 0)] = c => Slot.Ref(c.This!.ToString()!);
        i32b.Methods[("CompareTo", 1)] = c => Slot.I4(((int)c.This!).CompareTo(c.Args[0].AsI4()));
        host.Bind("System.Boolean", typeof(bool));
        host.Bind("System.Char", typeof(char));
        host.Bind("System.Byte", typeof(byte));
        host.Bind("System.SByte", typeof(sbyte));
        host.Bind("System.Int16", typeof(short));
        host.Bind("System.UInt16", typeof(ushort));
        host.Bind("System.UInt32", typeof(uint));
        host.Bind("System.UInt64", typeof(ulong));
        host.Bind("System.IntPtr", typeof(IntPtr));
        host.Bind("System.UIntPtr", typeof(UIntPtr));
        host.Bind("System.Single", typeof(float));
        host.Bind("System.Double", typeof(double));
    }

    /// <summary>Loads every module under <see cref="ModuleRoot"/> into a fresh domain.</summary>
    public ILDomain EnsureDomain()
    {
        if (ActiveDomain is not null) return ActiveDomain;

        DefaultHostBindings.Register(Host);
        RegisterCommonMarkers(Host);
        var domain = new ILDomain { Host = Host };
        domain.Host.AsyncSupport[typeof(int).FullName!] = MakeSupport<int>(host: domain.Host);
        domain.Host.AsyncSupport[typeof(float).FullName!] = MakeSupport<float>(host: domain.Host);
        domain.Host.AsyncSupport[typeof(double).FullName!] = MakeSupport<double>(host: domain.Host);
        domain.Host.AsyncSupport[typeof(string).FullName!] = MakeSupport<string>(host: domain.Host);
        // N4 scope: one primary module per domain generation. The module-set
        // manifest (multiple hotfix assemblies per domain) arrives with the N5
        // build pipeline, where the module list is part of the content receipt.
        var primary = Path.Combine(ModuleRoot, PrimaryModuleName + ".dll");
        if (File.Exists(primary))
        {
            domain.LoadModule(primary);
        }

        ActiveDomain = domain;
        DomainsCreated++;
        return domain;
    }

    private static ILAsyncSupport MakeSupport<T>(HostBindingRegistry host) where T : notnull
        => typeof(T) == typeof(int) ? new XTaskAsyncSupport<int>(s => (int)s.I, v => Slot.I4(v))
        : typeof(T) == typeof(float) ? new XTaskAsyncSupport<float>(s => (float)s.F, v => Slot.R4(v))
        : typeof(T) == typeof(double) ? new XTaskAsyncSupport<double>(s => s.F, v => Slot.R8(v))
        : typeof(T) == typeof(string) ? new XTaskAsyncSupport<string>(s => (string)(s.O ?? ""), v => Slot.Ref(v))
        : throw new NotSupportedException(typeof(T).Name);

    /// <summary>
    /// Creates an interpreted instance of <paramref name="typeName"/> from
    /// <paramref name="moduleName"/> and applies the serialized field pairs
    /// (name=invariant value; int/long/float/double/bool/string).
    /// </summary>
    public ILObject CreateBehaviour(string moduleName, string typeName, IReadOnlyList<string>? fieldPairs = null)
    {
        var domain = EnsureDomain();
        var def = domain.FindTypeDef(typeName)
            ?? throw new ILError($"interpreted type '{typeName}' not found in module '{moduleName}'");

        var ctor = def.Methods.TryGetValue((".ctor", 0), out var c)
            ? c
            : throw new ILError($"interpreted type '{typeName}' has no parameterless ctor");

        closedTypeOf(domain, def, out var closed, out var total);
        var instance = new ILObject(closed, total);
        var ctorArgs = new Slot[1];
        ctorArgs[0] = Slot.Ref(instance);
        Interpreter.CallMethod(ctor, ctorArgs, domain, 0);

        if (fieldPairs is not null)
        {
            closed.EnsureLayout();
            foreach (var pair in fieldPairs)
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string name = pair[..eq];
                string raw = pair[(eq + 1)..];
                if (!closed.Layout!.TryGetValue(name, out var idx))
                    throw new ILError($"interpreted field '{name}' not found on {typeName}");
                instance.F[idx] = ParseField(raw);
            }
        }

        _live[instance] = typeName;
        return instance;
    }

    /// <summary>Invokes an instance method on a live interpreted behaviour.</summary>
    public Slot Forward(ILObject instance, string methodName, params Slot[] args)
    {
        var domain = ActiveDomain ?? throw new ILError("no active interpreter domain");
        var def = instance.Type.Def ?? throw new ILError("instance has no module definition");
        if (!def.Methods.TryGetValue((methodName, args.Length), out var mi))
            return default; // lifecycle hooks are optional
        var callArgs = new Slot[args.Length + 1];
        callArgs[0] = Slot.Ref(instance);
        Array.Copy(args, 0, callArgs, 1, args.Length);
        return Interpreter.CallMethod(mi, callArgs, domain, 0);
    }

    /// <summary>Releases a behaviour instance; returns the live count after release.</summary>
    public int ReleaseBehaviour(ILObject instance) => _live.Remove(instance) ? _live.Count : _live.Count;

    /// <summary>
    /// Deactivates the current domain (late continuations are dropped and
    /// counted) and drops all live handles. The next <see cref="EnsureDomain"/>
    /// builds a fresh domain at a new generation.
    /// </summary>
    public void RecycleDomain()
    {
        if (ActiveDomain is null) return;
        LateContinuationsDropped += ActiveDomain.DrainDroppedContinuations();
        ActiveDomain.Deactivate();
        ActiveDomain = null;
        _live.Clear();
    }

    private static void closedTypeOf(ILDomain domain, ILTypeDef def, out ILType closed, out int total)
    {
        closed = domain.OpenTypeOf(def);
        closed.EnsureLayout();
        total = closed.TotalFields;
    }

    private static Slot ParseField(string raw) => raw switch
    {
        "true" => Slot.I4(1),
        "false" => Slot.I4(0),
        _ when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => Slot.I4(i),
        _ when long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => Slot.I8(l),
        _ when float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) => Slot.R4(f),
        _ when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) => Slot.R8(dd),
        _ => Slot.Ref(raw),
    };
}
