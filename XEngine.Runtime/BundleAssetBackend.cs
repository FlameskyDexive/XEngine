// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// BundleAssetBackend - the AssetBackendBase implementation that backs AssetDatabase / AssetRef<T> /
// GameResources (Resources.Load<T>) when the player was built with AssetPackagingMode.AssetBundle.
// It resolves a GUID through the XAssets/AssetPackage handle layer instead of reading loose files.
//
// This is the integration seam that lets the existing engine APIs (which all go through
// AssetDatabase.Get(guid)) keep working unchanged when the underlying bytes live in bundles. A
// ResourceLocator maps GUID -> (package, address) so the backend knows which package to ask.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Runtime;

using XEngine.Runtime.AssetBundles;
using XEngine.Runtime.AssetStreaming;

using Scene = XEngine.Runtime.Resources.Scene;

/// <summary>
/// AssetBackendBase backed by one or more <see cref="AssetPackage"/> instances. Each GUID is mapped
/// to the package that owns it via a <see cref="ResourceLocator"/>; the backend loads the asset
/// through that package's handle layer and caches the result in the base class's _loadedAssets cache.
/// </summary>
public sealed class BundleAssetBackend : PlayerAssetBackendBase
{
    private readonly ResourceLocator _locator;
    private readonly Dictionary<Guid, AssetHandle> _handles = new();
    private readonly List<string> _packageNames;

    public override Dictionary<string, Guid> ResourcesMap { get; }
    public override Guid DefaultSceneGuid { get; protected set; }

    /// <summary>
    /// Resolves a packaged project path (or an addressable location) to the manifest GUID. This is
    /// intentionally a small runtime view rather than the editor's <c>AssetEntry</c> type: player
    /// code that has only a path (for example a data-driven VFX spawner) must not reference editor
    /// assemblies or depend on the on-disk source database.
    /// </summary>
    public RuntimeAssetEntry? GetEntry(string path)
        => _locator.TryResolvePath(path, out Guid guid)
            ? new RuntimeAssetEntry(guid, path)
            : null;

    /// <param name="locator">Maps asset GUIDs to the package + addressable address that owns them.
    /// Built at player startup from the active manifests (or in-editor from the simulate manifest).</param>
    public BundleAssetBackend(ResourceLocator locator)
        : this(locator, Guid.Empty, [])
    {
    }

