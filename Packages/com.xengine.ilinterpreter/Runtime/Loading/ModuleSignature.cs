using System;
using System.Collections.Generic;
using System.IO;

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using XEngine.ILInterpreter.VM;

namespace XEngine.ILInterpreter.Loading;

/// <summary>
/// Read-only PE/CLI module over System.Reflection.Metadata. The DLL is pure
/// data: bytes are validated and read; nothing is handed to the CLR loader.
/// </summary>
public sealed class ILPeModule : IDisposable
{
    public required FileStream Stream;
    public required PEReader Pe;
    public required MetadataReader Md;
    public required string Path;
    public required string AssemblyName;
    public required long FileLength;

    public static ILPeModule Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new ILError($"module not found: {path}");
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            long length = stream.Length;
            var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                throw new ILError($"not a managed CLI image (no CLR metadata): {path}");
            }

            var md = pe.GetMetadataReader();
            var name = md.GetString(md.GetAssemblyDefinition().Name);
            return new ILPeModule { Stream = stream, Pe = pe, Md = md, Path = path, AssemblyName = name, FileLength = length };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public byte[] GetMethodIl(int rva)
    {
        if (rva == 0)
        {
            throw new ILError("method has no IL body (abstract or extern)");
        }

        var body = Pe.GetMethodBody(rva);
        var il = body.GetILBytes() ?? Array.Empty<byte>();

        // Validate EH region boundaries against the body size (malformed-input gate).
        foreach (var region in body.ExceptionRegions)
        {
            if (region.TryOffset + region.TryLength > il.Length
                || region.HandlerOffset + region.HandlerLength > il.Length)
            {
                throw new ILError($"exception clause outside IL bounds (rva 0x{rva:X})");
            }
        }

        return il;
    }


    public string UserString(UserStringHandle h) => Md.GetUserString(h);

    public string NameOf(StringHandle h) => Md.GetString(h);

    public void Dispose()
    {
        Pe.Dispose();
        Stream.Dispose();
    }
}

/// <summary>Decoded signature type node (resolution deferred through the ILDomain).</summary>
public abstract class SigTypeNode
{
    public abstract ILType Resolve(ILDomain d, TypeResolutionContext ctx);
}

public sealed class SigPrimitive : SigTypeNode
{
    public required string HostTypeName; // System.Int32 / System.Single / System.String / System.Object ...
    public ILPrim Prim;
    public bool IsVoid;

    public override ILType Resolve(ILDomain d, TypeResolutionContext ctx) => d.GetHostType(HostTypeName, Prim, IsVoid);
}

public sealed class SigTypeByToken : SigTypeNode
{
    public required int Token;

    public override ILType Resolve(ILDomain d, TypeResolutionContext ctx) => d.ResolveTypeToken(Token, ctx);
}

public sealed class SigGenericInst : SigTypeNode
{
    public required int GenericToken;   // TypeRef/TypeDef of the open generic
    public required bool IsValueType;
    public required SigTypeNode[] Args;

    public override ILType Resolve(ILDomain d, TypeResolutionContext ctx)
    {
        var open = d.ResolveTypeToken(GenericToken, ctx);
        var args = new ILType[Args.Length];
        for (int i = 0; i < Args.Length; i++)
        {
            args[i] = Args[i].Resolve(d, ctx);
        }
        return d.OpenGenericType(open, args);
    }
}

public sealed class SigSzArray : SigTypeNode
{
    public required SigTypeNode Elem;

    public override ILType Resolve(ILDomain d, TypeResolutionContext ctx)
    {
        var elem = Elem.Resolve(d, ctx);
        return d.GetArrayType(elem);
    }
}

public sealed class SigByRefNode : SigTypeNode
{
    public required SigTypeNode Elem;

    public override ILType Resolve(ILDomain d, TypeResolutionContext ctx) => Elem.Resolve(d, ctx);
}

/// <summary>Generic parameter placeholder (type Var n / method MVar n).</summary>
public sealed class SigGenericParam : SigTypeNode
{
    public required int Index;
    public required bool IsMethodVar;

    public override ILType Resolve(ILDomain d, TypeResolutionContext ctx)
    {
        var list = IsMethodVar ? ctx.MethodArgs : ctx.ClassArgs;
        if (Index >= list.Length)
        {
            throw new ILError($"generic parameter {(IsMethodVar ? "!!" : "!")}{Index} out of range ({list.Length} bound)");
        }
        return list[Index];
    }
}

public sealed class TypeResolutionContext
{
    public static readonly TypeResolutionContext Empty = new();
    public ILType[] ClassArgs = Array.Empty<ILType>();
    public ILType[] MethodArgs = Array.Empty<ILType>();
}

/// <summary>Blob reader + signature decoding for the probe subset of ECMA-335 II.23.2.</summary>
public static class ModuleSigDecoder
{
    public static DecodedMethodSig DecodeMethodSig(ILPeModule mod, BlobHandle blob)
    {
        var reader = mod.Md.GetBlobReader(blob);
        try
        {
            return DecodeMethodSig(reader);
        }
        catch (ILError e)
        {
            var copy = mod.Md.GetBlobReader(blob);
            var bytes = copy.ReadBytes(copy.Length);
            throw new ILError(e.Message + " methodsig-blob=[" + Convert.ToHexString(bytes) + "]");
        }
    }

    public static DecodedMethodSig DecodeMethodSig(BlobHandle blob, MetadataReader md)
    {
        return DecodeMethodSig(md.GetBlobReader(blob));
    }

