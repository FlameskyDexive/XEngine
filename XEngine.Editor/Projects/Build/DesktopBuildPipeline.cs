using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.Profiles;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;
using XEngine.Runtime.RHI;

namespace XEngine.Editor.Build;

/// <summary>
/// Build pipeline for Windows, Linux, and macOS desktop targets.
/// </summary>
public class DesktopBuildPipeline : BuildPipeline
{
    public override string DisplayName => "Desktop";
    public override string Id => "xengine.desktop";
    public override string[] SupportedRuntimeIdentifiers => ["win-x64", "linux-x64", "osx-x64", "osx-arm64"];

    public override BuildResult Build(BuildSettings settings, BuildProgress? progress)
    {
        return BuildAsync(Project.Current!.RootPath, settings, outputDirectory: null).GetAwaiter().GetResult();
    }

    public override async Task<BuildResult> BuildAsync(
        string projectPath,
        BuildSettings settings,
        string? outputDirectory = null,
        BuildProgress? progress = null,
        CancellationToken cancellation = default)
    {
        var sw = Stopwatch.StartNew();

        var project = Project.Current;
        if (project == null)
            return new BuildResult { Success = false, Errors = "No project open." };

        try
        {
            // 0. Compile user scripts (all user assemblies: default + asmdef-defined).
            progress?.Log("Compiling scripts...");
            var compileResult = ScriptCompiler.CompileAll(project);
            if (!compileResult.Success)
                return new BuildResult { Success = false, Errors = $"Script compilation failed:\n{compileResult.Errors}" };
            progress?.Log("Scripts compiled successfully.");

            // 1. Validate
            progress?.Log("Validating project...", 0.05f);
            ResolvedBuildOptions options = ResolvedBuildOptions.From(settings, outputDirectory);
            var enabledScenes = options.Scenes.Where(s => s.Enabled && s.SceneGuid != Guid.Empty).ToList();
            if (enabledScenes.Count == 0)
            {
                return new BuildResult { Success = false, Errors = "No scenes in build. Add at least one scene." };
            }

            DesktopBuildProfile desktopProfile = settings.GetProfile<DesktopBuildProfile>(this.GetType());
            string targetPlatform = BuildPlatforms.FromBuildTarget(desktopProfile.Platform);

            // User game-side assemblies to ship (default + asmdef, in dependency order).
            var buildAssemblies = ScriptCompiler.GetBuildAssemblies(project, targetPlatform);

            string rawOutput = options.OutputDirectory;
            outputDirectory = Path.IsPathRooted(rawOutput)
                ? rawOutput
                : Path.Combine(project.RootPath, rawOutput);

            // The build never deletes files - it requires an empty (or new) output directory. This
            // makes clobbering the project root / Assets / any existing data impossible by construction.
            if (!IsUsableOutputDirectory(rawOutput, outputDirectory))
                return new BuildResult
                {
                    Success = false,
                    Errors = $"Build output directory '{rawOutput}' must be empty. " +
                             "The build does not delete existing files - empty the folder or choose another."
                };

            Directory.CreateDirectory(outputDirectory);

            string contentDir = Path.Combine(outputDirectory, "Content");
            string settingsDir = Path.Combine(contentDir, "Settings");

            // Clean previous build output
            if (Directory.Exists(contentDir))
                try { Directory.Delete(contentDir, true); } catch { }

            // 1b. Always save current scene before building (build uses the cache which comes from the .scene file)
            if (EditorSceneManager.CurrentScenePath != null)
            {
                EditorSceneManager.Save();
                progress?.Log("Auto-saved current scene.");
            }
            else
            {
                Runtime.Debug.LogWarning("[Build] Current scene has no save path. Save it first for accurate build.");
            }

            // 1c. Reimport all build scenes to ensure caches are fresh
            var db = EditorAssetBackend.Instance;
            foreach (var sceneEntry in enabledScenes)
            {
                if (db != null && sceneEntry.SceneGuid != Guid.Empty)
                    db.Reimport(sceneEntry.SceneGuid);
            }

            progress?.Log("Start collecting assets...");

            // 2. Collect assets and ensure all caches exist
            // AssetBundle mode is collector-authoritative: pre-build cache/shader staging must use
            // the same explicit-root closure that the bundle writer uses. The legacy collector's
            // AllAssets option is intentionally ignored here so an unrelated project asset cannot
            // leak into the player inputs.
            var collection = CollectAssets(
                settings,
                progress,
                assetBundleOnly: options.PackagingMode == AssetPackagingMode.AssetBundle);
            progress?.Log($"Collected {collection.AllAssets.Count} assets, {collection.ResourcesMap.Count} resources.");

            // Reimport any collected assets missing their cache file
            progress?.Log("Verifying asset caches...", 0.15f);
            int reimported = 0;
            foreach (var guid in collection.AllAssets)
            {
                string cachePath = Path.Combine(project.CachePath, $"{guid}.asset");
                if (!File.Exists(cachePath))
                {
                    db?.Reimport(guid);
                    reimported++;
                }
            }
            if (reimported > 0)
                progress?.Log($"Reimported {reimported} assets with missing caches.");

            // 3. Clean and generate player source
            progress?.Log("Generating player...", 0.3f);
            string buildTempDir = project.BuildTempPath;
            if (Directory.Exists(buildTempDir))
                try { Directory.Delete(buildTempDir, true); } catch { }
            Directory.CreateDirectory(buildTempDir);

            if (!ShaderImporter.CompileBuiltInOfflineArtifacts(project))
                throw new InvalidOperationException("Failed to compile built-in offline shader artifacts.");
            // Desktop must collect runtime keyword combinations too; an import index only
            // contains individually authored variants and whatever the Editor happened to use.
            var runtimeVariants = VulkanShaderVariantCollector.CollectForBuild(collection.AllAssets);
            // macOS ships both Vulkan (MoltenVK, the M1 bring-up backend) and Metal (first-party
            // M4 milestone) variants; Windows/Linux keep the Vulkan+D3D12 pair. MetalLib emission
            // routes through Xcode's `metal` downstream compiler — degrade to Vulkan-only (with a
            // loud warning) on macOS hosts without the toolchain instead of failing the build.
            bool includeMetal = desktopProfile.Platform == BuildTarget.MacOS && HasMetalShaderCompiler();
            if (desktopProfile.Platform == BuildTarget.MacOS && !includeMetal)
                progress?.Log(
                    "[Build] Xcode's metal compiler not found — packaging Vulkan-only shader variants. "
                    + "Install Xcode 16+ for Metal artifacts.", Runtime.LogSeverity.Warning);
            var metalRuntimeVariants = includeMetal
                ? MetalShaderVariantCollector.CollectForBuild(collection.AllAssets)
                : null;
            var runtimeSelections = new List<ShaderArtifactBuildSelection>(
                runtimeVariants.Descriptors.Count * 2 + (metalRuntimeVariants?.Descriptors.Count ?? 0));
            foreach (var variant in runtimeVariants.Descriptors)
            {
                runtimeSelections.Add(new(variant.ShaderAssetGuid, variant.PassName,
                    GraphicsBackend.Vulkan, variant.CanonicalKeywordSet));
                runtimeSelections.Add(new(variant.ShaderAssetGuid, variant.PassName,
                    GraphicsBackend.Direct3D12, variant.CanonicalKeywordSet));
            }
            if (metalRuntimeVariants != null)
            {
                foreach (var variant in metalRuntimeVariants.Descriptors)
                    runtimeSelections.Add(new(variant.ShaderAssetGuid, variant.PassName,
                        GraphicsBackend.Metal, variant.CanonicalKeywordSet));
            }
            if (!ShaderImporter.CompileArtifactSelections(project, runtimeSelections))
                throw new InvalidOperationException("Failed to compile Desktop runtime shader variants.");
            string shaderPackageDir = Path.Combine(buildTempDir, "Shaders");
            // Import indexes can retain artifacts from other target environments (for example,
            // iOS Simulator Metal beside macOS Metal). Runtime lookup does not include that tier,
            // so package exactly the selections compiled for this desktop build.
            ShaderArtifactBuildPackage shaderPackage = ShaderArtifactBuildPackager.Prepare(
                project,
                collection.AllAssets,
                shaderPackageDir,
                selections: runtimeSelections);
            progress?.Log(
                $"Prepared {shaderPackage.ArtifactCount} shader artifacts for " +
                $"{shaderPackage.ShaderAssetCount} shader assets ({shaderPackage.ArtifactBytes} bytes).",
                0.32f);

            Guid defaultScene = enabledScenes[0].SceneGuid;
            IReadOnlyDictionary<Guid, byte[]> textureOverrides = BuildTexturePayloadDowngrader.CreateOverrides(
                project,
                collection.AllAssets,
                BuildTexturePayloadDowngrader.ResolveSettings(settings, desktopProfile.Platform));

            // For embedded mode, copy assets to temp dir before csproj generation
            // so they can be included as EmbeddedResource
            List<string>? embeddedAssetPaths = null;
            if (options.PackagingMode == AssetPackagingMode.Embedded)
            {
                progress?.Log("Preparing embedded assets...", 0.35f);
                string embeddedDir = Path.Combine(buildTempDir, "Assets");
                Directory.CreateDirectory(embeddedDir);
                CopyLooseAssets(collection.AllAssets, embeddedDir, progress, textureOverrides);
                GenerateManifest(Path.Combine(embeddedDir, "asset_manifest.bin"),
                    collection.AllAssets, collection.ResourcesMap, defaultScene);

                // Collect paths for csproj
                embeddedAssetPaths = new();
                foreach (var f in Directory.EnumerateFiles(embeddedDir, "*.*", SearchOption.AllDirectories))
                    embeddedAssetPaths.Add(f);
                foreach (var f in Directory.EnumerateFiles(shaderPackageDir, "*.*", SearchOption.AllDirectories))
                    embeddedAssetPaths.Add(f);
            }

            GeneratePlayerSource(project, settings, desktopProfile, defaultScene, buildTempDir,
                buildAssemblies.Select(b => b.Name).ToList(), options.PackagingMode);
            GeneratePlayerCsproj(project, settings, desktopProfile, buildTempDir, embeddedAssetPaths);
            progress?.Log("Generated player source and project.");

            // 4. Compile

            progress?.Log("Compiling player...", 0.7f);

            string csprojPath = Path.Combine(buildTempDir, $"{project.Name}.Player.csproj");

            var args = new StringBuilder();
            args.Append($"publish \"{csprojPath}\"");
            args.Append($" -c {options.Config.ToString()}");
            args.Append($" -r {desktopProfile.RuntimeIdentifier}");
            args.Append($" -o \"{outputDirectory}\"");
            args.Append($" --self-contained {desktopProfile.SelfContained.ToString().ToLowerInvariant()}");

            var (exitCode, stdout, stderr) = await RunDotnetAsync(args.ToString(),
                progress,
                cancellation).ConfigureAwait(false);

            progress?.Log(stdout);
            if (!string.IsNullOrEmpty(stderr))
                progress?.Log(stderr);

            if (exitCode != 0)
            {
                ScriptCompiler.LogBuildOutput(stdout, stderr);
                return new BuildResult
                {
                    Success = false,
                    OutputPath = outputDirectory,
                    Log = progress?.ToString(LogSeverity.Normal),
                    Errors = progress?.ToString(LogSeverity.Error),
                    Duration = sw.Elapsed,
                    AssetCount = 0
                };
            }

            // 4b. Copy pre-compiled user game assemblies (default + asmdef) into the output root.
            var userAssemblyFiles = new List<string>();
            foreach (var asm in buildAssemblies)
            {
                if (!File.Exists(asm.DllPath))
                {
                    Runtime.Debug.LogWarning($"[Build] Expected user assembly missing: {asm.DllPath}");
                    continue;
                }
                string destDll = Path.Combine(outputDirectory, Path.GetFileName(asm.DllPath));
                File.Copy(asm.DllPath, destDll, true);
                userAssemblyFiles.Add(Path.GetFileName(asm.DllPath));

                string pdb = Path.ChangeExtension(asm.DllPath, ".pdb");
                if (File.Exists(pdb))
                {
                    File.Copy(pdb, Path.Combine(outputDirectory, Path.GetFileName(pdb)), true);
                    userAssemblyFiles.Add(Path.GetFileName(pdb));
                }
                progress?.Log($"Copied game assembly: {Path.GetFileName(asm.DllPath)}");
            }

            // 4c. Copy managed plugins (to runtimes/, where the managed resolver probes) and native
            //     plugins (to runtimes/{rid}/native, where the native resolver probes), filtered to
            //     this platform and excluding editor-only plugins.
            CopyPlugins(project, outputDirectory, targetPlatform, progress);

            // 4d. Organize assemblies - move dependency DLLs to runtimes/ subfolder
            OrganizePublishOutput(outputDirectory, project.Name, userAssemblyFiles);

            // 5. Package assets AFTER publish (publish may clean the output dir)
            // For embedded mode, assets were already baked into the assembly at compile time
            progress?.Log("Packaging assets...", 0.8f);
            int assetCount = collection.AllAssets.Count;
            if (options.PackagingMode != AssetPackagingMode.Embedded)
            {
                Directory.CreateDirectory(contentDir);
                HashSet<Guid>? packedAssetGuids = options.PackagingMode == AssetPackagingMode.AssetBundle
                    ? new HashSet<Guid>()
                    : null;
                switch (options.PackagingMode)
                {
                    case AssetPackagingMode.XEnginePak:
                        assetCount = PackAssets(collection.AllAssets, contentDir, options.MaxPakSizeMB, progress, textureOverrides);
                        break;
                    case AssetPackagingMode.LooseFiles:
                        assetCount = CopyLooseAssets(collection.AllAssets, contentDir, progress, textureOverrides);
                        break;
                    case AssetPackagingMode.AssetBundle:
                        // Build every collector package into Content/{packageName}/. Each package
                        // writes its own version/manifest/bundle closure; no loose-cache manifest or
                        // project-wide asset archive is emitted in AB-only mode.
                        assetCount = PackAssetBundles(
                            desktopProfile.Platform,
                            contentDir,
                            progress,
                            textureOverrides,
                            packedAssetGuids);
                if (packedAssetGuids != null && packedAssetGuids.Count != collection.AllAssets.Count)
                {
                    throw new InvalidOperationException(
                        $"AssetBundle collector closure changed between preflight and write " +
                        $"({collection.AllAssets.Count} preflight assets, {packedAssetGuids.Count} written).");
                }
                break;
            }

            // Video sources stay streaming payloads outside the cache assets: copy each
            // selected VideoClip's source media to Content/payload/{guid}{ext}, addressed
            // by GUID at runtime (VideoClip.TryGetPayloadPath) — never an absolute path.
            if (options.PackagingMode != AssetPackagingMode.Embedded)
            {
                CopyVideoPayloads(collection.AllAssets, contentDir, progress);
            }
            if (options.PackagingMode != AssetPackagingMode.AssetBundle)
                    GenerateManifest(Path.Combine(contentDir, "asset_manifest.bin"),
                        collection.AllAssets, collection.ResourcesMap, defaultScene);
                ShaderArtifactBuildPackager.CopyPreparedPackage(
                    shaderPackageDir,
                    Path.Combine(contentDir, "Shaders"));
            }
            progress?.Log($"Packaged {assetCount} assets ({options.PackagingMode}).");

            // 6. Export settings
            ExportSettings(settingsDir, progress);
            progress?.Log("Exported project settings.");

            // 7. Copy native libraries from the engine runtime payload. The editor can run with its
            //    managed assemblies directly under the app root or under runtimes/, so resolve both
            //    layouts. The generated Player project references XEngine.Runtime and Silk assemblies
            //    as bare files; NuGet's runtime-native assets (including GLFW) therefore do not flow
            //    through dotnet publish and must be copied explicitly for this RID.
            progress?.Log("Copying native libraries...", 0.9f);
            string engineDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            int nativeLibrariesCopied = CopyEngineRuntimeNativeLibraries(
                engineDir,
                outputDirectory,
                desktopProfile.RuntimeIdentifier);
            progress?.Log($"Copied {nativeLibrariesCopied} engine native runtime file(s) for {desktopProfile.RuntimeIdentifier}.");
            int removedShaderCompilerFiles = RemoveRuntimeShaderCompilerFiles(outputDirectory);
            progress?.Log(
                $"Removed {removedShaderCompilerFiles} Runtime shader compiler file(s) from the Player.");

            // 7b. macOS ships as a real .app bundle, not a bare folder of files.
            if (desktopProfile.Platform == BuildTarget.MacOS)
            {
                progress?.Log("Bundling macOS .app...", 0.92f);
                var general = EditorRegistries.GetSettings<GeneralSettings>();
                BundleMacApp(outputDirectory, project.Name, general.ProductName, general.CompanyName, general.Version);
            }

            // 8. Clean temp
            if (Directory.Exists(buildTempDir))
                try { Directory.Delete(buildTempDir, true); } catch { }

            // Done
            progress?.Log("Build complete!", 1.0f);
            sw.Stop();

            Runtime.Debug.Log($"[Build] Desktop build completed in {sw.Elapsed.TotalSeconds:F1}s -> {outputDirectory}");

            return new BuildResult
            {
                Success = true,
                OutputPath = outputDirectory,
                Log = progress?.ToString(),
                Duration = sw.Elapsed,
                AssetCount = assetCount
            };
        }
        catch (Exception ex)
        {
            return new BuildResult
            {
                Success = false,
                Errors = ex.ToString(),
                Duration = sw.Elapsed
            };
        }
    }

