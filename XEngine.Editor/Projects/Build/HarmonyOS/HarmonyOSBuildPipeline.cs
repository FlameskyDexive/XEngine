// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Runtime.RHI;

namespace XEngine.Editor.Build;

/// <summary>
/// Builds the relocatable HarmonyOS Player/Stage project and, when requested, invokes the
/// provider-owned HAP/APP targets from the imported offline Bundle.
/// </summary>
public sealed class HarmonyOSBuildPipeline : BuildPipeline
{
    private readonly IHarmonyOSToolchainResolver _toolchainResolver;
    private readonly IHarmonyOSDeviceBridge _deviceBridge;

    public HarmonyOSBuildPipeline()
        : this(new HarmonyOSToolchainResolver(), new HarmonyOSDeviceBridge())
    {
    }

    internal HarmonyOSBuildPipeline(
        IHarmonyOSToolchainResolver toolchainResolver,
        IHarmonyOSDeviceBridge deviceBridge)
    {
        _toolchainResolver = toolchainResolver ?? throw new ArgumentNullException(nameof(toolchainResolver));
        _deviceBridge = deviceBridge ?? throw new ArgumentNullException(nameof(deviceBridge));
    }

    public override string DisplayName => "HarmonyOS";
    public override string Id => "xengine.harmonyos";
    public override string Icon => EditorIcons.Mobile;
    public override string[] SupportedRuntimeIdentifiers => ["openharmony-arm64", "openharmony-x64"];

    public override BuildResult Build(BuildSettings settings, BuildProgress? progress) =>
        BuildAsync(Project.Current?.RootPath ?? string.Empty, settings, outputDirectory: null, progress)
            .GetAwaiter()
            .GetResult();

