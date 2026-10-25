using System;
using System.Collections.Generic;

namespace XEngine.ILInterpreter.VM;

public enum ILSlotKind : byte
{
    Null,
    I4,
    I8,
    R4,
    R8,
    Obj,     // any reference: string, ILObject (class or boxed struct), ILArray, ILDelegate, host object
    ByRef,   // managed pointer, R (ILRef) set
}

/// <summary>
/// One storage cell on the eval stack / locals / fields / statics. Untyped with a
/// discriminator — probe-grade; the product VM replaces this with typed slots.
/// Struct values live as heap instances (<see cref="ILObject"/>) and copies are
/// made at assignment/argument/passing points, mirroring IL value semantics.
/// </summary>
public struct Slot
{
    public ILSlotKind Kind;
    public long I;
    public double F;
    public object? O;
    public ILRef? R;

    public static Slot I4(int v) => new() { Kind = ILSlotKind.I4, I = v };

    public static Slot I8(long v) => new() { Kind = ILSlotKind.I8, I = v };

    public static Slot R4(float v) => new() { Kind = ILSlotKind.R4, F = v };

    public static Slot R8(double v) => new() { Kind = ILSlotKind.R8, F = v };

    public static Slot Ref(object? o) => new() { Kind = ILSlotKind.Obj, O = o };

    public static Slot RefOrNull(object? o) => o is null ? Null : new Slot { Kind = ILSlotKind.Obj, O = o };

    public static Slot ByRef(ILRef r) => new() { Kind = ILSlotKind.ByRef, R = r };

    public static readonly Slot Null = new() { Kind = ILSlotKind.Null };

    public int AsI4() => Kind == ILSlotKind.I4 ? (int)I : (int)I8v();

    public long I8v() => I;

    public double AsF8() => Kind switch
    {
        ILSlotKind.R4 or ILSlotKind.R8 => F,
        ILSlotKind.I4 or ILSlotKind.I8 => I,
        _ => throw new ILError($"slot kind {Kind} is not numeric"),
    };

    public object? Boxed()
    {
        switch (Kind)
        {
            case ILSlotKind.I4: return (int)I;
            case ILSlotKind.I8: return I;
            case ILSlotKind.R4: return (float)F;
            case ILSlotKind.R8: return F;
            case ILSlotKind.Obj: return O;
            case ILSlotKind.Null: return null;
            default: throw new ILError($"cannot box slot kind {Kind}");
        }
    }

    public override string ToString() => Kind switch
    {
        ILSlotKind.Null => "null",
        ILSlotKind.I4 => $"i4({I})",
        ILSlotKind.I8 => $"i8({I})",
        ILSlotKind.R4 => $"r4({F})",
        ILSlotKind.R8 => $"r8({F})",
        ILSlotKind.Obj => O?.ToString() ?? "obj(null)",
        ILSlotKind.ByRef => "byref",
        _ => "?",
    };
}

public enum RefKind : byte
{
    Slots,      // Owner = Slot[] (locals, fields of an instance, statics)
    ArrayElem,  // Owner = ILArray
}

/// <summary>Minimal managed pointer: a reference to one storage cell.</summary>
public sealed class ILRef
{
    public object Owner = default!;
    public int Index;
    public ILRef? Base;

    public static ILRef ToSlots(Slot[] slots, int index) => new() { Owner = slots, Index = index };

    public static ILRef ToArrayElem(ILArray arr, int index) => new() { Owner = arr, Index = index };

    public static ILRef ToField(ILObject instance, int index) => new() { Owner = instance, Index = index };

    public static ILRef ToNestedField(ILRef parent, int index) => new() { Base = parent, Index = index };

    public Slot Read()
    {
        if (Base is not null)
        {
            var parent = Base.Read();
            if (parent.O is not ILObject instance) throw new ILError("field ref parent is not an instance");
            return instance.F[Index];
        }
        return Owner switch
        {
            Slot[] s => s[Index],
            ILArray a => a.LoadElement(Index),
            ILObject i => i.F[Index],
            _ => throw new ILError("bad ref owner"),
        };
    }

    public void Write(Slot value)
    {
        if (Base is not null)
        {
            var parent = Base.Read();
            if (parent.O is not ILObject instance) throw new ILError("field ref parent is not an instance");
            instance.F[Index] = value;
            Base.Write(Slot.Ref(instance));
            return;
        }
        switch (Owner)
        {
            case Slot[] s:
                s[Index] = value;
                break;
            case ILArray a:
                a.StoreElement(Index, value);
                break;
            case ILObject i:
                i.F[Index] = value;
                break;
            default:
                throw new ILError("bad ref owner");
        }
    }
}

/// <summary>Class instance or struct value. Struct copies duplicate <see cref="F"/>.</summary>
public sealed class ILObject
{
    public ILType Type;
    public Slot[] F;

