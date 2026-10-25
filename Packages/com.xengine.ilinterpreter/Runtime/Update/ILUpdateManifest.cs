using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XEngine.ILInterpreter.Update;

/// <summary>
/// Content update manifest (plan §6): versioned, hash-pinned module set with an
/// ECDsa signature over the canonical body. Signing uses platform-verified
/// crypto APIs; verification needs only the public key baked into the player.
/// </summary>
public sealed class ILUpdateManifest
{
    public string Schema => "xengine.ilinterpreter.update.v1";

    /// <summary>Monotonic content version. Activation refuses lower or equal versions
    /// from untrusted sources — downgrade protection.</summary>
    public long Version { get; set; }

    public string MinimumHostVersion = "";

    /// <summary>Module file name → SHA-256 (lowercase hex).</summary>
    public Dictionary<string, string> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Binding IDs the module set requires (host verifies item by item).</summary>
    public List<string> RequiredBindingIds { get; set; } = new();

    /// <summary>Signature algorithm ("ECDsa-P256-SHA256").</summary>
    public string Algorithm { get; set; } = "ECDsa-P256-SHA256";

    /// <summary>Base64 signature over the canonical body (set after signing).</summary>
    public string Signature { get; set; } = "";

    /// <summary>Canonical signing body: deterministic JSON without the Signature field.</summary>
    public string CanonicalBody()
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var modules = new string[Modules.Count];
        int i = 0;
        foreach (var (name, hash) in Modules)
        {
            modules[i++] = Q(name) + ":" + Q(hash);
        }
        Array.Sort(modules, StringComparer.Ordinal);

        var bindings = new string[RequiredBindingIds.Count];
        for (int b = 0; b < RequiredBindingIds.Count; b++) bindings[b] = Q(RequiredBindingIds[b]);
        Array.Sort(bindings, StringComparer.Ordinal);

        return new StringBuilder()
            .Append('{')
            .Append(Q("schema")).Append(':').Append(Q(Schema)).Append(',')
            .Append(Q("version")).Append(':').Append(Version).Append(',')
            .Append(Q("minimumHostVersion")).Append(':').Append(Q(MinimumHostVersion)).Append(',')
            .Append(Q("algorithm")).Append(':').Append(Q(Algorithm)).Append(',')
            .Append(Q("modules")).Append(":{").Append(string.Join(",", modules)).Append("},")
            .Append(Q("requiredBindingIds")).Append(":[").Append(string.Join(",", bindings)).Append(']')
            .Append('}')
            .ToString();
    }

    public string ToJson()
    {
        var body = CanonicalBody()[..^1]; // strip closing brace
        return body + "," + "\"" + "signature" + "\":\"" + Signature + "\"}";
    }

    public static ILUpdateManifest FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var m = new ILUpdateManifest
        {
            Version = root.GetProperty("version").GetInt64(),
            MinimumHostVersion = root.GetProperty("minimumHostVersion").GetString() ?? "",
            Algorithm = root.GetProperty("algorithm").GetString() ?? "",
            Signature = root.GetProperty("signature").GetString() ?? "",
        };
        foreach (var p in root.GetProperty("modules").EnumerateObject())
            m.Modules[p.Name] = p.Value.GetString() ?? "";
        foreach (var b in root.GetProperty("requiredBindingIds").EnumerateArray())
            m.RequiredBindingIds.Add(b.GetString() ?? "");
        return m;
    }

    public static string ComputeModuleSha256(byte[] dll) =>
        Convert.ToHexString(SHA256.HashData(dll)).ToLowerInvariant();
}

/// <summary>Signing/verification key material (ECDsa P-256).</summary>
public static class ILUpdateSigner
{
    public const string KeyAlgorithm = "ECDsa-P256-SHA256";

    /// <summary>Editor side only: creates a new key pair (SPKI public / PKCS#8 private,
    /// Base64). The private key never ships and never enters a manifest.</summary>
    public static (string PublicKey, string PrivateKey) CreateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    }

    public static string Sign(ILUpdateManifest manifest, string privateKeyBase64)
    {
        manifest.Algorithm = KeyAlgorithm;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        byte[] body = Encoding.UTF8.GetBytes(manifest.CanonicalBody());
        return Convert.ToBase64String(ecdsa.SignData(body, HashAlgorithmName.SHA256));
    }

    public static bool Verify(ILUpdateManifest manifest, string publicKeyBase64)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        }
        catch
        {
            return false;
        }
        byte[] body = Encoding.UTF8.GetBytes(manifest.CanonicalBody());
        try
        {
            return ecdsa.VerifyData(body, Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
