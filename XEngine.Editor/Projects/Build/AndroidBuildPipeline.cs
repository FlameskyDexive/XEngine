// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.Android;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.AssetBundles.PlatformVariants;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.Runtime.RHI;

namespace XEngine.Editor.Build;

/// <summary>
/// Build pipeline for Android (.NET Android project export, APK, and AAB).
/// </summary>
/// <remarks>The G0 implementation generates the supported .NET Android host contract and can package
/// its bootstrap activity. User assemblies, assets, and the Vulkan host are added by M1-M3.</remarks>
public class AndroidBuildPipeline : BuildPipeline
{
    public override string DisplayName => "Android";

    public override string Id => "xengine.android";

    /// <summary>Mobile glyph for the Build window card.</summary>
    public override string Icon => EditorIcons.Mobile;

    public override string[] SupportedRuntimeIdentifiers => ["android-arm64", "android-x64", "android-arm"];

    public override BuildResult Build(BuildSettings settings, BuildProgress? progress)
        => BuildAsync(Project.Current?.RootPath ?? "", settings, outputDirectory: null, progress)
            .GetAwaiter().GetResult();

    public override async Task<BuildResult> BuildAsync(
        string projectPath,
        BuildSettings settings,
        string? outputDirectory = null,
        BuildProgress? progress = null,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? stagingDirectory = null;
        string? resolvedOutput = null;
        try
        {
            var project = Project.Current;
            if (project == null)
                return Failure("No project open.", stopwatch.Elapsed);

            AndroidBuildProfile profile = settings.GetProfile<AndroidBuildProfile>(GetType());
            AndroidTrimmingReport.ValidateRequestedMode(profile.ManagedStripping);
            ResolvedBuildOptions options = ResolvedBuildOptions.From(settings, outputDirectory);
            string rawOutput = options.OutputDirectory;
            string resolvedOutputPath = Path.IsPathRooted(rawOutput)
                ? Path.GetFullPath(rawOutput)
                : Path.GetFullPath(Path.Combine(projectPath, rawOutput));
            resolvedOutput = resolvedOutputPath;
            if (!IsUsableOutputDirectory(rawOutput, resolvedOutputPath, project.RootPath))
            {
                return Failure(
                    $"Android build output '{rawOutput}' must be a new or empty directory outside the project root, Assets, Library, and Temp roots.",
                    stopwatch.Elapsed);
            }

            string lockPath = EditorSettings.Instance.AndroidToolchainLockPath;
            progress?.Log("[Android] Validating locked toolchain...", 0.05f);
            AndroidToolchainSnapshot toolchain = await AndroidToolchainLockFile
                .ReadAndValidateAsync(lockPath, cancellation)
                .ConfigureAwait(false);
            if (profile.TargetSdkVersion != toolchain.DryRun.TargetApi)
            {
                return Failure(
                    $"Android target SDK {profile.TargetSdkVersion} does not match locked dry-run API {toolchain.DryRun.TargetApi}.",
                    stopwatch.Elapsed);
            }

            string runtimeIdentifier = AndroidAbiMapper.ToRuntimeIdentifier(profile.TargetAbis[0]);
            string runtimePayloadDirectory = Path.Combine(
                project.BuildTempPath,
                "AndroidRuntime",
                runtimeIdentifier);
            progress?.Log("[Android] Building Vulkan-only managed Runtime payload...", 0.12f);
            await AndroidRuntimePayloadBuilder.BuildAsync(
                project.RootPath,
                runtimePayloadDirectory,
                runtimeIdentifier,
                toolchain,
                cancellation).ConfigureAwait(false);
            AndroidManagedDependencyCollection managedDependencies =
                AndroidManagedDependencyCollector.Collect(runtimePayloadDirectory, project);

            AssetCollector.CollectionResult assets = CollectAssets(
                settings,
                progress,
                assetBundleOnly: options.PackagingMode == XEngine.Runtime.AssetPackagingMode.AssetBundle);
            if (options.PackagingMode == XEngine.Runtime.AssetPackagingMode.Embedded)
                return Failure(
                    "Android packaging does not support Embedded assets; choose LooseFiles, XEnginePak, or AssetBundle.",
                    stopwatch.Elapsed);
            if (!ShaderImporter.CompileBuiltInOfflineArtifacts(project))
                return Failure("Failed to compile built-in offline shader artifacts.", stopwatch.Elapsed);

            AndroidShaderVariantTraceCollection approvedShaderTrace = AndroidShaderVariantTrace.ReadApproved(
                project.RootPath,
                profile.ApprovedShaderVariantTracePath);
            if (approvedShaderTrace.Entries.Count > 0)
            {
                progress?.Log(
                    $"[Android] Replaying {approvedShaderTrace.Entries.Count} approved runtime shader trace lookups.",
                    0.115f);
            }
            AndroidShaderVariantCollection shaderVariants = AndroidShaderVariantCollector.CollectForBuild(
                assets.AllAssets,
                approvedShaderTrace);
            IReadOnlyList<ShaderArtifactBuildSelection> shaderSelections =
                AndroidShaderVariantCollector.CreateArtifactSelections(shaderVariants);
            if (!ShaderImporter.CompileArtifactSelections(project, shaderSelections))
                return Failure("Failed to compile collected Android shader variants.", stopwatch.Elapsed);
            string shaderPackageDirectory = Path.Combine(
                project.BuildTempPath,
                "AndroidShaders-" + Guid.NewGuid().ToString("N"));
            ShaderArtifactBuildPackage shaderPackage = ShaderArtifactBuildPackager.Prepare(
                project,
                assets.AllAssets,
                shaderPackageDirectory,
                targetBackend: GraphicsBackend.Vulkan,
                selections: shaderSelections);
            AndroidManagedTypeRootCollection managedTypeRoots = AndroidManagedTypeRootCollector.Collect(
                assets,
                managedDependencies);
            AndroidTrimmingReport trimmingReport = AndroidTrimmingReport.Create(
                profile.ManagedStripping,
                assets.AllAssets.Count,
                shaderVariants.Descriptors.Count,
                profile.TargetAbis,
                managedTypeRoots.Types.Count);
            Guid defaultSceneGuid = options.Scenes
                .Where(s => s.Enabled && s.SceneGuid != Guid.Empty)
                .Select(s => s.SceneGuid)
                .FirstOrDefault();
            progress?.Log("Preparing Android asset payload...", 0.16f);
            AndroidAssetPayload assetPayload = PrepareAssetPayload(
                project,
                settings,
                profile,
                options,
                assets,
                defaultSceneGuid,
                progress);

            GeneralSettings? general = EditorRegistries.GetSettings<GeneralSettings>();
            string productName = string.IsNullOrWhiteSpace(general?.ProductName) ? "XEngine" : general.ProductName;
            string versionName = general?.Version ?? "1.0";
            string parent = Path.GetDirectoryName(resolvedOutputPath)!;
            Directory.CreateDirectory(parent);
            stagingDirectory = Path.Combine(
                parent,
                "." + Path.GetFileName(resolvedOutputPath) + ".xengine-android-" + Guid.NewGuid().ToString("N") + ".staging");

            progress?.Log("[Android] Generating standalone .NET Android project...", 0.2f);
            AndroidPlayerProjectLayout layout = await AndroidPlayerProjectGenerator.GenerateAsync(
                stagingDirectory,
                new AndroidPlayerProjectOptions
                {
                    ProjectName = productName,
                    ApplicationId = profile.ApplicationId,
                    ApplicationLabel = productName,
                    ApplicationIconPath = general?.ResolveApplicationIconPath(project),
                    VersionCode = profile.VersionCode,
                    VersionName = versionName,
                    MinSdkVersion = profile.MinSdkVersion,
                    TargetSdkVersion = profile.TargetSdkVersion,
                    ScreenOrientation = profile.ScreenOrientation,
                    ResizableActivity = profile.ResizableActivity,
                    AdditionalPermissions = profile.AdditionalPermissions,
                    OutputMode = profile.OutputMode,
                    ManagedStripping = profile.ManagedStripping,
                    TargetAbis = profile.TargetAbis,
                    ToolchainFingerprint = toolchain.Fingerprint,
                    ToolchainLockPath = lockPath,
                    ShaderVariants = shaderVariants,
                    ApprovedShaderVariantTrace = approvedShaderTrace,
                    ShaderPackageDirectory = shaderPackage.DirectoryPath,
                    AssetPayloadDirectory = assetPayload.RootDirectory,
                    EnableDevelopmentShaderTrace = options.Config == BuildConfiguration.Debug,
                    DefaultSceneGuid = defaultSceneGuid,
                    AssetPackagingMode = options.PackagingMode,
                    TrimmingReport = trimmingReport,
                    ManagedDependencies = managedDependencies,
                    ManagedTypeRoots = managedTypeRoots,
                    NativeLibraryRoot = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(AndroidRuntimePayloadBuilder.FindRuntimeProject(project.RootPath))!,
                        "..",
                        "Libraries")),
                    PackageSizeBudget = AndroidPackageSizeBudget.FromMebibytes(
                        profile.MaxPackageSizeMiB,
                        profile.MaxManagedSizeMiB,
                        profile.MaxNativeSizeMiB,
                        profile.MaxAssetSizeMiB,
                        profile.MaxShaderSizeMiB),
                },
                cancellation).ConfigureAwait(false);

