// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Runtime;

namespace XEngine.Editor.Build.HarmonyOS;

internal static class HarmonyOSAbiMapper
{
    internal static string ToRuntimeIdentifier(string abi) => abi switch
    {
        // The NativeAOT cross-compile is published under the .NET-SDK-recognized linux-musl RID;
        // PublishAotCross's Crosscompile.targets then derives OpenHarmonyAbi from the RID suffix
        // (-arm64 / -x64) and rewrites the toolchain to the OpenHarmony SDK clang/sysroot. Using
        // an 'openharmony-*' RID here would fail with NETSDK1083/NETSDK1203 (unrecognized RID,
        // no AOT support).
        "arm64-v8a" => "linux-musl-arm64",
        "x86_64" => "linux-musl-x64",
        _ => throw new ArgumentOutOfRangeException(nameof(abi), abi, "Unsupported HarmonyOS ABI."),
    };
}

/// <summary>Inputs to a standalone HarmonyOS NativeAOT player and Stage project export.</summary>
internal sealed class HarmonyOSPlayerProjectOptions
{
    public string ProjectName { get; init; } = "XEngineGame";
    public string ApplicationId { get; init; } = "com.xengine.game";
    public string ApplicationLabel { get; init; } = "XEngine";
    public bool ShowFpsOverlay { get; init; }
    /// <summary>Optional project PNG for launcher and splash branding.</summary>
    public string? ApplicationIconPath { get; init; }
    public int VersionCode { get; init; } = 1;
    public int ApiLevel { get; init; } = 13;
    public string Abi { get; init; } = "arm64-v8a";
    public string BundleRoot { get; init; } = string.Empty;
    public HarmonyOSSdkBundleLock? BundleLock { get; init; }
    public string BundleFingerprint { get; init; } = string.Empty;
    public string AssetPayloadDirectory { get; init; } = string.Empty;
    public string NativeHostSourceDirectory { get; init; } = string.Empty;
    /// <summary>
    /// Optional directory of extra prebuilt native libraries (e.g. the FFmpeg shared
    /// libraries for OpenHarmony) copied into the Stage's <c>entry/libs/&lt;abi&gt;/</c>
    /// so the HAP packages them and the app loader resolves their sonames. Null or a
    /// missing directory skips native payload staging entirely.
    /// </summary>
    public string? NativeLibraryPayloadDirectory { get; init; }
    public AssetPackagingMode PackagingMode { get; init; } = AssetPackagingMode.LooseFiles;
    public Guid DefaultSceneGuid { get; init; }
    public IReadOnlyList<HarmonyOSManagedAssemblyInput> ManagedAssemblies { get; init; } = [];
    public HarmonyOSManagedTypeRootCollection ManagedTypeRoots { get; init; } = HarmonyOSManagedTypeRootCollection.Empty;
}

/// <summary>Paths emitted by one deterministic standalone HarmonyOS player export.</summary>
internal sealed record HarmonyOSPlayerProjectLayout(
    string RootDirectory,
    string ProjectPath,
    string StageProjectDirectory,
    string OwnershipManifestPath,
    string AssemblyName,
    string RuntimeIdentifier,
    string Abi);

