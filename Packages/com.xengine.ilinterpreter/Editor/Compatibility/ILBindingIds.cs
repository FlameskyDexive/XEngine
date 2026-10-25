using System;
using System.Security.Cryptography;
using System.Text;

namespace XEngine.ILInterpreter.Compatibility;

/// <summary>
/// Stable binding IDs for host members exposed to interpreted modules
/// (plan §5.3): the ID is a schema-versioned SHA-256 over the NORMALIZED full
/// signature — never <c>GetHashCode</c>, which is not stable across runs.
/// Hot-update manifests list required IDs; the client verifies them item by
/// item before activation.
/// </summary>
public static class ILBindingIds
{
    /// <summary>Bump when the normalization rules change; old IDs stay resolvable
    /// through the host's schema table.</summary>
    public const string Schema = "binding-id-v1";

    /// <summary>
    /// Normalized signature form: <c>Full.Type::Member(count)#[schema]</c> —
    /// overloads distinguish by count only, matching the probe binding model.
    /// </summary>
    public static string NormalizedSignature(string declaringTypeFullName, string memberName, int paramCount)
        => $"{declaringTypeFullName}::{memberName}({paramCount})#{Schema}";

    /// <summary>Deterministic ID: first 16 bytes of SHA-256, lowercase hex.</summary>
    public static string IdFor(string declaringTypeFullName, string memberName, int paramCount)
    {
        string normalized = NormalizedSignature(declaringTypeFullName, memberName, paramCount);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>ID for an existing requirement string in "Type::Member/count" form.</summary>
    public static string IdForRequirement(string requirement)
    {
        int sep = requirement.LastIndexOf("::", StringComparison.Ordinal);
        int slash = requirement.LastIndexOf('/');
        if (sep <= 0 || slash <= sep) return IdFor(requirement, "", 0);
        string type = requirement[..sep];
        string member = requirement[(sep + 2)..slash];
        int count = int.TryParse(requirement[(slash + 1)..], out var n) ? n : 0;
        return IdFor(type, member, count);
    }
}
