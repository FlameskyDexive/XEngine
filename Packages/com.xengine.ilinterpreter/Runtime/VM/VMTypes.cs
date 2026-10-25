using System;
using System.Collections.Generic;

using XEngine.ILInterpreter.Loading;
namespace XEngine.ILInterpreter.VM;

public enum ILPrim : byte
{
    Other,
    I4,
    I8,
    R4,
    R8,
}

/// <summary>
/// A VM type: primitive, host type, module-defined type, or closed generic
/// instantiation of a module-defined type. Identity = (definition, type args).
/// Static fields and cctor state live per closed type.
/// </summary>
public sealed class ILType
{
    public string FullName = "";
    public ILPrim PrimKind;
    public bool IsValueType;
    public bool IsInterface;
    public bool IsDelegate;
    public ILType? Base;
    public List<ILType> Interfaces = new();
    public ILType[] Args = Array.Empty<ILType>();
    public ILTypeDef? Def;          // module-defined (open definition when Args non-empty)
    public Type? HostType;          // host-bridged
    public bool IsPlaceholder;      // referenced but not bound: never executable
    public ILType? OpenType;        // non-null when this is a closed instantiation

    public Slot[]? Statics;
    public bool CctorDone;

    /// <summary>Field layout across the interpreted base chain (base fields first).</summary>
    public Dictionary<string, int>? Layout;
    public int TotalFields;

    public bool IsGenericInstance => OpenType is not null;

    public void EnsureLayout()
    {
        if (Layout is not null) return;
        var chain = new List<ILType>();
        for (ILType? t = this; t is not null && t.Def is not null; t = t.Base)
        {
            chain.Add(t);
        }
        Layout = new Dictionary<string, int>();
        int idx = 0;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var def = chain[i].Def!;
            foreach (var f in def.FieldNames)
            {
                if (!Layout.ContainsKey(f))
                {
                    Layout[f] = idx++;
                }
            }
        }
        TotalFields = idx;
    }

    public bool IsExceptionLike => HostType is { } h && typeof(Exception).IsAssignableFrom(h);

    public override string ToString() => FullName;

    public bool IsAssignableTo(ILType target)
    {
        if (ReferenceEquals(this, target)) return true;
        if (target.IsInterface)
        {
            for (ILType? t = this; t is not null; t = t.Base)
            {
                for (int i = 0; i < t.Interfaces.Count; i++)
                {
                    if (t.Interfaces[i].IsAssignableTo(target)) return true;
                }
            }
            return false;
        }
        for (ILType? t = this; t is not null; t = t.Base)
        {
            if (ReferenceEquals(t, target)) return true;
        }
        return false;
    }

    /// <summary>True when the chain includes this host type (used for catch matching).</summary>
    public bool ChainHasHost(Type host)
    {
        if (HostType is not null) return host.IsAssignableFrom(HostType);
        for (ILType? t = this; t is not null; t = t.Base)
        {
            if (t.HostType is { } h && host.IsAssignableFrom(h)) return true;
        }
        return false;
    }
}

/// <summary>Module-defined type definition (open generic). Opened via <see cref="ILDomain.OpenGenericType"/>.</summary>
public sealed class ILTypeDef
{
    public required ILDomain D;
    public required string FullName;
    public bool IsValueType;
    public bool IsEnum;
    public bool IsDelegate;
    public bool IsInterface;
    public int GenericParamCount;
    public int BaseToken;           // raw token or 0
    public List<int> InterfaceTokens = new();
    public List<string> FieldNames = new();
    public Dictionary<(string Name, int Params), ILMethod> Methods = new();
    public Dictionary<string, ILType> GenericParamNames = new(); // name → placeholder type (Var i)

    public ILType BuildOpenType()
    {
        var t = new ILType
        {
            FullName = FullName,
            Def = this,
            IsValueType = IsValueType,
            IsInterface = IsInterface,
            IsDelegate = IsDelegate,
        };
        return t;
    }
}

/// <summary>An interpreted method. Generic methods are only invoked via <see cref="Instantiate"/>.</summary>
public sealed class ILMethod
{
    public required ILTypeDef Decl;
    public required string Name;
    public bool IsStatic;
    public bool ReturnsVoid;
    public int GenericParamCount;
    public int ParamCount;
    public byte[] Il = Array.Empty<byte>();
    public Instr[] Code = Array.Empty<Instr>();
    public int[] IlOffsets = Array.Empty<int>();
    public SigTypeNode[] LocalSig = Array.Empty<SigTypeNode>();
    public ILEhClause[] Ehs = Array.Empty<ILEhClause>();
    public int MaxStack = 64;
    public int LocalCount;
    public object?[] TokenCache = Array.Empty<object?>();  // per-instruction token resolution

    // Invocation-time generic context (set on instantiated copies).
    public ILType[] ClassArgs = Array.Empty<ILType>();
    public ILType[] MethodArgs = Array.Empty<ILType>();

    public string DisplayName => $"{Decl.FullName}.{Name}";

    public ILMethod Instantiate(ILType[] classArgs, ILType[] methodArgs)
    {
        return new ILMethod
        {
            Decl = Decl,
            Name = Name,
            IsStatic = IsStatic,
            ReturnsVoid = ReturnsVoid,
            GenericParamCount = GenericParamCount,
            ParamCount = ParamCount,
            Il = Il,
            Code = Code,
            IlOffsets = IlOffsets,
            LocalSig = LocalSig,
            Ehs = Ehs,
            MaxStack = MaxStack,
            LocalCount = LocalCount,
            TokenCache = new object?[Code.Length],
            ClassArgs = classArgs,
            MethodArgs = methodArgs,
        };
    }
}

/// <summary>One EH clause from the method body (instruction indexes; ends exclusive).</summary>
public sealed class ILEhClause
{
    public ILEhFlags Flags;
    public int TryStart, TryEnd;
    public int HandlerStart, HandlerEnd;
    public int ClassToken; // TypeRef/TypeDef token for catch clauses
}

[Flags]
public enum ILEhFlags : byte
{
    Exception = 0,
    Filter = 1,
    Finally = 2,
    Fault = 4,
}