    private void GeneratePlayerSource(Project project, BuildSettings settings, DesktopBuildProfile desktopProfile, Guid defaultSceneGuid, string outputDir, List<string> gameAssemblyNames, AssetPackagingMode packagingMode)
    {
        CopyDefaultDesktopBranding(outputDir);
        string productName = "XEngine";
        try { productName = EditorRegistries.GetSettings<GeneralSettings>().ProductName; } catch { }
        if (string.IsNullOrWhiteSpace(productName)) productName = "XEngine";

        // C# array literal of user game assemblies to load at startup (dependency order).
        string gameAssembliesLiteral = string.Join(", ", gameAssemblyNames.Select(n => $"\"{n}\""));

        string assetBackendInitialization = packagingMode == AssetPackagingMode.AssetBundle
            ? $$"""
                     // AssetBundle players are backed by the package/manifest layer. Discover only
                     // directories carrying their canonical <package>.version marker; Content also
                     // contains Shaders/ and Settings/, neither of which is an AB package root.
                     var __contentRoot = Path.Combine(XEngine.Runtime.Application.DataPath, "Content");
                     var __bundlePackageRoots = new List<string>();
                     if (Directory.Exists(__contentRoot))
                     {
                         foreach (var __packageDir in Directory.EnumerateDirectories(
                             __contentRoot, "*", SearchOption.TopDirectoryOnly))
                         {
                             string __packageName = Path.GetFileName(__packageDir);
                             if (string.IsNullOrEmpty(__packageName)) continue;
                             if (!File.Exists(Path.Combine(__packageDir, __packageName + ".version"))) continue;
                             __bundlePackageRoots.Add(Path.GetRelativePath(
                                 XEngine.Runtime.Application.DataPath, __packageDir).Replace('\\', '/'));
                         }
                     }
                     __bundlePackageRoots.Sort(StringComparer.Ordinal);
                     var db = XEngine.Runtime.BundleAssetBackend.CreateFromProvider(
                         new XEngine.Runtime.AssetStreaming.DirectoryAssetStreamProvider(
                             XEngine.Runtime.Application.DataPath),
                         __bundlePackageRoots,
                         new Guid("{{defaultSceneGuid:D}}"),
                         XEngine.Runtime.AssetBundles.AssetBundlePlatform.{{desktopProfile.Platform}})
                         ?? throw new InvalidDataException(
                             "AssetBundle player has no package roots under Content/. Ensure the startup scene " +
                             "and runtime assets are assigned to an AssetBundle collector.");
            """
            : $$"""
                     var db = new XEngine.Runtime.PlayerAssetBackend(
                         XEngine.Runtime.AssetPackagingMode.{{packagingMode}}, "Content");
            """;

        // Program.cs
        File.WriteAllText(Path.Combine(outputDir, "Program.cs"), $$"""
            using System;
            using System.IO;
            using System.Linq;
            using System.Collections;
            using System.Collections.Generic;
            using System.Reflection;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Runtime.Loader;

            // Configure the offline shader package before Game.Run creates PaperRenderer.
            // Packaged Players do not ship the runtime Slang compiler, so even the startup UI
            // shaders must resolve from the catalog on the first window-load callback.
            XEngine.Runtime.Application.IsPlaying = true;
            XEngine.Runtime.Application.IsEditor = false;
            XEngine.Runtime.Application.IsPlayerBuild = true;
            XEngine.Runtime.Application.DataPath = System.AppContext.BaseDirectory;
            XEngine.Runtime.RHI.Shaders.ShaderArtifactRuntimeLibrary.Configure(
                XEngine.Runtime.AssetPackagingMode.{{packagingMode}},
                "Content");

            var __game = new DesktopPlayer();
            var __args = Environment.GetCommandLineArgs();
            if (__args.Contains("--headless") || __args.Contains("-headless"))
            {
                var __opts = new XEngine.Runtime.HeadlessRunOptions();
                for (int __i = 0; __i < __args.Length - 1; __i++)
                {
                    if ((__args[__i] == "--frames" || __args[__i] == "--max-frames") && long.TryParse(__args[__i + 1], out var __f)) __opts.MaxFrames = __f;
                    else if ((__args[__i] == "--seconds" || __args[__i] == "--max-seconds") && double.TryParse(__args[__i + 1], System.Globalization.CultureInfo.InvariantCulture, out var __s)) __opts.MaxSeconds = __s;
                    else if (__args[__i] == "--fps" && int.TryParse(__args[__i + 1], out var __fps)) __opts.TargetFps = __fps;
                }
                __game.RunHeadless(__opts);
            }
            else
            {
                __game.Run("{{productName}}", {{desktopProfile.WindowWidth}}, {{desktopProfile.WindowHeight}});
            }

            internal static class ModuleInitializer
            {
                [ModuleInitializer]
                public static void Initialize()
                {
                    // 1. Managed assembly resolver
                    AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
                    {
                        string dllName = assemblyName.Name + ".dll";
                        string probePath = Path.Combine(AppContext.BaseDirectory, "runtimes", dllName);

                        // Log to file or debug output (Console may not be ready yet)
                        Log($"[Resolver] Probing: {probePath}");

                        if (File.Exists(probePath))
                        {
                            Log($"[Resolver] Found! Loading {probePath}");
                            return context.LoadFromAssemblyPath(probePath);
                        }
                            Log($"[Resolver] Not Found {probePath}!");
                        return null;
                    };

                    // 2. Native resolver (optional, but register early as well)
                    NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveNativeLibrary);
                    //NativeLibrary.SetDllImportResolver(typeof(XEngine.Runtime.Game).Assembly, ResolveNativeLibrary);

                    AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, libraryName) =>
                    {
                        Log($"[Native] Global resolver: {libraryName} requested by {assembly?.GetName()?.Name ?? "unknown"}");

                        string baseDir = AppContext.BaseDirectory;
                        string rid = RuntimeInformation.RuntimeIdentifier;

                        // Build RID fallback chain
                        List<string> rids = new();
                        if (!string.IsNullOrEmpty(rid))
                        {
                            rids.Add(rid);
                            int dash = rid.IndexOf('-');
                            while (dash > 0)
                            {
                                rid = rid.Substring(0, dash);
                                rids.Add(rid);
                                dash = rid.IndexOf('-');
                            }
                        }
                        rids.Add("any");
                        rids.Add("");

                        string[] exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                            ? new[] { ".dll", "" }
                            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                                ? new[] { ".dylib", "" }
                                : new[] { ".so", ".so.1", "" };

                        string[] nameVariants = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                            ? new[] { libraryName }
                            : new[] { libraryName, "lib" + libraryName };

                        foreach (var r in rids)
                        {
                            string nativeSubDir = string.IsNullOrEmpty(r)
                                ? Path.Combine("runtimes", "native")
                                : Path.Combine("runtimes", r, "native");

                            foreach (var name in nameVariants)
                            foreach (var ext in exts)
                            {
                                string fullPath = Path.Combine(baseDir, nativeSubDir, name + ext);
                                Log($"[Native] Probing: {fullPath}");

                                if (File.Exists(fullPath))
                                {
                                    if (NativeLibrary.TryLoad(fullPath, out IntPtr handle))
                                    {
                                        Log($"[Native] SUCCESS: {fullPath}");
                                        return handle;
                                    }
                                    else
                                    {
                                        Log($"[Native] TryLoad FAILED (missing dependency?): {fullPath}");
                                    }
                                }
                            }
                        }

                        // Fallback to root directory
                        foreach (var name in nameVariants)
                        foreach (var ext in exts)
                        {
                            string rootPath = Path.Combine(baseDir, name + ext);
                            Log($"[Native] Root probe: {rootPath}");
                            if (File.Exists(rootPath) && NativeLibrary.TryLoad(rootPath, out IntPtr handle))
                            {
                                Log($"[Native] SUCCESS from root: {rootPath}");
                                return handle;
                            }
                        }

                        Log($"[Native] NOT FOUND: {libraryName}");
                        return IntPtr.Zero;
                    };
                }

                public static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
                {
                    Log($"[Native] Resolving: {libraryName} for {assembly.GetName().Name}");

                    string baseDir = AppContext.BaseDirectory;
                    string rid = RuntimeInformation.RuntimeIdentifier;

                    // Build RID fallback chain
                    List<string> rids = new();
                    if (!string.IsNullOrEmpty(rid))
                    {
                        rids.Add(rid);
                        int dash = rid.IndexOf('-');
                        while (dash > 0)
                        {
                            rid = rid.Substring(0, dash);
                            rids.Add(rid);
                            dash = rid.IndexOf('-');
                        }
                    }
                    rids.Add("any");
                    rids.Add("");

                    string[] exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? new[] { ".dll", "" }
                        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                            ? new[] { ".dylib", "" }
                            : new[] { ".so", ".so.1", "" };

                    string[] nameVariants = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? new[] { libraryName }
                        : new[] { libraryName, "lib" + libraryName };

                    foreach (var r in rids)
                    {
                        string nativeSubDir = string.IsNullOrEmpty(r)
                            ? Path.Combine("runtimes", "native")
                            : Path.Combine("runtimes", r, "native");

                        foreach (var name in nameVariants)
                        foreach (var ext in exts)
                        {
                            string fullPath = Path.Combine(baseDir, nativeSubDir, name + ext);
                            Log($"[Native] Probing: {fullPath}");

                            if (File.Exists(fullPath))
                            {
                                if (NativeLibrary.TryLoad(fullPath, out IntPtr handle))
                                {
                                    Log($"[Native] SUCCESS: {fullPath}");
                                    return handle;
                                }
                                else
                                {
                                    Log($"[Native] TryLoad FAILED (missing dependency?): {fullPath}");
                                }
                            }
                        }
                    }

                    // Last chance: root directory
                    foreach (var name in nameVariants)
                    foreach (var ext in exts)
                    {
                        string rootPath = Path.Combine(baseDir, name + ext);
                        if (File.Exists(rootPath) && NativeLibrary.TryLoad(rootPath, out IntPtr handle))
                        {
                            Log($"[Native] SUCCESS from root: {rootPath}");
                            return handle;
                        }
                    }

                    Log($"[Native] NOT FOUND: {libraryName}");
                    return IntPtr.Zero;
                }

                private static void Log(string message)
                {
                    // Diagnostics are opt-in and must not mutate a signed .app bundle.
                    string? logPath = Environment.GetEnvironmentVariable("XENGINE_NATIVE_RESOLVER_LOG");
                    if (string.IsNullOrWhiteSpace(logPath))
                        return;

                    try
                    {
                        string? directory = Path.GetDirectoryName(logPath);
                        if (!string.IsNullOrEmpty(directory))
                            Directory.CreateDirectory(directory);
                        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
                    }
                    catch
                    {
                        // Resolver diagnostics must never break native or managed assembly loading.
                    }
                }
            }

            class DesktopPlayer : XEngine.Runtime.Game
            {
                private XEngine.Runtime.PlayerAssetBackendBase? _assetDb;

                public override void Initialize()
                {
                    XEngine.Runtime.Application.IsPlaying = true;
                    XEngine.Runtime.Application.IsEditor = false;
                    XEngine.Runtime.Application.IsPlayerBuild = true;
                    XEngine.Runtime.Application.DataPath = System.AppContext.BaseDirectory;

                    NativeLibrary.SetDllImportResolver(typeof(XEngine.Runtime.Game).Assembly, ModuleInitializer.ResolveNativeLibrary);

                    // Load user game script assemblies (default + asmdef-defined) in dependency order.
                    foreach (var __asmName in new string[] { {{gameAssembliesLiteral}} })
                    {
                        string __asmPath = Path.Combine(XEngine.Runtime.Application.DataPath, __asmName + ".dll");
                        if (File.Exists(__asmPath))
                            Assembly.LoadFrom(__asmPath);
                    }

                    // Initialize built-in assets BEFORE loading any scenes/assets
                    XEngine.Runtime.BuiltInAssets.Initialize();

                     // Initialize the selected asset database. AB mode uses package manifests and
                     // never falls back to the loose/pak cache backend.
                     {{assetBackendInitialization}}
                     XEngine.Runtime.AssetDatabase.Current = db;
                    XEngine.Runtime.GameResources.Initialize(db.ResourcesMap);
                    _assetDb = db;

                    // Apply the async-loading toggle before the scene loads (component OnEnable may resolve AssetRefs).
                    XEngine.Runtime.PlayerSettingsLoader.ApplyAssetConfig(Path.Combine(XEngine.Runtime.Application.DataPath, "Content", "Settings"));

                    // Load default scene
                    var scene = db.LoadScene(Guid.Parse("{{defaultSceneGuid}}"));
                    if (scene != null)
                        XEngine.Runtime.Resources.Scene.Load(scene);
                    else
                        XEngine.Runtime.Debug.LogError("Failed to load default scene.");

                    // Apply project settings (physics needs scene loaded first)
                    XEngine.Runtime.PlayerSettingsLoader.Apply(Path.Combine(XEngine.Runtime.Application.DataPath, "Content", "Settings"));

                }

                public override void OnUpdate(XEngine.Runtime.Resources.Scene? scene)
                {
                    _assetDb?.TickIdleSweep();
                    scene?.Update();
                }

                public override void OnRender(XEngine.Runtime.Resources.Scene? scene) => scene?.Render();
                public override void OnGui(XEngine.Runtime.Resources.Scene? scene, XEngine.PaperUI.Paper paper) => scene?.OnGui(paper);
            }
            """);
    }

