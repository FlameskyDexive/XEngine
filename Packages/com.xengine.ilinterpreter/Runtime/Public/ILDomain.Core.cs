using System;
using System.Collections.Generic;

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using XEngine.ILInterpreter.Binding;
using XEngine.ILInterpreter.Loading;

namespace XEngine.ILInterpreter.VM;

/// <summary>
/// One interpreted execution domain: a module set, resolved types, static state,
/// host bindings, and the async continuation queue with generation checking.
/// </summary>
public sealed class ILDomain
{
    public ILPeModule Module = default!;
    public HostBindingRegistry Host { get; init; } = new();

    private readonly List<ILLoadedModule> _modules = new();
    public IReadOnlyList<ILLoadedModule> Modules => _modules;

    public long InstructionCount;
    public long MaxInstructions = 200_000_000;
    public int MaxCallDepth = 256;

    // async pump state
    public int Generation = 1;
    public bool Active = true;
    private readonly Queue<ILAsyncContinuation> _continuations = new();
    public int DroppedContinuations { get; private set; }
    public int PendingContinuations => _continuations.Count;

    // type caches
    private readonly Dictionary<string, ILType> _hostTypes = new();
    private readonly Dictionary<int, ILType> _moduleTypes = new();
    private readonly Dictionary<string, ILType> _genericInstances = new();
    private readonly Dictionary<string, ILType> _arrayTypes = new();
    private readonly Dictionary<int, TypeDefBuild> _builds = new();
    private readonly Dictionary<string, ILType> _typeRefCache = new();
    private readonly Dictionary<string, ILType> _typeDefNames = new();
    private readonly Dictionary<int, ILMethod> _methodByRid = new();

    internal TypeDefBuild BuildsByRid(int rid) => _builds[rid];

    public ILMethod MethodByRid(int rid) => _methodByRid[rid];

    public ILTypeDef? FindTypeDef(string fullName)
    {
        foreach (var build in _builds.Values)
        {
            if (build.Def.FullName == fullName)
            {
                return build.Def;
            }
        }
        return null;
    }

    public ILType OpenTypeOf(ILTypeDef def)
    {
        foreach (var (rid, build) in _builds)
        {
            if (ReferenceEquals(build.Def, def))
            {
                return GetModuleType(rid);
            }
        }
        throw new ILError($"no open type for {def.FullName}");
    }

    public sealed class ILLoadedModule
    {
        public required ILPeModule Module;
        public required Dictionary<int, ILDomain.TypeDefBuild> Builds;
        public required Dictionary<int, ILMethod> Methods;
        public required Dictionary<string, ILType> TypeNames;
    }

    public sealed class TypeDefBuild
    {
        public ILTypeDef Def = default!;
        public int BaseToken;
        public int[] InterfaceTokens = Array.Empty<int>();
    }