    private BundleAssetBackend(
        ResourceLocator locator,
        Guid defaultSceneGuid,
        List<string> packageNames)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        DefaultSceneGuid = defaultSceneGuid;
        _packageNames = packageNames;
        ResourcesMap = locator.BuildResourcesMap();
    }

    /// <summary>
    /// Creates an AssetBundle backend over package roots inside a platform stream provider. Package
    /// roots use logical paths such as <c>Content/default</c> and must contain the canonical version,
    /// manifest, and bundle files produced by the editor build pipeline.
    /// </summary>
    public static BundleAssetBackend? CreateFromProvider(
        IAssetStreamProvider provider,
        IReadOnlyList<string> packageRoots,
        Guid defaultSceneGuid,
        AssetBundlePlatform platform = AssetBundlePlatform.Android,
        AssetPlayMode playMode = AssetPlayMode.Offline)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(packageRoots);
        if (packageRoots.Count == 0)
            return null;

        XAssets.Initialize();
        var locator = new ResourceLocator();
        var packageNames = new List<string>(packageRoots.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (int index = 0; index < packageRoots.Count; index++)
            {
                string root = AssetStreamPath.Normalize(packageRoots[index]);
                int separator = root.LastIndexOf('/');
                string packageName = separator < 0 ? root : root[(separator + 1)..];
                if (packageName.Length == 0 || !seen.Add(packageName))
                    throw new InvalidDataException($"Duplicate or invalid AssetBundle package root: {root}");
                if (XAssets.ContainsPackage(packageName))
                    throw new InvalidOperationException($"AssetBundle package is already active: {packageName}");

                var storage = new AssetStreamBuiltinStorage(provider, packageName, root, platform);
                AssetPackage package = XAssets.CreatePackage(packageName);
                packageNames.Add(packageName);
                package.Initialize(playMode, [storage]);
                PackageManifest manifest = storage.LoadActiveManifest();
                if (manifest.BuildTarget != AssetBundlePlatform.Any && manifest.BuildTarget != platform)
                {
                    throw new InvalidDataException(
                        $"AssetBundle package '{packageName}' targets {manifest.BuildTarget}, not {platform}.");
                }
                package.SetActiveManifest(manifest);
                locator.RegisterPackage(package);
            }

            return new BundleAssetBackend(locator, defaultSceneGuid, packageNames);
        }
        catch
        {
            for (int index = packageNames.Count - 1; index >= 0; index--)
                XAssets.RemovePackage(packageNames[index]);
            throw;
        }
    }

    protected override EngineObject? LoadFresh(Guid assetId)
    {
        if (!_locator.TryResolve(assetId, out var package, out var address))
        {
            // TEMP-DIAG (editor parity N4a follow-up): one stack per missing guid, then mute.
            Debug.LogError($"[BundleAssetBackend] No package owns asset {assetId}.\n{Environment.StackTrace}");
            return null;
        }

        try
        {
            // Blocking resolve: this runs on the AssetLoader background thread. LoadAssetAsync returns
            // a handle whose underlying provider completes synchronously when the bundle is resident
            // (the common case post-warmup), so the sync wait is short.
            var handle = package.LoadAssetSync<EngineObject>(assetId);
            ReplaceHandle(assetId, handle);

            var obj = handle.GetAssetObject<EngineObject>();
            if (obj != null)
            {
                obj.AssetID = assetId;
                SetLoaded(assetId, obj);
            }
            return obj;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BundleAssetBackend] Failed to load {assetId} ({address}): {ex.Message}");
            return null;
        }
    }

    public override Scene? LoadScene(Guid sceneGuid)
    {
        if (!_locator.TryResolve(sceneGuid, out AssetPackage package, out string address))
        {
            Debug.LogError($"[BundleAssetBackend] No package owns scene {sceneGuid}.");
            return null;
        }

        try
        {
            AssetHandle handle = package.LoadAssetSync<Scene>(sceneGuid);
            ReplaceHandle(sceneGuid, handle);
            Scene? scene = handle.GetAssetObject<Scene>();
            if (scene != null)
                scene.AssetID = sceneGuid;
            return scene;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[BundleAssetBackend] Failed to load scene {sceneGuid} ({address}): {exception.Message}");
            return null;
        }
    }

    private void ReplaceHandle(Guid assetId, AssetHandle handle)
    {
        if (_handles.Remove(assetId, out AssetHandle? previous))
        {
            try { previous.Release(); } catch { }
        }
        _handles[assetId] = handle;
    }

    /// <summary>Release every handle this backend holds (called on package teardown).</summary>
    public override void Dispose()
    {
        foreach (var kv in _handles)
        {
            try { kv.Value.Release(); } catch { }
        }
        _handles.Clear();
        foreach (var kv in _loadedAssets)
        {
            try { kv.Value?.Dispose(); } catch { }
        }
        _loadedAssets.Clear();
        for (int index = _packageNames.Count - 1; index >= 0; index--)
            XAssets.RemovePackage(_packageNames[index]);
        _packageNames.Clear();
    }
}