    private void GeneratePlayerCsproj(Project project, BuildSettings settings, DesktopBuildProfile desktopProfile, string outputDir, List<string>? embeddedAssetPaths = null)
    {
        // Relative references below are computed against outputDir; normalize the macOS /var
        // symlink prefix first so csc's lexical "../" resolution matches the real location.
        outputDir = ScriptCompiler.NormalizeMacSymlinkPrefix(outputDir);
        string engineDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string runtimeDll = Path.Combine(engineDir, "XEngine.Runtime.dll");
        string versionDefine = ScriptCompiler.GetVersionDefine();

        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <OutputType>Exe</OutputType>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine($"    <AssemblyName>{project.Name}</AssemblyName>");
        sb.AppendLine($"    <DefineConstants>XENGINE;{versionDefine};{FinalizeDefineString(settings, this)}</DefineConstants>"); // NO XENGINE_EDITOR
        sb.AppendLine($"    <RuntimeIdentifier>{desktopProfile.RuntimeIdentifier}</RuntimeIdentifier>");
        sb.AppendLine("    <ApplicationIcon>xengine.ico</ApplicationIcon>");

        sb.AppendLine("    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>");

        if (desktopProfile.SelfContained)
        {
            sb.AppendLine("    <SelfContained>true</SelfContained>");

        }
        sb.AppendLine("  </PropertyGroup>");

        // PublishTrimmed should still be regarded as experimental.
        // Using Partial as TrimMode should keep the engine from trimming the wrong assemblies,
        // But at this time it's unknown if it breaks anything with different build configurations
        // Forcing TrimmerRootAssembly to XEngine.Runtime prevents unexpected trimming from core assembly as well.
        if (desktopProfile.PublishTrimmed)
        {
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <PublishTrimmed>true</PublishTrimmed>");
            sb.AppendLine("    <TrimMode>partial</TrimMode>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("     <TrimmerRootAssembly Include=\"XEngine.Runtime\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        // Reference XEngine.Runtime Private=true forces fresh copy from HintPath
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <Reference Include=\"XEngine.Runtime\">");
        sb.AppendLine($"      <HintPath>{runtimeDll}</HintPath>");
        sb.AppendLine("      <Private>true</Private>");
        sb.AppendLine("      <SpecificVersion>false</SpecificVersion>");
        sb.AppendLine("    </Reference>");

        // Bare <Reference> items do NOT carry XEngine.Runtime's NuGet dependencies, so publish
        // would otherwise drop the Silk assemblies. Silk.NET.Windowing.Glfw is what registers the
        // desktop window platform (module initializer) — without it the player dies with
        // PlatformNotSupportedException at Window.Create. Reference the full engine-side set.
        foreach (string silkDll in Directory.GetFiles(engineDir, "Silk.NET.*.dll"))
        {
            string silkName = Path.GetFileNameWithoutExtension(silkDll);
            sb.AppendLine($"    <Reference Include=\"{silkName}\">");
            sb.AppendLine($"      <HintPath>{silkDll}</HintPath>");
            sb.AppendLine("      <Private>true</Private>");
            sb.AppendLine("      <SpecificVersion>false</SpecificVersion>");
            sb.AppendLine("    </Reference>");
        }
        sb.AppendLine("  </ItemGroup>");

        ListDependencies(sb);

        // User NuGet packages, project references, etc. are pulled from the project's
        // Directory.Build.props. This player csproj lives under Temp/ inside the project root, so
        // MSBuild auto-imports that file; packages in its default (non editor scoped) group are
        // restored and bundled by publish, matching the pre-compiled user assemblies.

        // Compile items just the generated Program.cs (user scripts are a separate pre-compiled DLL)
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <Compile Include=\"Program.cs\" />");
        sb.AppendLine("    <EmbeddedResource Include=\"xengine-icon.rgba\"><LogicalName>xengine-icon.rgba</LogicalName></EmbeddedResource>");
        sb.AppendLine("  </ItemGroup>");

        // Embedded assets (for Embedded packaging mode)
        if (embeddedAssetPaths != null && embeddedAssetPaths.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var assetPath in embeddedAssetPaths)
            {
                string rel = Path.GetRelativePath(outputDir, assetPath).Replace('\\', '/');
                // LogicalName determines the resource name used by Assembly.GetManifestResourceStream
                string logicalName = GetEmbeddedResourceLogicalName(outputDir, assetPath);

                sb.AppendLine($"    <EmbeddedResource Include=\"{rel}\">");
                sb.AppendLine($"      <LogicalName>{logicalName}</LogicalName>");
                sb.AppendLine("    </EmbeddedResource>");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine("</Project>");

        File.WriteAllText(Path.Combine(outputDir, $"{project.Name}.Player.csproj"), sb.ToString());
    }

    private static void CopyDefaultDesktopBranding(string outputDirectory)
    {
        Assembly assembly = typeof(DesktopBuildPipeline).Assembly;
        CopyEmbeddedBrandingFile(assembly, "XEngine.Editor.Resources.xengine.ico", Path.Combine(outputDirectory, "xengine.ico"));
        CopyEmbeddedBrandingFile(assembly, "XEngine.Editor.Resources.xengine-icon.rgba", Path.Combine(outputDirectory, "xengine-icon.rgba"));
    }

    private static void CopyEmbeddedBrandingFile(Assembly assembly, string resourceName, string destination)
    {
        using Stream? source = assembly.GetManifestResourceStream(resourceName);
        if (source == null)
            throw new InvalidOperationException($"Missing embedded branding resource '{resourceName}'.");
        using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(output);
    }

    internal static string GetEmbeddedResourceLogicalName(string buildTempDirectory, string resourcePath)
    {
        string relativePath = Path.GetRelativePath(buildTempDirectory, resourcePath).Replace('\\', '/');
        if (string.Equals(relativePath, "Assets/asset_manifest.bin", StringComparison.OrdinalIgnoreCase))
            return "Assets._manifest.bin";
        if (relativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase))
        {
            return relativePath.Replace('/', '.');
        }
        throw new InvalidOperationException($"Embedded player resource is outside a supported package: {relativePath}");
    }

