// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Stable errors emitted while validating the immutable OpenHarmony SDK Bundle contract.</summary>
internal enum HarmonyOSBundleErrorCode
{
    InvalidArchive,
    ZipTraversal,
    ManifestInvalid,
    HashMismatch,
    PackageContractMismatch,
    ReleaseConflict,
    ImportCancelled,
    LockInvalid,
    SdkRootInvalid,
    SdkRootMissing,
    ApiExcluded,
    ApiUnavailable,
    SdkApiMissing,
    AbiUnsupported,
    BundleCapabilityMissing,
    HdcMissing,
}

/// <summary>Exception carrying a stable Bundle-validation error code for UI and CLI diagnostics.</summary>
internal sealed class HarmonyOSBundleException : Exception
{
    internal HarmonyOSBundleException(HarmonyOSBundleErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal HarmonyOSBundleErrorCode Code { get; }
}

/// <summary>One verified file copied from an imported SDK Bundle.</summary>
internal sealed class HarmonyOSSdkBundleLockedFile
{
    public string RelativePath { get; init; } = string.Empty;
    public long Length { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>One NuGet package pinned by the imported SDK Bundle release.</summary>
internal sealed class HarmonyOSSdkBundleLockedPackage
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>
/// Project-local record of a verified, immutable OpenHarmony SDK Bundle. All Bundle paths are
/// relative to the XEngine project root so the same project can move between development machines.
/// </summary>
internal sealed class HarmonyOSSdkBundleLock
{
    public int SchemaVersion { get; init; } = 1;
    public string ReleaseId { get; init; } = string.Empty;
    public string PackageVersion { get; init; } = string.Empty;
    public string BundleRelativePath { get; init; } = string.Empty;
    public string ArchiveSha256 { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string? SourceUri { get; init; }
    public List<HarmonyOSSdkBundleLockedFile> Files { get; init; } = [];
    public List<HarmonyOSSdkBundleLockedPackage> Packages { get; init; } = [];
    public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>Request to import one immutable OpenHarmony SDK Bundle archive into a project.</summary>
internal sealed record HarmonyOSSdkBundleImportRequest(string ProjectRoot, string ArchivePath)
{
    public string BundleRelativeRoot { get; init; } = "ThirdParty/OpenHarmonySdk";
    public string SourceKind { get; init; } = "LocalArchive";
    public string? SourceUri { get; init; }
}

/// <summary>Progress reported while a verified SDK Bundle is extracted to its private staging directory.</summary>
internal readonly record struct HarmonyOSSdkBundleImportProgress(
    string Stage,
    string RelativePath,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes);

/// <summary>Published project-local SDK Bundle location and its verified lock record.</summary>
internal sealed class HarmonyOSSdkBundleImportResult
{
    public string ImportedRoot { get; init; } = string.Empty;
    public string LockPath { get; init; } = string.Empty;
    public bool Reused { get; init; }
    public HarmonyOSSdkBundleLock Lock { get; init; } = new();
}

/// <summary>Inputs for resolving one imported Bundle against an explicitly selected official SDK installation.</summary>
internal sealed class HarmonyOSToolchainResolveOptions
{
    public required string ProjectRoot { get; init; }
    public string? LockFilePath { get; init; }
    public string? CommandLineSdkRoot { get; init; }
    public string? UserPreferenceSdkRoot { get; init; }
    public string? EnvironmentSdkRoot { get; init; }
    public int TargetApi { get; init; } = 13;
    public string TargetAbi { get; init; } = "arm64-v8a";
    public IReadOnlyList<HarmonyOSBundleCapability> RequiredCapabilities { get; init; } = [];
}

/// <summary>
/// Redacted, deterministic input snapshot for a HarmonyOS build. It deliberately retains no
/// absolute SDK path, provider workspace path, package-cache path, or signing material.
/// </summary>
internal sealed class HarmonyOSToolchainSnapshot
{
    public string ReleaseId { get; init; } = string.Empty;
    public string BundleFingerprint { get; init; } = string.Empty;
    public string BundleRelativePath { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public string CatalogSha256 { get; init; } = string.Empty;
    public string SdkRootSource { get; init; } = string.Empty;
    public string SdkRootSha256 { get; init; } = string.Empty;
    public string SdkFolderName { get; init; } = string.Empty;
    public int TargetApi { get; init; }
    public string TargetAbi { get; init; } = string.Empty;
    public IReadOnlyList<string> PackageHashes { get; init; } = [];
    public string NormalizedJson { get; init; } = string.Empty;
}

/// <summary>Runtime-only HDC location paired with a redacted toolchain snapshot.</summary>
internal sealed class HarmonyOSHdcToolchainResolution
{
    public HarmonyOSToolchainSnapshot Snapshot { get; init; } = new();
    public string HdcExecutablePath { get; init; } = string.Empty;
}