/// <summary>
/// Maps asset GUIDs to the <see cref="AssetPackage"/> + addressable address that owns them. Built at
/// startup from the active manifests. The addressable address is the load key the package's
/// AssetManager resolves; for non-addressable packages it is the GUID string.
/// </summary>
public sealed class ResourceLocator
{
    private readonly Dictionary<Guid, (AssetPackage package, string address)> _map = new();
    private readonly Dictionary<string, Guid> _pathMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register every asset in a package's manifest under this locator.</summary>
    public void RegisterPackage(AssetPackage package)
    {
        if (package?.ActiveManifest == null) return;
        foreach (var asset in package.ActiveManifest.AssetList)
        {
            // If multiple packages claim the same GUID, the first registration wins (build-time should
            // prevent collisions; this is a defensive last-writer-wins for runtime robustness).
            _map[asset.AssetGuid] = (package, string.IsNullOrEmpty(asset.Address) ? asset.AssetGuid.ToString() : asset.Address);
            RegisterPath(asset.AssetPath, asset.AssetGuid);
            RegisterPath(asset.Address, asset.AssetGuid);
        }
    }

    public bool TryResolve(Guid guid, out AssetPackage package, out string address)
    {
        if (_map.TryGetValue(guid, out var entry))
        {
            package = entry.package;
            address = entry.address;
            return true;
        }
        package = null!;
        address = string.Empty;
        return false;
    }

    /// <summary>Resolve a source asset path or address from a package manifest to its GUID.</summary>
    public bool TryResolvePath(string path, out Guid guid)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length > 0 && _pathMap.TryGetValue(normalized, out guid))
            return true;

        guid = Guid.Empty;
        return false;
    }

    /// <summary>Build a Resources.Load path map (path -> GUID) by scanning every registered package's
    /// manifest for assets whose AssetPath falls under a Resources/ folder. Used to wire
    /// GameResources.Initialize at player startup so Resources.Load&lt;T&gt; keeps working in bundle mode.</summary>
    public Dictionary<string, Guid> BuildResourcesMap()
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _map)
        {
            string path = kv.Value.address;
            // In addressable mode the address already IS the Resources load path (the builder derives
            // addresses from the asset's path relative to its collector, mirroring the legacy
            // Resources/ convention). Register under the address.
            if (!string.IsNullOrEmpty(path))
                result[path] = kv.Key;

            // A collector may intentionally use ByGuid/NoAddress for a Resources asset. The
            // runtime still has the source path in the package manifest, so preserve the legacy
            // Resources.Load key independently of the address chosen for bundle lookup.
            PackageManifest? manifest = kv.Value.package.ActiveManifest;
            if (manifest != null &&
                manifest.TryGetAssetByGuid(kv.Key, out ManifestAsset asset) &&
                TryGetResourcesLoadPath(asset.AssetPath, out string resourcesPath))
            {
                result[resourcesPath] = kv.Key;
            }
        }
        return result;
    }

    private void RegisterPath(string? path, Guid guid)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length == 0)
            return;

        _pathMap.TryAdd(normalized, guid);
        if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            _pathMap.TryAdd(normalized["Assets/".Length..], guid);
        else if (!normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            _pathMap.TryAdd("Assets/" + normalized, guid);
    }

    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').TrimStart('/');

    private static bool TryGetResourcesLoadPath(string? assetPath, out string resourcesPath)
    {
        string normalized = NormalizePath(assetPath);
        int marker = normalized.LastIndexOf("/Resources/", StringComparison.OrdinalIgnoreCase);
        int start;
        if (marker >= 0)
            start = marker + "/Resources/".Length;
        else if (normalized.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase))
            start = "Resources/".Length;
        else
        {
            resourcesPath = string.Empty;
            return false;
        }

        resourcesPath = normalized[start..];
        int extension = resourcesPath.LastIndexOf('.');
        if (extension >= 0)
            resourcesPath = resourcesPath[..extension];
        return resourcesPath.Length > 0;
    }
}

/// <summary>
/// Minimal path lookup result exposed by a player asset backend. Public fields are intentional: the
/// editor and game-side compatibility shims can inspect the GUID without taking an editor reference.
/// </summary>
public sealed class RuntimeAssetEntry
{
    public Guid Guid;
    public string Path;

    public RuntimeAssetEntry(Guid guid, string path)
    {
        Guid = guid;
        Path = path;
    }
}
