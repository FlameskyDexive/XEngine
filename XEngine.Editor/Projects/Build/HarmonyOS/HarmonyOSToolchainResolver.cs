// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.HarmonyOS;

internal interface IHarmonyOSToolchainResolver
{
    Task<HarmonyOSToolchainSnapshot> ResolveAsync(
        HarmonyOSToolchainResolveOptions options,
        CancellationToken cancellationToken = default);

    Task<HarmonyOSHdcToolchainResolution> ResolveHdcAsync(
        HarmonyOSToolchainResolveOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves only a project-local verified Bundle and a configured official OpenHarmony SDK root.
/// It deliberately does not search provider workspaces or global package caches.
/// </summary>
internal sealed class HarmonyOSToolchainResolver : IHarmonyOSToolchainResolver
{
    private const string BundleRootRelativePath = "ThirdParty/OpenHarmonySdk";
    private const string CatalogRelativePath = "schemas/sdk-catalog.json";
    private const string ManifestRelativePath = "release-manifest.json";

    private static readonly string[] s_supportedAbis = ["arm64-v8a", "x86_64"];

    private static readonly string[] s_requiredPackageIds =
    [
        "OpenHarmony.NET.Runtime.NativeAot.arm64-v8a",
        "OpenHarmony.NET.Runtime.NativeAot.x86_64",
        "OpenHarmony.NET.PublishAotCross",
        "OpenHarmony.NDK.Bindings",
        "OpenHarmony.NET.Player.Sdk",
        "OpenHarmony.NET.HapTooling",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // The official OpenHarmony oh-uni-package.json emits apiVersion as a JSON string
        // (e.g. "23"); allow reading it into the int model so the manifest validates.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<HarmonyOSToolchainSnapshot> ResolveAsync(
        HarmonyOSToolchainResolveOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        string projectRoot = NormalizeProjectRoot(options.ProjectRoot);
        HarmonyOSSdkBundleLock bundleLock = await ReadLockAsync(projectRoot, options.LockFilePath, cancellationToken).ConfigureAwait(false);
        string bundleRoot = GetContainedPath(projectRoot, bundleLock.BundleRelativePath, HarmonyOSBundleErrorCode.LockInvalid);
        IReadOnlyList<string> declaredCapabilities = await ValidateImportedBundleAsync(bundleRoot, bundleLock, cancellationToken).ConfigureAwait(false);
        HarmonyOSBundleCapabilities.Require(declaredCapabilities, options.RequiredCapabilities);

        string catalogPath = GetContainedPath(bundleRoot, CatalogRelativePath, HarmonyOSBundleErrorCode.LockInvalid);
        string catalogSha256 = await ComputeFileSha256Async(catalogPath, cancellationToken).ConfigureAwait(false);
        HarmonyOSSdkCatalog catalog = await ReadCatalogAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        CatalogPackage selectedPackage = ResolveCatalogPackage(catalog, options.TargetApi);
        ValidateTargetAbi(options.TargetAbi);
        ResolvedSdkRoot sdkRoot = ResolveSdkRoot(options);

        string nativeSdkRoot = Path.Combine(sdkRoot.Path, selectedPackage.SdkFolderName, "native");
        await ValidateNativeSdkPackageAsync(nativeSdkRoot, selectedPackage, cancellationToken).ConfigureAwait(false);

        var packageHashes = new List<string>(bundleLock.Packages.Count);
        for (int i = 0; i < bundleLock.Packages.Count; i++)
        {
            HarmonyOSSdkBundleLockedPackage package = bundleLock.Packages[i];
            packageHashes.Add(package.Id + ":" + package.Sha256);
        }
        packageHashes.Sort(StringComparer.Ordinal);

        string sdkRootSha256 = ComputeTextSha256(NormalizeSdkRootForHash(sdkRoot.Path));
        string normalizedJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            releaseId = bundleLock.ReleaseId,
            bundleFingerprint = bundleLock.Fingerprint,
            bundleRelativePath = bundleLock.BundleRelativePath,
            manifestSha256 = bundleLock.ManifestSha256,
            catalogSha256,
            sdkRootSource = sdkRoot.Source,
            sdkRootSha256,
            sdkFolderName = selectedPackage.SdkFolderName,
            targetApi = options.TargetApi,
            targetAbi = options.TargetAbi,
            packageHashes,
        });

        return new HarmonyOSToolchainSnapshot
        {
            ReleaseId = bundleLock.ReleaseId,
            BundleFingerprint = bundleLock.Fingerprint,
            BundleRelativePath = bundleLock.BundleRelativePath,
            ManifestSha256 = bundleLock.ManifestSha256,
            CatalogSha256 = catalogSha256,
            SdkRootSource = sdkRoot.Source,
            SdkRootSha256 = sdkRootSha256,
            SdkFolderName = selectedPackage.SdkFolderName,
            TargetApi = options.TargetApi,
            TargetAbi = options.TargetAbi,
            PackageHashes = packageHashes.AsReadOnly(),
            NormalizedJson = normalizedJson,
        };
    }

