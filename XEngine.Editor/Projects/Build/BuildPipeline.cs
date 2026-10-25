using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Echo;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;

namespace XEngine.Editor.Build;

/// <summary>
/// Abstract base for platform-specific build pipelines.
/// Subclass for Desktop, Android, Web, Console, etc.
/// </summary>
public abstract class BuildPipeline
{
    // AssetBundle preflight and the later payload writer must observe the exact same collector
    // closure. Keep the assignment on this pipeline instance so an importer completing between
    // phases cannot change the bundle membership or the payload validation set.
    private Dictionary<XEngine.Editor.AssetBundles.Collector.AssetBundleCollectorPackage,
        XEngine.Editor.AssetBundles.BuildPipeline.BundleBuildCollection>? _precollectedAssetBundles;

    public abstract string DisplayName { get; }

    /// <summary>
    /// Stable, immutable identifier for this pipeline (e.g. <c>xengine.desktop</c>, <c>xengine.android</c>).
    /// Used to persist the active platform in <see cref="BuildSettings.ActivePipelineId"/> and to select
    /// the pipeline from the CLI (<c>--platform</c>). Must be unique across all pipeline types.
    /// </summary>
    public abstract string Id { get; }

    /// <summary>Glyph shown on the platform card in the Build window.</summary>
    public virtual string Icon => XEngine.Editor.Theming.EditorIcons.Desktop;

    public abstract string[] SupportedRuntimeIdentifiers { get; }

    /// <summary>Execute the full build. Override for platform-specific steps.</summary>
    public abstract BuildResult Build(BuildSettings settings, BuildProgress? progress);

    /// <summary>
    /// Executes a build with a Task for async status reporting back to the engine.
    /// </summary>
    /// <param name="projectPath">The path of the project to build</param>
    /// <param name="settings">The settings to use for the build</param>
    /// <param name="outputDirectory">The path for the build output. Can be null.</param>
    /// <param name="progress">The <see cref="BuildProgress"/> object that stores the build progress for UI updates. Can be null.</param>
    /// <param name="cancellation">The cancellation token to stop the build midway.</param>
    /// <returns></returns>
    public abstract Task<BuildResult> BuildAsync(
        string projectPath,
        BuildSettings settings,
        string? outputDirectory = null,
        BuildProgress? progress = null,
        CancellationToken cancellation = default);

    // ================================================================
    //  Shared utilities for all pipelines
    // ================================================================

    /// <summary>
    /// Collect assets based on the active profile's resolved scene list and export mode.
    /// When <paramref name="assetBundleOnly"/> is true, the returned set is the AB collector
    /// closure (explicit roots plus dependencies), not the legacy scene/Resources/all-assets walk.
    /// This is used by desktop AB builds before shader/cache preparation so uncollected project
    /// assets never leak into the player staging inputs.
    /// </summary>
    protected AssetCollector.CollectionResult CollectAssets(
        BuildSettings settings,
        BuildProgress? progress,
        bool assetBundleOnly = false)
    {
        progress?.Log("Collecting assets...", 0.1f);

        ResolvedBuildOptions options = ResolvedBuildOptions.From(settings);
        var sceneGuids = options.Scenes
            .Where(s => s.Enabled)
            .Select(s => s.SceneGuid)
            .Where(g => g != Guid.Empty)
            .ToList();

        if (assetBundleOnly)
            return CollectAssetBundleAssets(sceneGuids, progress);

        bool depsOnly = options.AssetMode == AssetExportMode.DependenciesOnly;
        return AssetCollector.Collect(sceneGuids, depsOnly, CollectSettingsAssetRoots(settings));
    }

    // ExportSettings serializes these same live instances under the active profile overrides.
    // Their AssetRefs are runtime entry points even when no scene refers to them (for example,
    // the default/quality render pipeline and its renderer). Keep the whole dependency closure.
    private static List<Guid> CollectSettingsAssetRoots(BuildSettings settings)
    {
        using var overrides = ProfileSettingsOverrideScope.Apply(
            BuildProfileRegistry.GetActive(settings)?.Profile);
        var roots = new HashSet<Guid>();
        foreach (var entry in EditorRegistries.SettingsEntries)
        {
            if (!entry.ExportToBuild) continue;
            var context = new Runtime.DependencySerializationContext();
            Serializer.Serialize(entry.Type, entry.Instance, context);
            foreach (var dependency in context.Dependencies)
                roots.Add(dependency.Guid);
        }
        return new List<Guid>(roots);
    }

