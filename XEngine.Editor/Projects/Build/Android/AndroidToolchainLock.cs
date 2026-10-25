// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

[JsonConverter(typeof(JsonStringEnumConverter<AndroidToolchainSourceKind>))]
internal enum AndroidToolchainSourceKind
{
    CommandLineOverride,
    UserPreference,
    UnityImport,
    StandardInstall,
}

internal sealed record AndroidToolchainHostLock(
    string OperatingSystem,
    string OperatingSystemVersion,
    string Architecture,
    bool Supported);

internal sealed record AndroidToolchainComponentLock(
    string Id,
    string ResolvedPath,
    string Version,
    string Sha256,
    AndroidToolchainSourceKind Source,
    string SourceRoot);

internal sealed record AndroidSdkPackageLock(
    string Id,
    string Version,
    string Path,
    string Sha256);

internal sealed record AndroidLicenseLock(
    string FileName,
    string Sha256,
    bool Present);

internal sealed record AndroidToolchainProbeLock(
    string Id,
    string ExecutablePath,
    string Arguments,
    int ExitCode,
    long DurationMilliseconds,
    string OutputTail);

internal sealed record AndroidToolchainDryRunLock(
    bool Succeeded,
    string TargetFramework,
    string RuntimeIdentifier,
    int TargetApi,
    long DurationMilliseconds,
    string ArtifactName,
    long ArtifactBytes,
    string ArtifactSha256,
    string Failure);

internal sealed class AndroidToolchainLock
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public AndroidToolchainHostLock Host { get; init; } = new(string.Empty, string.Empty, string.Empty, false);
    public string DotNetSdkVersion { get; init; } = string.Empty;
    public string DotNetRuntimeVersion { get; init; } = string.Empty;
    public string AndroidWorkloadVersion { get; init; } = string.Empty;
    public string AndroidWorkloadManifestPath { get; init; } = string.Empty;
    public string ImportedCachePath { get; init; } = string.Empty;
    public string ImportedCacheFingerprint { get; init; } = string.Empty;
    public List<AndroidToolchainComponentLock> Components { get; init; } = [];
    public List<AndroidSdkPackageLock> SdkPackages { get; init; } = [];
    public List<AndroidLicenseLock> Licenses { get; init; } = [];
    public List<AndroidToolchainProbeLock> Probes { get; init; } = [];
    public AndroidToolchainDryRunLock DryRun { get; init; } =
        new(false, "net10.0-android", "android-arm64", 36, 0, string.Empty, 0, string.Empty, "Not run");
}

internal sealed class AndroidToolchainSnapshot
{
    private readonly AndroidToolchainLock _lock;

    internal AndroidToolchainSnapshot(
        AndroidToolchainLock value,
        string dotNetExecutable,
        string javaSdkRoot,
        string androidSdkRoot,
        string androidNdkRoot,
        string bundletoolPath)
    {
        _lock = AndroidToolchainLockFile.Clone(value);
        DotNetExecutable = dotNetExecutable;
        JavaSdkRoot = javaSdkRoot;
        AndroidSdkRoot = androidSdkRoot;
        AndroidNdkRoot = androidNdkRoot;
        BundletoolPath = bundletoolPath;
        Components = new ReadOnlyCollection<AndroidToolchainComponentLock>([.. _lock.Components]);
        SdkPackages = new ReadOnlyCollection<AndroidSdkPackageLock>([.. _lock.SdkPackages]);
        Licenses = new ReadOnlyCollection<AndroidLicenseLock>([.. _lock.Licenses]);
        Probes = new ReadOnlyCollection<AndroidToolchainProbeLock>([.. _lock.Probes]);
    }

    internal string Fingerprint => _lock.Fingerprint;
    internal string DotNetExecutable { get; }
    internal string JavaSdkRoot { get; }
    internal string AndroidSdkRoot { get; }
    internal string AndroidNdkRoot { get; }
    internal string BundletoolPath { get; }
    internal AndroidToolchainHostLock Host => _lock.Host;
    internal AndroidToolchainDryRunLock DryRun => _lock.DryRun;
    internal IReadOnlyList<AndroidToolchainComponentLock> Components { get; }
    internal IReadOnlyList<AndroidSdkPackageLock> SdkPackages { get; }
    internal IReadOnlyList<AndroidLicenseLock> Licenses { get; }
    internal IReadOnlyList<AndroidToolchainProbeLock> Probes { get; }