    public void ListDependencies(StringBuilder sb)
    {
        sb.AppendLine("  <ItemGroup>");
        foreach (var asmName in GetVendoredDependencyNames())
        {
            string dllPath = Path.Combine(Path.GetDirectoryName(typeof(XEngine.Runtime.EngineObject).Assembly.Location)!, asmName + ".dll");
            // HintPath mirrors the XEngine.Runtime <Reference> above: an absolute path into the
            // engine output directory. Private=true forces a fresh copy into the player output.
            sb.AppendLine($"    <Reference Include=\"{asmName}\">");
            sb.AppendLine($"      <HintPath>{dllPath}</HintPath>");
            sb.AppendLine("      <Private>true</Private>");
            sb.AppendLine("      <SpecificVersion>false</SpecificVersion>");
            sb.AppendLine("    </Reference>");
        }
        sb.AppendLine("  </ItemGroup>");
    }

    /// <summary>
    /// Names of the vendored Anthology assemblies (Clay, Echo, Paper, Vector, ...) that the
    /// engine now consumes via ProjectReference to ThirdParty/. These compile alongside
    /// XEngine.Runtime and land as sibling DLLs in the engine output directory; the player
    /// build references each one explicitly so type resolution succeeds at runtime.
    /// </summary>
    private static IReadOnlyList<string> GetVendoredDependencyNames()
    {
        // Anthology library assembly names. These match the csproj AssemblyName (derived from
        // each package's directory: Clay/, Echo/, ...). Note the assembly names do NOT carry
        // the "XEngine." prefix (that's only the NuGet PackageId, unused now that they are vendored).
        return new[]
        {
            "Vector", "Scribe", "Quill", "Paper",
            "Echo", "Origami",
            "Clay", "Photonic", "Unwrapper", "Rosetta",
        };
    }