    /// <summary>
    /// Resolve the configured AB collectors once and use their closure as the only desktop AB
    /// staging set. Build scenes must be explicit collector roots: silently relying on a scene being
    /// an incidental dependency produces a player whose default scene cannot be resolved by address.
    /// </summary>
    private AssetCollector.CollectionResult CollectAssetBundleAssets(
        List<Guid> sceneGuids,
        BuildProgress? progress)
    {
        var setting = EditorRegistries.GetSettings<XEngine.Editor.AssetBundles.Collector.AssetBundleCollectorSetting>();
        if (setting == null || setting.Packages.Count == 0)
        {
            throw new InvalidOperationException(
                "AssetBundle packaging requires at least one collector package. Add a package and " +
                "collect the startup scene explicitly before building the player.");
        }

        var selection = new XEngine.Editor.AssetBundles.Builder.AssetBundleAssetSelection();
        _precollectedAssetBundles = new Dictionary<
            XEngine.Editor.AssetBundles.Collector.AssetBundleCollectorPackage,
            XEngine.Editor.AssetBundles.BuildPipeline.BundleBuildCollection>();
        foreach (var package in setting.Packages)
        {
            var collection = XEngine.Editor.AssetBundles.Builder.AssetBundleBuilder
                .CollectBuildCollection(package);
            _precollectedAssetBundles[package] = collection;
            selection.RootAssetGuids.UnionWith(collection.ExplicitRootAssets);
            selection.AssetGuids.UnionWith(collection.AssetToBundle.Keys);
        }
        if (selection.AssetGuids.Count == 0)
        {
            throw new InvalidOperationException(
                "AssetBundle packaging produced no assets. Add collectors for the startup scene " +
                "and its runtime content before building the player.");
        }

        var db = EditorAssetBackend.Instance;
        var missingScenes = new List<string>();
        for (int i = 0; i < sceneGuids.Count; i++)
        {
            Guid sceneGuid = sceneGuids[i];
            if (selection.RootAssetGuids.Contains(sceneGuid))
                continue;

            string path = db?.GuidToPath(sceneGuid) ?? sceneGuid.ToString();
            missingScenes.Add($"'{path}' ({sceneGuid:D})");
        }

        if (missingScenes.Count > 0)
        {
            throw new InvalidOperationException(
                "AssetBundle packaging requires every enabled build scene to be an explicit " +
                "collector root; add collectors with these paths: " + string.Join(", ", missingScenes));
        }

        // AssetCollector owns the editor-only importer policy and Resources load-path derivation.
        // Keep those maps, but intersect them with the AB closure so an uncollected Resources/
        // asset cannot reappear in a legacy manifest or shader/cache staging step.
        var legacy = AssetCollector.Collect(sceneGuids, dependenciesOnly: true);
        var resources = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in legacy.ResourcesMap)
            if (selection.AssetGuids.Contains(pair.Value))
                resources[pair.Key] = pair.Value;

        var reasons = new Dictionary<Guid, AssetCollector.AssetRetentionReason>();
        foreach (var pair in legacy.RetentionReasons)
            if (selection.AssetGuids.Contains(pair.Key))
                reasons[pair.Key] = pair.Value;

        progress?.Log(
            $"AB collector closure: {selection.RootAssetGuids.Count} explicit roots, " +
            $"{selection.AssetGuids.Count} assets including dependencies.",
            0.12f);