    public override async Task<BuildResult> BuildAsync(
        string projectPath,
        BuildSettings settings,
        string? outputDirectory = null,
        BuildProgress? progress = null,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? stagingDirectory = null;
        try
        {
            Project? project = Project.Current;
            if (project == null)
                return Failure("No project open.", stopwatch.Elapsed);

            HarmonyOSBuildProfile profile = settings.GetProfile<HarmonyOSBuildProfile>(GetType());
            profile.ValidateForBuild();
            if (profile.TargetAbis.Count != 1)
            {
                return Failure(
                    "HarmonyOS currently packages one ABI per build. Select exactly one target ABI; multi-ABI bundle assembly is a separate provider gate.",
                    stopwatch.Elapsed);
            }

            ResolvedBuildOptions options = ResolvedBuildOptions.From(settings, outputDirectory);
            string rawOutput = options.OutputDirectory;
            string rootForRelativeOutput = string.IsNullOrWhiteSpace(projectPath) ? project.RootPath : projectPath;
            string resolvedOutput = Path.IsPathRooted(rawOutput)
                ? Path.GetFullPath(rawOutput)
                : Path.GetFullPath(Path.Combine(rootForRelativeOutput, rawOutput));
            if (!IsUsableOutputDirectory(rawOutput, resolvedOutput, project.RootPath))
            {
                return Failure(
                    $"HarmonyOS build output '{rawOutput}' must be a new or empty directory outside the project root, Assets, Library, and Temp roots.",
                    stopwatch.Elapsed);
            }

            string lockPath = string.IsNullOrWhiteSpace(EditorSettings.Instance.HarmonyOSLastResolvedLockPath)
                ? Path.Combine(project.RootPath, "ThirdParty", "OpenHarmonySdk", HarmonyOSSdkBundleLockFile.DefaultFileName)
                : EditorSettings.Instance.HarmonyOSLastResolvedLockPath;
            progress?.Log("[HarmonyOS] Validating locked Bundle and official SDK...", 0.04f);
            HarmonyOSSdkBundleLock bundleLock = await HarmonyOSSdkBundleLockFile.ReadAsync(lockPath, cancellation).ConfigureAwait(false);
            IReadOnlyList<HarmonyOSBundleCapability> requiredCapabilities = profile.OutputMode switch
            {
                HarmonyOSOutputMode.ExportProject => [HarmonyOSBundleCapability.PlayerPublish, HarmonyOSBundleCapability.StageProject],
                HarmonyOSOutputMode.Hap => [HarmonyOSBundleCapability.PlayerPublish, HarmonyOSBundleCapability.StageProject, HarmonyOSBundleCapability.HapBuild, HarmonyOSBundleCapability.HapVerify],
                HarmonyOSOutputMode.App => [HarmonyOSBundleCapability.PlayerPublish, HarmonyOSBundleCapability.StageProject, HarmonyOSBundleCapability.AppBuild],
                _ => throw new ArgumentOutOfRangeException(),
            };
            HarmonyOSToolchainSnapshot toolchain = await _toolchainResolver
                .ResolveAsync(new HarmonyOSToolchainResolveOptions
                {
                    ProjectRoot = project.RootPath,
                    LockFilePath = lockPath,
                    UserPreferenceSdkRoot = EditorSettings.Instance.HarmonyOSOfficialSdkRoot,
                    EnvironmentSdkRoot = Environment.GetEnvironmentVariable("OHOS_SDK_HOME"),
                    TargetApi = profile.TargetApi,
                    TargetAbi = profile.TargetAbis[0],
                    RequiredCapabilities = requiredCapabilities,
                }, cancellation)
                .ConfigureAwait(false);
            string bundleRoot = Path.GetFullPath(Path.Combine(project.RootPath, bundleLock.BundleRelativePath.Replace('/', Path.DirectorySeparatorChar)));

            string buildScratchRoot = Path.Combine(Path.GetTempPath(), "XEngineHarmonyOS", Guid.NewGuid().ToString("N"));
            progress?.Log("[HarmonyOS] Building conditioned Runtime payload...", 0.10f);
            string runtimePayloadDirectory = Path.Combine(
                buildScratchRoot,
                "HarmonyOSRuntime-" + HarmonyOSAbiMapper.ToRuntimeIdentifier(profile.TargetAbis[0]) + "-" + Guid.NewGuid().ToString("N"));
            await new HarmonyOSRuntimePayloadBuilder().BuildAsync(project.RootPath, runtimePayloadDirectory, cancellation).ConfigureAwait(false);
            HarmonyOSManagedDependencyCollection managedDependencies = HarmonyOSManagedDependencyCollector.Collect(runtimePayloadDirectory, project);

            var sceneGuids = new List<Guid>();
            for (int i = 0; i < options.Scenes.Count; i++)
            {
                if (options.Scenes[i].Enabled && options.Scenes[i].SceneGuid != Guid.Empty)
                    sceneGuids.Add(options.Scenes[i].SceneGuid);
            }
            AssetCollector.CollectionResult assets = CollectAssets(settings, progress,
                assetBundleOnly: options.PackagingMode == AssetPackagingMode.AssetBundle);
            HarmonyOSManagedTypeRootCollection managedTypeRoots = HarmonyOSManagedTypeRootCollector.Collect(
                assets,
                managedDependencies);
            if (options.PackagingMode == AssetPackagingMode.Embedded)
                return Failure("HarmonyOS packaging does not support Embedded assets; choose LooseFiles, XEnginePak, or AssetBundle.", stopwatch.Elapsed);
            if (!ShaderImporter.CompileBuiltInOfflineArtifacts(project))
                return Failure("Failed to compile built-in offline shader artifacts.", stopwatch.Elapsed);
            // Pack the FULL declared offline variant set (like the desktop pipeline). The
            // usage-based VulkanShaderVariantCollector missed variants used only through
            // runtime-resolved materials (VFX prefabs instantiate materials by AssetRef at
            // runtime), which made particle/skill effects draw with missing artifacts on
            // device — every draw threw and was skipped, hiding all VFX and flooding the
            // frame loop with exceptions. The full set costs a few MB extra in the HAP.
            if (!ShaderImporter.CompileArtifactSelections(
                    project,
                    VulkanShaderVariantCollector.CreateArtifactSelections(
                        VulkanShaderVariantCollector.CollectForBuild(assets.AllAssets))))
            {
                return Failure("Failed to compile required HarmonyOS runtime shader variants.", stopwatch.Elapsed);
            }
            string shaderPackageDirectory = Path.Combine(buildScratchRoot, "Shaders");
            ShaderArtifactBuildPackage shaderPackage = ShaderArtifactBuildPackager.Prepare(
                project,
                assets.AllAssets,
                shaderPackageDirectory,
                targetBackend: GraphicsBackend.Vulkan,
                selections: null);
            (string assetPayloadDirectory, int assetCount) = await PrepareAssetPayloadAsync(
                project,
                settings,
                options,
                assets,
                sceneGuids.Count == 0 ? Guid.Empty : sceneGuids[0],
                shaderPackage.DirectoryPath,
                buildScratchRoot,
                progress,
                cancellation);

            string nativeHostSourceDirectory = Path.Combine(buildScratchRoot, "NativeHost");
            await HarmonyOSNativeHostSource.MaterializeAsync(nativeHostSourceDirectory, cancellation).ConfigureAwait(false);
            GeneralSettings? general = EditorRegistries.GetSettings<GeneralSettings>();
            string productName = string.IsNullOrWhiteSpace(general?.ProductName) ? "XEngine" : general.ProductName;
            string applicationLabel = productName;
            string parent = Path.GetDirectoryName(resolvedOutput) ?? throw new InvalidOperationException("HarmonyOS output directory has no parent.");
            Directory.CreateDirectory(parent);
            stagingDirectory = Path.Combine(parent, "." + Path.GetFileName(resolvedOutput) + ".xengine-harmonyos-" + Guid.NewGuid().ToString("N") + ".staging");

            progress?.Log("[HarmonyOS] Generating relocatable Player and Stage project...", 0.22f);
            string? nativeLibraryPayloadDirectory = ResolveNativeLibraryPayloadDirectory(profile.TargetAbis[0]);
            if (nativeLibraryPayloadDirectory != null)
                progress?.Log($"[HarmonyOS] Staging native library payload (FFmpeg) from {nativeLibraryPayloadDirectory}.", 0.24f);
            HarmonyOSPlayerProjectLayout layout = await HarmonyOSPlayerProjectGenerator.GenerateAsync(
                stagingDirectory,
                new HarmonyOSPlayerProjectOptions
                {
                    ShowFpsOverlay = options.Config == BuildConfiguration.Debug,
                    ProjectName = productName,
                    ApplicationId = profile.ApplicationId,
                    ApplicationLabel = applicationLabel,
                    ApplicationIconPath = general?.ResolveApplicationIconPath(project),
                    VersionCode = profile.VersionCode,
                    ApiLevel = profile.TargetApi,
                    Abi = profile.TargetAbis[0],
                    BundleRoot = bundleRoot,
                    BundleLock = bundleLock,
                    BundleFingerprint = toolchain.BundleFingerprint,
                    AssetPayloadDirectory = assetPayloadDirectory,
                    NativeHostSourceDirectory = nativeHostSourceDirectory,
                    NativeLibraryPayloadDirectory = nativeLibraryPayloadDirectory,
                    PackagingMode = options.PackagingMode,
                    DefaultSceneGuid = sceneGuids.Count == 0 ? Guid.Empty : sceneGuids[0],
                    ManagedAssemblies = managedDependencies.Assemblies,
                    ManagedTypeRoots = managedTypeRoots,
                },
                cancellation).ConfigureAwait(false);

            string playerPublishOutput = Path.Combine(buildScratchRoot, "Player");
            string playerIntermediate = Path.Combine(buildScratchRoot, "PlayerIntermediate");
            progress?.Log("[HarmonyOS] Publishing NativeAOT player from the locked feed...", 0.40f);
            HarmonyOSPlayerPayloadResult playerPayload = await new HarmonyOSPlayerPayloadBuilder().PublishAsync(
                new HarmonyOSPlayerPublishRequest
                {
                    PlayerProjectPath = layout.ProjectPath,
                    BundleRoot = bundleRoot,
                    BundleLock = bundleLock,
                    BundleFingerprint = toolchain.BundleFingerprint,
                    RuntimeIdentifier = layout.RuntimeIdentifier,
                    OutputDirectory = playerPublishOutput,
                    IntermediateDirectory = playerIntermediate,
                    OpenHarmonySdkRoot = EditorSettings.Instance.HarmonyOSOfficialSdkRoot ?? string.Empty,
                    OpenHarmonyApiLevel = profile.TargetApi,
                    OpenHarmonyAbi = profile.TargetAbis[0],
                },
                cancellation).ConfigureAwait(false);
            string stagedPlayerLibrary = Path.Combine(layout.StageProjectDirectory, "entry", "src", "main", "cpp", "xengine", "libxengine_player.so");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPlayerLibrary)!);
            File.Copy(playerPayload.PlayerLibraryPath, stagedPlayerLibrary, overwrite: false);
            await HarmonyOSPlayerProjectGenerator.RefreshOwnershipManifestAsync(layout, toolchain.BundleFingerprint, cancellation).ConfigureAwait(false);

