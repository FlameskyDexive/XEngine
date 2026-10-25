// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidPackageBuildResult(
    string ArtifactPath,
    long ArtifactBytes,
    string ArtifactSha256,
    long DurationMilliseconds,
    IReadOnlyList<string> Verification,
    string BuildOutput);

internal sealed class AndroidPackageBuilder(IAndroidToolchainProcessRunner? processRunner = null)
{
    private readonly IAndroidToolchainProcessRunner _processRunner =
        processRunner ?? new AndroidToolchainProcessRunner();

    internal async Task<AndroidPackageBuildResult> BuildAsync(
        AndroidPlayerProjectLayout layout,
        AndroidToolchainSnapshot toolchain,
        string configuration,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(toolchain);
        if (layout.OutputMode == AndroidOutputMode.ExportProject)
            throw new ArgumentException("ExportProject does not produce a package.", nameof(layout));
        if (layout.RuntimeIdentifiers.Count != 1)
            throw new NotSupportedException("The G0 package builder supports one ABI per package. Multi-ABI AAB lands in M1.");
        VerifyManagedClosure(layout);
        VerifyNativeAudioLibraries(layout);

        IReadOnlyDictionary<string, string> environment = CreateEnvironment(toolchain);
        string runtimeIdentifier = layout.RuntimeIdentifiers[0];
        string packageFormat = layout.OutputMode == AndroidOutputMode.Aab ? "aab" : "apk";
        string[] unstrippedArguments = CreateBuildArguments(
            layout,
            toolchain,
            configuration,
            runtimeIdentifier,
            packageFormat,
            stripNativeLibraries: false);
        AndroidToolchainProcessResult build = await RunPackageBuildAsync(
            toolchain.DotNetExecutable,
            unstrippedArguments,
            layout.RootDirectory,
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        long buildDuration = build.DurationMilliseconds;
        string buildOutput = build.CombinedOutput;
        if (build.ExitCode != 0)
            throw new InvalidOperationException("Android package build failed:" + Environment.NewLine + Tail(buildOutput));

        string extension = "." + packageFormat;
        string outputRoot = Path.Combine(layout.RootDirectory, "bin", configuration, "net10.0-android", runtimeIdentifier);
        string sourceArtifact = FindPackageArtifact(outputRoot, extension);
        AndroidNativeSymbolArchiveSnapshot? symbolArchive = null;
        AndroidR8MappingSnapshot? r8Mapping = null;
        if (IsReleaseConfiguration(configuration))
        {
            symbolArchive = AndroidNativeSymbolArchive.Create(layout, sourceArtifact, configuration);
            // Android workload native-strip targets are incremental. Clear only the generated
            // package outputs after archiving the unstripped artifact so the second build cannot
            // reuse native libraries produced with AndroidStripNativeLibraries=false.
            CleanPackageBuildOutputs(layout.RootDirectory);
            string[] strippedArguments = CreateBuildArguments(
                layout,
                toolchain,
                configuration,
                runtimeIdentifier,
                packageFormat,
                stripNativeLibraries: true);
            AndroidToolchainProcessResult strippedBuild = await RunPackageBuildAsync(
                toolchain.DotNetExecutable,
                strippedArguments,
                layout.RootDirectory,
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);
            buildDuration += strippedBuild.DurationMilliseconds;
            buildOutput += Environment.NewLine + "[XEngine] Release native stripping build." +
                Environment.NewLine + strippedBuild.CombinedOutput;
            if (strippedBuild.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Android native stripping package build failed:" + Environment.NewLine + Tail(buildOutput));
            }
            sourceArtifact = FindPackageArtifact(outputRoot, extension);
            symbolArchive = AndroidNativeSymbolArchive.ReconcileWithStrippedArtifact(
                symbolArchive,
                layout,
                configuration,
                sourceArtifact);
            r8Mapping = AndroidR8MappingArchive.Create(layout, configuration);
        }
        VerifyPackagedManagedAssets(layout, configuration, runtimeIdentifier);
        string artifactsDirectory = Path.Combine(layout.RootDirectory, "artifacts");
        Directory.CreateDirectory(artifactsDirectory);
        string artifactPath = Path.Combine(artifactsDirectory, layout.AssemblyName + extension);
        File.Copy(sourceArtifact, artifactPath, overwrite: true);
        string sizeReportPath = Path.Combine(layout.RootDirectory, "verification", AndroidPackageSizeReport.FileName);
        AndroidPackageSizeSnapshot sizeSnapshot = AndroidPackageSizeReport.MeasureAndWrite(
            artifactPath,
            layout.OutputMode,
            layout.PackageSizeBudget,
            sizeReportPath);
        IReadOnlyList<string> verification = await VerifyAsync(
            artifactPath,
            layout,
            toolchain,
            environment,
            configuration,
            timeout,
            cancellationToken).ConfigureAwait(false);
        sizeSnapshot.Enforce(sizeReportPath);
        var withSizeReport = new List<string>(verification) { "package-size-report" };
        if (layout.PackageSizeBudget.IsEnabled)
            withSizeReport.Add("package-size-budget");
        verification = withSizeReport;
        if (symbolArchive != null)
        {
            AndroidNativeSymbolArchive.VerifyAndWrite(symbolArchive, layout, artifactPath);
            var withSymbols = new List<string>(verification) { "native-strip-symbol-archive" };
            verification = withSymbols;
        }
        if (r8Mapping != null)
        {
            AndroidR8MappingArchive.VerifyAndWrite(r8Mapping, layout, artifactPath);
            var withR8Mapping = new List<string>(verification) { "r8-mapping-archive" };
            verification = withR8Mapping;
        }
        var info = new FileInfo(artifactPath);
        return new AndroidPackageBuildResult(
            artifactPath,
            info.Length,
            await HashFileAsync(artifactPath, cancellationToken).ConfigureAwait(false),
            buildDuration,
            verification,
            Tail(buildOutput));
    }

    private async Task<AndroidToolchainProcessResult> RunPackageBuildAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AndroidToolchainProcessResult result = await _processRunner.RunAsync(
            executable,
            arguments,
            workingDirectory,
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && IsRetryableArchiveLockFailure(result.CombinedOutput))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            AndroidToolchainProcessResult retry = await _processRunner.RunAsync(
                executable,
                arguments,
                workingDirectory,
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);
            return new AndroidToolchainProcessResult(
                retry.ExitCode,
                result.DurationMilliseconds + retry.DurationMilliseconds,
                result.CombinedOutput + Environment.NewLine + "[XEngine] Retried XABAA7000 archive lock." +
                    Environment.NewLine + retry.CombinedOutput,
                string.Empty);
        }
        return result;
    }

    private static string[] CreateBuildArguments(
        AndroidPlayerProjectLayout layout,
        AndroidToolchainSnapshot toolchain,
        string configuration,
        string runtimeIdentifier,
        string packageFormat,
        bool stripNativeLibraries) =>
    [
        "build",
        layout.ProjectPath,
        "-c", configuration,
        "-r", runtimeIdentifier,
        "-p:JavaSdkDirectory=" + toolchain.JavaSdkRoot,
        "-p:AndroidSdkDirectory=" + toolchain.AndroidSdkRoot,
        "-p:AndroidNdkDirectory=" + toolchain.AndroidNdkRoot,
        "-p:AndroidUseLatestPlatformSdk=false",
        "-p:AndroidPackageFormat=" + packageFormat,
        "-p:AndroidStripNativeLibraries=" + (stripNativeLibraries ? "true" : "false"),
        "-p:AndroidLinkTool=" + (IsReleaseConfiguration(configuration) ? "r8" : string.Empty),
        "-p:AndroidCreateProguardMappingFile=" + (IsReleaseConfiguration(configuration) ? "true" : "false"),
        "-p:AndroidR8IgnoreWarnings=false",
        "-p:RunAOTCompilation=false",
        "-p:EmbedAssembliesIntoApk=true",
        "--nologo",
        "-nr:false",
        "-v:minimal",
    ];

    private static string FindPackageArtifact(string outputRoot, string extension)
    {
        string[] candidates = Directory.Exists(outputRoot)
            ? Directory.GetFiles(outputRoot, "*" + extension, SearchOption.AllDirectories)
            : [];
        if (candidates.Length == 0)
            throw new InvalidDataException($"Android build succeeded but produced no {extension} artifact.");
        Array.Sort(candidates, CompareArtifacts);
        return candidates[^1];
    }

    private static bool IsReleaseConfiguration(string configuration) =>
        string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase);

    internal static void CleanPackageBuildOutputs(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root = Path.GetFullPath(projectRoot);
        string fileName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        if (fileName.Length == 0)
            throw new InvalidDataException($"Android package project root is invalid: {projectRoot}");

        string[] generatedDirectories =
        [
            Path.Combine(root, "bin"),
            Path.Combine(root, "obj"),
        ];
        for (int index = 0; index < generatedDirectories.Length; index++)
        {
            string directory = generatedDirectories[index];
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    internal static bool IsRetryableArchiveLockFailure(string output) =>
        output.Contains("XABAA7000", StringComparison.OrdinalIgnoreCase) &&
        output.Contains("Renaming temporary file failed", StringComparison.OrdinalIgnoreCase) &&
        output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);

    internal static void VerifyArchiveEntries(
        IEnumerable<string> entries,
        AndroidOutputMode outputMode,
        IReadOnlyList<string> targetAbis)
    {
        ArgumentNullException.ThrowIfNull(entries);
        IReadOnlyList<string> normalizedAbis = AndroidAbiMapper.Normalize(targetAbis);
        string manifest = outputMode == AndroidOutputMode.Aab
            ? "base/manifest/AndroidManifest.xml"
            : "AndroidManifest.xml";
        var names = new List<string>();
        bool hasManifest = false;
        foreach (string entry in entries)
        {
            string normalized = entry.Replace('\\', '/');
            names.Add(normalized);
            if (string.Equals(normalized, manifest, StringComparison.Ordinal))
                hasManifest = true;
        }
        if (!hasManifest)
            throw new InvalidDataException($"Android package is missing {manifest}.");

        string nativePrefix = outputMode == AndroidOutputMode.Aab ? "base/lib/" : "lib/";
        var packagedAbis = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            string fileName = Path.GetFileName(name);
            if (IsRuntimeCompilerFile(fileName))
                throw new InvalidDataException($"Android package contains forbidden runtime compiler file: {name}");
            if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                AndroidManagedDependencyCollector.IsForbidden(Path.GetFileNameWithoutExtension(fileName)))
            {
                throw new InvalidDataException($"Android package contains forbidden managed dependency: {name}");
            }
            if (!name.StartsWith(nativePrefix, StringComparison.Ordinal) || !name.EndsWith(".so", StringComparison.Ordinal))
                continue;
            string remainder = name[nativePrefix.Length..];
            int separator = remainder.IndexOf('/');
            if (separator > 0)
                packagedAbis.Add(remainder[..separator]);
        }

        if (packagedAbis.Count == 0)
            throw new InvalidDataException("Android package contains no native runtime libraries.");
        if (!packagedAbis.SetEquals(normalizedAbis))
        {
            string[] packaged = [.. packagedAbis];
            string[] targeted = [.. normalizedAbis];
            Array.Sort(packaged, StringComparer.Ordinal);
            Array.Sort(targeted, StringComparer.Ordinal);
            throw new InvalidDataException(
                $"Android package ABI set '{string.Join(',', packaged)}' does not match target " +
                $"'{string.Join(',', targeted)}'.");
        }
    }

    private async Task<IReadOnlyList<string>> VerifyAsync(
        string artifactPath,
        AndroidPlayerProjectLayout layout,
        AndroidToolchainSnapshot toolchain,
        IReadOnlyDictionary<string, string> environment,
        string configuration,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using (ZipArchive archive = ZipFile.OpenRead(artifactPath))
        {
            var entryNames = new string[archive.Entries.Count];
            for (int i = 0; i < archive.Entries.Count; i++)
                entryNames[i] = archive.Entries[i].FullName;
            VerifyArchiveEntries(entryNames, layout.OutputMode, layout.TargetAbis);
            VerifyNativeElfEntries(archive, layout, toolchain);
            VerifyManagedAssemblyStore(archive, layout, configuration);
            if (layout.IncludesShaderPackage)
                VerifyShaderEntries(archive, layout.OutputMode);
            if (layout.IncludesAssetPayload)
                VerifyAssetPayload(archive, layout);
        }

        var checks = new List<string>
        {
            "archive-layout",
            "abi-closure",
            "managed-assembly-store",
            "managed-direct-references",
            "desktop-dependency-exclusion",
            "runtime-compiler-exclusion",
            "native-elf-machine-16kb",
            "native-elf-dynamic-closure",
            "native-elf-identity-inventory",
            "native-elf-min-api-symbol-audit",
        };
        if (layout.IncludesShaderPackage)
            checks.Add("vulkan-shader-package");
        if (layout.IncludesAssetPayload)
            checks.Add("asset-payload-sha256");
        if (layout.NativeAudioAbis.Count > 0)
            checks.Add("miniaudio-elf-16kb");
        if (layout.OutputMode == AndroidOutputMode.Apk)
        {
            await RunVerificationAsync(
                "aapt2",
                FindComponent(toolchain, "aapt2"),
                ["dump", "badging", artifactPath],
                layout.RootDirectory,
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);
            checks.Add("manifest-aapt2");

            await RunVerificationAsync(
                "apksigner",
                FindComponent(toolchain, "apksigner"),
                ["verify", "--verbose", "--print-certs", artifactPath],
                layout.RootDirectory,
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);
            checks.Add("apk-signature");

            await RunVerificationAsync(
                "zipalign-16kb",
                FindComponent(toolchain, "zipalign"),
                ["-c", "-P", "16", "4", artifactPath],
                layout.RootDirectory,
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);
            checks.Add("zipalign-16kb");
        }
        else
        {
            string java = Path.Combine(toolchain.JavaSdkRoot, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            await RunVerificationAsync(
                "bundletool",
                java,
                ["-jar", toolchain.BundletoolPath, "validate", "--bundle=" + artifactPath],
                layout.RootDirectory,
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);
            checks.Add("bundletool-validate");
            if (layout.IncludesAssetPayload)
            {
                await VerifyBundleDeviceAssetPayloadAsync(
                    artifactPath,
                    layout,
                    toolchain,
                    environment,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                checks.Add("aab-device-asset-payload");
            }
        }
        return checks;
    }

    private async Task VerifyBundleDeviceAssetPayloadAsync(
        string artifactPath,
        AndroidPlayerProjectLayout layout,
        AndroidToolchainSnapshot toolchain,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string verificationRoot = Path.Combine(layout.RootDirectory, "verification", "aab-device-payload");
        Directory.CreateDirectory(verificationRoot);
        string apksPath = Path.Combine(verificationRoot, "device.apks");
        string universalApkPath = Path.Combine(verificationRoot, "universal.apk");
        string java = Path.Combine(toolchain.JavaSdkRoot, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        try
        {
            await RunVerificationAsync(
                "bundletool-device-apks",
                java,
                [
                    "-jar", toolchain.BundletoolPath,
                    "build-apks",
                    "--bundle=" + artifactPath,
                    "--output=" + apksPath,
                    "--mode=universal",
                    "--overwrite",
                ],
                layout.RootDirectory,
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);

            using (ZipArchive apks = ZipFile.OpenRead(apksPath))
            {
                ZipArchiveEntry? universal = apks.GetEntry("universal.apk");
                if (universal == null)
                    throw new InvalidDataException("bundletool universal APK output is missing universal.apk.");
                using Stream input = universal.Open();
                using FileStream output = new(universalApkPath, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }

            using ZipArchive devicePackage = ZipFile.OpenRead(universalApkPath);
            VerifyAssetPayload(devicePackage, layout, AndroidOutputMode.Apk);
            if (layout.IncludesShaderPackage)
                VerifyShaderEntries(devicePackage, AndroidOutputMode.Apk);
        }
        finally
        {
            try
            {
                if (Directory.Exists(verificationRoot))
                    Directory.Delete(verificationRoot, recursive: true);
            }
            catch
            {
                // A failed cleanup must not hide the package verification result.
            }
        }
    }

    private async Task RunVerificationAsync(
        string id,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AndroidToolchainProcessResult result = await _processRunner.RunAsync(
            executable, arguments, workingDirectory, environment, timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidDataException($"Android package verification '{id}' failed: {Tail(result.CombinedOutput)}");
    }

    private static IReadOnlyDictionary<string, string> CreateEnvironment(AndroidToolchainSnapshot toolchain)
    {
        string dotnetRoot = Path.GetDirectoryName(toolchain.DotNetExecutable)!;
        var pathEntries = new List<string>(4)
        {
            dotnetRoot,
            Path.Combine(toolchain.JavaSdkRoot, "bin"),
            Path.Combine(toolchain.AndroidSdkRoot, "platform-tools"),
        };
        string inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(inheritedPath))
            pathEntries.Add(inheritedPath);
        string path = string.Join(Path.PathSeparator, pathEntries);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = dotnetRoot,
            ["JAVA_HOME"] = toolchain.JavaSdkRoot,
            ["ANDROID_HOME"] = toolchain.AndroidSdkRoot,
            ["ANDROID_SDK_ROOT"] = toolchain.AndroidSdkRoot,
            ["ANDROID_NDK_ROOT"] = toolchain.AndroidNdkRoot,
            ["PATH"] = path,
        };
    }

    private static string FindComponent(AndroidToolchainSnapshot toolchain, string id)
    {
        for (int i = 0; i < toolchain.Components.Count; i++)
        {
            if (string.Equals(toolchain.Components[i].Id, id, StringComparison.Ordinal))
                return toolchain.Components[i].ResolvedPath;
        }
        throw new InvalidDataException($"Android toolchain lock is missing component: {id}");
    }

    private static bool IsRuntimeCompilerFile(string fileName) =>
        fileName.StartsWith("XEngine.Slang.", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("slang-", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("libslang", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("slang.dll", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("dxcompiler.", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("dxil.", StringComparison.OrdinalIgnoreCase);

    private static void VerifyManagedClosure(AndroidPlayerProjectLayout layout)
    {
        bool hasRuntime = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < layout.ManagedAssemblyNames.Count; i++)
        {
            string name = layout.ManagedAssemblyNames[i];
            if (!seen.Add(name))
                throw new InvalidDataException($"Android managed closure contains duplicate assembly '{name}'.");
            if (AndroidManagedDependencyCollector.IsForbidden(name))
                throw new InvalidDataException($"Android managed closure contains forbidden assembly '{name}'.");
            if (string.Equals(name, "XEngine.Runtime", StringComparison.OrdinalIgnoreCase))
                hasRuntime = true;
            string path = Path.Combine(layout.RootDirectory, "Managed", name + ".dll");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Android managed closure is missing '{name}'.", path);
        }
        if (!hasRuntime)
            throw new InvalidDataException("Android managed closure is missing XEngine.Runtime.");
    }

    private static void VerifyNativeAudioLibraries(AndroidPlayerProjectLayout layout)
    {
        for (int i = 0; i < layout.NativeAudioAbis.Count; i++)
        {
            string abi = layout.NativeAudioAbis[i];
            bool isTargetAbi = false;
            for (int targetIndex = 0; targetIndex < layout.TargetAbis.Count; targetIndex++)
            {
                if (string.Equals(layout.TargetAbis[targetIndex], abi, StringComparison.Ordinal))
                {
                    isTargetAbi = true;
                    break;
                }
            }
            if (!isTargetAbi)
                throw new InvalidDataException($"Android native audio ABI '{abi}' is not a target ABI.");
            string path = Path.Combine(
                layout.RootDirectory,
                "NativeLibraries",
                abi,
                "libminiaudioex.so");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Android miniaudio library is missing for {abi}.", path);
            AndroidElfInspector.RequireLoadAlignment(path, 16 * 1024);
            using FileStream stream = File.OpenRead(path);
            AndroidElfInspector.RequireIdentity(
                AndroidElfInspector.ReadDynamicMetadata(stream, path),
                path);
        }
    }

    private static void VerifyNativeElfEntries(
        ZipArchive archive,
        AndroidPlayerProjectLayout layout,
        AndroidToolchainSnapshot toolchain)
    {
        string prefix = layout.OutputMode == AndroidOutputMode.Aab ? "base/lib/" : "lib/";
        IReadOnlyDictionary<string, int> systemLibraries = ReadNdkSystemLibraries(toolchain.AndroidNdkRoot);
        var librariesByAbi = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var entries = new List<NativeElfPackageEntry>();
        var inventory = new List<AndroidNativeElfInventoryEntry>();
        int verified = 0;
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            ZipArchiveEntry entry = archive.Entries[i];
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                continue;

            string remainder = name[prefix.Length..];
            int separator = remainder.IndexOf('/');
            if (separator <= 0 || remainder.IndexOf('/', separator + 1) >= 0)
                throw new InvalidDataException($"Android native library has an invalid ABI path: {name}");
            string abi = remainder[..separator];
            string fileName = remainder[(separator + 1)..];
            if (!IsNativeLibraryFileName(fileName))
                throw new InvalidDataException($"Android native library has an invalid file name: {name}");
            bool targeted = false;
            for (int targetIndex = 0; targetIndex < layout.TargetAbis.Count; targetIndex++)
            {
                if (string.Equals(layout.TargetAbis[targetIndex], abi, StringComparison.Ordinal))
                {
                    targeted = true;
                    break;
                }
            }
            if (!targeted)
                throw new InvalidDataException($"Android native library targets an unexpected ABI: {name}");

            using Stream input = entry.Open();
            using var buffer = new MemoryStream(checked((int)entry.Length));
            input.CopyTo(buffer);
            buffer.Position = 0;
            AndroidElfInspector.RequireMachine(buffer, name, abi);
            AndroidElfInspector.RequireLoadAlignment(buffer, name, 16 * 1024);
            buffer.Position = 0;
            AndroidElfDynamicMetadata dynamicMetadata = AndroidElfInspector.ReadDynamicMetadata(buffer, name);
            if (dynamicMetadata.HasTextRelocations)
                throw new InvalidDataException($"Android native library has text relocations: {name}");
            AndroidElfInspector.RequireIdentity(dynamicMetadata, name);
            buffer.Position = 0;
            AndroidElfDynamicSymbols dynamicSymbols = AndroidElfInspector.ReadDynamicSymbols(buffer, name);
            if (!librariesByAbi.TryGetValue(abi, out HashSet<string>? libraryNames))
            {
                libraryNames = new HashSet<string>(StringComparer.Ordinal);
                librariesByAbi.Add(abi, libraryNames);
            }
            if (!libraryNames.Add(fileName))
                throw new InvalidDataException($"Android package contains duplicate native library '{fileName}' for {abi}.");
            entries.Add(new NativeElfPackageEntry(name, abi, dynamicMetadata, dynamicSymbols));
            inventory.Add(new AndroidNativeElfInventoryEntry(
                name,
                abi,
                dynamicMetadata.Soname!,
                dynamicMetadata.GnuBuildId!,
                dynamicMetadata.NeededLibraries));
            verified++;
        }

        if (verified == 0)
            throw new InvalidDataException("Android package contains no verifiable native ELF libraries.");
        VerifyNativeDependencyClosure(entries, librariesByAbi, systemLibraries, layout.MinSdkVersion);
        AndroidNativeElfInventory.Write(
            Path.Combine(layout.RootDirectory, "verification", AndroidNativeElfInventory.FileName),
            inventory);
        VerifyNativeMinimumApiSymbols(
            entries,
            systemLibraries,
            toolchain.AndroidNdkRoot,
            layout.MinSdkVersion,
            layout.RootDirectory);
    }

    private static void VerifyNativeMinimumApiSymbols(
        IReadOnlyList<NativeElfPackageEntry> entries,
        IReadOnlyDictionary<string, int> systemLibraries,
        string ndkRoot,
        int minSdkVersion,
        string outputRoot)
    {
        var packageExportsByAbi = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var systemExportsByAbi = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var systemNamesByAbi = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var auditEntries = new List<AndroidNativeSymbolAuditEntry>();
        for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            NativeElfPackageEntry entry = entries[entryIndex];
            if (!packageExportsByAbi.TryGetValue(entry.Abi, out HashSet<string>? packageExports))
            {
                packageExports = new HashSet<string>(StringComparer.Ordinal);
                packageExportsByAbi.Add(entry.Abi, packageExports);
            }
            for (int symbolIndex = 0; symbolIndex < entry.DynamicSymbols.DefinedSymbols.Count; symbolIndex++)
                packageExports.Add(entry.DynamicSymbols.DefinedSymbols[symbolIndex]);

            if (!systemNamesByAbi.TryGetValue(entry.Abi, out List<string>? systemNames))
            {
                systemNames = [];
                systemNamesByAbi.Add(entry.Abi, systemNames);
            }
            for (int dependencyIndex = 0; dependencyIndex < entry.DynamicMetadata.NeededLibraries.Count; dependencyIndex++)
            {
                string dependency = entry.DynamicMetadata.NeededLibraries[dependencyIndex];
                if (systemLibraries.ContainsKey(dependency) && !systemNames.Contains(dependency))
                    systemNames.Add(dependency);
            }
        }

        foreach ((string abi, List<string> systemNames) in systemNamesByAbi)
        {
            systemNames.Sort(StringComparer.Ordinal);
            HashSet<string> systemExports = new(StringComparer.Ordinal);
            systemExportsByAbi.Add(abi, systemExports);
            string target = abi switch
            {
                "arm64-v8a" => "aarch64-linux-android",
                "armeabi-v7a" => "arm-linux-androideabi",
                "x86_64" => "x86_64-linux-android",
                _ => throw new InvalidDataException($"Android native symbol audit has an unsupported ABI: {abi}"),
            };
            string apiDirectory = Path.Combine(
                ndkRoot,
                "toolchains",
                "llvm",
                "prebuilt",
                "windows-x86_64",
                "sysroot",
                "usr",
                "lib",
                target,
                minSdkVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int libraryIndex = 0; libraryIndex < systemNames.Count; libraryIndex++)
            {
                string library = systemNames[libraryIndex];
                if (!systemLibraries.TryGetValue(library, out int libraryMinApi))
                    continue;
                if (libraryMinApi > minSdkVersion)
                    throw new InvalidDataException(
                        $"Android native symbol audit found a system library above min SDK: {library}");
                string path = Path.Combine(apiDirectory, library);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"Android NDK API {minSdkVersion} symbol stub is missing for {abi}/{library}.",
                        path);
                }
                using FileStream stream = File.OpenRead(path);
                AndroidElfDynamicSymbols symbols = AndroidElfInspector.ReadDynamicSymbols(stream, path);
                for (int symbolIndex = 0; symbolIndex < symbols.DefinedSymbols.Count; symbolIndex++)
                    systemExports.Add(symbols.DefinedSymbols[symbolIndex]);
            }

            for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                NativeElfPackageEntry entry = entries[entryIndex];
                if (!string.Equals(entry.Abi, abi, StringComparison.Ordinal))
                    continue;
                int packageResolved = 0;
                int systemResolved = 0;
                int weakUnresolved = 0;
                for (int symbolIndex = 0; symbolIndex < entry.DynamicSymbols.UndefinedSymbols.Count; symbolIndex++)
                {
                    string symbol = entry.DynamicSymbols.UndefinedSymbols[symbolIndex];
                    if (packageExportsByAbi[abi].Contains(symbol))
                        packageResolved++;
                    else if (systemExports.Contains(symbol))
                        systemResolved++;
                    else if (ContainsOrdinal(entry.DynamicSymbols.WeakUndefinedSymbols, symbol))
                        weakUnresolved++;
                    else
                    {
                        throw new InvalidDataException(
                            $"Android native library imports symbol '{symbol}' unavailable at API {minSdkVersion}: {entry.Name}");
                    }
                }
                auditEntries.Add(new AndroidNativeSymbolAuditEntry(
                    entry.Name,
                    abi,
                    entry.DynamicSymbols.UndefinedSymbols.Count,
                    packageResolved,
                    systemResolved,
                    weakUnresolved,
                    systemNames.AsReadOnly()));
            }

        }

        AndroidNativeSymbolAudit.Write(
            Path.Combine(outputRoot, "verification", AndroidNativeSymbolAudit.FileName),
            minSdkVersion,
            auditEntries);
    }

    private static IReadOnlyDictionary<string, int> ReadNdkSystemLibraries(string ndkRoot)
    {
        string path = Path.Combine(ndkRoot, "meta", "system_libs.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Android NDK system library metadata is missing.", path);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Android NDK system library metadata is invalid: {path}");

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!IsNativeLibraryFileName(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String ||
                !int.TryParse(property.Value.GetString(), out int minimumApi) ||
                minimumApi <= 0)
            {
                throw new InvalidDataException($"Android NDK system library metadata is invalid: {path}");
            }
            result.Add(property.Name, minimumApi);
        }
        if (result.Count == 0)
            throw new InvalidDataException($"Android NDK system library metadata is empty: {path}");
        return result;
    }

    private static void VerifyNativeDependencyClosure(
        IReadOnlyList<NativeElfPackageEntry> entries,
        IReadOnlyDictionary<string, HashSet<string>> librariesByAbi,
        IReadOnlyDictionary<string, int> systemLibraries,
        int minSdkVersion)
    {
        for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            NativeElfPackageEntry entry = entries[entryIndex];
            HashSet<string> packaged = librariesByAbi[entry.Abi];
            for (int dependencyIndex = 0; dependencyIndex < entry.DynamicMetadata.NeededLibraries.Count; dependencyIndex++)
            {
                string dependency = entry.DynamicMetadata.NeededLibraries[dependencyIndex];
                if (!IsNativeLibraryFileName(dependency))
                {
                    throw new InvalidDataException(
                        $"Android native library has an invalid DT_NEEDED entry '{dependency}': {entry.Name}");
                }
                if (packaged.Contains(dependency))
                    continue;
                if (!systemLibraries.TryGetValue(dependency, out int minimumApi))
                {
                    throw new InvalidDataException(
                        $"Android native library has an unresolved DT_NEEDED entry '{dependency}': {entry.Name}");
                }
                if (minimumApi > minSdkVersion)
                {
                    throw new InvalidDataException(
                        $"Android native library DT_NEEDED entry '{dependency}' requires API {minimumApi}, " +
                        $"but min SDK is {minSdkVersion}: {entry.Name}");
                }
            }
        }
    }

    private static bool IsNativeLibraryFileName(string value) =>
        !string.IsNullOrEmpty(value) &&
        value.EndsWith(".so", StringComparison.Ordinal) &&
        value.IndexOf('/') < 0 &&
        value.IndexOf('\\') < 0;

    private static bool ContainsOrdinal(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private sealed record NativeElfPackageEntry(
        string Name,
        string Abi,
        AndroidElfDynamicMetadata DynamicMetadata,
        AndroidElfDynamicSymbols DynamicSymbols);

    internal static void VerifyPackagedManagedAssets(
        AndroidPlayerProjectLayout layout,
        string configuration,
        string runtimeIdentifier)
    {
        string abi = AndroidAbiMapper.ToAbi(runtimeIdentifier);
        string coreClrStorePath = Path.Combine(
            layout.RootDirectory,
            "obj",
            configuration,
            "net10.0-android",
            runtimeIdentifier,
            "app_shared_libraries",
            abi,
            "libxamarin-app.so");
        string assetsDirectory = Path.Combine(
            layout.RootDirectory,
            "obj",
            configuration,
            "net10.0-android",
            runtimeIdentifier,
            "android",
            "assets",
            abi);
        if (!Directory.Exists(assetsDirectory))
        {
            // .NET Android CoreCLR packages the managed closure in libxamarin-app.so rather
            // than emitting the Mono assembly assets directory. VerifyManagedAssemblyStore
            // subsequently checks this exact store is the one archived in the APK/AAB.
            if (File.Exists(coreClrStorePath))
                return;
            throw new DirectoryNotFoundException(
                $"Android package build produced no managed assembly store input for {abi}: {assetsDirectory}");
        }

        var packaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] files = Directory.GetFiles(assetsDirectory, "*.dll", SearchOption.TopDirectoryOnly);
        for (int i = 0; i < files.Length; i++)
        {
            string name = Path.GetFileNameWithoutExtension(files[i]);
            if (AndroidManagedDependencyCollector.IsForbidden(name))
                throw new InvalidDataException($"Android assembly store contains forbidden dependency '{name}'.");
            packaged.Add(name);
        }
        for (int i = 0; i < layout.ManagedAssemblyNames.Count; i++)
        {
            string expected = layout.ManagedAssemblyNames[i];
            if (!packaged.Contains(expected))
            {
                if (File.Exists(coreClrStorePath))
                    return;
                throw new InvalidDataException($"Android assembly store is missing required dependency '{expected}'.");
            }
        }
    }

    internal static void VerifyManagedAssemblyStore(
        ZipArchive archive,
        AndroidPlayerProjectLayout layout,
        string? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(layout);

        string abi = AndroidAbiMapper.ToAbi(layout.RuntimeIdentifiers[0]);
        string prefix = layout.OutputMode == AndroidOutputMode.Aab ? "base/lib/" : "lib/";
        var rawAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? assemblyStore = null;
        ZipArchiveEntry? coreClrAssemblyStore = null;
        bool hasCoreClrRuntime = false;
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            ZipArchiveEntry entry = archive.Entries[i];
            string name = entry.FullName.Replace('\\', '/');
            if (TryGetPackagedManagedAssemblyName(name, prefix, out string assemblyName))
            {
                if (AndroidManagedDependencyCollector.IsForbidden(assemblyName))
                {
                    throw new InvalidDataException(
                        $"Android package assembly store contains forbidden dependency '{assemblyName}'.");
                }
                rawAssemblyNames.Add(assemblyName);
            }

            if (assemblyStore == null &&
                name.StartsWith(prefix, StringComparison.Ordinal) &&
                name.EndsWith("/libassemblies." + abi + ".blob.so",
                    StringComparison.Ordinal))
            {
                assemblyStore = entry;
            }

            if (coreClrAssemblyStore == null &&
                string.Equals(name, prefix + abi + "/libxamarin-app.so", StringComparison.Ordinal))
            {
                coreClrAssemblyStore = entry;
            }

            if (string.Equals(name, prefix + abi + "/libcoreclr.so", StringComparison.Ordinal))
                hasCoreClrRuntime = true;
        }

        byte[]? blob = null;
        if (assemblyStore != null)
        {
            using var stream = new MemoryStream(checked((int)assemblyStore.Length));
            using Stream input = assemblyStore.Open();
            input.CopyTo(stream);
            blob = stream.ToArray();
        }

        if (!hasCoreClrRuntime)
            coreClrAssemblyStore = null;
        if (coreClrAssemblyStore != null && !string.IsNullOrWhiteSpace(configuration))
            VerifyCoreClrAssemblyStore(coreClrAssemblyStore, layout, configuration, abi);

        for (int i = 0; i < layout.ManagedAssemblyNames.Count; i++)
        {
            string assemblyName = layout.ManagedAssemblyNames[i];
            if (rawAssemblyNames.Contains(assemblyName))
                continue;
            if (blob != null && ContainsAssemblyName(blob, assemblyName + ".dll"))
                continue;
            if (coreClrAssemblyStore != null)
                continue;
            throw new InvalidDataException(
                $"Android package assembly store is missing required dependency '{assemblyName}'.");
        }

        if (blob == null)
            return;

        string[] forbiddenMarkers =
        [
            "XEngine.Editor.dll",
            "XEngine.Slang.dll",
            "XEngine.Slang.Compiler.dll",
            "ImageMagick.dll",
            "Magick.NET.dll",
            "Silk.NET.GLFW.dll",
            "Silk.NET.OpenAL.dll",
            "Silk.NET.OpenGL.dll",
            "Vortice.Direct3D12.dll",
        ];
        for (int i = 0; i < forbiddenMarkers.Length; i++)
        {
            if (ContainsAssemblyName(blob, forbiddenMarkers[i]))
            {
                throw new InvalidDataException(
                    $"Android package assembly store contains forbidden dependency '{forbiddenMarkers[i]}'.");
            }
        }
    }

    private static void VerifyCoreClrAssemblyStore(
        ZipArchiveEntry archiveEntry,
        AndroidPlayerProjectLayout layout,
        string configuration,
        string abi)
    {
        string expectedPath = Path.Combine(
            layout.RootDirectory,
            "obj",
            configuration,
            "net10.0-android",
            layout.RuntimeIdentifiers[0],
            "app_shared_libraries",
            abi,
            "libxamarin-app.so");
        if (!File.Exists(expectedPath))
        {
            throw new FileNotFoundException(
                "Android CoreCLR assembly store was not produced by the generated project.",
                expectedPath);
        }

        using var expected = new FileStream(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] expectedHash = SHA256.HashData(expected);
        using Stream packaged = archiveEntry.Open();
        byte[] packagedHash = SHA256.HashData(packaged);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, packagedHash))
        {
            throw new InvalidDataException(
                "Android CoreCLR assembly store in the package does not match the generated assembly store.");
        }
    }

    private static bool TryGetPackagedManagedAssemblyName(
        string archivePath,
        string nativeLibraryPrefix,
        out string assemblyName)
    {
        assemblyName = string.Empty;
        if (!archivePath.StartsWith(nativeLibraryPrefix, StringComparison.Ordinal))
            return false;

        string fileName = Path.GetFileName(archivePath);
        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            assemblyName = Path.GetFileNameWithoutExtension(fileName);
            return assemblyName.Length > 0;
        }

        const string wrappedPrefix = "lib_";
        const string wrappedSuffix = ".dll.so";
        if (!fileName.StartsWith(wrappedPrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(wrappedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        assemblyName = fileName[wrappedPrefix.Length..^3];
        assemblyName = Path.GetFileNameWithoutExtension(assemblyName);
        return assemblyName.Length > 0;
    }

    private static bool ContainsAssemblyName(ReadOnlySpan<byte> blob, string assemblyName)
    {
        byte[] marker = Encoding.UTF8.GetBytes(assemblyName);
        int offset = 0;
        while (offset <= blob.Length - marker.Length)
        {
            int relative = blob[offset..].IndexOf(marker);
            if (relative < 0)
                return false;
            int start = offset + relative;
            int end = start + marker.Length;
            bool beforeIsName = start > 0 && IsAssemblyNameCharacter(blob[start - 1]);
            bool afterIsName = end < blob.Length && IsAssemblyNameCharacter(blob[end]);
            if (!beforeIsName && !afterIsName)
                return true;
            offset = start + 1;
        }
        return false;
    }

    private static bool IsAssemblyNameCharacter(byte value) =>
        (value >= (byte)'A' && value <= (byte)'Z') ||
        (value >= (byte)'a' && value <= (byte)'z') ||
        (value >= (byte)'0' && value <= (byte)'9') ||
        value is (byte)'.' or (byte)'_' or (byte)'-';

    internal static void VerifyAssetPayload(
        ZipArchive archive,
        AndroidPlayerProjectLayout layout,
        AndroidOutputMode? packageMode = null)
    {
        string manifestPath = layout.AssetManifestPath ??
            throw new InvalidDataException("Android layout is missing the asset descriptor path.");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Android asset descriptor is missing.", manifestPath);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported Android asset descriptor schema.");
        JsonElement entries = document.RootElement.GetProperty("assets");
        string strippingReportPath = document.RootElement.GetProperty("assetStrippingReport").GetString() ?? string.Empty;
        if (!IsSafeAssetPath(strippingReportPath))
            throw new InvalidDataException("Android asset descriptor has an invalid stripping report path.");
        AndroidOutputMode archiveMode = packageMode ?? layout.OutputMode;
        string prefix = archiveMode == AndroidOutputMode.Aab
            ? "base/assets/xengine/"
            : "assets/xengine/";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement descriptor in entries.EnumerateArray())
        {
            string path = descriptor.GetProperty("path").GetString() ?? string.Empty;
            if (!IsSafeAssetPath(path) || !seen.Add(path))
            {
                throw new InvalidDataException($"Android asset descriptor contains invalid or duplicate path: {path}");
            }

            ZipArchiveEntry? packaged = archive.GetEntry(prefix + path);
            if (packaged == null)
                throw new InvalidDataException($"Android package is missing indexed asset: {path}");
            long expectedBytes = descriptor.GetProperty("bytes").GetInt64();
            if (packaged.Length != expectedBytes)
            {
                throw new InvalidDataException(
                    $"Android asset byte count mismatch for {path}: expected {expectedBytes}, got {packaged.Length}.");
            }

            string expectedHash = descriptor.GetProperty("sha256").GetString() ?? string.Empty;
            using Stream input = packaged.Open();
            string actualHash = Convert.ToHexString(SHA256.HashData(input));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Android asset SHA-256 mismatch: {path}");
            }

            bool requiresSeek = descriptor.GetProperty("requiresSeek").GetBoolean();
            if (archiveMode == AndroidOutputMode.Apk &&
                requiresSeek &&
                packaged.CompressedLength != packaged.Length)
            {
                throw new InvalidDataException(
                    $"Android seekable asset was compressed in the package: {path}");
            }
        }

        ZipArchiveEntry? descriptorEntry = archive.GetEntry(prefix + "android-assets.json");
        if (descriptorEntry == null)
            throw new InvalidDataException("Android package is missing android-assets.json.");

        VerifyAssetStrippingReport(archive, prefix, strippingReportPath, layout.AssetPackagingMode);
    }

    private static void VerifyAssetStrippingReport(
        ZipArchive archive,
        string prefix,
        string reportPath,
        XEngine.Runtime.AssetPackagingMode packagingMode)
    {
        ZipArchiveEntry? reportEntry = archive.GetEntry(prefix + reportPath);
        if (reportEntry == null)
            throw new InvalidDataException("Android package is missing the asset stripping report.");
        using Stream input = reportEntry.Open();
        using JsonDocument report = JsonDocument.Parse(input);
        if (report.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported Android asset stripping report schema.");
        string reportMode = report.RootElement.GetProperty("packagingMode").GetString() ?? string.Empty;
        if (!string.Equals(reportMode, packagingMode.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Android asset stripping report mode '{reportMode}' does not match '{packagingMode}'.");
        }
        if ((packagingMode is XEngine.Runtime.AssetPackagingMode.LooseFiles or
                XEngine.Runtime.AssetPackagingMode.XEnginePak or
                XEngine.Runtime.AssetPackagingMode.AssetBundle) &&
            !report.RootElement.GetProperty("physicalPayloadVerified").GetBoolean())
        {
            throw new InvalidDataException(
                "Android asset stripping report did not verify the physical payload closure.");
        }
    }

    private static bool IsSafeAssetPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path[0] == '/')
            return false;
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] == "." || segments[i] == "..")
                return false;
        }
        return true;
    }

    private static void VerifyShaderEntries(ZipArchive archive, AndroidOutputMode outputMode)
    {
        string packageName = outputMode == AndroidOutputMode.Aab
            ? "base/res/raw/android_vulkan.xshaderpack"
            : "res/raw/android_vulkan.xshaderpack";
        ZipArchiveEntry? packageEntry = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (string.Equals(name, packageName, StringComparison.Ordinal))
            {
                packageEntry = entry;
                break;
            }
        }
        if (packageEntry == null)
            throw new InvalidDataException("Android package is missing android_vulkan.xshaderpack.");

        using var buffer = new MemoryStream(checked((int)packageEntry.Length));
        using (Stream input = packageEntry.Open())
            input.CopyTo(buffer);
        buffer.Position = 0;
        using var shaderPackage = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: false);
        bool hasCatalog = false;
        bool hasArtifact = false;
        foreach (ZipArchiveEntry entry in shaderPackage.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (string.Equals(name, "catalog.xshadercatalog", StringComparison.Ordinal))
                hasCatalog = true;
            else if (name.EndsWith(".xshader", StringComparison.Ordinal) && !name.Contains('/'))
                hasArtifact = true;
        }

        if (!hasCatalog || !hasArtifact)
        {
            throw new InvalidDataException(
                "Android package is missing the Vulkan shader catalog or artifact payload.");
        }
    }

    private static int CompareArtifacts(string left, string right)
    {
        bool leftSigned = Path.GetFileName(left).Contains("Signed", StringComparison.OrdinalIgnoreCase);
        bool rightSigned = Path.GetFileName(right).Contains("Signed", StringComparison.OrdinalIgnoreCase);
        int signed = leftSigned.CompareTo(rightSigned);
        return signed != 0 ? signed : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string Tail(string value) => value.Length <= 4096 ? value : value[^4096..];
}