    public static DecodedMethodSig DecodeMethodSig(BlobReader r)
    {
        byte first = r.ReadByte();
        if ((first & 0x10) != 0) r.ReadCompressedInteger(); // generic param count
        int paramCount = r.ReadCompressedInteger();
        var ret = DecodeType(ref r);
        var ps = new SigTypeNode[paramCount];
        for (int i = 0; i < paramCount; i++)
        {
            ps[i] = DecodeType(ref r);
        }
        return new DecodedMethodSig { Ret = ret, Params = ps, HasThis = (first & 0x20) != 0 };
    }

    public static SigTypeNode[] DecodeLocals(ILPeModule mod, BlobHandle blob)
    {
        var r = mod.Md.GetBlobReader(blob);
        byte first = r.ReadByte(); // 0x07 LOCAL_SIG (| 0x10 init locals)
        if ((first & 0x0F) != 0x07) throw new ILError($"bad local sig tag 0x{first:X2}");
        int count = r.ReadCompressedInteger();
        var types = new SigTypeNode[count];
        for (int i = 0; i < count; i++)
        {
            types[i] = DecodeType(ref r);
        }
        return types;
    }

    public static SigTypeNode DecodeFieldSig(ILPeModule mod, BlobHandle blob)
    {
        var r = mod.Md.GetBlobReader(blob);
        if (r.ReadByte() != 0x06) throw new ILError("bad field sig tag");
        return DecodeType(ref r);
    }

    public static SigTypeNode DecodeTypeOnly(BlobReader r)
    {
        var local = r;
        return DecodeType(ref local);
    }

    private static SigTypeNode DecodeType(ref BlobReader r)
    {
        byte t = r.ReadByte();
        switch (t)
        {
            case 0x01: return new SigPrimitive { HostTypeName = "System.Void", IsVoid = true };
            case 0x02: return new SigPrimitive { HostTypeName = "System.Boolean" };
            case 0x03: return new SigPrimitive { HostTypeName = "System.Char" };
            case 0x04: return new SigPrimitive { HostTypeName = "System.SByte" };
            case 0x05: return new SigPrimitive { HostTypeName = "System.Byte" };
            case 0x06: return new SigPrimitive { HostTypeName = "System.Int16" };
            case 0x07: return new SigPrimitive { HostTypeName = "System.UInt16" };
            case 0x08: return new SigPrimitive { HostTypeName = "System.Int32", Prim = ILPrim.I4 };
            case 0x09: return new SigPrimitive { HostTypeName = "System.UInt32" };
            case 0x0A: return new SigPrimitive { HostTypeName = "System.Int64", Prim = ILPrim.I8 };
            case 0x0B: return new SigPrimitive { HostTypeName = "System.UInt64" };
            case 0x0C: return new SigPrimitive { HostTypeName = "System.Single", Prim = ILPrim.R4 };
            case 0x0D: return new SigPrimitive { HostTypeName = "System.Double", Prim = ILPrim.R8 };
            case 0x0E: return new SigPrimitive { HostTypeName = "System.String" };
            case 0x1C: return new SigPrimitive { HostTypeName = "System.Object" };
            case 0x18: return new SigPrimitive { HostTypeName = "System.IntPtr", Prim = ILPrim.I8 };
            case 0x19: return new SigPrimitive { HostTypeName = "System.UIntPtr", Prim = ILPrim.I8 };
            case 0x45: return DecodeType(ref r); // PINNED: transparent modifier
            case 0x1F: r.ReadCompressedInteger(); return DecodeType(ref r); // CMOD_REQD
            case 0x20: r.ReadCompressedInteger(); return DecodeType(ref r);
            case 0x10: return new SigByRefNode { Elem = DecodeType(ref r) };
            case 0x0F: DecodeType(ref r); return new SigPrimitive { HostTypeName = "System.IntPtr", Prim = ILPrim.I8 };
            case 0x11: // VALUETYPE token
                return new SigTypeByToken { Token = TokenTypeToToken(r.ReadCompressedInteger(), 0x01) };
            case 0x12: // CLASS token
                return new SigTypeByToken { Token = TokenTypeToToken(r.ReadCompressedInteger(), 0x01) };
            case 0x13: return new SigGenericParam { Index = r.ReadCompressedInteger(), IsMethodVar = false };
            case 0x1E: return new SigGenericParam { Index = r.ReadCompressedInteger(), IsMethodVar = true };
            case 0x1D: // SZARRAY
                return new SigSzArray { Elem = DecodeType(ref r) };
            case 0x15: // GENERICINST
            {
                byte kind = r.ReadByte(); // 0x01 valuetype | 0x02 class
                int token = TokenTypeToToken(r.ReadCompressedInteger(), 0x01);
                int argc = r.ReadCompressedInteger();
                var args = new SigTypeNode[argc];
                for (int i = 0; i < argc; i++)
                {
                    args[i] = DecodeType(ref r);
                }
                return new SigGenericInst { GenericToken = token, IsValueType = kind == 0x01, Args = args };
            }
            default:
                throw new ILError($"unsupported signature element type 0x{t:X2}");
        }
    }

    private static int TokenTypeToToken(int coded, int unused)
    {
        // TypeDefOrRef coded index: 2 tag bits.
        int index = coded >> 2;
        int tag = coded & 3;
        return tag switch
        {
            0 => 0x02000000 | index, // TypeDef
            1 => 0x01000000 | index, // TypeRef
            2 => 0x1B000000 | index, // TypeSpec
            _ => throw new ILError("bad TypeDefOrRef coded tag"),
        };
    }
}

public sealed class DecodedMethodSig
{
    public SigTypeNode Ret = default!;
    public SigTypeNode[] Params = Array.Empty<SigTypeNode>();
    public bool HasThis;
}
