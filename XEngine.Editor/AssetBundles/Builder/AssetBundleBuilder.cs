// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// AssetBundleBuilder - the public entry point for building one package's bundles. It is now a thin
// facade over the programmable BuildPipeline: it assembles the BuildContext (parameters + content)
// and runs the default task list. Callers that want custom build steps can pass their own task list
// to the Build overload.
//
// Independent rewrite informed by YooAsset's BundleBuilder and Unity's SBP ContentPipeline
// (Apache-2.0 / Unity Companion License respectively); XEngine's pipeline framework, task contracts,
// and context-object model are independent under MIT.

using System;
using System.Collections.Generic;

using XEngine.Editor.AssetBundles.BuildPipeline;
using XEngine.Editor.AssetBundles.Collector;

using XEngine.Runtime.AssetBundles;

namespace XEngine.Editor.AssetBundles.Builder;

/// <summary>Result of a single package build.</summary>
public sealed class AssetBundleBuildResult
{
    public string PackageName = "";
    public string Version = "";
    public int BundleCount;
    public int AssetCount;
    public long TotalBytes;
    public List<string> Warnings = new();

    /// <summary>
    /// GUIDs that were actually written to this package's bundles. This is kept on the build result
    /// so platform pipelines can prove that their pre-build cache/shader preparation matches the
    /// collector closure instead of falling back to a project-wide asset walk.
    /// </summary>
    public HashSet<Guid> AssetGuids = new();

    /// <summary>
    /// GUIDs authored directly in a collector. Dependencies are intentionally excluded; callers can
    /// use this to validate required roots (for example, the startup scene) before publishing.
    /// </summary>
    public HashSet<Guid> ExplicitRootAssetGuids = new();
}

/// <summary>
/// Read-only selection produced from one or more AB collectors. <see cref="RootAssetGuids"/> is the
/// authored collector surface; <see cref="AssetGuids"/> is its transitive dependency closure and
/// is the only set that an AB-only player build should reimport/package.
/// </summary>
public sealed class AssetBundleAssetSelection
{
    public HashSet<Guid> RootAssetGuids { get; } = new();
    public HashSet<Guid> AssetGuids { get; } = new();

    public bool ContainsRoot(Guid guid) => RootAssetGuids.Contains(guid);
}

/// <summary>Options for an AssetBundleBuilder run. Maps to <see cref="BundleBuildParameters"/>.</summary>
public sealed class AssetBundleBuildOptions
{
    public string Version = "1.0.0";
    public AssetBundlePlatform Platform = AssetBundlePlatform.Any;
    public BundleCompressionMode Compression = BundleCompressionMode.Lz4Block;
    public int Lz4BlockSize = BundleCompression.DefaultBlockSize;
    public Func<Guid, byte[], byte[]>? PayloadTransform;
    public string OutputRoot = "";
    public bool WriteReport = true;

    /// <summary>
    /// Reuses a closure collected during build preflight. This prevents an import completing
    /// between preflight and write from changing bundle membership half way through a build.
    /// </summary>
    public BundleBuildCollection? PrecollectedCollection;
}

/// <summary>
/// Builds AssetBundles for one package. Runs the programmable build pipeline (Collect ->
/// ComputeDependency -> WriteBundles -> WriteManifest) over an <see cref="AssetBundleCollectorPackage"/>.
/// </summary>
public static class AssetBundleBuilder
{
    /// <summary>
    /// Resolve a package's collectors without writing bundles. This is deliberately the same
    /// <see cref="CollectTask"/> used by <see cref="Build(AssetBundleCollectorPackage,
    /// AssetBundleBuildOptions)"/>, so desktop preflight and the eventual writer cannot drift in
    /// what they consider an AB root or dependency.
    /// </summary>
    public static AssetBundleAssetSelection CollectAssetSelection(AssetBundleCollectorPackage package)
    {
        BundleBuildCollection collection = CollectBuildCollection(package);
        var selection = new AssetBundleAssetSelection();
        selection.RootAssetGuids.UnionWith(collection.ExplicitRootAssets);
        selection.AssetGuids.UnionWith(collection.AssetToBundle.Keys);
        return selection;
    }

    /// <summary>Collects one package's full bundle assignment for reuse by the write phase.</summary>
    public static BundleBuildCollection CollectBuildCollection(AssetBundleCollectorPackage package)
    {
        if (package == null) throw new ArgumentNullException(nameof(package));

        var db = XEngine.Editor.EditorAssetBackend.Instance;
        if (db == null) throw new InvalidOperationException("EditorAssetBackend is not initialized.");

        var context = new BuildContext();
        context.Set(new BundleBuildContent
        {
            Package = package,
            AssetDatabase = db,
        });
        var code = new CollectTask().Run(context);
        if (!context.TryGet(out BundleBuildCollection collection))
            throw new InvalidOperationException($"AssetBundle collector selection failed for package '{package.PackageName}' (code {code}).");

        return collection;
    }

    /// <summary>Resolve all configured packages into one AB-only asset closure.</summary>
    public static AssetBundleAssetSelection CollectAssetSelection(AssetBundleCollectorSetting setting)
    {
        if (setting == null) throw new ArgumentNullException(nameof(setting));

        var selection = new AssetBundleAssetSelection();
        foreach (var package in setting.Packages)
        {
            var packageSelection = CollectAssetSelection(package);
            selection.RootAssetGuids.UnionWith(packageSelection.RootAssetGuids);
            selection.AssetGuids.UnionWith(packageSelection.AssetGuids);
        }
        return selection;
    }

    /// <summary>Build one package using the default task list. Returns the build summary.</summary>
    public static AssetBundleBuildResult Build(AssetBundleCollectorPackage package, AssetBundleBuildOptions options)
        => Build(package, options, DefaultAssetBundleTasks.Create());

    /// <summary>Build one package using a caller-supplied task list (for custom build steps). The
    /// context is seeded with parameters + content; tasks populate the rest.</summary>
    public static AssetBundleBuildResult Build(AssetBundleCollectorPackage package, AssetBundleBuildOptions options, IList<IBuildTask> taskList)
    {
        if (package == null) throw new ArgumentNullException(nameof(package));
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (taskList == null || taskList.Count == 0) throw new ArgumentException("taskList is required.", nameof(taskList));
        if (string.IsNullOrEmpty(options.OutputRoot)) throw new ArgumentException("OutputRoot is required");

        var db = XEngine.Editor.EditorAssetBackend.Instance;
        if (db == null) throw new InvalidOperationException("EditorAssetBackend is not initialized.");

        var context = new BuildContext();
        context.Set(new BundleBuildParameters
        {
            PackageName = package.PackageName,
            Version = options.Version,
            Platform = options.Platform,
            Compression = options.Compression,
            Lz4BlockSize = options.Lz4BlockSize,
            PayloadTransform = options.PayloadTransform,
            EnableAddressable = package.EnableAddressable,
            OutputRoot = options.OutputRoot,
            WriteReport = options.WriteReport,
        });
        context.Set(new BundleBuildContent
        {
            Package = package,
            AssetDatabase = db,
            PrecollectedCollection = options.PrecollectedCollection,
        });

        var code = BuildPipelineRunner.Run(taskList, context);
        if (code < BuildReturnCode.Success)
            throw new InvalidOperationException($"AssetBundle build failed (code {code}) for package '{package.PackageName}'.");

        if (context.TryGet<BundleBuildResults>(out var results)) return results.Summary;

        // Fallback: pipeline ran but no results object (shouldn't happen with the default task list).
        return new AssetBundleBuildResult { PackageName = package.PackageName, Version = options.Version };
    }
}
