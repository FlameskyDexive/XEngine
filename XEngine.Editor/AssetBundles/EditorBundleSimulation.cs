using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using XEngine.Editor.AssetBundles.BuildPipeline;
using XEngine.Editor.AssetBundles.Builder;
using XEngine.Editor.AssetBundles.Collector;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.AssetBundles;
using XEngine.Runtime.AssetStreaming;

namespace XEngine.Editor.AssetBundles;

/// <summary>Runs Play with real collector-built bundles; never falls back to uncollected editor assets.</summary>
internal sealed class EditorBundleSimulation : IDisposable
{
    private readonly AssetBackendBase? _previous;
    private readonly DirectoryAssetStreamProvider _provider;
    private bool _disposed;
    public BundleAssetBackend Backend { get; }

    private EditorBundleSimulation(DirectoryAssetStreamProvider provider, BundleAssetBackend backend)
    {
        _previous = Runtime.AssetDatabase.Current;
        _provider = provider;
        Backend = backend;
    }

    public static EditorBundleSimulation Prepare(Guid sceneGuid)
    {
        if (sceneGuid == Guid.Empty)
            throw new InvalidOperationException("Save and collect the scene before entering AssetBundle EditorSimulate.");
        var setting = EditorRegistries.GetSettings<AssetBundleCollectorSetting>();
        string cacheRoot = Path.Combine(Project.Current!.CachePath, "EditorSimulate");
        string output = Path.Combine(cacheRoot, "current");
        var roots = new List<string>();
        var collections = new List<BundleBuildCollection>();
        bool compressMeshes = Build.Profiles.BuildMeshVertexCompression.Enabled;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        EditorSimulationCache.AppendText(hash, typeof(AssetBundleBuilder).Module.ModuleVersionId.ToString());
        EditorSimulationCache.AppendText(hash, typeof(AssetBundleWriter).Module.ModuleVersionId.ToString());
        EditorSimulationCache.AppendText(hash, compressMeshes.ToString());
        bool sceneCollected = false;
        foreach (var package in setting.Packages)
        {
            var collection = AssetBundleBuilder.CollectBuildCollection(package);
            sceneCollected |= collection.ExplicitRootAssets.Contains(sceneGuid);
            collections.Add(collection);
            EditorSimulationCache.AppendText(hash, package.PackageName);
            EditorSimulationCache.AppendText(hash, package.EnableAddressable.ToString());
            EditorSimulationCache.AppendText(hash, JsonSerializer.Serialize(collection, jsonOptions));
            var context = new BuildContext();
            context.Set(new BundleBuildContent { Package = package, AssetDatabase = EditorAssetBackend.Instance });
            context.Set(collection);
            if (new ComputeDependencyTask().Run(context) < BuildReturnCode.Success)
                throw new InvalidOperationException("Could not resolve simulation bundle dependencies.");
            EditorSimulationCache.AppendText(hash, JsonSerializer.Serialize(context.Get<BundleDependencyData>(), jsonOptions));
            foreach (Guid guid in collection.AssetToBundle.Keys)
                EditorSimulationCache.AppendFile(hash, Path.Combine(Project.Current.CachePath, $"{guid}.asset"));
            roots.Add(package.PackageName);
        }
        if (!sceneCollected)
            throw new InvalidOperationException("AssetBundleCollector must explicitly collect the active scene.");
        bool reused = EditorSimulationCache.Prepare(cacheRoot, Convert.ToHexString(hash.GetHashAndReset()), buildOutput =>
        {
            for (int i = 0; i < setting.Packages.Count; i++)
                AssetBundleBuilder.Build(setting.Packages[i], new Builder.AssetBundleBuildOptions
                {
                    OutputRoot = buildOutput, Platform = AssetBundlePlatform.Any,
                    PrecollectedCollection = collections[i],
                    PayloadTransform = compressMeshes
                        ? (_, payload) => Build.Profiles.BuildMeshVertexCompression.Transform(payload) : null,
                });
        });
        var provider = new DirectoryAssetStreamProvider(output);
        try
        {
            var backend = BundleAssetBackend.CreateFromProvider(provider, roots, sceneGuid,
                AssetBundlePlatform.Any, AssetPlayMode.EditorSimulate)
                ?? throw new InvalidOperationException("No AssetBundle packages configured.");
            Runtime.Debug.Log($"[EditorSimulate] {(reused ? "Reused" : "Built")} {roots.Count} collector packages at {output}");
            return new EditorBundleSimulation(provider, backend);
        }
        catch { provider.Dispose(); throw; }
    }

    public void Activate() => Runtime.AssetDatabase.Current = Backend;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Runtime.AssetDatabase.Current = _previous;
        Backend.Dispose();
        _provider.Dispose();
        // Keep the verified disk build for the next Play; provider/backend ownership still ends here.
    }
}
