using System.Runtime.InteropServices;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace XEngine.XTS;

/// <summary>
/// One platform entry of <c>Native/native-manifest.json</c>. Paths are
/// package-relative with forward slashes.
/// </summary>
public sealed class XtsNativeManifestPlatform
{
    public string Library { get; init; } = string.Empty;
    /// <summary>Machine ABI of the library, e.g. "x64" or "arm64-v8a".</summary>
    public string Abi { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    /// <summary>Human-readable provenance (toolchain + build script).</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>
    /// Native build id. Required for every platform whose player packages are
    /// cross-checked by build id (HarmonyOS receipts record it per library).
    /// </summary>
    public string BuildId { get; init; } = string.Empty;
}

/// <summary>
/// The native dependency manifest shipped in the package
/// (<c>Native/native-manifest.json</c>). It pins, per platform, which native
/// library the runtime needs, its ABI, its SHA256, where it was built and its
/// build id. Player build pipelines call <see cref="Validate"/> before
/// packaging: a missing platform, wrong ABI version, missing build id or hash
/// mismatch is a hard failure — never a warning and never a reason to fall
/// back to some other binary.
/// </summary>
public sealed class XtsNativeManifest
{
    public const string ManifestSchema = "xengine.xts.native.manifest.v1";
    public const string ManifestPath = "Native/native-manifest.json";

    public string Schema { get; init; } = string.Empty;
    public int AbiVersion { get; init; }
    public string QuickjsVersion { get; init; } = string.Empty;
    public Dictionary<string, XtsNativeManifestPlatform> Platforms { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Parse the manifest JSON. Throws on malformed JSON.</summary>
    public static XtsNativeManifest Parse(string json) =>
        JsonSerializer.Deserialize<XtsNativeManifest>(json, XtsNativeManifestJson.Options)
        ?? throw new InvalidDataException("native manifest parsed to null");

    /// <summary>Load the manifest shipped in this package.</summary>
    public static XtsNativeManifest Load(string packageRoot) =>
        Parse(File.ReadAllText(Path.Combine(packageRoot, ManifestPath.Replace('/', Path.DirectorySeparatorChar))));

    /// <summary>
    /// Structural validation only (schema, ABI version, platform entry,
    /// required fields). Does not touch the file system.
    /// </summary>
    public List<string> ValidateStructure(string platform)
    {
        var errors = new List<string>();
        if (Schema != ManifestSchema)
            errors.Add($"manifest schema '{Schema}' is not {ManifestSchema}");
        if (AbiVersion != Binding.XtsNativeAbi.AbiVersion)
            errors.Add($"manifest ABI version {AbiVersion} does not match the native ABI version {Binding.XtsNativeAbi.AbiVersion}");
        if (string.IsNullOrWhiteSpace(QuickjsVersion))
            errors.Add("manifest does not record the pinned quickjs version");

        if (!Platforms.TryGetValue(platform, out var entry))
        {
            errors.Add($"platform '{platform}' is not declared in the native manifest; " +
                       "packaging this platform is refused until the library, ABI, sha256, source and build id are recorded");
            return errors;
        }
        if (string.IsNullOrWhiteSpace(entry.Library))
            errors.Add($"platform '{platform}' has no library path");
        if (string.IsNullOrWhiteSpace(entry.Abi))
            errors.Add($"platform '{platform}' has no ABI");
        if (string.IsNullOrWhiteSpace(entry.Sha256))
            errors.Add($"platform '{platform}' has no sha256");
        if (string.IsNullOrWhiteSpace(entry.Source))
            errors.Add($"platform '{platform}' has no build source provenance");
        // Desktop libraries are built locally by build-windows.ps1; every
        // packaged platform (HarmonyOS receipts match on build id) must pin one.
        if (string.IsNullOrWhiteSpace(entry.BuildId))
            errors.Add($"platform '{platform}' has no build id");
        return errors;
    }

    /// <summary>
    /// Structural validation plus hash verification of the library file found
    /// under <paramref name="packageRoot"/>. Returns every problem found; an
    /// empty list means the platform is safe to package.
    /// </summary>
    public List<string> Validate(string platform, string packageRoot)
    {
        var errors = ValidateStructure(platform);
        if (errors.Count > 0 || !Platforms.TryGetValue(platform, out var entry))
            return errors;

        var libraryPath = Path.Combine(packageRoot, entry.Library.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(libraryPath))
        {
            errors.Add($"library '{entry.Library}' for platform '{platform}' is missing from the package");
            return errors;
        }
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(libraryPath)))
            .ToLowerInvariant();
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            errors.Add($"library hash mismatch for platform '{platform}': manifest pins {entry.Sha256}, file is {actual}");
        return errors;
    }
}

internal static class XtsNativeManifestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };
}
