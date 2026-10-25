// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Non-secret inputs used to create a deterministic HarmonyOS build receipt.</summary>
internal sealed class HarmonyOSBuildReceiptInput
{
    public string Action { get; init; } = string.Empty;
    public string BundleFingerprint { get; init; } = string.Empty;
    public string OfficialSdkHash { get; init; } = string.Empty;
    public string XEngineCommit { get; init; } = string.Empty;
    public string SigningReference { get; init; } = "None";
    public string? DeviceId { get; init; }
    public string ArtifactRelativePath { get; init; } = string.Empty;
    public string ArtifactSha256 { get; init; } = string.Empty;
    public long ArtifactBytes { get; init; }
    public HarmonyOSBuildProvenanceInput? Provenance { get; init; }
    public IReadOnlyDictionary<string, string> ToolVersions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> NativeBuildIds { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> Findings { get; init; } = [];
}

/// <summary>Normalized, non-secret inputs which make a HarmonyOS receipt reproducible.</summary>
internal sealed class HarmonyOSBuildProvenanceInput
{
    public string SourceState { get; init; } = string.Empty;
    public string ProviderReleaseId { get; init; } = string.Empty;
    public string ProviderManifestSha256 { get; init; } = string.Empty;
    public IReadOnlyList<string> ProviderPackageHashes { get; init; } = [];
    public int TargetApi { get; init; }
    public string TargetAbi { get; init; } = string.Empty;
    public string BuildConfiguration { get; init; } = string.Empty;
    public string BuildSettingsSha256 { get; init; } = string.Empty;
    public string? OwnershipManifestSha256 { get; init; }
    public string? PayloadManifestSha256 { get; init; }
    public string? ParentReceiptSha256 { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
}

/// <summary>
/// Deterministic, redacted provenance for one HarmonyOS export, package, verify, or deploy action.
/// </summary>
internal sealed class HarmonyOSBuildReceipt
{
    private const int SchemaVersion = 2;
    private static readonly UTF8Encoding s_utf8NoBom = new(false);
    private static readonly Regex s_credentialAssignment = new(
        @"(?:^|[\s;])(password|passphrase|token|private[_-]?key|secret)\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex s_windowsAbsolutePath = new(
        @"(?:^|[\s=])[A-Za-z]:[\\/]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex s_unixLocalPath = new(
        @"(?:^|[\s=])/(?:Users|home|tmp|var|opt|mnt)/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _action;
    private readonly string _bundleFingerprint;
    private readonly string _officialSdkHash;
    private readonly string _xengineCommit;
    private readonly string _signingReferenceKind;
    private readonly string? _deviceIdHash;
    private readonly string _artifactRelativePath;
    private readonly string _artifactSha256;
    private readonly long _artifactBytes;
    private readonly ReceiptProvenance _provenance;
    private readonly IReadOnlyList<ReceiptToolVersion> _toolVersions;
    private readonly IReadOnlyList<ReceiptNativeBuildId> _nativeBuildIds;
    private readonly IReadOnlyList<string> _findings;

    private HarmonyOSBuildReceipt(
        string action,
        string bundleFingerprint,
        string officialSdkHash,
        string xengineCommit,
        string signingReferenceKind,
        string? deviceIdHash,
        string artifactRelativePath,
        string artifactSha256,
        long artifactBytes,
        ReceiptProvenance provenance,
        IReadOnlyList<ReceiptToolVersion> toolVersions,
        IReadOnlyList<ReceiptNativeBuildId> nativeBuildIds,
        IReadOnlyList<string> findings)
    {
        _action = action;
        _bundleFingerprint = bundleFingerprint;
        _officialSdkHash = officialSdkHash;
        _xengineCommit = xengineCommit;
        _signingReferenceKind = signingReferenceKind;
        _deviceIdHash = deviceIdHash;
        _artifactRelativePath = artifactRelativePath;
        _artifactSha256 = artifactSha256;
        _artifactBytes = artifactBytes;
        _provenance = provenance;
        _toolVersions = toolVersions;
        _nativeBuildIds = nativeBuildIds;
        _findings = findings;
    }

    internal static HarmonyOSBuildReceipt Create(HarmonyOSBuildReceiptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string action = RequireText(input.Action, nameof(input.Action));
        string bundleFingerprint = RequireHash(input.BundleFingerprint, nameof(input.BundleFingerprint));
        string officialSdkHash = RequireHash(input.OfficialSdkHash, nameof(input.OfficialSdkHash));
        string xengineCommit = RequireCommit(input.XEngineCommit);
        string signingReferenceKind = NormalizeSigningReference(input.SigningReference);
        string? deviceIdHash = string.IsNullOrWhiteSpace(input.DeviceId)
            ? null
            : ComputeTextSha256(input.DeviceId);
        string artifactRelativePath = RequireRelativePath(input.ArtifactRelativePath, nameof(input.ArtifactRelativePath));
        string artifactSha256 = RequireHash(input.ArtifactSha256, nameof(input.ArtifactSha256));
        if (input.ArtifactBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(input.ArtifactBytes), "Receipt artifact bytes cannot be negative.");
        ReceiptProvenance provenance = NormalizeProvenance(input.Provenance);
        ValidateActionProvenance(action, provenance);

        var toolVersions = new List<ReceiptToolVersion>(input.ToolVersions.Count);
        foreach (KeyValuePair<string, string> tool in input.ToolVersions)
            toolVersions.Add(new ReceiptToolVersion(RequireEvidenceText(tool.Key, "ToolVersions.Name"), RequireEvidenceText(tool.Value, "ToolVersions.Version")));
        toolVersions.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        EnsureDistinctToolNames(toolVersions);

        var nativeBuildIds = new List<ReceiptNativeBuildId>(input.NativeBuildIds.Count);
        foreach (KeyValuePair<string, string> nativeBuildId in input.NativeBuildIds)
        {
            nativeBuildIds.Add(new ReceiptNativeBuildId(
                RequireEvidenceText(nativeBuildId.Key, "NativeBuildIds.Name"),
                RequireBuildId(nativeBuildId.Value, "NativeBuildIds.GnuBuildId")));
        }
        nativeBuildIds.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        EnsureDistinctNativeBuildNames(nativeBuildIds);

        var findings = new List<string>(input.Findings.Count);
        for (int i = 0; i < input.Findings.Count; i++)
            findings.Add(RequireEvidenceText(input.Findings[i], "Findings"));
        findings.Sort(StringComparer.Ordinal);

        return new HarmonyOSBuildReceipt(
            action,
            bundleFingerprint,
            officialSdkHash,
            xengineCommit,
            signingReferenceKind,
            deviceIdHash,
            artifactRelativePath,
            artifactSha256,
            input.ArtifactBytes,
            provenance,
            toolVersions,
            nativeBuildIds,
            findings);
    }

    internal string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = SchemaVersion,
        action = _action,
        bundleFingerprint = _bundleFingerprint,
        officialSdkHash = _officialSdkHash,
        xengineCommit = _xengineCommit,
        signingReferenceKind = _signingReferenceKind,
        deviceIdHash = _deviceIdHash,
        artifact = new
        {
            relativePath = _artifactRelativePath,
            sha256 = _artifactSha256,
            bytes = _artifactBytes,
        },
        provenance = _provenance,
        toolVersions = _toolVersions,
        nativeBuildIds = _nativeBuildIds,
        findings = _findings,
    }, s_jsonOptions);