    internal AndroidToolchainLock CreateLock() => AndroidToolchainLockFile.Clone(_lock);
}

internal static class AndroidToolchainLockFile
{
    internal const string DefaultFileName = "android-toolchain-lock.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter<AndroidToolchainSourceKind>() },
    };

    internal static async Task WriteAsync(
        string path,
        AndroidToolchainSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        string absolutePath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(absolutePath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(absolutePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string json = JsonSerializer.Serialize(snapshot.CreateLock(), s_jsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, absolutePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal static async Task<AndroidToolchainSnapshot> ReadAndValidateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string absolutePath = Path.GetFullPath(path);
        AndroidToolchainLock value;
        try
        {
            string json = await File.ReadAllTextAsync(absolutePath, cancellationToken).ConfigureAwait(false);
            value = JsonSerializer.Deserialize<AndroidToolchainLock>(json, s_jsonOptions)
                ?? throw new InvalidDataException("Android toolchain lock is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Android toolchain lock is invalid JSON.", exception);
        }

        ValidateStructure(value);
        string fingerprint = ComputeFingerprint(value);
        if (!string.Equals(value.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Android toolchain lock fingerprint does not match its contents.");

        for (int i = 0; i < value.Components.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AndroidToolchainComponentLock component = value.Components[i];
            var info = new FileInfo(component.ResolvedPath);
            if (!info.Exists)
                throw new FileNotFoundException($"Locked Android toolchain component is missing: {component.Id}", component.ResolvedPath);
            string hash = await HashFileAsync(info.FullName, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, component.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Locked Android toolchain component failed SHA-256 verification: {component.Id}");
        }

        for (int i = 0; i < value.SdkPackages.Count; i++)
        {
            AndroidSdkPackageLock package = value.SdkPackages[i];
            if (!Directory.Exists(package.Path) && !File.Exists(package.Path))
                throw new DirectoryNotFoundException($"Locked Android SDK package is missing: {package.Id} ({package.Path})");
        }

        for (int i = 0; i < value.Licenses.Count; i++)
        {
            AndroidLicenseLock license = value.Licenses[i];
            string licensePath = Path.Combine(GetSdkRoot(value), "licenses", license.FileName);
            if (license.Present && (!File.Exists(licensePath) ||
                !string.Equals(await HashFileAsync(licensePath, cancellationToken).ConfigureAwait(false), license.Sha256, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Locked Android SDK license state changed: {license.FileName}");
            }
        }

        if (!string.IsNullOrWhiteSpace(value.ImportedCachePath))
        {
            AndroidToolchainImportManifest manifest = await AndroidToolchainCacheVerifier
                .VerifyAsync(value.ImportedCachePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(manifest.Fingerprint, value.ImportedCacheFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("Locked Unity import fingerprint does not match the verified cache.");
        }

        string dotnet = GetComponent(value, "dotnet").ResolvedPath;
        string javaRoot = Path.GetDirectoryName(Path.GetDirectoryName(GetComponent(value, "java").ResolvedPath)!)!;
        string sdkRoot = GetSdkRoot(value);
        string ndkRoot = FindCommonRoot(GetComponent(value, "clang").ResolvedPath, "toolchains");
        string bundletool = GetComponent(value, "bundletool").ResolvedPath;
        return new AndroidToolchainSnapshot(value, dotnet, javaRoot, sdkRoot, ndkRoot, bundletool);
    }

    internal static string ComputeFingerprint(AndroidToolchainLock value)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(value.SchemaVersion);
            writer.Write(value.Host.OperatingSystem);
            writer.Write(value.Host.OperatingSystemVersion);
            writer.Write(value.Host.Architecture);
            writer.Write(value.DotNetSdkVersion);
            writer.Write(value.DotNetRuntimeVersion);
            writer.Write(value.AndroidWorkloadVersion);
            writer.Write(value.ImportedCacheFingerprint);
            writer.Write(value.Components.Count);
            for (int i = 0; i < value.Components.Count; i++)
            {
                AndroidToolchainComponentLock component = value.Components[i];
                writer.Write(component.Id);
                writer.Write(component.ResolvedPath);
                writer.Write(component.Version);
                writer.Write(component.Sha256);
                writer.Write((int)component.Source);
                writer.Write(component.SourceRoot);
            }
            writer.Write(value.SdkPackages.Count);
            for (int i = 0; i < value.SdkPackages.Count; i++)
            {
                AndroidSdkPackageLock package = value.SdkPackages[i];
                writer.Write(package.Id);
                writer.Write(package.Version);
                writer.Write(package.Path);
                writer.Write(package.Sha256);
            }
            writer.Write(value.Licenses.Count);
            for (int i = 0; i < value.Licenses.Count; i++)
            {
                AndroidLicenseLock license = value.Licenses[i];
                writer.Write(license.FileName);
                writer.Write(license.Sha256);
                writer.Write(license.Present);
            }
            writer.Write(value.DryRun.Succeeded);
            writer.Write(value.DryRun.TargetFramework);
            writer.Write(value.DryRun.RuntimeIdentifier);
            writer.Write(value.DryRun.TargetApi);
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static AndroidToolchainLock Clone(AndroidToolchainLock value) => new()
    {
        SchemaVersion = value.SchemaVersion,
        Fingerprint = value.Fingerprint,
        GeneratedAtUtc = value.GeneratedAtUtc,
        Host = value.Host with { },
        DotNetSdkVersion = value.DotNetSdkVersion,
        DotNetRuntimeVersion = value.DotNetRuntimeVersion,
        AndroidWorkloadVersion = value.AndroidWorkloadVersion,
        AndroidWorkloadManifestPath = value.AndroidWorkloadManifestPath,
        ImportedCachePath = value.ImportedCachePath,
        ImportedCacheFingerprint = value.ImportedCacheFingerprint,
        Components = [.. value.Components],
        SdkPackages = [.. value.SdkPackages],
        Licenses = [.. value.Licenses],
        Probes = [.. value.Probes],
        DryRun = value.DryRun with { },
    };

    private static void ValidateStructure(AndroidToolchainLock value)
    {
        if (value.SchemaVersion != AndroidToolchainLock.CurrentSchemaVersion ||
            value.Host == null || !value.Host.Supported ||
            value.Components == null || value.SdkPackages == null || value.Licenses == null || value.Probes == null ||
            value.DryRun == null || !value.DryRun.Succeeded ||
            string.IsNullOrWhiteSpace(value.DryRun.ArtifactName) || value.DryRun.ArtifactBytes <= 0 ||
            value.DryRun.ArtifactSha256.Length != 64 || !string.IsNullOrWhiteSpace(value.DryRun.Failure) ||
            string.IsNullOrWhiteSpace(value.DotNetSdkVersion) ||
            string.IsNullOrWhiteSpace(value.AndroidWorkloadVersion) ||
            value.Fingerprint.Length != 64)
        {
            throw new InvalidDataException("Android toolchain lock is incomplete or unsupported.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < value.Components.Count; i++)
        {
            AndroidToolchainComponentLock component = value.Components[i];
            if (component == null || string.IsNullOrWhiteSpace(component.Id) || !ids.Add(component.Id) ||
                string.IsNullOrWhiteSpace(component.ResolvedPath) || string.IsNullOrWhiteSpace(component.Version) ||
                component.Sha256.Length != 64)
            {
                throw new InvalidDataException("Android toolchain lock contains invalid component metadata.");
            }
        }
        foreach (string required in new[] { "dotnet", "dotnet-android-workload", "java", "aapt2", "apksigner", "zipalign", "adb", "sdkmanager", "clang", "bundletool" })
        {
            if (!ids.Contains(required))
                throw new InvalidDataException($"Android toolchain lock is missing component: {required}");
        }
    }

    private static AndroidToolchainComponentLock GetComponent(AndroidToolchainLock value, string id)
    {
        for (int i = 0; i < value.Components.Count; i++)
        {
            if (string.Equals(value.Components[i].Id, id, StringComparison.Ordinal))
                return value.Components[i];
        }
        throw new InvalidDataException($"Android toolchain lock is missing component: {id}");
    }

    private static string GetSdkRoot(AndroidToolchainLock value) =>
        FindCommonRoot(GetComponent(value, "aapt2").ResolvedPath, "build-tools");

    private static string FindCommonRoot(string path, string marker)
    {
        DirectoryInfo? directory = new FileInfo(path).Directory;
        while (directory != null && !string.Equals(directory.Name, marker, PathComparison))
            directory = directory.Parent;
        return directory?.Parent?.FullName
            ?? throw new InvalidDataException($"Locked component path does not contain expected directory '{marker}': {path}");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