            AndroidPackageBuildResult? package = null;
            if (profile.OutputMode != AndroidOutputMode.ExportProject)
            {
                progress?.Log($"[Android] Building {profile.OutputMode}...", 0.45f);
                package = await new AndroidPackageBuilder().BuildAsync(
                    layout,
                    toolchain,
                    options.Config.ToString(),
                    TimeSpan.FromMinutes(15),
                    cancellation).ConfigureAwait(false);
                progress?.Log(
                    $"[Android] Verified {Path.GetFileName(package.ArtifactPath)} " +
                    $"({package.ArtifactBytes} bytes, {string.Join(", ", package.Verification)}).",
                    0.9f);
            }

            PublishStaging(stagingDirectory, resolvedOutputPath);
            stagingDirectory = null;
            stopwatch.Stop();
            progress?.Log("[Android] Build complete.", Runtime.LogSeverity.Success);
            return new BuildResult
            {
                Success = true,
                OutputPath = resolvedOutputPath,
                Log = (package?.BuildOutput ?? "Generated standalone .NET Android project.") +
                    Environment.NewLine +
                    $"Android Vulkan shader package: {shaderPackage.ArtifactCount} artifacts, " +
                    $"{shaderPackage.ArtifactBytes} bytes.",
                Duration = stopwatch.Elapsed,
                AssetCount = assetPayload.AssetCount,
            };
        }
        catch (OperationCanceledException)
        {
            return Failure("Android build cancelled.", stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            string message = exception.Message;
            if (exception is AndroidPackageSizeBudgetExceededException &&
                stagingDirectory != null && resolvedOutput != null)
            {
                try
                {
                    PublishStaging(stagingDirectory, resolvedOutput);
                    stagingDirectory = null;
                    message += $" Failed package and report retained at '{resolvedOutput}'.";
                }
                catch (Exception preserveException)
                {
                    message += $" Failed output could not be retained: {preserveException.Message}";
                }
            }
            progress?.Log("[Android] " + message, Runtime.LogSeverity.Error);
            return Failure(message, stopwatch.Elapsed);
        }
        finally
        {
            // XENGINE_KEEP_STAGING=1 keeps the generated .NET Android project after a failed (or
            // successful) build so packaging problems can be inspected or rebuilt by hand.
            if (stagingDirectory != null &&
                !string.Equals(Environment.GetEnvironmentVariable("XENGINE_KEEP_STAGING"), "1", StringComparison.OrdinalIgnoreCase))
                TryDeleteStaging(stagingDirectory);
        }
    }

    /// <summary>Path to the produced APK (or AAB) in the output directory.</summary>
    public override string GetExecutablePath(string outputPath, BuildSettings settings)
    {
        AndroidBuildProfile profile = settings.GetProfile<AndroidBuildProfile>(GetType());
        if (profile.OutputMode == AndroidOutputMode.ExportProject)
            return Path.Combine(outputPath, ".xengine-export-project-is-not-runnable");
        string productName = EditorRegistries.GetSettings<GeneralSettings>()?.ProductName ?? "XEngineGame";
        string extension = profile.OutputMode == AndroidOutputMode.Aab ? ".aab" : ".apk";
        return Path.Combine(outputPath, "artifacts", AndroidPlayerProjectGenerator.GetAssemblyName(productName) + extension);
    }

    /// <summary>
    /// Installs and launches the produced APK/AAB on a connected device through adb, owning the
    /// Android Build &amp; Run path that <see cref="ProjectBuilder"/> used to handle inline. Mirrors the
    /// device-launch contract the HarmonyOS pipeline exposes so the CLI build flow stays uniform.
    /// </summary>
    public override async Task<BuildLaunchResult> LaunchAsync(
        BuildLaunchRequest request,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Settings);
        ArgumentNullException.ThrowIfNull(request.Build);
        if (!request.Build.Success)
            return BuildLaunchResult.Failed("Android Build & Run requires a successful package build.");

        AndroidBuildProfile profile = request.Settings.GetProfile<AndroidBuildProfile>(GetType());
        if (profile.OutputMode == AndroidOutputMode.ExportProject)
            return BuildLaunchResult.Failed("Android Build & Run requires APK or AAB output, not Export Project.");

        string artifactPath = GetExecutablePath(request.Build.OutputPath, request.Settings);
        if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
            return BuildLaunchResult.Failed("Android Build & Run could not find the packaged artifact.");

        try
        {
            cancellation.ThrowIfCancellationRequested();
            request.Progress?.Log("[Android] Selecting device, installing, and launching...");
            AndroidToolchainSnapshot toolchain = await AndroidToolchainLockFile
                .ReadAndValidateAsync(EditorSettings.Instance.AndroidToolchainLockPath, cancellation)
                .ConfigureAwait(false);
            AndroidDeploymentResult deployment = await new AndroidDeployer().DeployAsync(
                artifactPath,
                profile.ApplicationId,
                profile.TargetAbis,
                profile.DeploymentDeviceSerial,
                toolchain,
                TimeSpan.FromMinutes(2),
                cancellation).ConfigureAwait(false);

            var logcat = new StringBuilder();
            for (int i = 0; i < deployment.FilteredLogcat.Count; i++)
            {
                string line = deployment.FilteredLogcat[i];
                logcat.AppendLine("[Android][logcat] " + line);
            }
            string diagnostics = logcat.ToString();
            return BuildLaunchResult.Succeeded(
                $"[Android] Running on {deployment.DeviceSerial} ({string.Join(", ", deployment.DeviceAbis)}): {deployment.Activity}",
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return BuildLaunchResult.Failed("Android Build & Run failed: " + exception.Message);
        }
    }

    internal static bool IsUsableOutputDirectory(string rawOutput, string resolvedOutput, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return false;
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedOutput));
        string project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        string[] protectedRoots =
        [
            project,
            Path.Combine(project, "Assets"),
            Path.Combine(project, "Library"),
            Path.Combine(project, "Temp"),
        ];
        for (int i = 0; i < protectedRoots.Length; i++)
        {
            if (string.Equals(output, protectedRoots[i], PathComparison))
                return false;
        }
        return !Directory.Exists(output) || !HasEntries(output);
    }

    private static void PublishStaging(string stagingDirectory, string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
        {
            if (HasEntries(outputDirectory))
                throw new IOException($"Android output directory became non-empty during build: {outputDirectory}");
            Directory.Delete(outputDirectory, recursive: false);
        }
        Directory.Move(stagingDirectory, outputDirectory);
    }

    private static void TryDeleteStaging(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Path.GetFileName(fullPath).Contains(".xengine-android-", StringComparison.Ordinal) ||
            !Path.GetFileName(fullPath).EndsWith(".staging", StringComparison.Ordinal))
        {
            return;
        }
        try { Directory.Delete(fullPath, recursive: true); }
        catch { }
    }

    private static BuildResult Failure(string error, TimeSpan duration) => new()
    {
        Success = false,
        Errors = error,
        Duration = duration,
    };

    private AndroidAssetPayload PrepareAssetPayload(
        Project project,
        BuildSettings settings,
        AndroidBuildProfile profile,
        ResolvedBuildOptions options,
        AssetCollector.CollectionResult assets,
        Guid defaultSceneGuid,
        BuildProgress? progress)
    {
        string root = Path.Combine(
            project.BuildTempPath,
            "AndroidAssets-" + Guid.NewGuid().ToString("N"));
        string content = Path.Combine(root, "Content");
        string settingsDirectory = Path.Combine(root, "Settings");
        Directory.CreateDirectory(content);

        TexturePlatformSettings textureSettings = new()
        {
            Platform = BuildTarget.Android,
            Override = true,
            MaxTextureSize = profile.TextureMaxSize,
            Format = profile.TextureFormat,
            CompressionQuality = profile.TextureCompressionQuality,
        };

        // A build profile can downgrade textures for this build alone (iteration builds), without
        // touching the .meta files that would otherwise force a project-wide reimport.
        textureSettings = BuildProfileRegistry.GetActive(settings)?.Profile.AssetImportOverrides
            .ApplyTo(textureSettings) ?? textureSettings;
        AndroidTextureVariantBuildPlan texturePlan = AndroidTexturePayloadEncoder.CreatePlan(
            project,
            assets.AllAssets,
            textureSettings,
            AndroidTextureCapabilities.Baseline);
        // ForceUncompressed (and any ASTC miss) leaves the Color4b cache in place; still honour
        // MaxTextureSize so an iteration profile actually shrinks those payloads.
        BuildTexturePayloadDowngrader.MergeMissing(
            texturePlan.PayloadOverrides,
            BuildTexturePayloadDowngrader.CreateOverrides(project, assets.AllAssets, textureSettings));

        int assetCount = options.PackagingMode switch
        {
            XEngine.Runtime.AssetPackagingMode.LooseFiles => CopyLooseAssets(assets.AllAssets, content, progress, texturePlan.PayloadOverrides),
            XEngine.Runtime.AssetPackagingMode.XEnginePak => PackAssets(
                assets.AllAssets,
                content,
                options.MaxPakSizeMB,
                progress,
                texturePlan.PayloadOverrides),
            XEngine.Runtime.AssetPackagingMode.AssetBundle => PackAssetBundles(
                BuildTarget.Android,
                content,
                progress,
                texturePlan.PayloadOverrides),
            _ => throw new InvalidOperationException(
                $"Unsupported Android asset packaging mode: {options.PackagingMode}"),
        };

        GenerateManifest(
            Path.Combine(content, "asset_manifest.bin"),
            assets.AllAssets,
            assets.ResourcesMap,
            defaultSceneGuid);
        ExportSettings(settingsDirectory, progress);

        int textureCount = AndroidTextureVariantManifest.Write(
            Path.Combine(settingsDirectory, AndroidTextureVariantManifest.FileName),
            project,
            assets.AllAssets,
            textureSettings,
            AndroidTextureCapabilities.Baseline,
            texturePlan);
        string encoderStatus = "encoder unavailable";
        if (texturePlan.AstcEncoder != null && texturePlan.Etc2Encoder != null)
            encoderStatus = $"{texturePlan.AstcEncoder.Id} {texturePlan.AstcEncoder.Version}, {texturePlan.Etc2Encoder.Id} {texturePlan.Etc2Encoder.Version}";
        else if (texturePlan.AstcEncoder != null)
            encoderStatus = $"{texturePlan.AstcEncoder.Id} {texturePlan.AstcEncoder.Version}";
        else if (texturePlan.Etc2Encoder != null)
            encoderStatus = $"{texturePlan.Etc2Encoder.Id} {texturePlan.Etc2Encoder.Version}";
        progress?.Log($"Prepared Android texture variant contract ({textureCount} texture(s), {encoderStatus}).", 0.18f);

        AndroidPhysicalAssetPayload physicalPayload = AndroidAssetPayloadInspector.Inspect(
            root,
            options.PackagingMode,
            assets.AllAssets);
        string reportsDirectory = Path.Combine(root, "Reports");
        AndroidAssetStrippingReport.Write(
            Path.Combine(reportsDirectory, AndroidAssetStrippingReport.FileName),
            options.AssetMode.ToString(),
            options.PackagingMode,
            assets,
            physicalPayload);

        string manifestPath = Path.Combine(root, "android-assets.json");
        WriteAndroidAssetManifest(manifestPath, root, options.PackagingMode);
        return new AndroidAssetPayload(root, manifestPath, assetCount);
    }

    private static void WriteAndroidAssetManifest(
        string manifestPath,
        string payloadRoot,
        XEngine.Runtime.AssetPackagingMode packagingMode)
    {
        string[] files = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var entries = new List<object>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            string relative = Path.GetRelativePath(payloadRoot, files[i]).Replace('\\', '/');
            if (string.Equals(relative, "android-assets.json", StringComparison.Ordinal))
                continue;
            FileInfo info = new(files[i]);
            string extension = Path.GetExtension(relative);
            entries.Add(new
            {
                path = relative,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(files[i]))),
                bytes = info.Length,
                requiresSeek = extension.Equals(".xenginepak", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".bundle", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".bytes", StringComparison.OrdinalIgnoreCase),
            });
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        string textureVariantManifest = "Settings/" + AndroidTextureVariantManifest.FileName;
        bool hasTextureVariantManifest = File.Exists(Path.Combine(payloadRoot, textureVariantManifest.Replace('/', Path.DirectorySeparatorChar)));
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            root = "xengine",
            packagingMode = packagingMode.ToString(),
            assetStrippingReport = "Reports/" + AndroidAssetStrippingReport.FileName,
            textureVariants = hasTextureVariantManifest ? textureVariantManifest : null,
            assets = entries,
        }, options);
        File.WriteAllText(manifestPath, json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n");
    }

    private sealed record AndroidAssetPayload(string RootDirectory, string ManifestPath, int AssetCount);

    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