        return new AssetCollector.CollectionResult
        {
            AllAssets = new HashSet<Guid>(selection.AssetGuids),
            ResourcesMap = resources,
            RetentionReasons = reasons,
        };
    }

    /// <summary>Copy binary cache files to output as loose files.</summary>
    protected int CopyLooseAssets(
        HashSet<Guid> assets,
        string outputAssetsDir,
        BuildProgress? progress,
        IReadOnlyDictionary<Guid, byte[]>? payloadOverrides = null)
    {
        var project = Project.Current!;
        Directory.CreateDirectory(outputAssetsDir);
        int count = 0;

        bool compressMeshes = BuildMeshVertexCompression.Enabled;
        foreach (var guid in assets)
        {
            string cachePath = Path.Combine(project.CachePath, $"{guid}.asset");
            if (File.Exists(cachePath))
            {
                string destPath = Path.Combine(outputAssetsDir, $"{guid}.asset");
                if (compressMeshes)
                {
                    byte[] payload = payloadOverrides != null && payloadOverrides.TryGetValue(guid, out var overridden)
                        ? overridden : File.ReadAllBytes(cachePath);
                    File.WriteAllBytes(destPath, BuildMeshVertexCompression.Transform(payload));
                }
                else if (payloadOverrides != null && payloadOverrides.TryGetValue(guid, out byte[]? replacement))
                    File.WriteAllBytes(destPath, replacement);
                else
                    File.Copy(cachePath, destPath, true);
                count++;
            }

            if (count % 50 == 0)
                progress?.Log($"Copying assets... ({count}/{assets.Count})", 0.2f + 0.3f * count / assets.Count);
        }

        return count;
    }

    /// <summary>Pack assets into .xenginepak ZipArchive files, auto-splitting at maxSizeMB.</summary>
    protected int PackAssets(
        HashSet<Guid> assets,
        string outputAssetsDir,
        int maxSizeMB,
        BuildProgress? progress,
        IReadOnlyDictionary<Guid, byte[]>? payloadOverrides = null)
    {
        var project = Project.Current!;
        Directory.CreateDirectory(outputAssetsDir);

        // Clean old paks
        foreach (var old in Directory.GetFiles(outputAssetsDir, "*.xenginepak"))
            try { File.Delete(old); } catch { }

        long maxBytes = (long)maxSizeMB * 1024 * 1024;
        int pakIndex = 0;
        long currentSize = 0;
        int count = 0;

        ZipArchive? currentPak = null;
        bool compressMeshes = BuildMeshVertexCompression.Enabled;

        try
        {
            foreach (var guid in assets)
            {
                string cachePath = Path.Combine(project.CachePath, $"{guid}.asset");
                if (!File.Exists(cachePath)) continue;

                byte[]? replacement = payloadOverrides != null && payloadOverrides.TryGetValue(guid, out byte[]? overridePayload)
                    ? overridePayload
                    : null;
                if (compressMeshes) replacement = BuildMeshVertexCompression.Transform(replacement ?? File.ReadAllBytes(cachePath));
                long payloadLength = replacement?.LongLength ?? new FileInfo(cachePath).Length;

                // Start new pak if needed
                if (currentPak == null || currentSize + payloadLength > maxBytes)
                {
                    currentPak?.Dispose();
                    string pakPath = Path.Combine(outputAssetsDir, $"data_{pakIndex}.xenginepak");
                    currentPak = ZipFile.Open(pakPath, ZipArchiveMode.Create);
                    pakIndex++;
                    currentSize = 0;
                }

                // Add to pak
                if (replacement == null)
                {
                    currentPak.CreateEntryFromFile(cachePath, $"{guid}.asset", CompressionLevel.Optimal);
                }
                else
                {
                    ZipArchiveEntry entry = currentPak.CreateEntry($"{guid}.asset", CompressionLevel.Optimal);
                    using Stream destination = entry.Open();
                    destination.Write(replacement, 0, replacement.Length);
                }
                currentSize += payloadLength;
                count++;

                if (count % 50 == 0)
                    progress?.Log($"Packing assets... ({count}/{assets.Count})", 0.2f + 0.3f * count / assets.Count);
            }
        }
        finally
        {
            currentPak?.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Build AssetBundles for every package in the project's
    /// <see cref="XEngine.Editor.AssetBundles.Collector.AssetBundleCollectorSetting"/> into
    /// <paramref name="outputAssetsDir"/>. Used by platform build pipelines when
    /// <see cref="AssetPackagingMode.AssetBundle"/> is selected. Returns the total asset count across
    /// all built packages. The bundles land in {outputAssetsDir}/{packageName}/ and the player entry
    /// template wires a BundleAssetBackend at runtime (see DesktopBuildPipeline's generated Program.cs).
    /// </summary>
    protected int PackAssetBundles(
        BuildTarget target,
        string outputAssetsDir,
        BuildProgress? progress,
        IReadOnlyDictionary<Guid, byte[]>? payloadOverrides = null,
        ISet<Guid>? packedAssetGuids = null)
    {
        var collector = EditorRegistries.GetSettings<XEngine.Editor.AssetBundles.Collector.AssetBundleCollectorSetting>();
        if (collector == null || collector.Packages.Count == 0)
        {
            progress?.Log("[AssetBundles] No collector packages configured; producing no bundles.", Runtime.LogSeverity.Warning);
            return 0;
        }

        var abPlatform = XEngine.Editor.AssetBundles.PlatformVariants.PlatformVariantMapping.ToAssetBundlePlatform(target);
        bool compressMeshes = BuildMeshVertexCompression.Enabled;
        int total = 0;
        foreach (var package in collector.Packages)
        {
            progress?.Log($"[AssetBundles] Building package '{package.PackageName}' for {abPlatform}...", 0.4f);
            // Collect once and hand the exact assignment to the writer. Re-running the dependency
            // walk inside Build after an importer finishes can otherwise produce a different
            // closure from the preflight set used for the player manifest.
            XEngine.Editor.AssetBundles.BuildPipeline.BundleBuildCollection? precollected = null;
            _precollectedAssetBundles?.TryGetValue(package, out precollected);
            var result = XEngine.Editor.AssetBundles.Builder.AssetBundleBuilder.Build(package, new XEngine.Editor.AssetBundles.Builder.AssetBundleBuildOptions
            {
                Version = "1.0.0",
                Platform = abPlatform,
                Compression = XEngine.Runtime.AssetBundles.BundleCompressionMode.Lz4Block,
                OutputRoot = outputAssetsDir,
                PayloadTransform = payloadOverrides == null && !compressMeshes
                    ? null
                    : (guid, payload) =>
                    {
                        if (payloadOverrides != null && payloadOverrides.TryGetValue(guid, out byte[]? replacement)) payload = replacement;
                        return compressMeshes ? BuildMeshVertexCompression.Transform(payload) : payload;
                    },
                PrecollectedCollection = precollected,
            });
            total += result.AssetCount;
            if (packedAssetGuids != null)
                packedAssetGuids.UnionWith(result.AssetGuids);
            foreach (var warn in result.Warnings)
                progress?.Log($"[AssetBundles] {warn}", Runtime.LogSeverity.Warning);
        }
        return total;
    }

    public abstract string GetExecutablePath(string outputPath, BuildSettings settings);

    /// <summary>
    /// Lets a platform own the post-build launch operation. Desktop-style pipelines leave the
    /// request unhandled and retain the historical executable-launch behavior in
    /// <see cref="ProjectBuilder"/>. Device platforms can install, launch, and collect runtime
    /// diagnostics without treating a package archive as a host executable.
    /// </summary>
    public virtual Task<BuildLaunchResult> LaunchAsync(
        BuildLaunchRequest request,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(BuildLaunchResult.NotHandled());
    }

    internal static string FinalizeDefineString(BuildSettings settings, BuildPipeline pipeline)
    {
        var profile = settings.GetOrCreateProfile(pipeline.GetType());
        var symbols = new List<string>(profile.ScriptingDefineSymbols);

        profile.ModifyDefines(symbols);

        var config = ResolvedBuildOptions.From(settings).Config;

        // For when profiling will be implemented
        if (config == BuildConfiguration.Debug)
            symbols.Add("XENGINE_PROFILING");

        return string.Join(";", symbols.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>Generate the asset manifest as Echo binary.</summary>
    protected void GenerateManifest(string outputPath, HashSet<Guid> assets,
        Dictionary<string, Guid> resourcesMap, Guid defaultSceneGuid)
    {
        var root = EchoObject.NewCompound();
        root["defaultScene"] = new EchoObject(defaultSceneGuid.ToString());

        var assetsTag = EchoObject.NewCompound();
        foreach (var guid in assets)
            assetsTag[guid.ToString()] = new EchoObject($"{guid}.asset");
        root["assets"] = assetsTag;

        var resTag = EchoObject.NewCompound();
        foreach (var (path, guid) in resourcesMap)
            resTag[path] = new EchoObject(guid.ToString());
        root["resources"] = resTag;

        root.WriteToBinary(new FileInfo(outputPath));
    }

    /// <summary>Export only build-relevant project settings as Echo YAML files.</summary>
    protected void ExportSettings(string outputSettingsDir, BuildProgress? progress)
    {
        progress?.Log("Exporting settings...", 0.6f);
        Directory.CreateDirectory(outputSettingsDir);

        // The active build profile may override individual Player Settings for this build only. The
        // scope mutates the live singletons that get serialized below and restores them afterwards,
        // so the editor's own settings are the same before and after a build.
        using var overrides = ProfileSettingsOverrideScope.Apply(
            BuildProfileRegistry.GetActive(EditorRegistries.GetSettings<BuildSettings>())?.Profile);
        overrides.LogSummary();

        // Serialize the live (in-memory) settings instances. The in-memory registry is the source
        // of truth at build time. TypeMode.None keeps the output a flat compound keyed by field name
        // so the player (PlayerSettingsLoader) can read it without referencing the settings types.
        foreach (var entry in EditorRegistries.SettingsEntries)
        {
            if (!entry.ExportToBuild) continue;

            try
            {
                EchoObject echo = Serializer.Serialize(entry.Type, entry.Instance, TypeMode.None);
                File.WriteAllText(Path.Combine(outputSettingsDir, $"{entry.Name}.yaml"), echo.WriteToYaml());
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"[Build] Failed to export setting '{entry.Name}': {ex.Message}");
            }
        }
    }

    protected static async Task<(int exitCode, string stdout, string stderr)> RunDotnetAsync(
        string arguments,
        BuildProgress? progress = null,
        CancellationToken cancellation = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process.");

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        // Stream output line-by-line so the UI can show live progress
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                stdoutBuilder.AppendLine(line);
                progress?.Log(line, ClassifyDotnetLine(line));
            }
        }, cancellation);

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                stderrBuilder.AppendLine(line);
                progress?.Log(line, Runtime.LogSeverity.Error);
            }
        }, cancellation);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            // On cancellation the awaits above throw before we get here, so the kill must live in a
            // finally - otherwise the dotnet/MSBuild process tree is orphaned and keeps running.
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    /// <summary>
    /// Classifies a line of dotnet build output by severity.
    /// </summary>
    private static Runtime.LogSeverity ClassifyDotnetLine(string line)
    {
        if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            return Runtime.LogSeverity.Error;
        if (line.Contains(": warning ", StringComparison.OrdinalIgnoreCase))
            return Runtime.LogSeverity.Warning;
        if (line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase))
            return Runtime.LogSeverity.Success;
        return Runtime.LogSeverity.Normal;
    }
}

