using System;
using System.Collections.Generic;

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using XEngine.ILInterpreter.Loading;

namespace XEngine.ILInterpreter.VM;

internal enum CallSiteKind
{
    InterpretedMethod,
    InterpretedCtor,
    HostMethod,
    HostCtor,
}

internal sealed class ResolvedCallSite
{
    public CallSiteKind Kind;
    public ILMethod? Method;   // interpreted target (instantiated when generic)
    public ILType? ClosedDecl;     // closed declaring type when the ref is on a TypeSpec
    public string? HostType;
    public string? HostName;
    public int HostParamCount;
    public bool HostHasThis;
    public bool HostReturnsVoid;
}

internal static class CallSiteResolver
{
    public static ResolvedCallSite Resolve(ILDomain d, ILMethod m, int ip)
    {
        if ((uint)ip >= (uint)m.TokenCache.Length)
        {
            throw new ILError($"call token index {ip} outside cache length {m.TokenCache.Length} in {m.DisplayName}");
        }
        int token = (int)m.Code[ip].I;
        if (m.TokenCache[ip] is ResolvedCallSite cached)
        {
            return cached;
        }

        var site = ResolveToken(d, token, new TypeResolutionContext
        {
            ClassArgs = m.ClassArgs,
            MethodArgs = m.MethodArgs,
        });
        m.TokenCache[ip] = site;
        return site;
    }

    public static ResolvedCallSite ResolveToken(ILDomain d, int token, TypeResolutionContext callerCtx)
    {
        var handle = MetadataTokens.EntityHandle(token);
        var md = d.Module.Md;

        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var mi = d.MethodByRid(MetadataTokens.GetRowNumber((MethodDefinitionHandle)handle));
                if (mi.Name == ".ctor")
                {
                    // same-module newobj tokens point at the ctor MethodDef directly
                    return new ResolvedCallSite
                    {
                        Kind = CallSiteKind.InterpretedCtor,
                        Method = mi,
                        ClosedDecl = d.OpenTypeOf(mi.Decl),
                    };
                }
                return new ResolvedCallSite { Kind = CallSiteKind.InterpretedMethod, Method = mi };
            }

            case HandleKind.MemberReference:
                return ResolveMemberRef(d, (MemberReferenceHandle)handle, callerCtx);

            case HandleKind.MethodSpecification:
            {
                var ms = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                var methodArgs = DecodeMethodSpecArgs(d, ms.Signature, callerCtx);
                var parent = ms.Method;

                if (parent.Kind == HandleKind.MethodDefinition)
                {
                    var mi = d.MethodByRid(MetadataTokens.GetRowNumber((MethodDefinitionHandle)parent));
                    var inst = mi.Instantiate(Array.Empty<ILType>(), methodArgs);
                    return new ResolvedCallSite { Kind = CallSiteKind.InterpretedMethod, Method = inst };
                }

                if (parent.Kind == HandleKind.MemberReference)
                {
                    var inner = ResolveMemberRef(d, (MemberReferenceHandle)parent, callerCtx);
                    if (inner.Kind == CallSiteKind.InterpretedMethod)
                    {
                        var closed = inner.ClosedDecl;
                        var classArgs = closed?.Args ?? Array.Empty<ILType>();
                        inner.Method = inner.Method!.Instantiate(classArgs, methodArgs);
                        return inner;
                    }
                    // Generic methods on a host type (the async builder's
                    // Start/AwaitUnsafeOnCompleted<TAwaiter,TStateMachine>)
                    // are intercepted by the VM using the declaring type and
                    // method name.  Their method type arguments do not alter
                    // the probe binding, so retain the resolved host call site
                    // instead of rejecting it as an unsupported generic call.
                    if (inner.Kind == CallSiteKind.HostMethod)
                    {
                        return inner;
                    }
                    throw new ILError("generic host method calls are not supported by the probe");
                }

                throw new ILError("bad MethodSpec parent");
            }