    public void LoadModule(string path)
    {
        try
        {
            Module = ILPeModule.Open(path);
            BuildTypeDefs();
        }
        catch (ILError)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Public error surface: every malformed-module failure is an ILError with the
            // underlying cause; never a raw PEReader/metadata exception.
            throw new ILError($"invalid module '{System.IO.Path.GetFileName(path)}': {ex.Message} @ {ex.StackTrace?.TrimEnd()}");
        }
    }

    // ---------------------------------------------------------------- module scan

    private void BuildTypeDefs()
    {
        var md = Module.Md;

        // Pass 1: create defs (so base/interface resolution can reference any type).
        var rowToHandle = new Dictionary<int, TypeDefinitionHandle>();
        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            int rid = MetadataTokens.GetRowNumber(handle);
            string full = TypeFullName(td);

            var def = new ILTypeDef { D = this, FullName = full };

            foreach (var fh in td.GetFields())
            {
                var fd = md.GetFieldDefinition(fh);
                def.FieldNames.Add(Module.NameOf(fd.Name));
            }

            var build = new TypeDefBuild
            {
                Def = def,
                BaseToken = td.BaseType.IsNil ? 0 : MetadataTokens.GetToken(td.BaseType),
            };

            var ifaces = new List<int>();
            foreach (var ih in td.GetInterfaceImplementations())
            {
                ifaces.Add(MetadataTokens.GetToken(md.GetInterfaceImplementation(ih).Interface));
            }
            build.InterfaceTokens = ifaces.ToArray();

            _builds[rid] = build;
            rowToHandle[rid] = handle;
            _typeDefNames[full] = GetModuleType(rid);
        }

        // Pass 2: methods + base/interfaces.
        foreach (var (rid, build) in _builds)
        {
            var def = build.Def;
            var td = md.GetTypeDefinition(rowToHandle[rid]);

            foreach (var mh in td.GetMethods())
            {
                var mdd = md.GetMethodDefinition(mh);
                string mname = Module.NameOf(mdd.Name);
                bool isStatic = (mdd.Attributes & System.Reflection.MethodAttributes.Static) != 0;

                var mi = new ILMethod
                {
                    Decl = def,
                    Name = mname,
                    IsStatic = isStatic,
                };

                _methodByRid[MetadataTokens.GetRowNumber(mh)] = mi;

                var sig = ModuleSigDecoder.DecodeMethodSig(Module, mdd.Signature);
                mi.ParamCount = sig.Params.Length;
                mi.ReturnsVoid = sig.Ret is SigPrimitive { IsVoid: true };
                mi.GenericParamCount = mdd.GetGenericParameters().Count;

                int rva = mdd.RelativeVirtualAddress;
                if (rva != 0)
                {
                    mi.Il = Module.GetMethodIl(rva);
                    int[] byteOffsets;
                    try
                    {
                        var decoded = ILDecoder.Decode(mi.Il);
                        var code = decoded.code;
                        byteOffsets = decoded.offsets;
                        mi.Code = code;
                        // The decoder uses a byte-offset → instruction-index
                        // table for branch/EH resolution.  Keep a separate
                        // instruction-index → byte-offset map for diagnostics.
                        mi.IlOffsets = new int[code.Length];
                        Array.Fill(mi.IlOffsets, -1);
                        for (int byteOffset = 0; byteOffset < byteOffsets.Length; byteOffset++)
                        {
                            int instructionIndex = byteOffsets[byteOffset];
                            if ((uint)instructionIndex < (uint)mi.IlOffsets.Length)
                            {
                                mi.IlOffsets[instructionIndex] = byteOffset;
                            }
                        }
                    }
                    catch (ILError e)
                    {
                        throw new ILError($"decode {def.FullName}::{mname}: {e.Message}");
                    }
                    mi.TokenCache = new object?[mi.Code.Length];

                    var body = Module.Pe.GetMethodBody(rva);
                    var localsSig = body.LocalSignature;
                    mi.LocalSig = localsSig.IsNil
                        ? Array.Empty<SigTypeNode>()
                        : DecodeLocalsSafe(localsSig);
                    mi.LocalCount = mi.LocalSig.Length;
                    mi.Ehs = DecodeEhs(body, il: mi.Il, byteOffsets, mi.Code.Length);
                }

                def.Methods[(mname, mi.ParamCount)] = mi;
            }

            ResolveBaseAndInterfaces(def, build, GetModuleType(rid));
        }
    }

    private SigTypeNode[] DecodeLocalsSafe(StandaloneSignatureHandle h)
    {
        var blob = Module.Md.GetStandaloneSignature(h).Signature;
        try
        {
            return ModuleSigDecoder.DecodeLocals(Module, blob);
        }
        catch (ILError e)
        {
            var rr = Module.Md.GetBlobReader(blob);
            var bytes = rr.ReadBytes(rr.Length);
            throw new ILError(e.Message + " locals-blob=[" + Convert.ToHexString(bytes) + "]");
        }
    }

    private ILEhClause[] DecodeEhs(System.Reflection.Metadata.MethodBodyBlock body, byte[] il, int[] offsets, int instrCount)
    {
        var list = new List<ILEhClause>();
        foreach (var eh in body.ExceptionRegions)
        {
            if (eh.Kind == System.Reflection.Metadata.ExceptionRegionKind.Filter)
            {
                throw new ILError("exception filters are not supported by the probe VM");
            }

            var c = new ILEhClause
            {
                TryStart = ConvStart(offsets, eh.TryOffset),
                HandlerStart = ConvStart(offsets, eh.HandlerOffset),
                TryEnd = ConvEnd(offsets, il.Length, eh.TryOffset + eh.TryLength, instrCount),
                HandlerEnd = ConvEnd(offsets, il.Length, eh.HandlerOffset + eh.HandlerLength, instrCount),
                ClassToken = eh.CatchType.IsNil ? 0 : MetadataTokens.GetToken(eh.CatchType),
            };
            c.Flags = eh.Kind switch
            {
                System.Reflection.Metadata.ExceptionRegionKind.Finally => ILEhFlags.Finally,
                System.Reflection.Metadata.ExceptionRegionKind.Fault => ILEhFlags.Fault,
                _ => ILEhFlags.Exception,
            };

            if (c.Flags == ILEhFlags.Exception && c.ClassToken == 0)
            {
                throw new ILError("catch clause without type token");
            }

            list.Add(c);
        }
        return list.ToArray();
    }

    private static int ConvStart(int[] offsets, int ilOffset)
    {
        if (ilOffset < offsets.Length && offsets[ilOffset] >= 0)
        {
            return offsets[ilOffset];
        }
        throw new ILError($"EH region start 0x{ilOffset:X} not on an instruction boundary");
    }

    private static int ConvEnd(int[] offsets, int ilLength, int ilOffset, int instrCount)
    {
        if (ilOffset == ilLength) return instrCount;
        if (ilOffset < offsets.Length && offsets[ilOffset] >= 0)
        {
            return offsets[ilOffset];
        }
        throw new ILError($"EH region end 0x{ilOffset:X} not on an instruction boundary");
    }

    private string TypeFullName(TypeDefinition td)
    {
        string name = Module.NameOf(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil)
        {
            var outer = Module.Md.GetTypeDefinition(decl);
            return TypeFullName(outer) + "+" + name;
        }

        string ns = Module.NameOf(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private void ResolveBaseAndInterfaces(ILTypeDef def, TypeDefBuild build, ILType open)
    {
        if (build.BaseToken != 0)
        {
            var ctx = TypeResolutionContext.Empty;
            open.Base = ResolveTypeToken(build.BaseToken, ctx);
            var baseName = open.Base.FullName;
            if (baseName == "System.ValueType" || baseName == "System.Enum")
            {
                open.IsValueType = true;
                def.IsValueType = true;
            }
            if (baseName == "System.Enum") def.IsEnum = true;
            if (baseName is "System.Delegate" or "System.MulticastDelegate")
            {
                open.IsDelegate = true;
                def.IsDelegate = true;
            }
            if (open.Base.HostType == typeof(object)) open.Base = null;
        }

        foreach (var token in build.InterfaceTokens)
        {
            var ctx = TypeResolutionContext.Empty;
            open.Interfaces.Add(ResolveTypeToken(token, ctx));
        }
    }

    // ---------------------------------------------------------------- type resolution

    public ILType GetHostType(string fullName, ILPrim prim = ILPrim.Other, bool isVoid = false)
    {
        if (isVoid)
        {
            if (!_hostTypes.TryGetValue("void", out var v))
            {
                v = new ILType { FullName = "void" };
                _hostTypes["void"] = v;
            }
            return v;
        }

        if (_hostTypes.TryGetValue(fullName, out var t)) return t;

        var clr = Host.FindClrType(fullName);
        if (clr is null)
        {
            // Referenced-but-never-executed host types resolve to a placeholder:
            // hierarchy/catch/cast checks see an unbound type; member-level binding
            // enforcement still errors at the call site (plan: no silent fallback).
            t = new ILType
            {
                FullName = fullName,
                IsPlaceholder = true,
            };
            _hostTypes[fullName] = t;
            return t;
        }

        t = new ILType
        {
            FullName = fullName,
            HostType = clr,
            IsValueType = clr.IsValueType,
            PrimKind = prim,
        };
        if (clr != typeof(Exception) && typeof(Exception).IsAssignableFrom(clr))
        {
            t.Base = GetHostType("System.Exception");
        }
        _hostTypes[fullName] = t;
        return t;
    }

    public ILType GetModuleType(int typeDefRid)
    {
        if (_moduleTypes.TryGetValue(typeDefRid, out var t)) return t;

        var def = _builds[typeDefRid].Def;
        var open = def.BuildOpenType();
        _moduleTypes[typeDefRid] = open;
        return open;
    }

    public ILType OpenGenericType(ILType open, ILType[] args)
    {
        var key = open.FullName + "<" + Join(args) + ">";
        if (_genericInstances.TryGetValue(key, out var t)) return t;

        t = new ILType
        {
            FullName = key,
            Def = open.Def,
            OpenType = open,
            Args = args,
            IsValueType = open.IsValueType,
            IsInterface = open.IsInterface,
            IsDelegate = open.IsDelegate,
            Base = open.Base,
        };
        foreach (var i in open.Interfaces)
        {
            t.Interfaces.Add(i);
        }
        _genericInstances[key] = t;
        return t;
    }

    private static string Join(ILType[] args)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(args[i].FullName);
        }
        return sb.ToString();
    }

    public ILType GetArrayType(ILType elem)
    {
        var key = elem.FullName + "[]";
        if (_arrayTypes.TryGetValue(key, out var t)) return t;

        t = new ILType { FullName = key, PrimKind = elem.PrimKind };
        _arrayTypes[key] = t;
        return t;
    }

    public ILType ResolveTypeToken(int token, TypeResolutionContext ctx)
    {
        return (token >> 24) switch
        {
            0x02 => GetModuleType(MetadataTokens.GetRowNumber(MetadataTokens.TypeDefinitionHandle(token))),
            0x01 => ResolveTypeRef(MetadataTokens.TypeReferenceHandle(token)),
            0x1B => ResolveTypeSpec(MetadataTokens.TypeSpecificationHandle(token), ctx),
            _ => throw new ILError($"bad type token 0x{token:X8}"),
        };
    }

    private ILType ResolveTypeRef(TypeReferenceHandle h)
    {
        var tr = Module.Md.GetTypeReference(h);
        string ns = Module.NameOf(tr.Namespace);
        string name = Module.NameOf(tr.Name);
        string full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

        // nested TypeRefs: resolution scope is a TypeRef of the outer type
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var outerFull = ResolveTypeRef((TypeReferenceHandle)tr.ResolutionScope).FullName;
            full = outerFull + "+" + full;
        }

        if (_typeRefCache.TryGetValue(full, out var t)) return t;

        t = GetHostType(full);
        _typeRefCache[full] = t;
        return t;
    }

    public ILType ResolveTypeSpec(TypeSpecificationHandle h, TypeResolutionContext ctx)
    {
        var r = Module.Md.GetBlobReader(Module.Md.GetTypeSpecification(h).Signature);
        var st = ModuleSigDecoder.DecodeTypeOnly(r);
        return st.Resolve(this, ctx);
    }

    // ---------------------------------------------------------------- async pump

    public void EnqueueContinuation(ILRef stateRef, ILMethod moveNext)
    {
        _continuations.Enqueue(new ILAsyncContinuation(Generation, stateRef, moveNext));
    }

    /// <summary>Runs every queued continuation. Returns how many ran.</summary>
    public int Pump()
    {
        int ran = 0;
        while (_continuations.Count > 0)
        {
            var c = _continuations.Dequeue();
            if (!Active || c.Generation != Generation)
            {
                DroppedContinuations++; // late continuation after domain switch: dropped
                continue;
            }
            ran++;
            Interpreter.CallMethod(c.MoveNext, new Slot[] { Slot.ByRef(c.StateRef) }, this, 0);
        }
        return ran;
    }

    public void Deactivate()
    {
        Active = false;
        DroppedContinuations += _continuations.Count;
        _continuations.Clear();
        Generation++;
    }

    public int DrainDroppedContinuations()
    {
        int d = DroppedContinuations;
        DroppedContinuations = 0;
        return d;
    }

    public void NoteDroppedContinuation() => DroppedContinuations++;
}

public readonly struct ILAsyncContinuation
{
    public ILAsyncContinuation(int generation, ILRef stateRef, ILMethod moveNext)
    {
        Generation = generation;
        StateRef = stateRef;
        MoveNext = moveNext;
    }

    public int Generation { get; }
    public ILRef StateRef { get; }
    public ILMethod MoveNext { get; }
}