    internal async Task WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(path) + ".tmp");
        await File.WriteAllTextAsync(temporaryPath, ToJson() + "\n", s_utf8NoBom, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    private static string NormalizeSigningReference(string value)
    {
        value = RequireText(value, nameof(HarmonyOSBuildReceiptInput.SigningReference));
        if (string.Equals(value, "None", StringComparison.Ordinal))
            return "None";
        const string environmentPrefix = "EnvironmentVariable:";
        if (!value.StartsWith(environmentPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("HarmonyOS receipt signing reference must be None or an environment-variable reference.");
        string name = value[environmentPrefix.Length..];
        if (name.Length == 0 || !IsIdentifierStart(name[0]))
            throw new InvalidDataException("HarmonyOS receipt signing environment variable name is invalid.");
        for (int i = 1; i < name.Length; i++)
        {
            if (!IsIdentifierPart(name[i]))
                throw new InvalidDataException("HarmonyOS receipt signing environment variable name is invalid.");
        }
        return "EnvironmentVariable";
    }

    private static string RequireHash(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64)
            throw new InvalidDataException($"HarmonyOS receipt hash '{name}' must be a 64-character SHA-256 value.");
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                throw new InvalidDataException($"HarmonyOS receipt hash '{name}' must use lowercase hexadecimal.");
        }
        return value;
    }

    private static string RequireBuildId(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Length % 2 != 0)
            throw new InvalidDataException($"HarmonyOS receipt build-id '{name}' must be a non-empty even-length hexadecimal value.");
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                throw new InvalidDataException($"HarmonyOS receipt build-id '{name}' must use lowercase hexadecimal.");
        }
        return value;
    }

    private static string RequireCommit(string value)
    {
        if (string.IsNullOrEmpty(value) || (value.Length != 40 && value.Length != 64))
            throw new InvalidDataException("HarmonyOS receipt XEngine commit must be a 40- or 64-character SHA-1/SHA-256 value.");
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                throw new InvalidDataException("HarmonyOS receipt XEngine commit must use lowercase hexadecimal.");
        }
        return value;
    }

    private static ReceiptProvenance NormalizeProvenance(HarmonyOSBuildProvenanceInput? input)
    {
        if (input == null)
            throw new InvalidDataException("HarmonyOS receipt requires structured provenance.");

        string sourceState = input.SourceState switch
        {
            "Clean" or "Dirty" or "Unverified" => input.SourceState,
            _ => throw new InvalidDataException("HarmonyOS receipt source state must be Clean, Dirty, or Unverified."),
        };
        string releaseId = RequireEvidenceText(input.ProviderReleaseId, "Provenance.ProviderReleaseId");
        string manifestSha256 = RequireHash(input.ProviderManifestSha256, "Provenance.ProviderManifestSha256");
        if (input.TargetApi is < 13 or 25 or > 26)
            throw new InvalidDataException("HarmonyOS receipt target API must be 13 through 24 or 26.");
        if (input.TargetAbi is not "arm64-v8a" and not "x86_64")
            throw new InvalidDataException("HarmonyOS receipt target ABI is unsupported.");

        var packageHashes = new List<ReceiptPackageHash>(input.ProviderPackageHashes.Count);
        for (int i = 0; i < input.ProviderPackageHashes.Count; i++)
        {
            string value = RequireEvidenceText(input.ProviderPackageHashes[i], "Provenance.ProviderPackageHashes");
            int separator = value.LastIndexOf(':');
            if (separator <= 0 || separator == value.Length - 1)
                throw new InvalidDataException("HarmonyOS receipt provider package hash must be 'package-id:sha256'.");
            packageHashes.Add(new ReceiptPackageHash(
                RequireEvidenceText(value[..separator], "Provenance.ProviderPackageId"),
                RequireHash(value[(separator + 1)..], "Provenance.ProviderPackageSha256")));
        }
        packageHashes.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        for (int i = 1; i < packageHashes.Count; i++)
        {
            if (string.Equals(packageHashes[i - 1].Id, packageHashes[i].Id, StringComparison.Ordinal))
                throw new InvalidDataException($"HarmonyOS receipt duplicates provider package '{packageHashes[i].Id}'.");
        }
        if (packageHashes.Count == 0)
            throw new InvalidDataException("HarmonyOS receipt requires provider package checksums.");

        string configuration = RequireEvidenceText(input.BuildConfiguration, "Provenance.BuildConfiguration");
        string settingsSha256 = RequireHash(input.BuildSettingsSha256, "Provenance.BuildSettingsSha256");
        return new ReceiptProvenance(
            sourceState,
            new ReceiptProvider(releaseId, manifestSha256, packageHashes),
            new ReceiptBuild(input.TargetApi, input.TargetAbi, configuration, settingsSha256),
            new ReceiptContent(
                RequireOptionalHash(input.OwnershipManifestSha256, "Provenance.OwnershipManifestSha256"),
                RequireOptionalHash(input.PayloadManifestSha256, "Provenance.PayloadManifestSha256")),
            RequireOptionalHash(input.ParentReceiptSha256, "Provenance.ParentReceiptSha256"),
            input.ObservedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static void ValidateActionProvenance(string action, ReceiptProvenance provenance)
    {
        if (action is "export-project" or "verify-hap" or "verify-app")
        {
            if (provenance.Content.OwnershipManifestSha256 == null || provenance.Content.PayloadManifestSha256 == null)
            {
                throw new InvalidDataException(
                    "HarmonyOS export and package receipts require ownership and payload manifest checksums.");
            }
        }
        if (action == "deploy-run" && (provenance.ParentReceiptSha256 == null || provenance.ObservedAtUtc == null))
        {
            throw new InvalidDataException(
                "HarmonyOS run receipts require the parent package receipt checksum and an observation timestamp.");
        }
    }

    private static string? RequireOptionalHash(string? value, string name) =>
        value == null ? null : RequireHash(value, name);

    private static string RequireRelativePath(string value, string name)
    {
        value = RequireText(value, name).Replace('\\', '/');
        if (value[0] == '/' || Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"HarmonyOS receipt path '{name}' must be relative.");
        string[] segments = value.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] is "." or "..")
                throw new InvalidDataException($"HarmonyOS receipt path '{name}' contains an unsafe segment.");
        }
        return value;
    }

    private static string RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
            throw new InvalidDataException($"HarmonyOS receipt value '{name}' is invalid.");
        return value;
    }

    private static string RequireEvidenceText(string? value, string name)
    {
        value = RequireText(value, name);
        if (s_credentialAssignment.IsMatch(value) || s_windowsAbsolutePath.IsMatch(value) || s_unixLocalPath.IsMatch(value))
            throw new InvalidDataException($"HarmonyOS receipt value '{name}' contains a local path or credential assignment.");
        return value;
    }

    private static void EnsureDistinctToolNames(IReadOnlyList<ReceiptToolVersion> values)
    {
        for (int i = 1; i < values.Count; i++)
        {
            if (string.Equals(values[i - 1].Name, values[i].Name, StringComparison.Ordinal))
                throw new InvalidDataException($"HarmonyOS receipt contains duplicate tool '{values[i].Name}'.");
        }
    }

    private static void EnsureDistinctNativeBuildNames(IReadOnlyList<ReceiptNativeBuildId> values)
    {
        for (int i = 1; i < values.Count; i++)
        {
            if (string.Equals(values[i - 1].Name, values[i].Name, StringComparison.Ordinal))
                throw new InvalidDataException($"HarmonyOS receipt contains duplicate native build-id '{values[i].Name}'.");
        }
    }

    private static string ComputeTextSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(s_utf8NoBom.GetBytes(value))).ToLowerInvariant();
    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);

    private sealed record ReceiptToolVersion(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string Version);

    private sealed record ReceiptNativeBuildId(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("gnuBuildId")] string GnuBuildId);

    private sealed record ReceiptProvenance(
        [property: JsonPropertyName("sourceState")] string SourceState,
        [property: JsonPropertyName("provider")] ReceiptProvider Provider,
        [property: JsonPropertyName("build")] ReceiptBuild Build,
        [property: JsonPropertyName("content")] ReceiptContent Content,
        [property: JsonPropertyName("parentReceiptSha256")] string? ParentReceiptSha256,
        [property: JsonPropertyName("observedAtUtc")] string? ObservedAtUtc);

    private sealed record ReceiptProvider(
        [property: JsonPropertyName("releaseId")] string ReleaseId,
        [property: JsonPropertyName("manifestSha256")] string ManifestSha256,
        [property: JsonPropertyName("packageHashes")] IReadOnlyList<ReceiptPackageHash> PackageHashes);

    private sealed record ReceiptPackageHash(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("sha256")] string Sha256);

    private sealed record ReceiptBuild(
        [property: JsonPropertyName("targetApi")] int TargetApi,
        [property: JsonPropertyName("targetAbi")] string TargetAbi,
        [property: JsonPropertyName("configuration")] string Configuration,
        [property: JsonPropertyName("settingsSha256")] string SettingsSha256);

    private sealed record ReceiptContent(
        [property: JsonPropertyName("ownershipManifestSha256")] string? OwnershipManifestSha256,
        [property: JsonPropertyName("payloadManifestSha256")] string? PayloadManifestSha256);
}