/// <summary>
/// Creates a relocatable NativeAOT player project and Stage project using only files pinned by an
/// imported OpenHarmony SDK Bundle lock. Package build execution remains outside this generator.
/// </summary>
internal static class HarmonyOSPlayerProjectGenerator
{
    private const int OwnershipSchemaVersion = 1;
    private const string TemplateManifestRelativePath = "templates/stage-player/template-manifest.json";
    private const string PlayerLibraryFileName = "libxengine_player.so";
    // Assets go into 'resfile' (not 'rawfile'): OHOS maps resfile/ to the filesystem (ctx.filesDir),
    // so System.IO.Directory/File can read them. rawfile/ is only accessible via ResourceManager NDK.
    private const string AssetStageRelativePath = "entry/src/main/resources/resfile";
    private const string NativeHostStageRelativePath = "entry/src/main/cpp/xengine";
    // Prebuilt native libraries (FFmpeg and friends) ship in the module's libs/<abi>/ folder;
    // hvigor packages that folder into the HAP and the OS app loader puts it on the dlopen
    // search path, which is how libxengine_player.so itself is resolved.
    private const string NativeLibsStageRelativePath = "entry/libs";
    // The FFmpeg sonames the video module's native loader expects (FFmpegNativeLoader on
    // OpenHarmony constructs exactly these ABI-major-only names). Required whenever a native
    // library payload directory is supplied; additional files (e.g. license text) are copied
    // verbatim into libs/<abi>/ only when they end in .so[.<digits>] — hvigor rejects other
    // file kinds there.
    private static readonly string[] s_requiredNativeLibraryPayloadFiles =
    [
        "libavutil.so.60",
        "libswresample.so.6",
        "libswscale.so.9",
        "libavcodec.so.62",
        "libavformat.so.62",
        "libavfilter.so.11",
    ];
    private static readonly string[] s_nativeHostFiles =
    [
        "CMakeLists.txt",
        "xengine_host_napi.cpp",
        "xengine_player_abi.h",
    ];
    private static readonly UTF8Encoding s_utf8NoBom = new(false);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static async Task<HarmonyOSPlayerProjectLayout> GenerateAsync(
        string outputDirectory,
        HarmonyOSPlayerProjectOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);

        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && HasEntries(root))
            throw new IOException($"HarmonyOS export directory must be empty: {root}");

        ValidatedInputs inputs = await ValidateInputsAsync(options, cancellationToken).ConfigureAwait(false);
        string runtimeIdentifier = HarmonyOSAbiMapper.ToRuntimeIdentifier(options.Abi);
        string assemblyName = GetAssemblyName(options.ProjectName);
        string playerDirectory = Path.Combine(root, "Player");
        string stageDirectory = Path.Combine(root, "Stage");
        string feedDirectory = Path.Combine(root, "feed");
        string projectPath = Path.Combine(playerDirectory, assemblyName + ".csproj");
        string ownershipManifestPath = Path.Combine(root, "xengine-harmonyos-project.json");

        Directory.CreateDirectory(playerDirectory);
        Directory.CreateDirectory(feedDirectory);
        await WriteTextAsync(
                projectPath,
                CreateProject(options, assemblyName, runtimeIdentifier, inputs.ManagedAssemblies),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(playerDirectory, "HarmonyOSPlayer.cs"),
                HarmonyOSManagedPlayerSource.Create(options, inputs.ManagedAssemblies),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(playerDirectory, HarmonyOSManagedTrimRoots.SourceFileName),
                HarmonyOSManagedTrimRoots.CreateSource(options.ManagedTypeRoots),
                cancellationToken)
            .ConfigureAwait(false);
        HarmonyOSManagedTypeRootReport.Write(
            Path.Combine(playerDirectory, "trimming", HarmonyOSManagedTypeRootReport.FileName),
            options.ManagedTypeRoots);
        await CopyInputFilesAsync(
                inputs.ManagedAssemblies,
                Path.Combine(playerDirectory, "Managed"),
                cancellationToken)
            .ConfigureAwait(false);
        await CopyLockedPackagesAsync(inputs.Packages, feedDirectory, cancellationToken).ConfigureAwait(false);
        await HarmonyOSHvigorProjectGenerator.MaterializeAsync(
                stageDirectory,
                inputs.TemplateFiles,
                new HarmonyOSStageTemplateValues(
                    options.ApplicationId,
                    options.ApplicationLabel,
                    options.VersionCode,
                    options.ApiLevel,
                    options.Abi,
                    PlayerLibraryFileName,
                assemblyName),
                cancellationToken)
            .ConfigureAwait(false);
        await ApplyBrandingAsync(stageDirectory, options.ApplicationIconPath, cancellationToken).ConfigureAwait(false);
        await CopyInputFilesAsync(
                inputs.AssetFiles,
                Path.Combine(stageDirectory, AssetStageRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                cancellationToken)
            .ConfigureAwait(false);
        await CopyInputFilesAsync(
                inputs.NativeHostFiles,
                Path.Combine(stageDirectory, NativeHostStageRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                cancellationToken)
            .ConfigureAwait(false);
        if (inputs.NativeLibraryPayloadFiles.Count != 0)
        {
            // Only ELF shared libraries may live in libs/<abi>/; license/provenance text that
            // rides along in the payload directory is filtered out here and kept only in the
            // source tree (Libraries/openharmony-<arch>/native) and the build receipts.
            var packagable = new List<HarmonyOSVerifiedInputFile>(inputs.NativeLibraryPayloadFiles.Count);
            for (int i = 0; i < inputs.NativeLibraryPayloadFiles.Count; i++)
            {
                if (IsNativeLibraryPayloadName(inputs.NativeLibraryPayloadFiles[i].RelativePath))
                    packagable.Add(inputs.NativeLibraryPayloadFiles[i]);
            }
            if (packagable.Count == 0)
                throw new InvalidDataException("HarmonyOS native library payload contains no .so libraries.");
            await CopyInputFilesAsync(
                    packagable,
                    Path.Combine(stageDirectory, NativeLibsStageRelativePath, options.Abi),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        await WriteOwnershipManifestAsync(root, ownershipManifestPath, options.BundleFingerprint, cancellationToken)
            .ConfigureAwait(false);

        return new HarmonyOSPlayerProjectLayout(
            root,
            projectPath,
            stageDirectory,
            ownershipManifestPath,
            assemblyName,
            runtimeIdentifier,
            options.Abi);
    }

    internal static async Task RefreshOwnershipManifestAsync(
        HarmonyOSPlayerProjectLayout layout,
        string bundleFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFingerprint);

        string root = Path.GetFullPath(layout.RootDirectory);
        string manifestPath = Path.GetFullPath(layout.OwnershipManifestPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"HarmonyOS export root is missing: {root}");
        if (!string.Equals(Path.GetDirectoryName(manifestPath), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "HarmonyOS ownership manifest must be directly under the export root.");
        }

        await WriteOwnershipManifestAsync(root, manifestPath, bundleFingerprint, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ValidatedInputs> ValidateInputsAsync(
        HarmonyOSPlayerProjectOptions options,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        HarmonyOSSdkBundleLock bundleLock = options.BundleLock!;
        string bundleRoot = Path.GetFullPath(options.BundleRoot);
        if (!Directory.Exists(bundleRoot))
            throw new DirectoryNotFoundException($"Imported OpenHarmony SDK Bundle is missing: {bundleRoot}");

        var lockedFiles = new Dictionary<string, HarmonyOSSdkBundleLockedFile>(StringComparer.Ordinal);
        for (int i = 0; i < bundleLock.Files.Count; i++)
        {
            HarmonyOSSdkBundleLockedFile file = bundleLock.Files[i];
            lockedFiles.Add(file.RelativePath, file);
        }

        string templateRoot = Path.Combine(bundleRoot, "templates", "stage-player");
        string manifestPath = GetBundlePath(bundleRoot, TemplateManifestRelativePath);
        HarmonyOSSdkBundleLockedFile templateManifestLock = GetLockedFile(lockedFiles, TemplateManifestRelativePath);
        await VerifyLockedFileAsync(manifestPath, templateManifestLock, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> templateFiles = await ReadTemplateManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        ValidateTemplateOwnership(templateRoot, templateFiles);

        var verifiedTemplateFiles = new List<HarmonyOSVerifiedTemplateFile>(templateFiles.Count);
        for (int i = 0; i < templateFiles.Count; i++)
        {
            string relativePath = templateFiles[i];
            string bundleRelativePath = "templates/stage-player/" + relativePath;
            HarmonyOSSdkBundleLockedFile lockedFile = GetLockedFile(lockedFiles, bundleRelativePath);
            string sourcePath = GetBundlePath(bundleRoot, bundleRelativePath);
            await VerifyLockedFileAsync(sourcePath, lockedFile, cancellationToken).ConfigureAwait(false);
            verifiedTemplateFiles.Add(new HarmonyOSVerifiedTemplateFile(relativePath, sourcePath));
        }

        var packages = new List<HarmonyOSVerifiedPackage>(bundleLock.Packages.Count);
        var packageFileNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < bundleLock.Packages.Count; i++)
        {
            HarmonyOSSdkBundleLockedPackage package = bundleLock.Packages[i];
            if (!package.RelativePath.StartsWith("feed/", StringComparison.Ordinal) ||
                !package.RelativePath.EndsWith(".nupkg", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"OpenHarmony SDK Bundle package is outside the locked feed: {package.RelativePath}");
            }

            HarmonyOSSdkBundleLockedFile lockedFile = GetLockedFile(lockedFiles, package.RelativePath);
            if (!string.Equals(lockedFile.Sha256, package.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"OpenHarmony SDK Bundle package hash does not match its locked file: {package.Id}");
            string fileName = Path.GetFileName(package.RelativePath);
            if (!packageFileNames.Add(fileName))
                throw new InvalidDataException($"OpenHarmony SDK Bundle packages have a conflicting feed file name: {fileName}");
            string sourcePath = GetBundlePath(bundleRoot, package.RelativePath);
            await VerifyLockedFileAsync(sourcePath, lockedFile, cancellationToken).ConfigureAwait(false);
            packages.Add(new HarmonyOSVerifiedPackage(fileName, sourcePath));
        }

        IReadOnlyList<HarmonyOSVerifiedInputFile> assetFiles = ValidateInputTree(
                options.AssetPayloadDirectory,
                requiredFiles: ["xengine-harmonyos-payload.json"],
                rejectAdditionalFiles: false,
                "HarmonyOS asset payload",
                cancellationToken);
        IReadOnlyList<HarmonyOSVerifiedInputFile> nativeHostFiles = ValidateInputTree(
                options.NativeHostSourceDirectory,
                s_nativeHostFiles,
                rejectAdditionalFiles: true,
                "HarmonyOS native host",
                cancellationToken);
        IReadOnlyList<HarmonyOSVerifiedInputFile> nativeLibraryPayloadFiles = ValidateNativeLibraryPayload(
                options.NativeLibraryPayloadDirectory,
                cancellationToken);
        IReadOnlyList<HarmonyOSVerifiedInputFile> managedAssemblies = ValidateManagedAssemblies(options.ManagedAssemblies);
        ValidateNoTemplateCollisions(templateFiles, assetFiles, AssetStageRelativePath);
        ValidateNoTemplateCollisions(templateFiles, nativeHostFiles, NativeHostStageRelativePath);
        ValidateNoTemplateCollisions(templateFiles, nativeLibraryPayloadFiles, NativeLibsStageRelativePath + "/" + options.Abi);

        return new ValidatedInputs(verifiedTemplateFiles, packages, assetFiles, nativeHostFiles, nativeLibraryPayloadFiles, managedAssemblies);
    }

    /// <summary>
    /// Validates the optional prebuilt native library payload directory. A null or missing
    /// directory yields an empty list (video decoding is then simply unavailable at runtime);
    /// an existing one must contain every required FFmpeg soname.
    /// </summary>
    private static IReadOnlyList<HarmonyOSVerifiedInputFile> ValidateNativeLibraryPayload(
        string? payloadDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadDirectory))
            return [];
        string root = Path.GetFullPath(payloadDirectory);
        if (!Directory.Exists(root))
            return [];

        IReadOnlyList<HarmonyOSVerifiedInputFile> files = ValidateInputTree(
            root,
            s_requiredNativeLibraryPayloadFiles,
            rejectAdditionalFiles: false,
            "HarmonyOS native library payload",
            cancellationToken);
        return files;
    }

    /// <summary>True for ELF shared-library file names: exactly "lib*.so" or "lib*.so.NN[N]".</summary>
    private static bool IsNativeLibraryPayloadName(string relativePath)
    {
        if (relativePath.Contains('/') || !relativePath.StartsWith("lib", StringComparison.Ordinal))
            return false;
        int soIndex = relativePath.IndexOf(".so", StringComparison.Ordinal);
        if (soIndex < 0)
            return false;
        string suffix = relativePath[(soIndex + 3)..];
        if (suffix.Length == 0)
            return true;
        if (suffix[0] != '.')
            return false;
        for (int i = 1; i < suffix.Length; i++)
        {
            if (suffix[i] < '0' || suffix[i] > '9')
                return false;
        }
        return true;
    }

    private static void ValidateOptions(HarmonyOSPlayerProjectOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProjectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BundleRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BundleFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AssetPayloadDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.NativeHostSourceDirectory);
        ArgumentNullException.ThrowIfNull(options.ManagedAssemblies);
        ArgumentNullException.ThrowIfNull(options.BundleLock);
        if (options.ApiLevel < 13)
            throw new ArgumentOutOfRangeException(nameof(options.ApiLevel), "HarmonyOS API level must be at least 13.");
        if (options.VersionCode < 1)
            throw new ArgumentOutOfRangeException(nameof(options.VersionCode), "HarmonyOS version code must be positive.");
        if (options.PackagingMode == AssetPackagingMode.Embedded)
            throw new InvalidDataException("HarmonyOS player does not support Embedded asset packaging.");
        if (!Enum.IsDefined(options.PackagingMode))
            throw new ArgumentOutOfRangeException(nameof(options.PackagingMode));
        if (!IsApplicationId(options.ApplicationId))
            throw new ArgumentException("HarmonyOS application ID must be dot-separated lowercase identifiers.", nameof(options.ApplicationId));
        if (!IsSafeApplicationLabel(options.ApplicationLabel))
            throw new ArgumentException("HarmonyOS application label contains unsupported characters.", nameof(options.ApplicationLabel));
        _ = HarmonyOSAbiMapper.ToRuntimeIdentifier(options.Abi);
        if (!string.Equals(options.BundleFingerprint, options.BundleLock.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("HarmonyOS export Bundle fingerprint does not match the supplied lock.");
        if (string.IsNullOrWhiteSpace(options.BundleLock.PackageVersion))
            throw new InvalidDataException("HarmonyOS export Bundle lock is missing PackageVersion.");
    }

    private static IReadOnlyList<HarmonyOSVerifiedInputFile> ValidateManagedAssemblies(
        IReadOnlyList<HarmonyOSManagedAssemblyInput> assemblies)
    {
        if (assemblies.Count == 0)
            throw new InvalidDataException("HarmonyOS player managed closure is empty.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<HarmonyOSVerifiedInputFile>(assemblies.Count);
        bool hasRuntime = false;
        for (int i = 0; i < assemblies.Count; i++)
        {
            HarmonyOSManagedAssemblyInput assembly = assemblies[i]
                ?? throw new InvalidDataException("HarmonyOS player managed closure contains a null entry.");
            ArgumentException.ThrowIfNullOrWhiteSpace(assembly.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(assembly.SourcePath);
            if (HarmonyOSManagedDependencyCollector.IsForbidden(assembly.Name))
                throw new InvalidDataException($"HarmonyOS player managed closure contains forbidden assembly '{assembly.Name}'.");

            string sourcePath = Path.GetFullPath(assembly.SourcePath);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"HarmonyOS managed assembly '{assembly.Name}' is missing.", sourcePath);
            if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"HarmonyOS managed assembly cannot be a reparse point: {sourcePath}");
            string identity = AssemblyName.GetAssemblyName(sourcePath).Name
                ?? throw new InvalidDataException($"HarmonyOS managed assembly has no identity: {sourcePath}");
            if (!string.Equals(identity, assembly.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"HarmonyOS managed assembly identity '{identity}' does not match expected '{assembly.Name}'.");
            }
            if (!names.Add(identity))
                throw new InvalidDataException($"HarmonyOS player managed closure duplicates assembly '{identity}'.");

            hasRuntime |= string.Equals(identity, "XEngine.Runtime", StringComparison.OrdinalIgnoreCase);
            files.Add(new HarmonyOSVerifiedInputFile(identity + ".dll", sourcePath));
        }
        if (!hasRuntime)
            throw new InvalidDataException("HarmonyOS player managed closure must contain XEngine.Runtime.");

        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return files.AsReadOnly();
    }

    private static async Task<IReadOnlyList<string>> ReadTemplateManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1 ||
            !root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("HarmonyOS Stage template manifest must declare schemaVersion 1 and files.");
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement entry in files.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("HarmonyOS Stage template manifest contains a non-string file path.");
            string relativePath = entry.GetString() ?? string.Empty;
            if (!IsSafeRelativePath(relativePath) || !seen.Add(relativePath))
                throw new InvalidDataException($"HarmonyOS Stage template manifest contains an invalid file path: {relativePath}");
            result.Add(relativePath);
        }
        if (result.Count == 0)
            throw new InvalidDataException("HarmonyOS Stage template manifest must own at least one file.");
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void ValidateTemplateOwnership(string templateRoot, IReadOnlyList<string> templateFiles)
    {
        if (!Directory.Exists(templateRoot))
            throw new DirectoryNotFoundException($"HarmonyOS Stage template directory is missing: {templateRoot}");
        var owned = new HashSet<string>(templateFiles, StringComparer.Ordinal)
        {
            "template-manifest.json",
        };
        string[] actualFiles = Directory.GetFiles(templateRoot, "*", SearchOption.AllDirectories);
        Array.Sort(actualFiles, StringComparer.Ordinal);
        for (int i = 0; i < actualFiles.Length; i++)
        {
            string relativePath = Path.GetRelativePath(templateRoot, actualFiles[i]).Replace('\\', '/');
            if (!owned.Contains(relativePath))
                throw new InvalidDataException($"HarmonyOS Stage template contains an unowned file: {relativePath}");
        }
        for (int i = 0; i < templateFiles.Count; i++)
        {
            string sourcePath = Path.Combine(templateRoot, templateFiles[i].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
                throw new InvalidDataException($"HarmonyOS Stage template manifest references a missing file: {templateFiles[i]}");
        }
    }

    private static async Task CopyLockedPackagesAsync(
        IReadOnlyList<HarmonyOSVerifiedPackage> packages,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < packages.Count; i++)
        {
            HarmonyOSVerifiedPackage package = packages[i];
            string destination = Path.Combine(destinationDirectory, package.FileName);
            await using FileStream source = new(
                package.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ApplyBrandingAsync(string stageDirectory, string? applicationIconPath, CancellationToken cancellationToken)
    {
        string[] targets =
        [
            "AppScope/resources/base/media/app_icon.png",
            "entry/src/main/resources/base/media/startIcon.png",
            "AppScope/resources/base/media/foreground.png",
            "entry/src/main/resources/base/media/foreground.png",
        ];

        await using Stream? source = string.IsNullOrWhiteSpace(applicationIconPath)
            ? typeof(HarmonyOSPlayerProjectGenerator).Assembly.GetManifestResourceStream("XEngine.Editor.Resources.xengine.png")
            : File.OpenRead(applicationIconPath);
        if (source == null)
            throw new InvalidOperationException("Missing embedded XEngine launcher icon.");

        for (int i = 0; i < targets.Length; i++)
        {
            string destination = Path.Combine(stageDirectory, targets[i].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination))
                continue;

            source.Position = 0;
            await using FileStream output = new(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<HarmonyOSVerifiedInputFile> ValidateInputTree(
        string inputDirectory,
        IReadOnlyList<string> requiredFiles,
        bool rejectAdditionalFiles,
        string description,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(inputDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"{description} directory is missing: {root}");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{description} directory cannot be a reparse point: {root}");

        var files = new List<HarmonyOSVerifiedInputFile>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pendingDirectories.Pop();
            string[] entries = Directory.GetFileSystemEntries(directory);
            Array.Sort(entries, StringComparer.Ordinal);
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                string entry = entries[i];
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"{description} contains a reparse point: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                    continue;
                }

                string relativePath = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (!IsSafeRelativePath(relativePath))
                    throw new InvalidDataException($"{description} contains an invalid file path: {relativePath}");
                files.Add(new HarmonyOSVerifiedInputFile(relativePath, entry));
            }
        }

        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < files.Count; i++)
            actualFiles.Add(files[i].RelativePath);
        for (int i = 0; i < requiredFiles.Count; i++)
        {
            if (!actualFiles.Contains(requiredFiles[i]))
                throw new InvalidDataException($"{description} is missing required file: {requiredFiles[i]}");
        }
        if (rejectAdditionalFiles && actualFiles.Count != requiredFiles.Count)
        {
            for (int i = 0; i < files.Count; i++)
            {
                if (!ContainsOrdinal(requiredFiles, files[i].RelativePath))
                    throw new InvalidDataException($"{description} contains an unowned file: {files[i].RelativePath}");
            }
        }

        return files;
    }

    private static void ValidateNoTemplateCollisions(
        IReadOnlyList<string> templateFiles,
        IReadOnlyList<HarmonyOSVerifiedInputFile> inputFiles,
        string stageRoot)
    {
        var templatePaths = new HashSet<string>(templateFiles, StringComparer.Ordinal);
        for (int i = 0; i < inputFiles.Count; i++)
        {
            string destination = stageRoot + "/" + inputFiles[i].RelativePath;
            foreach (string templatePath in templatePaths)
            {
                if (string.Equals(templatePath, destination, StringComparison.Ordinal) ||
                    templatePath.StartsWith(destination + "/", StringComparison.Ordinal) ||
                    destination.StartsWith(templatePath + "/", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"HarmonyOS Stage input collides with a Bundle template file: {destination}");
                }
            }
        }
    }

    private static async Task CopyInputFilesAsync(
        IReadOnlyList<HarmonyOSVerifiedInputFile> files,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(destinationRoot);
        for (int i = 0; i < files.Count; i++)
        {
            HarmonyOSVerifiedInputFile file = files[i];
            string destination = GetContainedPath(root, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using FileStream source = new(
                file.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteOwnershipManifestAsync(
        string root,
        string manifestPath,
        string bundleFingerprint,
        CancellationToken cancellationToken)
    {
        string normalizedManifestPath = Path.GetFullPath(manifestPath);
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var entries = new List<object>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            if (string.Equals(Path.GetFullPath(files[i]), normalizedManifestPath, StringComparison.OrdinalIgnoreCase))
                continue;
            string relativePath = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            entries.Add(new
            {
                path = relativePath,
                sha256 = await HashFileAsync(files[i], cancellationToken).ConfigureAwait(false),
            });
        }
        await WriteTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = OwnershipSchemaVersion,
                    bundleFingerprint,
                    files = entries,
                }, s_jsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string CreateProject(
        HarmonyOSPlayerProjectOptions options,
        string assemblyName,
        string runtimeIdentifier,
        IReadOnlyList<HarmonyOSVerifiedInputFile> managedAssemblies)
    {
        string packageVersion = options.BundleLock!.PackageVersion;
        var project = new StringBuilder($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <!-- NativeAOT names the .so after AssemblyName; the payload inspector and the native
                     host both expect libxengine_player.so, so emit that name regardless of game name. -->
                <AssemblyName>libxengine_player</AssemblyName>
                <RuntimeIdentifier>{Xml(runtimeIdentifier)}</RuntimeIdentifier>
                <PublishAot>true</PublishAot>
                <NativeLib>Shared</NativeLib>
                <SelfContained>true</SelfContained>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Nullable>enable</Nullable>
                <OpenHarmonyTarget>true</OpenHarmonyTarget>
                <!-- The generated player csproj lives under the XEngine repo and so auto-imports
                     Directory.Build.props, which adds the XEngine.Analyzers Roslyn project as an
                     analyzer reference. Analyzers are a build-time tool and have no runtime target
                     compatible with the linux-musl NativeAOT restore (NETSDK1005); disable them. -->
                <XEngineEnableAnalyzers>false</XEngineEnableAnalyzers>
                <!-- Server GC uses multi-heap t_join synchronization whose assert fails on OpenHarmony's
                     pthread/futex semantics. Workstation GC (single heap, no t_join) runs correctly.
                     Override the repo-wide ServerGarbageCollection=true from Directory.Build.props. -->
                <ServerGarbageCollection>false</ServerGarbageCollection>
                <!-- HarmonyOS ships no ICU (libicu). The default Unix globalization mode probes for
                     ICU on first culture/encoding/ordinal use and aborts (FailFast) when it is missing.
                     Invariant globalization removes the ICU dependency entirely: culture-aware APIs
                     fall back to invariant behavior and string comparisons use ordinal semantics.
                     This is mandatory for any P/Invoke or Console access to work on HarmonyOS. -->
                <InvariantGlobalization>true</InvariantGlobalization>
                <!-- Shrink the NativeAOT .so: OpenHarmony's mmap can fail to reserve the GC card table
                     when the managed closure is very large (33MB+ .so). Disable expensive features that
                     are not needed for a headless player to bring the .so closer to the ~11MB range the
                     Blazor.Hybrid sample achieves (which initializes GC successfully). -->
                <StackTraceSupport>false</StackTraceSupport>
                <IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>
                <IlcOptimizationPreference>Size</IlcOptimizationPreference>
                <!-- Remove unused code aggressively to reduce .so size and avoid problematic type
                     initializers (Silk.NET Vulkan extension types) triggering runtime aborts. -->
                <TrimMode>full</TrimMode>
                <IlcDisableReflection>true</IlcDisableReflection>
                <RestoreSources>$(MSBuildThisFileDirectory)..\\feed</RestoreSources>
                <XEngineStageProjectDirectory>$(MSBuildThisFileDirectory)..\\Stage</XEngineStageProjectDirectory>
                <XEnginePlayerLibraryName>{PlayerLibraryFileName}</XEnginePlayerLibraryName>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="OpenHarmony.NET.Player.Sdk" Version="{Xml(packageVersion)}" />
                <PackageReference Include="OpenHarmony.NET.HapTooling" Version="{Xml(packageVersion)}" PrivateAssets="all" />
                <PackageReference Include="OpenHarmony.NDK.Bindings" Version="{Xml(packageVersion)}" />
              </ItemGroup>
            """);
        project.AppendLine("  <ItemGroup>");
        for (int i = 0; i < managedAssemblies.Count; i++)
        {
            string name = Path.GetFileNameWithoutExtension(managedAssemblies[i].RelativePath);
            project.Append("    <Reference Include=\"").Append(Xml(name)).AppendLine("\">");
            project.Append("      <HintPath>Managed\\").Append(Xml(name)).AppendLine(".dll</HintPath>");
            project.AppendLine("      <Private>true</Private>");
            project.AppendLine("      <SpecificVersion>false</SpecificVersion>");
            project.AppendLine("    </Reference>");
        }
        project.AppendLine("  </ItemGroup>");
        project.AppendLine("  <ItemGroup>");
        for (int i = 0; i < managedAssemblies.Count; i++)
        {
            string name = Path.GetFileNameWithoutExtension(managedAssemblies[i].RelativePath);
            project.Append("    <TrimmerRootAssembly Include=\"").Append(Xml(name)).AppendLine("\" />");
        }
        project.AppendLine("  </ItemGroup>");
        project.AppendLine("</Project>");
        return project.ToString();
    }

    private static async Task VerifyLockedFileAsync(
        string path,
        HarmonyOSSdkBundleLockedFile lockedFile,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != lockedFile.Length ||
            !string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), lockedFile.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"OpenHarmony SDK Bundle locked file verification failed: {lockedFile.RelativePath}");
        }
    }

    private static HarmonyOSSdkBundleLockedFile GetLockedFile(
        IReadOnlyDictionary<string, HarmonyOSSdkBundleLockedFile> lockedFiles,
        string relativePath) =>
        lockedFiles.TryGetValue(relativePath, out HarmonyOSSdkBundleLockedFile? lockedFile)
            ? lockedFile
            : throw new InvalidDataException($"OpenHarmony SDK Bundle lock does not own required file: {relativePath}");

    private static string GetBundlePath(string bundleRoot, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new InvalidDataException($"OpenHarmony SDK Bundle path is invalid: {relativePath}");
        string rootPrefix = Path.GetFullPath(bundleRoot) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(bundleRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(rootPrefix, PathComparison))
            throw new InvalidDataException($"OpenHarmony SDK Bundle path escapes its root: {relativePath}");
        return path;
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new InvalidDataException($"HarmonyOS Stage input path is invalid: {relativePath}");
        string rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(rootPrefix, PathComparison))
            throw new InvalidDataException($"HarmonyOS Stage input path escapes its root: {relativePath}");
        return path;
    }

    private static async Task WriteTextAsync(string path, string value, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
        await File.WriteAllTextAsync(path, normalized, s_utf8NoBom, cancellationToken).ConfigureAwait(false);
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
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static bool IsApplicationId(string value)
    {
        string[] segments = value.Split('.', StringSplitOptions.None);
        if (segments.Length < 2)
            return false;
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0 || segment[0] < 'a' || segment[0] > 'z')
                return false;
            for (int j = 1; j < segment.Length; j++)
            {
                char c = segment[j];
                if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '_')
                    return false;
            }
        }
        return true;
    }

    private static bool IsSafeApplicationLabel(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsControl(c) || c == '"' || c == '\\' || c == '{' || c == '}')
                return false;
        }
        return true;
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == '/' || value.Contains('\\') || Path.IsPathFullyQualified(value))
            return false;
        string[] segments = value.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] == "." || segments[i] == "..")
                return false;
        }
        return true;
    }

    private static string GetAssemblyName(string projectName)
    {
        var result = new StringBuilder(projectName.Length + 11);
        if (!IsIdentifierStart(projectName[0]))
            result.Append('_');
        for (int i = 0; i < projectName.Length; i++)
            result.Append(IsIdentifierPart(projectName[i]) ? projectName[i] : '_');
        result.Append(".HarmonyOS");
        return result.ToString();
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
    private static bool ContainsOrdinal(IReadOnlyList<string> values, string candidate)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private sealed record ValidatedInputs(
        IReadOnlyList<HarmonyOSVerifiedTemplateFile> TemplateFiles,
        IReadOnlyList<HarmonyOSVerifiedPackage> Packages,
        IReadOnlyList<HarmonyOSVerifiedInputFile> AssetFiles,
        IReadOnlyList<HarmonyOSVerifiedInputFile> NativeHostFiles,
        IReadOnlyList<HarmonyOSVerifiedInputFile> NativeLibraryPayloadFiles,
        IReadOnlyList<HarmonyOSVerifiedInputFile> ManagedAssemblies);
}

internal sealed record HarmonyOSVerifiedTemplateFile(string RelativePath, string SourcePath);
internal sealed record HarmonyOSVerifiedPackage(string FileName, string SourcePath);
internal sealed record HarmonyOSVerifiedInputFile(string RelativePath, string SourcePath);
