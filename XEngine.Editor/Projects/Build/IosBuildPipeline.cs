// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.Ios;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Importers;
using XEngine.Editor.Theming;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Editor.Build;

/// <summary>
/// Build pipeline for iOS (.app via net10.0-ios publish).
/// </summary>
/// <remarks>
/// <para><b>P1 player export.</b> <see cref="BuildAsync"/> generates a relocatable
/// <c>net10.0-ios</c> Mono player (UIViewController + CAMetalLayer host); device builds use AOT,
/// while ARM64 Simulator builds enable the interpreter for development compatibility.</para>
/// <para><b>P5 signing / Build &amp; Run.</b> Debug uses Automatic signing + Apple
/// Development; Release requires an explicit provisioning profile. Publish and
/// <c>xcrun devicectl</c> require macOS + Xcode. Off-Mac hosts generate sources and
/// <para><b>P6 ILLink / size.</b> Safe writes Echo <c>$type</c> roots and
/// <c>trimming/ios-managed-type-roots.json</c>. Full is fail-closed. Package size is
/// measured from the generated Player layout on Windows (no signed .app). Cold-start
/// p50/p95 and the I0 device matrix are skip reports, not Supported.</para>
/// </remarks>
public class IosBuildPipeline : BuildPipeline
{
    internal const string OffMacPublishError =
        "Generated net10.0-ios player sources. Signing, `dotnet publish -r ios-arm64`, and `xcrun devicectl` require macOS + Xcode. " +
        "This host is not a Mac; no desktop executable or .ipa was emitted. " +
        "On a Mac, Xcode is located via DEVELOPER_DIR / xcode-select and must be 16.0+ " +
        "(App Store: https://apps.apple.com/app/xcode/id497799835); use the iOS profile's Toolchain check to validate.";

    public override string DisplayName => "iOS";

    public override string Id => "xengine.ios";

    /// <summary>Mobile glyph for the Build window card.</summary>
    public override string Icon => EditorIcons.Mobile;

    public override string[] SupportedRuntimeIdentifiers => ["ios-arm64", "iossimulator-arm64"];

    internal static IosSigningPlan CreateSigningPlan(
        IosBuildProfile profile,
        bool useSimulator,
        ResolvedBuildOptions buildOptions)
        => useSimulator
            ? IosSigningPlanner.CreateSimulator()
            : IosSigningPlanner.Create(profile, buildOptions.Config);