    public ILObject(ILType type, int fieldCount)
    {
        Type = type;
        F = new Slot[fieldCount];
    }

    public ILObject CloneStruct() => new(Type, F.Length) { F = (Slot[])F.Clone() };

    public override string ToString() => $"{Type.FullName} instance";
}

/// <summary>Single-dimension zero-based array.</summary>
public sealed class ILArray
{
    public ILType ElementType;
    public Array Data;

    public ILArray(ILType elementType, int length)
    {
        ElementType = elementType;
        Data = elementType.PrimKind switch
        {
            ILPrim.I4 => new int[length],
            ILPrim.I8 => new long[length],
            ILPrim.R4 => new float[length],
            ILPrim.R8 => new double[length],
            _ => new object?[length],
        };
    }

    public int Length => Data.Length;

    public void CheckIndex(int i)
    {
        if ((uint)i >= (uint)Data.Length)
        {
            throw new ILThrow(new IndexOutOfRangeException($"array length {Data.Length}, index {i}"));
        }
    }

    public Slot LoadElement(int i)
    {
        CheckIndex(i);
        switch (Data)
        {
            case int[] a: return Slot.I4(a[i]);
            case long[] a: return Slot.I8(a[i]);
            case float[] a: return Slot.R4(a[i]);
            case double[] a: return Slot.R8(a[i]);
            case object?[] a:
                var v = a[i];
                if (v is ILObject st && st.Type.IsValueType)
                {
                    return Slot.Ref(st.CloneStruct()); // copy-out semantics
                }
                return v is null ? Slot.Null : Slot.Ref(v);
            default: throw new ILError("bad array type");
        }
    }

    public void StoreElement(int i, Slot value)
    {
        CheckIndex(i);
        switch (Data)
        {
            case int[] a: a[i] = value.AsI4(); break;
            case long[] a: a[i] = value.I8v(); break;
            case float[] a: a[i] = (float)value.AsF8(); break;
            case double[] a: a[i] = value.AsF8(); break;
            case object?[] a:
                if (value.O is ILObject st && st.Type.IsValueType)
                {
                    a[i] = st.CloneStruct(); // copy-in semantics
                }
                else
                {
                    a[i] = value.O;
                }
                break;
            default: throw new ILError("bad array type");
        }
    }
}

/// <summary>Interpreted or bridged delegate. Multicast keeps a flat handler list.</summary>
public sealed class ILDelegate
{
    public object? Target;             // ILObject or host object
    public ILMethod? Method;       // interpreted method
    public Delegate? HostDel;          // host callable
    public List<ILDelegate>? Multi;    // multicast body (never nested)

    public static ILDelegate Combine(ILDelegate? a, ILDelegate? b)
    {
        if (a is null) return b!;
        if (b is null) return a;
        var list = new List<ILDelegate>();
        AppendInto(list, a);
        AppendInto(list, b);
        return new ILDelegate { Multi = list };
    }

    private static void AppendInto(List<ILDelegate> list, ILDelegate d)
    {
        if (d.Multi is not null)
        {
            list.AddRange(d.Multi);
        }
        else
        {
            list.Add(d);
        }
    }

    public static ILDelegate? Remove(ILDelegate? source, ILDelegate? value)
    {
        if (source is null || value is null) return source;
        var src = source.Multi ?? new List<ILDelegate> { source };
        var val = value.Multi ?? new List<ILDelegate> { value };
        // Delegate.Remove drops the last occurrence of the value sequence.
        for (int start = src.Count - val.Count; start >= 0; start--)
        {
            bool match = true;
            for (int i = 0; i < val.Count; i++)
            {
                if (!ReferenceEquals(src[start + i], val[i]))
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                var remaining = new List<ILDelegate>();
                for (int i = 0; i < src.Count; i++)
                {
                    if (i < start || i >= start + val.Count)
                    {
                        remaining.Add(src[i]);
                    }
                }
                return remaining.Count switch
                {
                    0 => null,
                    1 => remaining[0],
                    _ => new ILDelegate { Multi = remaining },
                };
            }
        }
        return source;
    }
}

/// <summary>A VM-level exception in flight (interpreted object or host exception).</summary>
public sealed class ILThrow : Exception
{
    public object Value; // ILObject or host Exception

    public ILThrow(object value) : base($"vm-throw: {value}") => Value = value;
}

/// <summary>Host-side wrapper when an interpreted exception escapes to host code.</summary>
public sealed class ILHostException : Exception
{
    public ILObject Inner;

    public ILHostException(string message, ILObject inner) : base(message) => Inner = inner;
}

/// <summary>Hard VM error: unsupported construct, missing binding, malformed module. Not catchable by interpreted code.</summary>
public sealed class ILError : Exception
{
    public string? MethodName;
    public int IlOffset;

    public ILError(string message) : base(message)
    {
    }

    public ILError At(string method, int offset)
    {
        MethodName = method;
        IlOffset = offset;
        return this;
    }
}
