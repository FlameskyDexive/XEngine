using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

using XEngine.ILInterpreter.VM;

namespace XEngine.ILInterpreter.Compatibility;

/// <summary>One finding of the compatibility scan, deterministically ordered.</summary>
public sealed class ILCompatibilityFinding
{
    public required string Kind;          // "unsupported-il" | "unsupported-signature"
    public required string Type;
    public required string Method;
    public int IlOffset;
    public string Detail = "";
}

/// <summary>
/// Result of scanning one compiled module. Serializes to a stable JSON report
/// (plan §5.2/§7: the update-build report carries IL/binding requirements).
/// </summary>
public sealed class ILCompatibilityReport
{
    public string Schema => "xengine.ilinterpreter.compat.v1";
    public string ModuleName = "";
    public string ModuleSha256 = "";
    public List<ILCompatibilityFinding> Findings = new();
    public List<string> RequiredBindings = new();

    public bool IsCompatible => Findings.Count == 0;

    public string ToJson()
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var sb = new StringBuilder();
        sb.Append("{\n  \"schema\": ").Append(Q(Schema)).Append(",\n");
        sb.Append("  \"module\": ").Append(Q(ModuleName)).Append(",\n");
        sb.Append("  \"moduleSha256\": ").Append(Q(ModuleSha256)).Append(",\n");
        sb.Append("  \"compatible\": ").Append(IsCompatible ? "true" : "false").Append(",\n");
        sb.Append("  \"requiredBindings\": [");
        for (int i = 0; i < RequiredBindings.Count; i++)
        {
            sb.Append(Q(RequiredBindings[i]));
            if (i < RequiredBindings.Count - 1) sb.Append(", ");
        }
        sb.Append("],\n  \"findings\": [\n");
        for (int i = 0; i < Findings.Count; i++)
        {
            var f = Findings[i];
            sb.Append("    {\"kind\": ").Append(Q(f.Kind))
              .Append(", \"type\": ").Append(Q(f.Type))
              .Append(", \"method\": ").Append(Q(f.Method))
              .Append(", \"ilOffset\": ").Append(f.IlOffset)
              .Append(", \"detail\": ").Append(Q(f.Detail))
              .Append("}").Append(i < Findings.Count - 1 ? "," : "").Append('\n');
        }
        sb.Append("  ]\n}");
        return sb.ToString();
    }
}

/// <summary>
/// Build-time compatibility gate: decodes every method body of a compiled
/// module with the SAME decoder the VM uses, then reports unsupported IL and
/// missing host bindings before anything runs. Missing bindings surface as
/// RequiredBindings entries the host must register (or refuse the module for);
/// unsupported IL surfaces as located findings — the runtime never silently
/// falls back to the CLR.
/// </summary>
public static class ILModuleScanner
{
    /// <param name="boundMembers">Known host bindings as "Type::Member/paramCount".</param>
    public static ILCompatibilityReport Scan(string modulePath, IEnumerable<string> boundMembers)
    {
        var bound = new HashSet<string>(boundMembers, StringComparer.OrdinalIgnoreCase);
        var report = new ILCompatibilityReport { ModuleName = Path.GetFileNameWithoutExtension(modulePath) };
        report.ModuleSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modulePath))).ToLowerInvariant();

        using var fs = File.OpenRead(modulePath);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        foreach (var tdHandle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdHandle);
            string typeName = FullName(md, td);

            foreach (var mh in td.GetMethods())
            {
                var method = md.GetMethodDefinition(mh);
                string methodName = md.GetString(method.Name);
                int rva = method.RelativeVirtualAddress;
                if (rva == 0) continue;

                try
                {
                    ScanMethodBody(pe, md, method, bound, report, typeName, methodName);
                }
                catch (Exception ex)
                {
                    report.Findings.Add(new ILCompatibilityFinding
                    {
                        Kind = "unsupported-signature",
                        Type = typeName,
                        Method = methodName,
                        IlOffset = -1,
                        Detail = ex.Message,
                    });
                }
            }
        }

        report.Findings.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Type, b.Type);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Method, b.Method);
            if (c != 0) return c;
            return a.IlOffset.CompareTo(b.IlOffset);
        });
        report.RequiredBindings.Sort(StringComparer.Ordinal);
        return report;
    }

    private static void ScanMethodBody(
        PEReader pe, MetadataReader md, MethodDefinition method,
        HashSet<string> bound, ILCompatibilityReport report, string typeName, string methodName)
    {
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        byte[] il = body.GetILBytes() ?? Array.Empty<byte>();

        // Decode with the VM's own decoder so unsupported opcodes classify
        // identically at build time and run time.
        var (code, offsets) = ILDecoder.Decode(il);
        for (int i = 0; i < code.Length; i++)
        {
            var ins = code[i];

            if (ins.Op == Op.Unsupported)
            {
                report.Findings.Add(new ILCompatibilityFinding
                {
                    Kind = "unsupported-il",
                    Type = typeName,
                    Method = methodName,
                    IlOffset = offsets[i],
                    Detail = ins.Reason ?? "unknown",
                });
                continue;
            }

            if (ins.Op is not (Op.Call or Op.CallVirt or Op.NewObj)) continue;

            // Collect host member references as binding requirements. Module-internal
            // calls use MemberRefs on TypeDefs/TypeSpecs — only TypeRef parents (host
            // assemblies) need bindings.
            var handle = MetadataTokens.EntityHandle((int)ins.I);
            if (handle.Kind != HandleKind.MemberReference) continue;
            var mr = md.GetMemberReference((MemberReferenceHandle)handle);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;

            string member = md.GetString(mr.Name);
            var parentType = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            string parentNs = md.GetString(parentType.Namespace);
            string parentName = md.GetString(parentType.Name);
            string parentFull = string.IsNullOrEmpty(parentNs) ? parentName : parentNs + "." + parentName;

            int paramCount = CountParams(md, mr);
            string requirement = $"{parentFull}::{member}/{paramCount}";
            report.RequiredBindings.Add(requirement);
            if (bound.Contains(requirement))
                continue;
            report.Findings.Add(new ILCompatibilityFinding
            {
                Kind = "missing-binding",
                Type = parentFull,
                Method = member + "/" + paramCount,
                IlOffset = offsets[i],
                Detail = "host binding not declared in ILBindings.txt",
            });
        }
    }

    private static int CountParams(MetadataReader md, MemberReference mr)
    {
        try
        {
            var reader = md.GetBlobReader(mr.Signature);
            byte first = reader.ReadByte();
            if ((first & 0x10) != 0) reader.ReadCompressedInteger();
            return reader.ReadCompressedInteger();
        }
        catch
        {
            return -1;
        }
    }

    private static string FullName(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil)
        {
            return FullName(md, md.GetTypeDefinition(decl)) + "+" + name;
        }
        string ns = md.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}