            // Optional packages (ads, push, payment, …) extend the materialized Stage project with
            // ArkTS glue, permissions, and local HAR dependencies before any output mode consumes
            // it; a failed contribution fails the build like any other packaging error.
            progress?.Log("[HarmonyOS] Applying package Stage-project contributions...", 0.42f);
            RunStageProjectContributions(layout, profile);

            if (profile.OutputMode == HarmonyOSOutputMode.ExportProject)
            {
                await WriteExportReceiptAsync(layout, toolchain, profile, settings, playerPayload.PlayerBuildId, cancellation).ConfigureAwait(false);
                PublishStaging(stagingDirectory, resolvedOutput);
                stagingDirectory = null;
                stopwatch.Stop();
                progress?.Log("[HarmonyOS] Export project complete.", Runtime.LogSeverity.Success);
                return new BuildResult
                {
                    Success = true,
                    OutputPath = resolvedOutput,
                    Log = $"Generated HarmonyOS {layout.Abi} Player/Stage project; {shaderPackage.ArtifactCount} Vulkan shader artifacts.",
                    Duration = stopwatch.Elapsed,
                    AssetCount = assetCount,
                };
            }

            HarmonyOSPackagingContract contract = await HarmonyOSPackagingContract.ReadAsync(layout.StageProjectDirectory, cancellation).ConfigureAwait(false);
            contract.RequireNativeHostDependencies();
            HarmonyOSPackagingAction action = contract.GetAction(profile.OutputMode);
            IReadOnlyDictionary<string, string> expectedNativePayload = await CollectNativePayloadHashesAsync(
                nativeLibraryPayloadDirectory,
                cancellation).ConfigureAwait(false);
            string packageProject = layout.ProjectPath;
            string playerIntermediateRoot = Path.Combine(playerIntermediate, "obj") + Path.DirectorySeparatorChar;
            string playerBuildOutputRoot = Path.Combine(playerIntermediate, "bin") + Path.DirectorySeparatorChar;
            // Inject debug signing material into the materialized Stage build-profile.json5 so hvigor's
            // SignHap step can produce a signed HAP. The signing directory + encrypted passwords come
            // from environment variables (XENGINE_HARMONYOS_SIGNING_DIR, _STORE_PASSWORD, _KEY_PASSWORD),
            // never from project settings, so secrets stay out of the repo. This is the debug auto-signing
            // material DevEco generates under %USERPROFILE%\.ohos\config. Release signing is a separate
            // SigningReference-driven feature.
            await InjectStageSigningAsync(layout.StageProjectDirectory, cancellation).ConfigureAwait(false);
            await EnsureStageInternetPermissionAsync(layout.StageProjectDirectory, cancellation).ConfigureAwait(false);
            string[] packageArguments =
            [
                "msbuild",
                packageProject,
                "-t:" + action.BuildTarget,
                "-p:Configuration=" + options.Config,
                "-p:BaseIntermediateOutputPath=" + playerIntermediateRoot,
                "-p:BaseOutputPath=" + playerBuildOutputRoot,
                "-p:XEnginePlayerLibraryPath=" + stagedPlayerLibrary,
                // The package targets must not trigger a NuGet restore (the feed is already materialized
                // in the Bundle); 'dotnet msbuild --no-restore' is rejected by MSBuild as MSB1001, so use
                // the MSBuild property instead.
                "-p:Restore=false",
            ];
            var packageBuilder = new HarmonyOSPackageBuilder();
            HarmonyOSPendingPackage pending = await packageBuilder.BuildAsync(
                new HarmonyOSPackageBuildRequest
                {
                    StageProjectDirectory = layout.StageProjectDirectory,
                    ArtifactRelativePath = action.ArtifactRelativePath,
                    PublishedOutputDirectory = resolvedOutput,
                    OutputMode = profile.OutputMode,
                    HvigorExecutablePath = "dotnet",
                    HvigorArguments = packageArguments,
                    Redactions = [project.RootPath, bundleRoot, EditorSettings.Instance.HarmonyOSOfficialSdkRoot ?? string.Empty],
                },
                cancellation).ConfigureAwait(false);
            HarmonyOSPackageVerification verification = await new HarmonyOSPackageVerifier(new HarmonyOSProcessRunner()).VerifyAsync(
                new HarmonyOSPackageVerificationRequest
                {
                    ArtifactPath = pending.ArtifactPath,
                    OutputMode = profile.OutputMode,
                    ApplicationId = profile.ApplicationId,
                    VersionCode = profile.VersionCode,
                    Abi = profile.TargetAbis[0],
                    BundleFingerprint = toolchain.BundleFingerprint,
                    OfficialSdkHash = toolchain.SdkRootSha256,
                    XEngineCommit = ResolveXEngineRevision().Commit,
                    SigningReference = profile.SigningReference,
                    Provenance = await CreateBuildProvenanceAsync(
                        toolchain,
                        profile,
                        settings,
                        layout,
                        includeContentHashes: true,
                        parentReceiptSha256: null,
                        observedAtUtc: null,
                        cancellation).ConfigureAwait(false),
                    AllowedElfDependencies = contract.AllowedElfDependencies,
                    ForbiddenPlaintext = [project.RootPath, bundleRoot, EditorSettings.Instance.HarmonyOSOfficialSdkRoot ?? string.Empty],
                    ExpectedNativePayloadFiles = expectedNativePayload,
                    ProviderVerifierCommand = new HarmonyOSProcessCommand
                    {
                        ExecutablePath = "dotnet",
                        Arguments =
                        [
                            "msbuild",
                            packageProject,
                            "-t:" + action.VerifyTarget,
                            "-p:BaseIntermediateOutputPath=" + playerIntermediateRoot,
                            "-p:BaseOutputPath=" + playerBuildOutputRoot,
                            "-p:XEnginePlayerLibraryPath=" + stagedPlayerLibrary,
                            "-p:Restore=false",
                        ],
                        WorkingDirectory = layout.StageProjectDirectory,
                        Redactions = [project.RootPath, bundleRoot],
                    },
                },
                cancellation).ConfigureAwait(false);
            if (!verification.Success)
                throw new HarmonyOSBuildException(HarmonyOSBuildErrorCode.PackageVerificationFailed, string.Join(Environment.NewLine, verification.Errors));
            string artifactPath = await packageBuilder.PublishVerifiedAsync(pending, verification, cancellation).ConfigureAwait(false);
            stopwatch.Stop();
            progress?.Log("[HarmonyOS] Package verified and published.", Runtime.LogSeverity.Success);
            return new BuildResult
            {
                Success = true,
                OutputPath = artifactPath,
                Log = $"Published {Path.GetFileName(artifactPath)} with {shaderPackage.ArtifactCount} Vulkan shader artifacts.",
                Duration = stopwatch.Elapsed,
                AssetCount = assetCount,
            };
        }
        catch (OperationCanceledException)
        {
            return Failure("HarmonyOS build cancelled.", stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            progress?.Log("[HarmonyOS] " + exception.Message, Runtime.LogSeverity.Error);
            return Failure(exception.Message, stopwatch.Elapsed);
        }
        finally
        {
            if (stagingDirectory != null)
                TryDeleteStaging(stagingDirectory);
        }
    }

    public override async Task<BuildLaunchResult> LaunchAsync(
        BuildLaunchRequest request,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Settings);
        ArgumentNullException.ThrowIfNull(request.Build);
        if (!request.Build.Success)
            return BuildLaunchResult.Failed("HarmonyOS Build & Run requires a successful package build.");

        HarmonyOSBuildProfile profile = request.Settings.GetProfile<HarmonyOSBuildProfile>(GetType());
        if (profile.OutputMode != HarmonyOSOutputMode.Hap)
            return BuildLaunchResult.Failed("HarmonyOS Build & Run requires HAP output mode.");

        string artifactPath = GetExecutablePath(request.Build.OutputPath, request.Settings);
        if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
            return BuildLaunchResult.Failed("HarmonyOS Build & Run could not find the verified HAP artifact.");

        string? sdkRoot = EditorSettings.Instance.HarmonyOSOfficialSdkRoot;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            profile.ValidateForBuild();
            if (profile.TargetAbis.Count != 1)
                return BuildLaunchResult.Failed("HarmonyOS Build & Run requires exactly one target ABI.");

            string projectRoot = Path.GetFullPath(request.ProjectPath);
            if (!Directory.Exists(projectRoot))
                return BuildLaunchResult.Failed("HarmonyOS Build & Run requires an existing project root.");
            string lockPath = string.IsNullOrWhiteSpace(EditorSettings.Instance.HarmonyOSLastResolvedLockPath)
                ? Path.Combine(projectRoot, "ThirdParty", "OpenHarmonySdk", HarmonyOSSdkBundleLockFile.DefaultFileName)
                : EditorSettings.Instance.HarmonyOSLastResolvedLockPath;

            request.Progress?.Log("[HarmonyOS] Resolving HDC and selecting a compatible target...", 0.92f);
            HarmonyOSHdcToolchainResolution toolchain = await _toolchainResolver.ResolveHdcAsync(
                new HarmonyOSToolchainResolveOptions
                {
                    ProjectRoot = projectRoot,
                    LockFilePath = lockPath,
                    UserPreferenceSdkRoot = sdkRoot,
                    EnvironmentSdkRoot = Environment.GetEnvironmentVariable("OHOS_SDK_HOME"),
                    TargetApi = profile.TargetApi,
                    TargetAbi = profile.TargetAbis[0],
                    RequiredCapabilities = [HarmonyOSBundleCapability.HdcDeploy],
                },
                cancellation).ConfigureAwait(false);

            string screenshotPath = Path.Combine(
                Path.GetDirectoryName(artifactPath)!,
                Path.GetFileNameWithoutExtension(artifactPath) + ".run.png");
            HarmonyOSDeviceRunResult run = await _deviceBridge.InstallAndLaunchAsync(
                new HarmonyOSDeviceRunRequest
                {
                    HdcExecutablePath = toolchain.HdcExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(artifactPath)!,
                    HapPath = artifactPath,
                    ApplicationId = profile.ApplicationId,
                    AbilityName = profile.AbilityName,
                    TargetAbi = profile.TargetAbis[0],
                    TargetApi = profile.TargetApi,
                    RequestedDeviceId = string.IsNullOrWhiteSpace(profile.RunDeviceId) ? null : profile.RunDeviceId.Trim(),
                    InstallPolicy = profile.InstallMode == HarmonyOSBuildRunInstallMode.LaunchOnly
                        ? HarmonyOSInstallPolicy.LaunchOnly
                        : HarmonyOSInstallPolicy.InstallOrReplace,
                    OwnsTestInstallation = profile.CleanupNewInstallationOnFailure,
                    ScreenshotOutputPath = profile.CaptureRunScreenshot ? screenshotPath : null,
                    Redactions = [projectRoot, sdkRoot ?? string.Empty],
                },
                cancellation).ConfigureAwait(false);
            if (!run.Ready)
                return BuildLaunchResult.Failed("HarmonyOS Build & Run completed without a readiness marker.", run.RedactedLogs);

            await WriteRunReceiptAsync(
                artifactPath,
                toolchain.Snapshot,
                profile,
                request.Settings,
                run,
                cancellation).ConfigureAwait(false);
            request.Progress?.Log("[HarmonyOS] Readiness marker observed and run receipt written.", 1.0f);
            return BuildLaunchResult.Succeeded(
                "HarmonyOS HAP launched and reported readiness.",
                run.RedactedLogs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string diagnostics = RedactLaunchDiagnostic(
                exception.Message,
                artifactPath,
                request.ProjectPath,
                sdkRoot,
                profile.RunDeviceId);
            return BuildLaunchResult.Failed("HarmonyOS Build & Run failed.", diagnostics);
        }
    }

    public override string GetExecutablePath(string outputPath, BuildSettings settings)
    {
        HarmonyOSBuildProfile profile = settings.GetProfile<HarmonyOSBuildProfile>(GetType());
        if (profile.OutputMode == HarmonyOSOutputMode.ExportProject)
            return Path.Combine(outputPath, ".xengine-export-project-is-not-runnable");

        string extension = profile.OutputMode == HarmonyOSOutputMode.Hap ? ".hap" : ".app";
        if (File.Exists(outputPath) && string.Equals(Path.GetExtension(outputPath), extension, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(outputPath);
        if (!Directory.Exists(outputPath))
            return string.Empty;
        string[] artifacts = Directory.GetFiles(outputPath, "*" + extension, SearchOption.TopDirectoryOnly);
        return artifacts.Length == 1 ? artifacts[0] : string.Empty;
    }

    /// <summary>
    /// Resolves the engine-vendored prebuilt native library payload for the target ABI
    /// (<c>Libraries/openharmony-{arch}/native</c>, e.g. the FFmpeg shared libraries the video
    /// module dlopens). The <c>XENGINE_HARMONYOS_NATIVE_PAYLOAD_DIR</c> environment variable
    /// overrides the engine-root walk for editors deployed away from the repository; it must
    /// point at the ABI-specific native directory. Returns null when no payload exists for
    /// that ABI, in which case the HAP carries no extra native libraries and video decoding
    /// is unavailable at runtime with a clear loader error.
    /// </summary>
    private static string? ResolveNativeLibraryPayloadDirectory(string abi)
    {
        string? envOverride = Environment.GetEnvironmentVariable("XENGINE_HARMONYOS_NATIVE_PAYLOAD_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
            return Directory.Exists(envOverride) ? Path.GetFullPath(envOverride) : null;
        string? sourceRoot = FindXEngineSourceRoot();
        if (sourceRoot == null)
            return null;
        string arch = abi switch
        {
            "arm64-v8a" => "arm64",
            "x86_64" => "x64",
            _ => string.Empty,
        };
        if (arch.Length == 0)
            return null;
        string payloadDirectory = Path.Combine(sourceRoot, "Libraries", "openharmony-" + arch, "native");
        return Directory.Exists(payloadDirectory) ? payloadDirectory : null;
    }

    /// <summary>
    /// Hashes every ELF shared library in the native payload directory so the package
    /// verifier can prove the exact staged bytes reached <c>libs/&lt;abi&gt;/</c> inside the HAP.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> CollectNativePayloadHashesAsync(
        string? payloadDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadDirectory))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string root = Path.GetFullPath(payloadDirectory);
        foreach (string file in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(file);
            cancellationToken.ThrowIfCancellationRequested();
            if (!name.StartsWith("lib", StringComparison.Ordinal) || !name.Contains(".so", StringComparison.Ordinal))
                continue;
            result[name] = await ComputeSha256Async(file, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// Copies video source media for selected VideoClip assets into
    /// <c>Content/payload/{guid}{ext}</c>. Sources are addressed by GUID at runtime
    /// (never by absolute path); media bytes stay outside the cache assets and paks.
    /// Mirrors <see cref="DesktopBuildPipeline"/>'s distribution layout so
    /// <c>VideoClip.TryGetPayloadPath</c> resolves identically on device.
    /// </summary>
    private static int CopyVideoPayloads(HashSet<Guid> assets, string contentDir, BuildProgress? progress)
    {
        var db = EditorAssetBackend.Instance;
        if (db == null)
            return 0;

        string payloadDir = Path.Combine(contentDir, "payload");
        Directory.CreateDirectory(payloadDir);

        string[] videoExtensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv"];
        string assetsRoot = Project.Current!.AssetsPath;
        int count = 0;
        foreach (Guid guid in assets)
        {
            AssetEntry? entry = db.GetEntry(guid);
            if (entry == null)
                continue;

            string extension = Path.GetExtension(entry.Path);
            bool isVideo = false;
            foreach (string candidate in videoExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    isVideo = true;
                    break;
                }
            }

            if (!isVideo)
                continue;

            string sourcePath = Path.Combine(assetsRoot, entry.Path);
            if (!File.Exists(sourcePath))
            {
                progress?.Log($"[Video] Source payload missing for '{entry.Path}', skipping.");
                continue;
            }

            File.Copy(sourcePath, Path.Combine(payloadDir, guid.ToString("N") + extension), true);
            count++;
        }

        if (count > 0)
            progress?.Log($"[HarmonyOS] Copied {count} video payload(s) to Content/payload.");
        return count;
    }

    private async Task<(string RootDirectory, int AssetCount)> PrepareAssetPayloadAsync(
        Project project,
        BuildSettings settings,
        ResolvedBuildOptions options,
        AssetCollector.CollectionResult assets,
        Guid defaultSceneGuid,
        string shaderPackageDirectory,
        string buildScratchRoot,
        BuildProgress? progress,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(buildScratchRoot, "Assets");
        string content = Path.Combine(root, "Content");
        Directory.CreateDirectory(content);
        IReadOnlyDictionary<Guid, byte[]> textureOverrides = BuildTexturePayloadDowngrader.CreateOverrides(
            project,
            assets.AllAssets,
            BuildTexturePayloadDowngrader.ResolveSettings(settings, BuildTarget.HarmonyOS));
        int assetCount = options.PackagingMode switch
        {
            AssetPackagingMode.LooseFiles => CopyLooseAssets(assets.AllAssets, content, progress, textureOverrides),
            AssetPackagingMode.XEnginePak => PackAssets(assets.AllAssets, content, options.MaxPakSizeMB, progress, textureOverrides),
            AssetPackagingMode.AssetBundle => PackAssetBundles(BuildTarget.HarmonyOS, content, progress, textureOverrides),
            _ => throw new InvalidOperationException("Unsupported HarmonyOS asset packaging mode."),
        };
        // Video source media ship as streaming payloads (Content/payload/{guid}{ext}), never
        // inside cache assets or paks — mirrors the desktop pipeline's N6 distribution layout.
        CopyVideoPayloads(assets.AllAssets, content, progress);
        GenerateManifest(Path.Combine(content, "asset_manifest.bin"), assets.AllAssets, assets.ResourcesMap, defaultSceneGuid);
        ExportSettings(Path.Combine(root, "Settings"), progress);
        await HarmonyOSAssetPayloadFinalizer.FinalizeAsync(
                root,
                options.PackagingMode,
                assets.AllAssets,
                shaderPackageDirectory,
                cancellationToken,
                project.RootPath)
            .ConfigureAwait(false);
        return (root, assetCount);
    }

    private static async Task WriteExportReceiptAsync(
        HarmonyOSPlayerProjectLayout layout,
        HarmonyOSToolchainSnapshot toolchain,
        HarmonyOSBuildProfile profile,
        BuildSettings settings,
        string playerBuildId,
        CancellationToken cancellationToken)
    {
        string manifestPath = layout.OwnershipManifestPath;
        string hash = await ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false);
        HarmonyOSBuildReceipt receipt = HarmonyOSBuildReceipt.Create(new HarmonyOSBuildReceiptInput
        {
            Action = "export-project",
            BundleFingerprint = toolchain.BundleFingerprint,
            OfficialSdkHash = toolchain.SdkRootSha256,
            XEngineCommit = ResolveXEngineRevision().Commit,
            SigningReference = profile.SigningReference,
            ArtifactRelativePath = Path.GetFileName(manifestPath),
            ArtifactSha256 = hash,
            ArtifactBytes = new FileInfo(manifestPath).Length,
            Provenance = await CreateBuildProvenanceAsync(
                toolchain,
                profile,
                settings,
                layout,
                includeContentHashes: true,
                parentReceiptSha256: null,
                observedAtUtc: null,
                cancellationToken).ConfigureAwait(false),
            NativeBuildIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["libxengine_player.so"] = playerBuildId,
            },
            Findings = ["NativeAOT player and native host source are staged; package/device verification remains a separate gate."],
        });
        await receipt.WriteAsync(Path.Combine(layout.RootDirectory, "xengine-harmonyos-export.receipt.json"), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRunReceiptAsync(
        string artifactPath,
        HarmonyOSToolchainSnapshot toolchain,
        HarmonyOSBuildProfile profile,
        BuildSettings settings,
        HarmonyOSDeviceRunResult run,
        CancellationToken cancellationToken)
    {
        string artifactHash = await ComputeSha256Async(artifactPath, cancellationToken).ConfigureAwait(false);
        string packageReceiptPath = artifactPath + ".receipt.json";
        if (!File.Exists(packageReceiptPath))
            throw new FileNotFoundException("HarmonyOS Build & Run requires the verified package receipt.", packageReceiptPath);
        string packageReceiptHash = await ComputeSha256Async(packageReceiptPath, cancellationToken).ConfigureAwait(false);
        var findings = new List<string>
        {
            "deploymentAction=" + run.DeploymentAction,
            "deviceAbi=" + run.DeviceAbi,
            "deviceApi=" + run.DeviceApi.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "readiness=PASS",
        };
        if (!string.IsNullOrWhiteSpace(run.ScreenshotSha256))
            findings.Add("screenshotSha256=" + run.ScreenshotSha256);
        HarmonyOSBuildReceipt receipt = HarmonyOSBuildReceipt.Create(new HarmonyOSBuildReceiptInput
        {
            Action = "deploy-run",
            BundleFingerprint = toolchain.BundleFingerprint,
            OfficialSdkHash = toolchain.SdkRootSha256,
            XEngineCommit = ResolveXEngineRevision().Commit,
            SigningReference = profile.SigningReference,
            DeviceId = run.DeviceId,
            ArtifactRelativePath = Path.GetFileName(artifactPath),
            ArtifactSha256 = artifactHash,
            ArtifactBytes = new FileInfo(artifactPath).Length,
            Provenance = CreateBuildProvenance(
                toolchain,
                profile,
                settings,
                ownershipManifestSha256: null,
                payloadManifestSha256: null,
                parentReceiptSha256: packageReceiptHash,
                observedAtUtc: DateTimeOffset.UtcNow),
            ToolVersions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hdc"] = run.HdcVersion,
            },
            Findings = findings,
        });
        await receipt.WriteAsync(artifactPath + ".run.receipt.json", cancellationToken).ConfigureAwait(false);
    }

    private static string RedactLaunchDiagnostic(string value, params string?[] redactions)
    {
        string result = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        for (int i = 0; i < redactions.Length; i++)
        {
            string? redaction = redactions[i];
            if (!string.IsNullOrWhiteSpace(redaction))
                result = result.Replace(redaction, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        }
        return result.Length <= 2048 ? result : result[..2048] + "...";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<HarmonyOSBuildProvenanceInput> CreateBuildProvenanceAsync(
        HarmonyOSToolchainSnapshot toolchain,
        HarmonyOSBuildProfile profile,
        BuildSettings settings,
        HarmonyOSPlayerProjectLayout layout,
        bool includeContentHashes,
        string? parentReceiptSha256,
        DateTimeOffset? observedAtUtc,
        CancellationToken cancellationToken)
    {
        string? ownershipManifestSha256 = null;
        string? payloadManifestSha256 = null;
        if (includeContentHashes)
        {
            ownershipManifestSha256 = await ComputeSha256Async(layout.OwnershipManifestPath, cancellationToken).ConfigureAwait(false);
            string payloadManifestPath = Path.Combine(
                layout.StageProjectDirectory,
                "entry",
                "src",
                "main",
                "resources",
                "resfile",
                HarmonyOSAssetPayloadInspector.HashManifestFileName);
            payloadManifestSha256 = await ComputeSha256Async(payloadManifestPath, cancellationToken).ConfigureAwait(false);
        }
        return CreateBuildProvenance(
            toolchain,
            profile,
            settings,
            ownershipManifestSha256,
            payloadManifestSha256,
            parentReceiptSha256,
            observedAtUtc);
    }

    private static HarmonyOSBuildProvenanceInput CreateBuildProvenance(
        HarmonyOSToolchainSnapshot toolchain,
        HarmonyOSBuildProfile profile,
        BuildSettings settings,
        string? ownershipManifestSha256,
        string? payloadManifestSha256,
        string? parentReceiptSha256,
        DateTimeOffset? observedAtUtc)
    {
        HarmonyOSSourceRevision revision = ResolveXEngineRevision();
        ResolvedBuildOptions options = ResolvedBuildOptions.From(settings);
        return new HarmonyOSBuildProvenanceInput
        {
            SourceState = revision.State,
            ProviderReleaseId = toolchain.ReleaseId,
            ProviderManifestSha256 = toolchain.ManifestSha256,
            ProviderPackageHashes = toolchain.PackageHashes,
            TargetApi = profile.TargetApi,
            TargetAbi = profile.TargetAbis[0],
            BuildConfiguration = options.Config.ToString(),
            BuildSettingsSha256 = ComputeBuildSettingsSha256(profile, settings),
            OwnershipManifestSha256 = ownershipManifestSha256,
            PayloadManifestSha256 = payloadManifestSha256,
            ParentReceiptSha256 = parentReceiptSha256,
            ObservedAtUtc = observedAtUtc,
        };
    }

    private static string ComputeBuildSettingsSha256(HarmonyOSBuildProfile profile, BuildSettings settings)
    {
        ResolvedBuildOptions options = ResolvedBuildOptions.From(settings);
        string normalized = JsonSerializer.Serialize(new
        {
            outputMode = profile.OutputMode.ToString(),
            applicationId = profile.ApplicationId,
            versionCode = profile.VersionCode,
            targetApi = profile.TargetApi,
            targetAbi = profile.TargetAbis.Count == 1 ? profile.TargetAbis[0] : string.Empty,
            buildConfiguration = options.Config.ToString(),
            packagingMode = options.PackagingMode.ToString(),
            signingReferenceKind = string.Equals(profile.SigningReference, "None", StringComparison.Ordinal)
                ? "None"
                : "EnvironmentVariable",
            abilityName = profile.AbilityName,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    /// <summary>
    /// Patches the materialized Stage's build-profile.json5 with debug signing material so hvigor's
    /// SignHap step produces a signed HAP. The signing directory (containing the .cer/.p12/.p7b that
    /// DevEco's "Automatically generate signature" produced) and the DevEco-encrypted store/key
    /// passwords are read from environment variables only — never from project settings — so secrets
    /// stay out of the repo. This is a debug-signing convenience for the local build loop; release
    /// signing remains a SigningReference-driven feature.
    /// </summary>
    /// <summary>
    /// Ensures the staged module declares <c>ohos.permission.INTERNET</c>. Player builds
    /// contact update endpoints (content hot-update); without the permission the OS
    /// denies socket creation and every HttpClient call fails with EPERM.
    /// </summary>
    private static async Task EnsureStageInternetPermissionAsync(string stageProjectDirectory, CancellationToken cancellationToken)
    {
        string modulePath = Path.Combine(stageProjectDirectory, "entry", "src", "main", "module.json5");
        if (!File.Exists(modulePath))
            return;
        string json = await File.ReadAllTextAsync(modulePath, cancellationToken).ConfigureAwait(false);
        if (json.Contains("ohos.permission.INTERNET", StringComparison.Ordinal))
            return;
        const string permission = "\"requestPermissions\": [ { \"name\": \"ohos.permission.INTERNET\" } ],";
        int insertion = json.IndexOf("\"abilities\"", StringComparison.Ordinal);
        if (insertion < 0)
            return;
        json = json.Insert(insertion, "    " + permission + Environment.NewLine);
        await File.WriteAllTextAsync(modulePath, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs every discovered <see cref="IHarmonyOSStageProjectContributor"/> against the
    /// materialized Stage project; aggregated contributor errors fail the build.
    /// </summary>
    internal static void RunStageProjectContributions(HarmonyOSPlayerProjectLayout layout, HarmonyOSBuildProfile profile)
    {
        var context = new HarmonyOSStageProjectContext
        {
            StageProjectDirectory = layout.StageProjectDirectory,
            ProjectPath = layout.ProjectPath,
            ApplicationId = profile.ApplicationId,
            Abi = profile.TargetAbis[0],
            ApiLevel = profile.TargetApi,
        };
        List<string> errors = HarmonyOSStageProjectContribution.RunAll(context);
        if (errors.Count == 0)
            return;
        throw new HarmonyOSBuildException(
            HarmonyOSBuildErrorCode.StageContributionFailed,
            string.Join(Environment.NewLine, errors));
    }

    private static async Task InjectStageSigningAsync(string stageProjectDirectory, CancellationToken cancellationToken)
    {
        string? signingDir = Environment.GetEnvironmentVariable("XENGINE_HARMONYOS_SIGNING_DIR");
        string? storePassword = Environment.GetEnvironmentVariable("XENGINE_HARMONYOS_STORE_PASSWORD");
        string? keyPassword = Environment.GetEnvironmentVariable("XENGINE_HARMONYOS_KEY_PASSWORD");
        if (string.IsNullOrWhiteSpace(signingDir) || string.IsNullOrWhiteSpace(storePassword) || string.IsNullOrWhiteSpace(keyPassword))
            return; // No signing material configured; hvigor will fail at SignHap (expected for unsigned builds).

        string buildProfilePath = Path.Combine(stageProjectDirectory, "build-profile.json5");
        if (!File.Exists(buildProfilePath))
            return;
        string signingDirFull = Path.GetFullPath(signingDir);
        string? cerPath = FindSigningFile(signingDirFull, ".cer");
        string? p12Path = FindSigningFile(signingDirFull, ".p12");
        string? p7bPath = FindSigningFile(signingDirFull, ".p7b");
        if (cerPath == null || p12Path == null || p7bPath == null)
            return;

        string json = await File.ReadAllTextAsync(buildProfilePath, cancellationToken).ConfigureAwait(false);
        // The Stage template's signingConfigs entry has empty material paths; inject the real ones.
        // Encrypted passwords are opaque DevEco strings; pass them through verbatim.
        json = json.Replace("\"certpath\": \"\"", "\"certpath\": \"" + cerPath.Replace("\\", "\\\\") + "\"", StringComparison.Ordinal);
        json = json.Replace("\"storeFile\": \"\"", "\"storeFile\": \"" + p12Path.Replace("\\", "\\\\") + "\"", StringComparison.Ordinal);
        json = json.Replace("\"profile\": \"\"", "\"profile\": \"" + p7bPath.Replace("\\", "\\\\") + "\"", StringComparison.Ordinal);
        json = json.Replace("\"storePassword\": \"\"", "\"storePassword\": \"" + storePassword + "\"", StringComparison.Ordinal);
        json = json.Replace("\"keyPassword\": \"\"", "\"keyPassword\": \"" + keyPassword + "\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(buildProfilePath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static string? FindSigningFile(string dir, string extension)
    {
        var files = Directory.GetFiles(dir, "*" + extension);
        return files.Length > 0 ? files[0] : null;
    }

    private static HarmonyOSSourceRevision ResolveXEngineRevision()
    {
        string? value = Environment.GetEnvironmentVariable("XENGINE_COMMIT");
        if ((value?.Length == 40 || value?.Length == 64) && IsLowerHex(value))
        {
            string? state = Environment.GetEnvironmentVariable("XENGINE_SOURCE_STATE");
            return new HarmonyOSSourceRevision(value, state is "Clean" or "Dirty" ? state : "Unverified");
        }

        string? sourceRoot = FindXEngineSourceRoot();
        if (sourceRoot != null)
        {
            string? commit = RunGit(sourceRoot, "rev-parse", "HEAD");
            if ((commit?.Length == 40 || commit?.Length == 64) && IsLowerHex(commit))
            {
                string? status = RunGit(sourceRoot, "status", "--porcelain", "--untracked-files=all");
                return new HarmonyOSSourceRevision(commit, string.IsNullOrEmpty(status) ? "Clean" : "Dirty");
            }
        }

        string informationalVersion = typeof(HarmonyOSBuildPipeline).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        int separator = informationalVersion.LastIndexOf('+');
        string fallbackCommit = separator >= 0 ? informationalVersion[(separator + 1)..] : string.Empty;
        if ((fallbackCommit.Length == 40 || fallbackCommit.Length == 64) && IsLowerHex(fallbackCommit))
            return new HarmonyOSSourceRevision(fallbackCommit, "Unverified");
        throw new InvalidOperationException("Unable to resolve the XEngine source revision for the HarmonyOS receipt.");
    }

    private static string? FindXEngineSourceRoot()
    {
        string? assemblyDirectory = Path.GetDirectoryName(typeof(HarmonyOSBuildPipeline).Assembly.Location);
        for (DirectoryInfo? directory = string.IsNullOrWhiteSpace(assemblyDirectory) ? null : new DirectoryInfo(assemblyDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                File.Exists(Path.Combine(directory.FullName, "XEngine.Editor", "XEngine.Editor.csproj")))
            {
                return directory.FullName;
            }
        }
        return null;
    }

    private static string? RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            for (int i = 0; i < arguments.Length; i++)
                process.StartInfo.ArgumentList.Add(arguments[i]);
            if (process.Start() && process.WaitForExit(3000) && process.ExitCode == 0)
                return process.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            // Source archives may lack Git metadata; AssemblyInformationalVersion is the fallback.
        }
        return null;
    }

    private static bool IsLowerHex(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                return false;
        }
        return true;
    }

    private sealed record HarmonyOSSourceRevision(string Commit, string State);

    internal static bool IsUsableOutputDirectory(string rawOutput, string resolvedOutput, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return false;
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedOutput));
        string project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        string[] protectedRoots = [project, Path.Combine(project, "Assets"), Path.Combine(project, "Library"), Path.Combine(project, "Temp")];
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (int i = 0; i < protectedRoots.Length; i++)
        {
            if (string.Equals(output, protectedRoots[i], comparison))
                return false;
        }
        return !Directory.Exists(output) || !HasEntries(output);
    }

    private static void PublishStaging(string stagingDirectory, string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
        {
            if (HasEntries(outputDirectory))
                throw new IOException($"HarmonyOS output directory became non-empty during build: {outputDirectory}");
            Directory.Delete(outputDirectory, recursive: false);
        }
        Directory.Move(stagingDirectory, outputDirectory);
    }

    private static void TryDeleteStaging(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Path.GetFileName(fullPath).Contains(".xengine-harmonyos-", StringComparison.Ordinal) || !Path.GetFileName(fullPath).EndsWith(".staging", StringComparison.Ordinal))
            return;
        try
        {
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }

    private static BuildResult Failure(string error, TimeSpan duration) => new()
    {
        Success = false,
        Errors = error,
        Duration = duration,
    };
}