            default:
                throw new ILError($"bad call token 0x{token:X8} ({handle.Kind})");
        }
    }

    private static ResolvedCallSite ResolveMemberRef(ILDomain d, MemberReferenceHandle h, TypeResolutionContext callerCtx)
    {
        var mr = d.Module.Md.GetMemberReference(h);
        string name = d.Module.NameOf(mr.Name);
        var sig = ModuleSigDecoder.DecodeMethodSig(mr.Signature, d.Module.Md);
        int pc = sig.Params.Length;
        var parent = mr.Parent;

        switch (parent.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                int rid = MetadataTokens.GetRowNumber((TypeDefinitionHandle)parent);
                var def = d.BuildsByRid(rid).Def;
                if (name == ".ctor")
                {
                    return new ResolvedCallSite
                    {
                        Kind = CallSiteKind.InterpretedCtor,
                        Method = def.Methods[(".ctor", pc)],
                        ClosedDecl = d.GetModuleType(rid),
                    };
                }
                if (!def.Methods.TryGetValue((name, pc), out var mi))
                {
                    throw new ILError($"method {def.FullName}::{name}/{pc} not found in module");
                }
                return new ResolvedCallSite { Kind = CallSiteKind.InterpretedMethod, Method = mi, ClosedDecl = d.GetModuleType(rid) };
            }

            case HandleKind.TypeSpecification:
            {
                var closed = d.ResolveTypeSpec((TypeSpecificationHandle)parent, callerCtx);

                if (closed.Def is null)
                {
                    // closed generic over a host type (e.g. ProbeTask<int>)
                    return new ResolvedCallSite
                    {
                        Kind = name == ".ctor" ? CallSiteKind.HostCtor : CallSiteKind.HostMethod,
                        HostType = closed.OpenType?.FullName ?? closed.FullName,
                        HostName = name,
                        HostParamCount = pc,
                        HostHasThis = sig.HasThis,
                        HostReturnsVoid = sig.Ret is SigPrimitive { IsVoid: true },
                        ClosedDecl = closed,
                    };
                }

                if (name == ".ctor")
                {
                    return new ResolvedCallSite
                    {
                        Kind = CallSiteKind.InterpretedCtor,
                        Method = closed.Def.Methods[(".ctor", pc)],
                        ClosedDecl = closed,
                    };
                }
                if (!closed.Def.Methods.TryGetValue((name, pc), out var mi))
                {
                    throw new ILError($"method {closed.FullName}::{name}/{pc} not found in module");
                }
                return new ResolvedCallSite
                {
                    Kind = CallSiteKind.InterpretedMethod,
                    Method = mi.Instantiate(closed.Args, Array.Empty<ILType>()),
                    ClosedDecl = closed,
                };
            }

            case HandleKind.TypeReference:
            {
                string typeName = TypeRefFullName(d, (TypeReferenceHandle)parent);
                return new ResolvedCallSite
                {
                    Kind = name == ".ctor" ? CallSiteKind.HostCtor : CallSiteKind.HostMethod,
                    HostType = typeName,
                    HostName = name,
                    HostParamCount = pc,
                    HostHasThis = sig.HasThis,
                    HostReturnsVoid = sig.Ret is SigPrimitive { IsVoid: true },
                };
            }

            default:
                throw new ILError($"unsupported member ref parent {parent.Kind}");
        }
    }

    private static ILType[] DecodeMethodSpecArgs(ILDomain d, BlobHandle sig, TypeResolutionContext callerCtx)
    {
        var r = d.Module.Md.GetBlobReader(sig);
        if (r.ReadByte() != 0x0A) throw new ILError("bad MethodSpec signature");
        int n = r.ReadCompressedInteger();
        var args = new ILType[n];
        for (int i = 0; i < n; i++)
        {
            args[i] = ModuleSigDecoder.DecodeTypeOnly(r).Resolve(d, callerCtx);
        }
        return args;
    }

    public static string TypeRefFullName(ILDomain d, TypeReferenceHandle h)
    {
        var tr = d.Module.Md.GetTypeReference(h);
        string ns = d.Module.NameOf(tr.Namespace);
        string name = d.Module.NameOf(tr.Name);
        string full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return TypeRefFullName(d, (TypeReferenceHandle)tr.ResolutionScope) + "+" + full;
        }
        return full;
    }
}