    private static bool? _metalCompilerAvailable;

    /// <summary>True when Xcode's Metal toolchain can compile and link a minimal library.</summary>
    internal static bool HasMetalShaderCompiler()
    {
        if (_metalCompilerAvailable.HasValue)
            return _metalCompilerAvailable.Value;
        bool available = MacMetalToolchain.TryPrepare(out string diagnostic);
        if (!available)
            Runtime.Debug.LogWarning(diagnostic);
        _metalCompilerAvailable = available;
        return available;
    }

    public override string GetExecutablePath(string outputPath, BuildSettings settings)
    {
        var profile = settings.GetProfile<DesktopBuildProfile>(GetType());
        if (profile.Platform == BuildTarget.MacOS)
        {
            // BundleMacApp moved the payload into "<ProductName>.app/Contents/MacOS"; the
            // launcher executable lives inside the bundle (bundle name follows ProductName).
            string productName = Project.Current.Name;
            try { productName = EditorRegistries.GetSettings<GeneralSettings>().ProductName; } catch { }
            string bundle = Path.Combine(outputPath, productName + ".app",
                "Contents", "MacOS", Project.Current.Name);
            if (File.Exists(bundle))
                return bundle;
            return Path.Combine(outputPath, Project.Current.Name);
        }
        string exe = Path.Combine(outputPath,
            Project.Current!.Name + (profile.Platform == BuildTarget.Windows ? ".exe" : ""));
        return exe;
    }