    public async Task<HarmonyOSHdcToolchainResolution> ResolveHdcAsync(
        HarmonyOSToolchainResolveOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ContainsCapability(options.RequiredCapabilities, HarmonyOSBundleCapability.HdcDeploy))
        {
            throw new ArgumentException(
                "Resolving HDC requires the hdcDeploy Bundle capability.",
                nameof(options.RequiredCapabilities));
        }

        HarmonyOSToolchainSnapshot snapshot = await ResolveAsync(options, cancellationToken).ConfigureAwait(false);
        ResolvedSdkRoot sdkRoot = ResolveSdkRoot(options);
        return new HarmonyOSHdcToolchainResolution
        {
            Snapshot = snapshot,
            HdcExecutablePath = ResolveHdcExecutablePath(sdkRoot.Path, snapshot.SdkFolderName),
        };
    }

    private static async Task<HarmonyOSSdkBundleLock> ReadLockAsync(
        string projectRoot,
        string? configuredLockPath,
        CancellationToken cancellationToken)
    {
        string lockPath = string.IsNullOrWhiteSpace(configuredLockPath)
            ? GetContainedPath(
                projectRoot,
                BundleRootRelativePath + "/" + HarmonyOSSdkBundleLockFile.DefaultFileName,
                HarmonyOSBundleErrorCode.LockInvalid)
            : GetProjectContainedAbsolutePath(projectRoot, configuredLockPath);
        if (!File.Exists(lockPath))
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The project does not contain an OpenHarmony SDK Bundle lock.");

        try
        {
            HarmonyOSSdkBundleLock bundleLock = await HarmonyOSSdkBundleLockFile.ReadAsync(lockPath, cancellationToken).ConfigureAwait(false);
            if (!bundleLock.BundleRelativePath.StartsWith(BundleRootRelativePath + "/", StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle lock points outside the vendored Bundle root.");
            return bundleLock;
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle lock is invalid.", exception);
        }
    }

    private static async Task<IReadOnlyList<string>> ValidateImportedBundleAsync(
        string bundleRoot,
        HarmonyOSSdkBundleLock bundleLock,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(bundleRoot))
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The locked OpenHarmony SDK Bundle directory is missing.");

        var filesByPath = new Dictionary<string, HarmonyOSSdkBundleLockedFile>(StringComparer.Ordinal);
        for (int i = 0; i < bundleLock.Files.Count; i++)
        {
            HarmonyOSSdkBundleLockedFile file = bundleLock.Files[i];
            if (!filesByPath.TryAdd(file.RelativePath, file))
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, $"The OpenHarmony SDK Bundle lock duplicates '{file.RelativePath}'.");

            string path = GetContainedPath(bundleRoot, file.RelativePath, HarmonyOSBundleErrorCode.LockInvalid);
            if (!File.Exists(path))
                throw BundleError(HarmonyOSBundleErrorCode.HashMismatch, $"The locked OpenHarmony SDK Bundle file is missing: {file.RelativePath}");
            FileInfo info = new(path);
            string actualSha256 = await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (info.Length != file.Length || !string.Equals(actualSha256, file.Sha256, StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.HashMismatch, $"The locked OpenHarmony SDK Bundle file failed verification: {file.RelativePath}");
        }

        if (!filesByPath.ContainsKey(CatalogRelativePath))
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle lock does not declare sdk-catalog.json.");

        ValidateLockedPackageContract(bundleLock, filesByPath);

        string manifestPath = GetContainedPath(bundleRoot, ManifestRelativePath, HarmonyOSBundleErrorCode.LockInvalid);
        if (!File.Exists(manifestPath) ||
            !string.Equals(await ComputeFileSha256Async(manifestPath, cancellationToken).ConfigureAwait(false), bundleLock.ManifestSha256, StringComparison.Ordinal))
        {
            throw BundleError(HarmonyOSBundleErrorCode.HashMismatch, "The locked OpenHarmony SDK Bundle manifest failed verification.");
        }

        return await ValidateManifestProjectionAsync(bundleRoot, manifestPath, bundleLock, filesByPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HarmonyOSSdkCatalog> ReadCatalogAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            HarmonyOSSdkCatalog? catalog = JsonSerializer.Deserialize<HarmonyOSSdkCatalog>(json, s_jsonOptions);
            if (catalog == null || catalog.SchemaVersion != 1)
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The OpenHarmony SDK catalog schema is unsupported.");
            ValidateCatalog(catalog);
            return catalog;
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The OpenHarmony SDK catalog is invalid.", exception);
        }
    }

    private static void ValidateLockedPackageContract(
        HarmonyOSSdkBundleLock bundleLock,
        Dictionary<string, HarmonyOSSdkBundleLockedFile> filesByPath)
    {
        if (bundleLock.Packages.Count != s_requiredPackageIds.Length)
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle lock does not contain the complete package contract.");

        var packagesById = new Dictionary<string, HarmonyOSSdkBundleLockedPackage>(StringComparer.Ordinal);
        for (int i = 0; i < bundleLock.Packages.Count; i++)
        {
            HarmonyOSSdkBundleLockedPackage package = bundleLock.Packages[i];
            string expectedPath = "feed/" + package.Id + "." + bundleLock.PackageVersion + ".nupkg";
            if (!packagesById.TryAdd(package.Id, package) ||
                !string.Equals(package.Version, bundleLock.PackageVersion, StringComparison.Ordinal) ||
                !string.Equals(package.RelativePath, expectedPath, StringComparison.Ordinal) ||
                !filesByPath.TryGetValue(package.RelativePath, out HarmonyOSSdkBundleLockedFile? file) ||
                !string.Equals(file.Sha256, package.Sha256, StringComparison.Ordinal))
            {
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, $"The OpenHarmony SDK Bundle package lock is inconsistent: {package.Id}");
            }
        }

        for (int i = 0; i < s_requiredPackageIds.Length; i++)
        {
            if (!packagesById.ContainsKey(s_requiredPackageIds[i]))
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, $"The OpenHarmony SDK Bundle lock is missing package: {s_requiredPackageIds[i]}");
        }
    }

    private static async Task<IReadOnlyList<string>> ValidateManifestProjectionAsync(
        string bundleRoot,
        string manifestPath,
        HarmonyOSSdkBundleLock bundleLock,
        Dictionary<string, HarmonyOSSdkBundleLockedFile> filesByPath,
        CancellationToken cancellationToken)
    {
        ReleaseManifest manifest;
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, s_jsonOptions)
                ?? throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The locked OpenHarmony SDK Bundle manifest is empty.");
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The locked OpenHarmony SDK Bundle manifest is invalid.", exception);
        }

        if (manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.ReleaseId, bundleLock.ReleaseId, StringComparison.Ordinal) ||
            !string.Equals(manifest.PackageVersion, bundleLock.PackageVersion, StringComparison.Ordinal) ||
            manifest.Files == null ||
            manifest.Packages == null)
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest does not match its lock identity.");
        }

        var manifestFiles = new Dictionary<string, ReleaseManifestFile>(StringComparer.Ordinal);
        bool hasTemplate = false;
        bool hasLicense = false;
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            ReleaseManifestFile file = manifest.Files[i] ?? throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest contains a null file entry.");
            string path = ValidateManifestFilePath(file.Path);
            if (path.StartsWith("templates/stage-player/", StringComparison.Ordinal))
                hasTemplate = true;
            if (path.StartsWith("licenses/", StringComparison.Ordinal))
                hasLicense = true;
            if (!manifestFiles.TryAdd(path, file) || file.Length < 0 || !IsLowercaseSha256(file.Sha256))
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest file contract is invalid.");
        }

        if (!manifestFiles.ContainsKey("SHA256SUMS") ||
            !manifestFiles.ContainsKey("sbom.spdx.json") ||
            !manifestFiles.ContainsKey(CatalogRelativePath) ||
            !hasTemplate ||
            !hasLicense ||
            manifestFiles.Count != filesByPath.Count)
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest file contract is incomplete.");
        }

        foreach ((string path, HarmonyOSSdkBundleLockedFile lockedFile) in filesByPath)
        {
            if (!manifestFiles.TryGetValue(path, out ReleaseManifestFile? manifestFile) ||
                manifestFile.Length != lockedFile.Length ||
                !string.Equals(manifestFile.Sha256, lockedFile.Sha256, StringComparison.Ordinal))
            {
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, $"The OpenHarmony SDK Bundle manifest does not match lock file '{path}'.");
            }
        }

        var manifestPackages = new Dictionary<string, ReleaseManifestPackage>(StringComparer.Ordinal);
        for (int i = 0; i < manifest.Packages.Count; i++)
        {
            ReleaseManifestPackage package = manifest.Packages[i] ?? throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest contains a null package entry.");
            if (!manifestPackages.TryAdd(package.Id, package))
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest duplicates a package ID.");
        }

        if (manifestPackages.Count != bundleLock.Packages.Count)
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest package contract is incomplete.");
        for (int i = 0; i < bundleLock.Packages.Count; i++)
        {
            HarmonyOSSdkBundleLockedPackage lockedPackage = bundleLock.Packages[i];
            if (!manifestPackages.TryGetValue(lockedPackage.Id, out ReleaseManifestPackage? manifestPackage) ||
                !string.Equals(manifestPackage.Version, lockedPackage.Version, StringComparison.Ordinal) ||
                !string.Equals(manifestPackage.Path, lockedPackage.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(manifestPackage.Sha256, lockedPackage.Sha256, StringComparison.Ordinal))
            {
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, $"The OpenHarmony SDK Bundle manifest does not match package '{lockedPackage.Id}'.");
            }
        }

        foreach (string path in manifestFiles.Keys)
        {
            if (!path.StartsWith("feed/", StringComparison.Ordinal))
                continue;
            bool declaredPackage = false;
            foreach (ReleaseManifestPackage package in manifestPackages.Values)
            {
                if (string.Equals(package.Path, path, StringComparison.Ordinal))
                {
                    declaredPackage = true;
                    break;
                }
            }
            if (!declaredPackage)
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, $"The OpenHarmony SDK Bundle feed file is not a declared package: {path}");
        }

        var expectedPaths = new HashSet<string>(manifestFiles.Keys, StringComparer.Ordinal)
        {
            ManifestRelativePath,
        };
        try
        {
            foreach (string path in Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(bundleRoot, path).Replace('\\', '/');
                if (!expectedPaths.Remove(relativePath))
                    throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, $"The OpenHarmony SDK Bundle contains an undeclared file: {relativePath}");
            }
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle directory could not be enumerated.", exception);
        }

        if (expectedPaths.Count != 0)
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle directory is missing a manifest-declared file.");

        return HarmonyOSBundleCapabilities.ValidateDeclared(manifest.Capabilities);
    }

    private static void ValidateCatalog(HarmonyOSSdkCatalog catalog)
    {
        if (catalog.SupportedApis.Count == 0 || catalog.BuildApis.Count == 0 || catalog.Packages.Count == 0)
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The OpenHarmony SDK catalog is missing API policy entries.");

        for (int i = 0; i < catalog.BuildApis.Count; i++)
        {
            int api = catalog.BuildApis[i];
            if (!Contains(catalog.SupportedApis, api) || Contains(catalog.NativeUnavailableApis, api) || Contains(catalog.ExcludedApis, api))
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The OpenHarmony SDK catalog has an inconsistent build API: {api}");
        }

        for (int i = 0; i < catalog.Packages.Count; i++)
        {
            CatalogPackage package = catalog.Packages[i] ?? throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The OpenHarmony SDK catalog contains a null package entry.");
            if (!Contains(catalog.BuildApis, package.ApiLevel) ||
                !IsSafeSdkFolderName(package.SdkFolderName) ||
                string.IsNullOrWhiteSpace(package.Version) ||
                string.IsNullOrWhiteSpace(package.ReleaseType) ||
                !IsSha256(package.ManifestSha256))
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The OpenHarmony SDK catalog has an invalid package entry.");
            for (int j = i + 1; j < catalog.Packages.Count; j++)
            {
                if (catalog.Packages[j]?.ApiLevel == package.ApiLevel)
                    throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The OpenHarmony SDK catalog duplicates API {package.ApiLevel}.");
            }
        }
    }

    private static CatalogPackage ResolveCatalogPackage(HarmonyOSSdkCatalog catalog, int targetApi)
    {
        if (targetApi == 25 || Contains(catalog.ExcludedApis, targetApi))
            throw BundleError(HarmonyOSBundleErrorCode.ApiExcluded, $"OpenHarmony API {targetApi} is intentionally excluded.");
        if (Contains(catalog.NativeUnavailableApis, targetApi) || !Contains(catalog.BuildApis, targetApi))
            throw BundleError(HarmonyOSBundleErrorCode.ApiUnavailable, $"OpenHarmony API {targetApi} is not available as a Native SDK build target.");

        for (int i = 0; i < catalog.Packages.Count; i++)
        {
            CatalogPackage package = catalog.Packages[i];
            if (package.ApiLevel == targetApi)
                return package;
        }
        throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The OpenHarmony SDK catalog has no package mapping for API {targetApi}.");
    }

    private static async Task ValidateNativeSdkPackageAsync(
        string nativeSdkRoot,
        CatalogPackage expected,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(nativeSdkRoot, "oh-uni-package.json");
        if (!Directory.Exists(nativeSdkRoot) || !File.Exists(manifestPath))
        {
            throw BundleError(
                HarmonyOSBundleErrorCode.SdkApiMissing,
                $"The official OpenHarmony SDK does not contain Native SDK API {expected.ApiLevel}.");
        }

        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            HarmonyOSNativeSdkPackage? actual = JsonSerializer.Deserialize<HarmonyOSNativeSdkPackage>(json, s_jsonOptions);
            string actualSha256 = await ComputeFileSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
            if (actual == null ||
                actual.ApiVersion != expected.ApiLevel ||
                !string.Equals(actual.Version, expected.Version, StringComparison.Ordinal) ||
                !string.Equals(actual.ReleaseType, expected.ReleaseType, StringComparison.Ordinal) ||
                !string.Equals(actualSha256, expected.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw BundleError(
                    HarmonyOSBundleErrorCode.SdkApiMissing,
                    $"The official OpenHarmony Native SDK API {expected.ApiLevel} does not match the locked catalog.");
            }
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.SdkApiMissing, $"The official OpenHarmony Native SDK API {expected.ApiLevel} manifest is invalid.", exception);
        }
    }

    private static void ValidateTargetAbi(string? targetAbi)
    {
        for (int i = 0; i < s_supportedAbis.Length; i++)
        {
            if (string.Equals(targetAbi, s_supportedAbis[i], StringComparison.Ordinal))
                return;
        }
        throw BundleError(HarmonyOSBundleErrorCode.AbiUnsupported, $"OpenHarmony ABI '{targetAbi}' is unsupported.");
    }

    private static ResolvedSdkRoot ResolveSdkRoot(HarmonyOSToolchainResolveOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CommandLineSdkRoot))
            return ValidateSdkRoot(options.CommandLineSdkRoot, "CommandLine");
        if (!string.IsNullOrWhiteSpace(options.UserPreferenceSdkRoot))
            return ValidateSdkRoot(options.UserPreferenceSdkRoot, "EditorPreference");
        if (!string.IsNullOrWhiteSpace(options.EnvironmentSdkRoot))
            return ValidateSdkRoot(options.EnvironmentSdkRoot, "Environment");
        throw BundleError(HarmonyOSBundleErrorCode.SdkRootMissing, "No official OpenHarmony SDK root was configured.");
    }

    private static string ResolveHdcExecutablePath(string sdkRoot, string sdkFolderName)
    {
        string toolchains = Path.Combine(sdkRoot, sdkFolderName, "toolchains");
        string[] fileNames = OperatingSystem.IsWindows() ? ["hdc.exe", "hdc"] : ["hdc", "hdc.exe"];
        for (int i = 0; i < fileNames.Length; i++)
        {
            string candidate = Path.Combine(toolchains, fileNames[i]);
            if (File.Exists(candidate))
                return candidate;
        }
        throw BundleError(
            HarmonyOSBundleErrorCode.HdcMissing,
            $"The official OpenHarmony SDK API folder '{sdkFolderName}' does not contain HDC tooling.");
    }

    private static bool ContainsCapability(
        IReadOnlyList<HarmonyOSBundleCapability> capabilities,
        HarmonyOSBundleCapability expected)
    {
        for (int i = 0; i < capabilities.Count; i++)
        {
            if (capabilities[i] == expected)
                return true;
        }
        return false;
    }

    private static ResolvedSdkRoot ValidateSdkRoot(string value, string source)
    {
        string path;
        try
        {
            path = Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.SdkRootInvalid, $"The {source} OpenHarmony SDK root is invalid.", exception);
        }

        if (!Directory.Exists(path))
            throw BundleError(HarmonyOSBundleErrorCode.SdkRootInvalid, $"The {source} OpenHarmony SDK root does not exist.");
        return new ResolvedSdkRoot(path, source);
    }

    private static string NormalizeProjectRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "A project root is required to resolve an OpenHarmony SDK Bundle.");
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony project root is invalid.", exception);
        }
    }

    private static string GetContainedPath(string root, string relativePath, HarmonyOSBundleErrorCode code)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        string containedRoot = fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(containedRoot, StringComparison.OrdinalIgnoreCase))
            throw BundleError(code, $"OpenHarmony SDK path escapes its project root: {relativePath}");
        return path;
    }

    private static string GetProjectContainedAbsolutePath(string projectRoot, string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string containedRoot = fullRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(containedRoot, StringComparison.OrdinalIgnoreCase))
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The configured OpenHarmony SDK Bundle lock is outside the project root.");
            return fullPath;
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The configured OpenHarmony SDK Bundle lock path is invalid.", exception);
        }
    }

    private static bool IsSafeSdkFolderName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOfAny(['/', '\\', ':']) < 0 &&
        value != "." &&
        value != ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool Contains(List<int> values, int value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == value)
                return true;
        }
        return false;
    }

    private static bool IsSha256(string? value)
    {
        if (value == null || value.Length != 64)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if ((character < '0' || character > '9') &&
                (character < 'a' || character > 'f') &&
                (character < 'A' || character > 'F'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowercaseSha256(string? value)
    {
        if (value == null || value.Length != 64)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
                return false;
        }
        return true;
    }

    private static string ValidateManifestFilePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.IndexOf('\\') >= 0 ||
            value.IndexOf(':') >= 0 ||
            value[0] == '/' ||
            value.EndsWith("/", StringComparison.Ordinal))
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest contains an unsafe file path.");
        }

        string[] segments = value.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] == "." || segments[i] == ".." ||
                segments[i].EndsWith(" ", StringComparison.Ordinal) ||
                segments[i].EndsWith(".", StringComparison.Ordinal))
            {
                throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest contains an unsafe file path.");
            }
        }

        if (string.Equals(value, ManifestRelativePath, StringComparison.Ordinal) ||
            !(string.Equals(value, "SHA256SUMS", StringComparison.Ordinal) ||
              string.Equals(value, "sbom.spdx.json", StringComparison.Ordinal) ||
              value.StartsWith("feed/", StringComparison.Ordinal) ||
              value.StartsWith("schemas/", StringComparison.Ordinal) ||
              value.StartsWith("templates/stage-player/", StringComparison.Ordinal) ||
              value.StartsWith("licenses/", StringComparison.Ordinal)))
        {
            throw BundleError(HarmonyOSBundleErrorCode.LockInvalid, "The OpenHarmony SDK Bundle manifest contains a file outside the Bundle contract.");
        }
        return value;
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeTextSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string NormalizeSdkRootForHash(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

    private static HarmonyOSBundleException BundleError(HarmonyOSBundleErrorCode code, string message, Exception? innerException = null) =>
        new(code, message, innerException);

    private sealed record ResolvedSdkRoot(string Path, string Source);

    private sealed class HarmonyOSSdkCatalog
    {
        public int SchemaVersion { get; init; }
        public List<int> SupportedApis { get; init; } = [];
        public List<int> BuildApis { get; init; } = [];
        public List<int> NativeUnavailableApis { get; init; } = [];
        public List<int> ExcludedApis { get; init; } = [];
        public List<CatalogPackage> Packages { get; init; } = [];
    }

    private sealed class CatalogPackage
    {
        public int ApiLevel { get; init; }
        public string SdkFolderName { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string ReleaseType { get; init; } = string.Empty;
        public string ManifestSha256 { get; init; } = string.Empty;
    }

    private sealed class HarmonyOSNativeSdkPackage
    {
        public int ApiVersion { get; init; }
        public string Version { get; init; } = string.Empty;
        public string ReleaseType { get; init; } = string.Empty;
    }

    private sealed class ReleaseManifest
    {
        public int SchemaVersion { get; init; }
        public string ReleaseId { get; init; } = string.Empty;
        public string PackageVersion { get; init; } = string.Empty;
        public List<string>? Capabilities { get; init; }
        public List<ReleaseManifestFile>? Files { get; init; }
        public List<ReleaseManifestPackage>? Packages { get; init; }
    }

    private sealed class ReleaseManifestFile
    {
        public string Path { get; init; } = string.Empty;
        public long Length { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class ReleaseManifestPackage
    {
        public string Id { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }
}