    internal static IosPublishPlan CreatePublishPlan(
        IosPlayerProjectLayout layout,
        IosSigningPlan signing,
        ResolvedBuildOptions buildOptions,
        string runtimeIdentifier)
        => IosPublishPlanner.Create(
            layout.ProjectPath,
            layout.RootDirectory,
            layout.AssemblyName,
            signing,
            buildOptions.Config,
            runtimeIdentifier);

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
            try
            {
                cancellation.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(settings);
                string exportRoot = PrepareExportRoot(projectPath, outputDirectory);
                progress?.Log("[iOS] Generating net10.0-ios player project...", 0.2f);

                IosBuildProfile profile = settings.GetProfile<IosBuildProfile>(GetType());
                // Build-flag escape hatch: XENGINE_IOS_SIMULATOR=1 forces the simulator target
                // regardless of the persisted profile (CI/headless builds, profile-asset probing).
                bool useSimulator = ResolveUseSimulator(profile);
                string runtimeIdentifier = useSimulator ? "iossimulator-arm64" : "ios-arm64";
                Project? project = Project.Current;
                ResolvedBuildOptions buildOptions = ResolvedBuildOptions.From(settings);
                AssetCollector.CollectionResult playerAssets = CollectPlayerAssets(settings, buildOptions, progress);
                Console.WriteLine($"[iOS-build] UseSimulator={useSimulator} (profile={profile.UseSimulator}) appId={profile.ApplicationId} activeGuid={settings.ActiveProfileGuid} pipeline={settings.ActivePipelineId}");
                IosManagedTrimRoots.ValidateRequestedMode(profile.ManagedStripping);
            IosSigningPlan signing = CreateSigningPlan(profile, useSimulator, buildOptions);
            string? runtimePayloadDirectory = null;
            IReadOnlyList<IosManagedAssemblyInput> assemblies;
            if (OperatingSystem.IsMacOS())
            {
                if (project == null)
                    throw new InvalidOperationException("Open an XEngine project before building an iOS Player.");
                runtimePayloadDirectory = Path.Combine(
                    Path.GetTempPath(), "XEngineIosRuntime", Guid.NewGuid().ToString("N"));
                progress?.Log($"[iOS] Publishing {runtimeIdentifier} Runtime dependency closure...", 0.25f);
                string runtimeRoot = IosRuntimePayloadBuilder.FindRuntimeProject(typeof(EngineObject).Assembly.Location);
                runtimePayloadDirectory = await IosRuntimePayloadBuilder.BuildAsync(
                        runtimeRoot,
                        runtimePayloadDirectory,
                        runtimeIdentifier,
                        cancellation)
                    .ConfigureAwait(false);
                assemblies = IosManagedDependencyCollector.Collect(
                    runtimePayloadDirectory,
                    project,
                    runtimeIdentifier);
            }
            else
            {
                string loadedRuntime = typeof(EngineObject).Assembly.Location;
                if (string.IsNullOrWhiteSpace(loadedRuntime) || !File.Exists(loadedRuntime))
                    throw new FileNotFoundException("Cannot locate XEngine.Runtime.dll for the generated iOS project.");
                assemblies =
                [
                    new IosManagedAssemblyInput(
                        "XEngine.Runtime",
                        loadedRuntime,
                        "engine-runtime",
                        "Off-Mac source generation only; the Mac build regenerates a RID-specific Runtime closure."),
                ];
            }

            Guid defaultSceneGuid = FindDefaultSceneGuid(buildOptions.Scenes);
            IosManagedTypeRootCollection typeRoots = TryCollectTypeRoots(profile, playerAssets, assemblies);
                IosPlayerProjectLayout layout = await IosPlayerProjectGenerator.GenerateAsync(
                    exportRoot,
                    CreateOptions(profile, signing, typeRoots, assemblies, defaultSceneGuid, buildOptions.PackagingMode),
                    cancellation)
                .ConfigureAwait(false);
            if (runtimePayloadDirectory != null)
                TryDeleteDirectory(runtimePayloadDirectory);

            int assetCount = 0;
            if (project != null && EditorAssetBackend.Instance != null)
            {
                assetCount = PackagePlayerContent(
                    project,
                    settings,
                    buildOptions,
                    playerAssets,
                    layout,
                    defaultSceneGuid,
                    useSimulator,
                    progress);
            }

            IosSigningPlanner.WriteVerification(layout.RootDirectory, signing);
            IosPublishPlanner.WriteDsymVerification(layout.RootDirectory, signing, executed: false);
            IosPackageSizeBudget budget = IosPackageSizeBudget.FromMebibytes(
                profile.MaxPackageSizeMiB,
                profile.MaxManagedSizeMiB,
                profile.MaxNativeSizeMiB,
                profile.MaxAssetSizeMiB,
                profile.MaxShaderSizeMiB);
            IosPackageSizeSnapshot size = IosPackageSizeReport.MeasureAndWrite(layout.RootDirectory, budget);
            IosP6Evidence.WriteStartupSkip(layout.RootDirectory);
            IosP6Evidence.WriteDeviceMatrixSkip(layout.RootDirectory);
            size.Enforce(Path.Combine(layout.RootDirectory, "verification", IosPackageSizeReport.FileName));
            progress?.Log("[iOS] Player sources written to " + layout.RootDirectory, 0.7f);

            if (!OperatingSystem.IsMacOS())
            {
                progress?.Log("[iOS] " + OffMacPublishError, Runtime.LogSeverity.Error);
                return new BuildResult
                {
                    Success = false,
                    OutputPath = layout.RootDirectory,
                    Errors = OffMacPublishError,
                    Log = "Generated " + layout.ProjectPath,
                    Duration = stopwatch.Elapsed,
                };
            }

            if (!signing.Ready)
            {
                progress?.Log("[iOS] " + signing.Error, Runtime.LogSeverity.Error);
                return Failure(signing.Error, stopwatch.Elapsed, layout.RootDirectory);
            }

            IosPublishPlan publish = CreatePublishPlan(layout, signing, buildOptions, runtimeIdentifier);
            progress?.Log("[iOS] Publishing " + runtimeIdentifier + (useSimulator ? " (unsigned)..." : " with codesign..."), 0.85f);
            await IosPublishPlanner.RunAsync(publish, cancellation).ConfigureAwait(false);
            IosPublishPlanner.WriteDsymVerification(layout.RootDirectory, signing, executed: true);

            if (!Directory.Exists(publish.AppBundlePath))
            {
                return Failure(
                    "iOS build did not produce a .app bundle at " + publish.AppBundlePath + ".",
                    stopwatch.Elapsed,
                    layout.RootDirectory);
            }

            string appBundlePath = publish.AppBundlePath;
            if (useSimulator)
            {
                appBundlePath = Path.Combine(layout.RootDirectory, layout.AssemblyName + ".app");
                CopyDirectory(publish.AppBundlePath, appBundlePath);
                progress?.Log("[iOS] Signing the completed Simulator app bundle...");
                await IosSimulatorCodeSigner.SignAndVerifyAsync(
                        appBundlePath,
                        layout.RootDirectory,
                        cancellation)
                    .ConfigureAwait(false);
            }

            return new BuildResult
            {
                Success = true,
                OutputPath = layout.RootDirectory,
                Log = "Published " + appBundlePath,
                Duration = stopwatch.Elapsed,
                AssetCount = assetCount,
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, stopwatch.Elapsed);
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
            return BuildLaunchResult.Failed("iOS Build & Run requires a successful signed .app.");
        if (!OperatingSystem.IsMacOS())
            return BuildLaunchResult.Failed(IosDeviceBridge.OffMacLaunchError);

        IosBuildProfile profile = request.Settings.GetProfile<IosBuildProfile>(GetType());
        string appPath = GetExecutablePath(request.Build.OutputPath, request.Settings);
        return await IosDeviceBridge.InstallAndLaunchAsync(
                appPath,
                string.IsNullOrWhiteSpace(profile.ApplicationId) ? "com.xengine.game" : profile.ApplicationId,
                profile.DeploymentDeviceUdid,
                cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>Path to the produced .app bundle (dotnet publish output).</summary>
    public override string GetExecutablePath(string outputPath, BuildSettings settings)
    {
        string productName = ResolveProductName();
        string assemblyName = IosPlayerProjectGenerator.GetAssemblyName(productName);
        return Path.Combine(outputPath, assemblyName + ".app");
    }

    private static IosPlayerProjectOptions CreateOptions(
        IosBuildProfile profile,
        IosSigningPlan signing,
        IosManagedTypeRootCollection typeRoots,
        IReadOnlyList<IosManagedAssemblyInput> assemblies,
        Guid defaultSceneGuid,
        AssetPackagingMode packagingMode)
    {
        string productName = ResolveProductName();
        string versionName = "1.0";
        GeneralSettings? general = EditorRegistries.GetSettings<GeneralSettings>();
        if (!string.IsNullOrWhiteSpace(general?.Version))
            versionName = general.Version;

        string applicationId = string.IsNullOrWhiteSpace(profile.ApplicationId)
            ? "com.xengine.game"
            : profile.ApplicationId;

        return new IosPlayerProjectOptions
        {
            ProjectName = productName,
            ApplicationId = applicationId,
            ApplicationLabel = productName,
            VersionName = versionName,
            VersionCode = 1,
            RuntimeIdentifier = ResolveUseSimulator(profile) ? "iossimulator-arm64" : "ios-arm64",
            DefaultSceneGuid = defaultSceneGuid,
            SupportedOSPlatformVersion = "16.0",
            PreferredFramesPerSecond = 60,
            PackagingMode = packagingMode,
            NativeLibraryRoot = FindEngineLibraries(),
            TeamId = signing.TeamId,
            CodesignIdentity = string.IsNullOrWhiteSpace(signing.CodesignIdentity)
                ? IosSigningPlanner.AutomaticIdentity
                : signing.CodesignIdentity,
            AutomaticSigning = signing.Style == IosSigningStyle.Automatic,
            CodesignProvision = signing.ProvisioningProfileUuid ?? string.Empty,
            ManagedStripping = profile.ManagedStripping,
            ManagedTypeRoots = typeRoots,
            ManagedAssemblies = assemblies,
        };
    }

    private int PackagePlayerContent(
        Project project,
        BuildSettings settings,
        ResolvedBuildOptions options,
        AssetCollector.CollectionResult assets,
        IosPlayerProjectLayout layout,
        Guid defaultSceneGuid,
        bool useSimulator,
        BuildProgress? progress)
    {
        if (options.PackagingMode == AssetPackagingMode.Embedded)
        {
            throw new InvalidOperationException(
                "iOS player does not support Embedded assets; select LooseFiles, XEnginePak, or AssetBundle.");
        }

        if (assets.AllAssets.Count == 0)
            throw new InvalidOperationException("iOS asset collection produced no assets; check the build scene list.");

        IReadOnlyDictionary<Guid, byte[]> textureOverrides = BuildTexturePayloadDowngrader.CreateOverrides(
            project,
            assets.AllAssets,
            BuildTexturePayloadDowngrader.ResolveSettings(settings, BuildTarget.iOS));
        int assetCount;
        if (options.PackagingMode == AssetPackagingMode.AssetBundle)
        {
            var packedAssetGuids = new HashSet<Guid>();
            assetCount = PackAssetBundles(
                BuildTarget.iOS,
                layout.ContentDirectory,
                progress,
                textureOverrides,
                packedAssetGuids);
            if (packedAssetGuids.Count != assets.AllAssets.Count)
            {
                throw new InvalidOperationException(
                    $"iOS AssetBundle collector closure changed between preflight and write " +
                    $"({assets.AllAssets.Count} preflight assets, {packedAssetGuids.Count} written).");
            }
        }
        else if (options.PackagingMode == AssetPackagingMode.XEnginePak)
        {
            assetCount = PackAssets(
                assets.AllAssets,
                layout.ContentDirectory,
                options.MaxPakSizeMB,
                progress,
                textureOverrides);
            GenerateManifest(
                Path.Combine(layout.ContentDirectory, "asset_manifest.bin"),
                assets.AllAssets,
                assets.ResourcesMap,
                defaultSceneGuid);
        }
        else
        {
            assetCount = CopyLooseAssets(assets.AllAssets, layout.ContentDirectory, progress, textureOverrides);
            GenerateManifest(
                Path.Combine(layout.ContentDirectory, "asset_manifest.bin"),
                assets.AllAssets,
                assets.ResourcesMap,
                defaultSceneGuid);
        }
        ExportSettings(Path.Combine(layout.ContentDirectory, "Settings"), progress);

        if (!ShaderImporter.CompileBuiltInOfflineArtifacts(project))
            throw new InvalidOperationException("Failed to compile iOS built-in offline shader artifacts.");
        ShaderTargetEnvironment shaderTargetEnvironment = useSimulator
            ? ShaderTargetEnvironment.IOSSimulator
            : ShaderTargetEnvironment.IOS;
        IReadOnlyList<ShaderArtifactBuildSelection> selections = MetalShaderVariantCollector.CreateArtifactSelections(
            MetalShaderVariantCollector.CollectForBuild(assets.AllAssets),
            shaderTargetEnvironment);
        if (selections.Count == 0)
            throw new InvalidOperationException("iOS shader collection produced no Metal variants.");
        if (!ShaderImporter.CompileArtifactSelections(project, selections))
            throw new InvalidOperationException("Failed to compile iOS Metal shader variants.");

        string shaderPackageDirectory = Path.Combine(
            Path.GetTempPath(), "XEngineIosShaders", Guid.NewGuid().ToString("N"));
        try
        {
            ShaderArtifactBuildPackager.Prepare(
                project,
                assets.AllAssets,
                shaderPackageDirectory,
                targetBackend: GraphicsBackend.Metal,
                selections: selections);
            IosAssetPackager.CopyMetalShaderPackage(
                shaderPackageDirectory,
                Path.Combine(layout.ContentDirectory, "Shaders"));
        }
        finally
        {
            TryDeleteDirectory(shaderPackageDirectory);
        }

        progress?.Log($"[iOS] Packaged {assetCount} loose assets and the Metal shader catalog.");
        return assetCount;
    }

    private static Guid FindDefaultSceneGuid(IReadOnlyList<SceneBuildEntry> scenes)
    {
        for (int i = 0; i < scenes.Count; i++)
        {
            if (scenes[i].Enabled && scenes[i].SceneGuid != Guid.Empty)
                return scenes[i].SceneGuid;
        }
        return Guid.Empty;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IosManagedTypeRootCollection TryCollectTypeRoots(
        IosBuildProfile profile,
        AssetCollector.CollectionResult assets,
        IReadOnlyList<IosManagedAssemblyInput> assemblies)
    {
        if (profile.ManagedStripping != IosManagedStrippingMode.Safe)
            return IosManagedTypeRootCollection.Empty;
        return IosManagedTypeRootCollector.Collect(assets, assemblies);
    }

    private AssetCollector.CollectionResult CollectPlayerAssets(
        BuildSettings settings,
        ResolvedBuildOptions options,
        BuildProgress? progress)
    {
        progress?.Log("Collecting iOS player assets and settings references...", 0.1f);
        var rootGuids = new List<Guid>();
        for (int i = 0; i < options.Scenes.Count; i++)
        {
            SceneBuildEntry scene = options.Scenes[i];
            if (scene.Enabled && scene.SceneGuid != Guid.Empty)
                rootGuids.Add(scene.SceneGuid);
        }

        var exportedSettings = new List<EchoObject>();
        BuildProfile? activeProfile = BuildProfileRegistry.GetActive(settings)?.Profile;
        using (ProfileSettingsOverrideScope overrides = ProfileSettingsOverrideScope.Apply(activeProfile))
        {
            overrides.LogSummary();
            foreach (EditorRegistries.SettingsEntry entry in EditorRegistries.SettingsEntries)
            {
                if (!entry.ExportToBuild)
                    continue;

                try
                {
                    exportedSettings.Add(Serializer.Serialize(entry.Type, entry.Instance, TypeMode.None));
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException(
                        $"iOS build could not inspect exported setting '{entry.Name}' for asset references.",
                        exception);
                }
            }
        }

        HashSet<Guid> settingsAssetGuids = IosAssetPackager.CollectSettingsAssetGuids(exportedSettings);
        if (options.PackagingMode == AssetPackagingMode.AssetBundle)
        {
            AssetCollector.CollectionResult bundleAssets = CollectAssets(settings, progress, assetBundleOnly: true);
            foreach (Guid guid in settingsAssetGuids)
            {
                if (!bundleAssets.AllAssets.Contains(guid))
                {
                    EditorAssetBackend database = EditorAssetBackend.Instance
                        ?? throw new InvalidOperationException("iOS player asset collection requires an initialized asset database.");
                    string path = database.GuidToPath(guid) ?? guid.ToString("D");
                    throw new InvalidDataException(
                        $"iOS AssetBundle settings asset '{path}' is not included by an AssetBundle collector.");
                }
            }
            return bundleAssets;
        }
        if (settingsAssetGuids.Count > 0)
        {
            EditorAssetBackend database = EditorAssetBackend.Instance
                ?? throw new InvalidOperationException("iOS player asset collection requires an initialized asset database.");
            foreach (Guid guid in settingsAssetGuids)
            {
                if (database.GetEntry(guid) == null)
                {
                    throw new InvalidDataException(
                        $"iOS player setting references asset '{guid}', but the asset is missing from the project database.");
                }
                rootGuids.Add(guid);
            }
        }

        return AssetCollector.Collect(
            rootGuids,
            options.AssetMode == AssetExportMode.DependenciesOnly);
    }

    private static string ResolveProductName()
    {
        string productName = "XEngine";
        GeneralSettings? general = EditorRegistries.GetSettings<GeneralSettings>();
        if (!string.IsNullOrWhiteSpace(general?.ProductName))
            productName = general.ProductName;
        return productName;
    }

    private static string PrepareExportRoot(string projectPath, string? outputDirectory)
    {
        string raw = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(
                string.IsNullOrWhiteSpace(projectPath) ? Path.GetTempPath() : projectPath,
                "ios-player")
            : outputDirectory;
        string resolved = Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(
                string.IsNullOrWhiteSpace(projectPath) ? Directory.GetCurrentDirectory() : projectPath,
                raw));

        string exportRoot = Path.Combine(resolved, "xengine-ios-player");
        if (Directory.Exists(exportRoot) && HasEntries(exportRoot))
            exportRoot = Path.Combine(resolved, "xengine-ios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportRoot);
        return exportRoot;
    }

    /// <summary>
    /// Simulator target when the persisted profile asks for it or the
    /// <c>XENGINE_IOS_SIMULATOR=1</c> build flag forces it (CI/headless builds,
    /// profile-asset probing).
    /// </summary>
    internal static bool ResolveUseSimulator(IosBuildProfile profile) =>
        profile.UseSimulator || Environment.GetEnvironmentVariable("XENGINE_IOS_SIMULATOR") == "1";

    /// <summary>
    /// Resolve the XEngine.Runtime.dll packaged into the player: the simulator build consumes
    /// the RID-specific <c>bin/&lt;config&gt;/net10.0/iossimulator-arm64</i> output (built with
    /// IsIosTarget), while device builds still copy the currently loaded desktop assembly
    /// (P1 debt — the device path needs an IsIosTarget rebuild).
    /// </summary>
    private static string ResolveRuntimeAssemblyPath(IosBuildProfile profile, Stopwatch stopwatch)
    {
        string loaded = typeof(EngineObject).Assembly.Location;
        if (!ResolveUseSimulator(profile))
        {
            if (string.IsNullOrWhiteSpace(loaded) || !File.Exists(loaded))
            {
                throw new FileNotFoundException(
                    "Cannot locate XEngine.Runtime.dll to copy into the iOS player Managed folder.");
            }
            return loaded;
        }

        string? librariesRoot = FindEngineLibraries();
        string? engineRoot = librariesRoot != null ? Path.GetDirectoryName(librariesRoot) : null;
        if (engineRoot != null)
        {
            foreach (string configuration in new[] { "Release", "Debug" })
            {
                string candidate = Path.Combine(
                    engineRoot, "XEngine.Runtime", "bin", configuration, "net10.0",
                    "iossimulator-arm64", "XEngine.Runtime.dll");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            "Simulator build requires an IsIosTarget XEngine.Runtime. Run: " +
            "dotnet build XEngine.Runtime -c Release -r iossimulator-arm64 " +
            $"(searched {engineRoot ?? "<engine root>"}/XEngine.Runtime/bin/*/net10.0/iossimulator-arm64).",
            "XEngine.Runtime.dll");
    }

    private static string? FindEngineLibraries()
    {        string? dir = Path.GetDirectoryName(typeof(EngineObject).Assembly.Location);
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "Libraries");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }

    private static BuildResult Failure(string errors, TimeSpan duration, string? outputPath = null) => new()
    {
        Success = false,
        Errors = errors,
        OutputPath = outputPath ?? string.Empty,
        Duration = duration,
    };
}