    /// <summary>
    /// Whether the build may use the resolved output directory. The build never deletes files, so the
    /// directory must be specified (non-empty path) and either not yet exist or be empty. This makes
    /// clobbering the project root, Assets/, or any existing data impossible.
    /// </summary>
    public static bool IsUsableOutputDirectory(string rawOutput, string resolvedOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return false;
        if (!Directory.Exists(resolvedOutput))
            return true; // will be created
        return !Directory.EnumerateFileSystemEntries(resolvedOutput).Any();
    }

    /// <summary>
    /// Copies project plugins into the player output: managed plugins go to <c>runtimes/</c> (probed
    /// by the managed assembly resolver), native plugins go to <c>runtimes/{rid}/native</c> (probed by
    /// the native resolver). Editor-only plugins and plugins not targeting this platform are skipped.
    /// </summary>
    /// <summary>
    /// Copies video source media for selected VideoClip assets into
    /// <c>Content/payload/{guid}{ext}</c>. Sources are addressed by GUID at runtime
    /// (never by absolute path); media bytes stay outside the cache assets and paks.
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
            progress?.Log($"Copied {count} video payload(s) to Content/payload.");
        return count;
    }

    private static void CopyPlugins(Project project, string outputDir, string platform, BuildProgress? progress)
    {
        string runtimesDir = Path.Combine(outputDir, "runtimes");
        int managed = 0, native = 0;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in PluginScanner.ScanAll(project))
        {
            if (!plugin.AppliesToBuild(platform)) continue;

            string dest;
            if (plugin.IsManaged)
            {
                // The player's managed resolver probes runtimes/{assemblyName}.dll - the CLR identity
                // recorded in referencing assemblies, which is not necessarily the file name on disk.
                string asmName;
                try { asmName = AssemblyName.GetAssemblyName(plugin.AbsolutePath).Name ?? Path.GetFileNameWithoutExtension(plugin.FileName); }
                catch { asmName = Path.GetFileNameWithoutExtension(plugin.FileName); }

                Directory.CreateDirectory(runtimesDir);
                dest = Path.Combine(runtimesDir, asmName + ".dll");
                managed++;
            }
            else
            {
                string nativeDir = Path.Combine(runtimesDir, plugin.RuntimeIdentifierFor(platform), "native");
                Directory.CreateDirectory(nativeDir);
                dest = Path.Combine(nativeDir, plugin.FileName);
                native++;
            }

            if (!written.Add(dest))
                Runtime.Debug.LogWarning($"[Build] Plugin '{plugin.FileName}' overwrites '{Path.GetFileName(dest)}' (another plugin maps to the same destination).");
            File.Copy(plugin.AbsolutePath, dest, true);
        }

        if (managed + native > 0)
            progress?.Log($"Copied {managed} managed and {native} native plugin(s).");
    }

    /// <summary>
    /// Copies shared runtime payload files and native libraries for one RID from the editor's
    /// runtime directory to a Player output. Handles both an editor rooted at <c>.../Contents/MacOS</c>
    /// and one whose executing assembly lives under <c>.../Contents/MacOS/runtimes</c>.
    /// </summary>
    internal static int CopyEngineRuntimeNativeLibraries(
        string engineAssemblyDirectory,
        string outputDirectory,
        string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineAssemblyDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        string nestedRuntimesDirectory = Path.Combine(engineAssemblyDirectory, "runtimes");
        bool assemblyDirectoryIsRuntimes = string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(engineAssemblyDirectory)),
            "runtimes",
            StringComparison.OrdinalIgnoreCase);
        string engineRuntimesDirectory = Directory.Exists(nestedRuntimesDirectory)
            ? nestedRuntimesDirectory
            : assemblyDirectoryIsRuntimes && Directory.Exists(engineAssemblyDirectory)
                ? engineAssemblyDirectory
                : nestedRuntimesDirectory;

        string outputRuntimesDirectory = Path.Combine(outputDirectory, "runtimes");
        string engineRootDirectory = assemblyDirectoryIsRuntimes
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(engineAssemblyDirectory))!
            : engineAssemblyDirectory;
        int copied = CopyPublishedRuntimeNativeLibraries(engineRootDirectory, outputDirectory, runtimeIdentifier);
        if (!Directory.Exists(engineRuntimesDirectory))
            return copied;

        // Keep shared notices/readmes alongside the runtime payload. Managed assemblies are
        // already copied by publish/OrganizePublishOutput and should not be duplicated here.
        foreach (string file in Directory.EnumerateFiles(engineRuntimesDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(file), ".pdb", StringComparison.OrdinalIgnoreCase))
                continue;

            string destination = Path.Combine(outputRuntimesDirectory, Path.GetFileName(file));
            if (File.Exists(destination))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            copied++;
        }

        string sourceNativeDirectory = Path.Combine(engineRuntimesDirectory, runtimeIdentifier, "native");
        if (!Directory.Exists(sourceNativeDirectory))
            return copied;

        foreach (string file in Directory.EnumerateFiles(sourceNativeDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceNativeDirectory, file);
            string destination = Path.Combine(outputRuntimesDirectory, runtimeIdentifier, "native", relative);
            if (File.Exists(destination))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            copied++;
        }

        return copied;
    }

    // RID-specific publish flattens GLFW/OpenAL and Magick.NET natives into the editor root. Bare
    // assembly references in the generated Player do not propagate those native assets.
    // The deps runtime target proves which RID the flattened payload belongs to; cross-builds
    // must continue using runtimes/<target-rid>/native rather than borrowing the host libraries.
    private static int CopyPublishedRuntimeNativeLibraries(string engineRoot, string outputDirectory, string runtimeIdentifier)
    {
        string dependencies = Path.Combine(engineRoot, "XEngine.Editor.deps.json");
        if (!File.Exists(dependencies)) return 0;
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(dependencies));
        if (!document.RootElement.TryGetProperty("runtimeTarget", out var target)
            || !target.TryGetProperty("name", out var name)
            || !string.Equals(name.GetString()?.Split('/')[^1], runtimeIdentifier, StringComparison.Ordinal))
            return 0;

        string[] libraries = runtimeIdentifier switch
        {
            "osx-arm64" => ["libglfw.3.dylib", "libopenal.dylib", "Magick.Native-Q8-arm64.dll.dylib"],
            "osx-x64" => ["libglfw.3.dylib", "libopenal.dylib", "Magick.Native-Q8-x64.dll.dylib"],
            "linux-x64" => ["libglfw.so.3", "libopenal.so.1", "Magick.Native-Q8-x64.dll.so"],
            "win-x64" => ["glfw3.dll", "soft_oal.dll", "OpenAL32.dll", "Magick.Native-Q8-x64.dll"],
            _ => [],
        };
        int copied = 0;
        foreach (string library in libraries)
        {
            string source = Path.Combine(engineRoot, library);
            string ridSource = Path.Combine(engineRoot, "runtimes", runtimeIdentifier, "native", library);
            string destination = Path.Combine(outputDirectory, library);
            string nestedDestination = Path.Combine(outputDirectory, "runtimes", runtimeIdentifier, "native", library);
            if (!File.Exists(source) || File.Exists(ridSource) || File.Exists(destination) || File.Exists(nestedDestination)) continue;
            Directory.CreateDirectory(outputDirectory);
            File.Copy(source, destination);
            copied++;
        }
        return copied;
    }

    /// <summary>
    /// Moves DLLs from the publish root into a runtimes/ subfolder,
    /// keeping only the player executable, game assembly, and core runtime in the root.
    /// </summary>
    private static void OrganizePublishOutput(string outputDir, string projectName, IEnumerable<string> userAssemblies)
    {
        string libsDir = Path.Combine(outputDir, "runtimes");
        Directory.CreateDirectory(libsDir);

        // Files that must remain in the root for the app to start
        var keepInRoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{projectName}.dll",
            $"{projectName}.exe",
            $"{projectName}.pdb",
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Runtime.InteropServices.dll",
            "System.Runtime.Loader.dll",
            "System.Runtime.dll",
            "XEngine.Runtime.dll",
            "XEngine.Runtime.pdb",
        };
        foreach (var name in userAssemblies)
            keepInRoot.Add(name);
        // Vendored Anthology libraries (Clay, Echo, ...) are referenced as bare <Reference>
        // items in the player csproj, so deps.json records them as "type: reference" with no
        // path. The .NET runtime resolves reference-type dependencies only from the app root,
        // so they must NOT be moved into runtimes/. (Package-style deps resolve from runtimes/.)
        foreach (var name in GetVendoredDependencyNames())
        {
            keepInRoot.Add(name + ".dll");
            keepInRoot.Add(name + ".pdb");
        }

        foreach (var file in Directory.GetFiles(outputDir, "*.dll"))
        {
            string fileName = Path.GetFileName(file);
            if (keepInRoot.Contains(fileName)) continue;

            // Skip native (unmanaged) DLLs - only move managed assemblies
            try { AssemblyName.GetAssemblyName(file); }
            catch (BadImageFormatException) { continue; }

            string dest = Path.Combine(libsDir, fileName);
            //Runtime.Debug.Log($"WillMove to: {dest}");
            File.Move(file, dest, true);

            // Also move corresponding PDB if present
            string pdbPath = Path.ChangeExtension(file, ".pdb");
            if (File.Exists(pdbPath))
            {
                string pdbDest = Path.Combine(libsDir, Path.GetFileName(pdbPath));
                File.Move(pdbPath, pdbDest, true);
            }
        }
    }

    internal static int RemoveRuntimeShaderCompilerFiles(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!Directory.Exists(outputDirectory))
            return 0;

        int removed = 0;
        foreach (string path in Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories))
        {
            if (!IsRuntimeShaderCompilerFile(Path.GetFileName(path)))
                continue;
            File.Delete(path);
            removed++;
        }
        return removed;
    }

    internal static bool IsRuntimeShaderCompilerFile(string fileName) =>
        fileName.StartsWith("XEngine.Slang.", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("slang-", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("libslang", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("slang.dll", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("dxcompiler.", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("dxil.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wraps a finished publish output into a real "ProductName.app" bundle: everything moves under
    /// Contents/MacOS unchanged (so relative lookups like "Content/Settings" keep working), plus a
    /// minimal Contents/Info.plist. Does NOT code-sign or notarize - Gatekeeper will still show an
    /// "unidentified developer" warning until the user right-clicks Open (or runs `xattr -cr`).
    /// </summary>
    private static void BundleMacApp(string outputDir, string executableName, string productName, string companyName, string version)
    {
        // Snapshot BEFORE creating the bundle folder so it never sweeps up itself.
        var existingEntries = Directory.GetFileSystemEntries(outputDir).ToList();

        string appDir = Path.Combine(outputDir, $"{productName}.app");
        string macOsDir = Path.Combine(appDir, "Contents", "MacOS");
        Directory.CreateDirectory(macOsDir);

        foreach (var entry in existingEntries)
        {
            string dest = Path.Combine(macOsDir, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                Directory.Move(entry, dest);
            else
                File.Move(entry, dest);
        }

        // Windows can't hold POSIX exec bits - a .app built there will need `chmod +x` once it
        // actually reaches a Mac.
        string exePath = Path.Combine(macOsDir, executableName);
        if (!OperatingSystem.IsWindows() && File.Exists(exePath))
        {
            try
            {
                UnixFileMode mode = File.GetUnixFileMode(exePath);
                File.SetUnixFileMode(exePath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch { /* best-effort */ }
        }

        string plist = CreateMacInfoPlist(executableName, productName, companyName, version);
        File.WriteAllText(Path.Combine(appDir, "Contents", "Info.plist"), plist);

        // Sign only after the bundle metadata and payload are final. Writing Info.plist after
        // codesign invalidates the resource seal and leaves an app that cannot pass verification.
        // Ad-hoc signing supports local Apple Silicon launches without a Developer ID; notarization
        // remains a separate distribution step.
        if (OperatingSystem.IsMacOS())
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/codesign",
                ArgumentList = { "--force", "--deep", "--sign", "-", appDir },
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start codesign for the macOS Player bundle.");
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("codesign timed out while signing the macOS Player bundle.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"codesign failed for the macOS Player bundle (exit code {process.ExitCode}).");
        }
    }

    internal static string CreateMacInfoPlist(string executableName, string productName, string companyName, string version)
    {
        string bundleId = $"com.{SanitizeBundleIdSegment(companyName)}.{SanitizeBundleIdSegment(productName)}";
        string displayName = System.Security.SecurityElement.Escape(productName)!;
        string executable = System.Security.SecurityElement.Escape(executableName)!;
        string buildVersion = System.Security.SecurityElement.Escape(version)!;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleName</key><string>{displayName}</string>
                <key>CFBundleDisplayName</key><string>{displayName}</string>
                <key>CFBundleExecutable</key><string>{executable}</string>
                <key>CFBundleIdentifier</key><string>{bundleId}</string>
                <key>CFBundlePackageType</key><string>APPL</string>
                <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
                <key>CFBundleVersion</key><string>{buildVersion}</string>
                <key>CFBundleShortVersionString</key><string>{buildVersion}</string>
                <key>NSHighResolutionCapable</key><true/>
            </dict>
            </plist>
            """;
    }

    // Apple bundle IDs allow ASCII alphanumerics, dots and hyphens. Unicode display names
    // retain a stable digest in the ID so different names do not collapse to the same fallback.
    private static string SanitizeBundleIdSegment(string s)
    {
        var characters = new StringBuilder(s.Length);
        bool nonAscii = false;
        foreach (char c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.') characters.Append(c);
            if (c > 127) nonAscii = true;
        }
        string cleaned = characters.Length > 0 ? characters.ToString() : "xenginegame";
        if (!nonAscii) return cleaned;
        byte[] digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return cleaned + "-" + Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
    }
}