/// <summary>Immutable context supplied to a platform-owned Build &amp; Run operation.</summary>
public sealed class BuildLaunchRequest
{
    public string ProjectPath { get; init; } = string.Empty;
    public BuildSettings Settings { get; init; } = null!;
    public BuildResult Build { get; init; } = null!;
    public BuildProgress? Progress { get; init; }
}

/// <summary>Outcome of a platform-owned Build &amp; Run operation.</summary>
public sealed class BuildLaunchResult
{
    private BuildLaunchResult(bool handled, bool success, string message, string diagnostics)
    {
        Handled = handled;
        Success = success;
        Message = message;
        Diagnostics = diagnostics;
    }

    /// <summary>Whether the platform took ownership of launching the build result.</summary>
    public bool Handled { get; }

    /// <summary>Whether the platform-owned launch completed successfully.</summary>
    public bool Success { get; }

    /// <summary>Short user-facing launch outcome without sensitive command arguments.</summary>
    public string Message { get; }

    /// <summary>Redacted diagnostic text suitable for the build log.</summary>
    public string Diagnostics { get; }

    public static BuildLaunchResult NotHandled() => new(false, true, string.Empty, string.Empty);

    public static BuildLaunchResult Succeeded(string message, string diagnostics = "") =>
        new(true, true, RequireMessage(message), diagnostics ?? string.Empty);

    public static BuildLaunchResult Failed(string message, string diagnostics = "") =>
        new(true, false, RequireMessage(message), diagnostics ?? string.Empty);

    private static string RequireMessage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A handled build-launch result must include a message.", nameof(value));
        return value;
    }
}
