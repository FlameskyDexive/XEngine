using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace XEngine.XTS;

/// <summary>
/// A signed content manifest: pins every content file by sha256 plus the
/// minimum host version. The manifest is HMAC-SHA256-signed over its
/// canonical JSON body; verification uses the shared signing key configured
/// on the install. QuickJS bytecode is never shipped, so plain JS + hashes
/// + this signature is the whole trust chain (plan §4). Swapping in an
/// asymmetric scheme later is an internal change to Sign/Verify only.
/// </summary>
public sealed class XtsContentManifest
{
    public const string CurrentSchema = "xengine.xts.content.manifest.v1";
    public const string ManifestFileName = "manifest.json";

    public string Schema { get; init; } = CurrentSchema;
    public string Version { get; init; } = string.Empty;
    public string MinHostVersion { get; init; } = string.Empty;
    public List<XtsContentFile> Files { get; init; } = new();
    /// <summary>Base64 HMAC-SHA256 over <see cref="CanonicalBody"/>.</summary>
    public string Signature { get; set; } = string.Empty;

    public static XtsContentManifest Parse(string json)
    {
        // JsonDocument (DOM) instead of JsonSerializer.Deserialize: the AOT
        // player disables reflection-based serialization, so manifest parsing
        // maps the flat schema by hand.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var manifest = new XtsContentManifest
        {
            Schema = root.TryGetProperty("schema", out var schemaEl) ? schemaEl.GetString() ?? string.Empty : string.Empty,
            Version = root.TryGetProperty("version", out var versionEl) ? versionEl.GetString() ?? string.Empty : string.Empty,
            MinHostVersion = root.TryGetProperty("minHostVersion", out var minHostEl) ? minHostEl.GetString() ?? string.Empty : string.Empty,
            Signature = root.TryGetProperty("signature", out var signatureEl) ? signatureEl.GetString() ?? string.Empty : string.Empty,
        };
        if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesEl.EnumerateArray())
            {
                manifest.Files.Add(new XtsContentFile
                {
                    Path = file.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty,
                    Sha256 = file.TryGetProperty("sha256", out var shaEl) ? shaEl.GetString() ?? string.Empty : string.Empty,
                });
            }
        }
        return manifest;
    }

    /// <summary>Canonical bytes the signature covers: everything except the
    /// signature field, re-serialized deterministically. Built with
    /// Utf8JsonWriter — reflection-free (NativeAOT players ship without it).
    /// Byte-compatible with the previous JsonSerializer anonymous-object form
    /// (minified, schema/version/minHostVersion/files order, files sorted by
    /// path ordinal).</summary>
    public string CanonicalBody()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("version", Version);
            writer.WriteString("minHostVersion", MinHostVersion);
            writer.WriteStartArray("files");
            foreach (var file in Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Full JSON document (canonical body + signature), AOT-safe.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("version", Version);
            writer.WriteString("minHostVersion", MinHostVersion);
            writer.WriteStartArray("files");
            foreach (var file in Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("signature", Signature);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Sign(string base64Key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(base64Key));
        Signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(CanonicalBody())));
    }

    public bool Verify(string base64Key)
    {
        if (string.IsNullOrEmpty(Signature))
            return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(base64Key));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(CanonicalBody()));
        var provided = Convert.FromBase64String(Signature);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}

public sealed class XtsContentFile
{
    /// <summary>Path relative to the content root, forward slashes.</summary>
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}
