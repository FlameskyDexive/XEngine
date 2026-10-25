// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// PackageManifest - the per-package manifest describing every bundle and asset, plus the maps the
// runtime needs to resolve an address/GUID to its bundle(s). The binary format is a compact varint
// layout (see BundleBufferReader/Writer); the in-memory model exposes typed lookup indexes.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// The manifest for one asset package: the list of bundles, the list of assets, and the runtime
/// indexes mapping addresses/GUIDs to bundles. Built by the editor's AssetBundleBuilder and loaded
/// by <see cref="AssetPackage"/> at runtime.
/// </summary>
public sealed class PackageManifest
{
    // ---- Build metadata (header) ----
    public int FileVersion { get; set; } = PackageManifestConsts.FileVersion;
    public string PackageName { get; set; } = string.Empty;
    public string PackageVersion { get; set; } = string.Empty;
    public string PackageNote { get; set; } = string.Empty;
    public AssetBundlePlatform BuildTarget { get; set; } = AssetBundlePlatform.Any;
    public BundleCompressionMode Compression { get; set; } = BundleCompressionMode.Lz4Block;
    public string BuildPipeline { get; set; } = string.Empty;

    // ---- Naming / addressing flags ----
    public bool EnableAddressable { get; set; }
    public bool LocationToLower { get; set; } = true;
    public bool IncludeAssetGuid { get; set; } = true;
    public ManifestFileNameStyle OutputNameStyle { get; set; } = ManifestFileNameStyle.HashName;

    // ---- Collections ----
    public List<BundleInfo> BundleList { get; set; } = new();
    public List<ManifestAsset> AssetList { get; set; } = new();

    // ---- Runtime indexes (rebuilt on load; not serialized directly) ----
    private readonly Dictionary<string, ManifestAsset> _assetsByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ManifestAsset> _assetsByGuid = new();
    private readonly Dictionary<string, BundleInfo> _bundlesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BundleInfo> _bundlesByFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<BundleHash, BundleInfo> _bundlesByGuid = new();
    private bool _indexed;

    /// <summary>(Re)build the runtime lookup indexes from <see cref="BundleList"/> and <see cref="AssetList"/>.
    /// Called automatically after deserialization; callers who construct a manifest in code must call
    /// this once before querying.</summary>
    public void BuildIndexes()
    {
        _indexed = false;
        _assetsByAddress.Clear();
        _assetsByGuid.Clear();
        _bundlesByName.Clear();
        _bundlesByFileName.Clear();
        _bundlesByGuid.Clear();

        // Forward + reverse bundle edges.
        var referrers = new List<List<int>>(BundleList.Count);
        for (int i = 0; i < BundleList.Count; i++) referrers.Add(new List<int>());

        for (int i = 0; i < BundleList.Count; i++)
        {
            var b = BundleList[i];
            _bundlesByName[b.BundleName] = b;
            if (!string.IsNullOrEmpty(b.FileName)) _bundlesByFileName[b.FileName] = b;
            _bundlesByGuid[b.BundleGuid] = b;
            foreach (int depId in b.DependentBundleIds)
            {
                if (depId >= 0 && depId < BundleList.Count && depId != i)
                    referrers[depId].Add(i);
            }
        }
        for (int i = 0; i < BundleList.Count; i++)
            BundleList[i].ReferrerBundleIds = referrers[i].ToArray();

        foreach (var a in AssetList)
        {
            _assetsByGuid[a.AssetGuid] = a;
            string key = EnableAddressable ? a.Address : a.AssetPath;
            if (!string.IsNullOrEmpty(key))
            {
                if (LocationToLower) key = key.ToLowerInvariant();
                if (EnableAddressable && _assetsByAddress.TryGetValue(key, out var existing) && existing.AssetGuid != a.AssetGuid)
                {
                    XEngine.Runtime.Debug.LogError(
                        $"[AssetBundles] Duplicate Addressable name '{key}': '{existing.AssetPath}' and '{a.AssetPath}'. Keeping the first asset.");
                    continue;
                }
                _assetsByAddress[key] = a;
            }
        }

        _indexed = true;
    }

    // ---- Address / GUID -> ManifestAsset ----

    public bool TryGetAssetByAddress(string location, out ManifestAsset asset)
    {
        EnsureIndexed();
        if (string.IsNullOrEmpty(location)) { asset = null!; return false; }
        string key = LocationToLower ? location.ToLowerInvariant() : location;
        return _assetsByAddress.TryGetValue(key, out asset!);
    }

    public bool TryGetAssetByGuid(Guid guid, out ManifestAsset asset)
    {
        EnsureIndexed();
        return _assetsByGuid.TryGetValue(guid, out asset!);
    }

    public bool TryGetBundleByName(string bundleName, out BundleInfo bundle)
    {
        EnsureIndexed();
        return _bundlesByName.TryGetValue(bundleName, out bundle!);
    }

    public bool TryGetBundleByGuid(BundleHash guid, out BundleInfo bundle)
    {
        EnsureIndexed();
        return _bundlesByGuid.TryGetValue(guid, out bundle!);
    }

    public bool TryGetBundleByFileName(string fileName, out BundleInfo bundle)
    {
        EnsureIndexed();
        return _bundlesByFileName.TryGetValue(fileName, out bundle!);
    }

    public IReadOnlyList<ManifestAsset> GetAssetsByTag(string tag)
    {
        EnsureIndexed();
        var result = new List<ManifestAsset>();
        foreach (var a in AssetList)
        {
            var tags = a.Tags;
            for (int i = 0; i < tags.Length; i++)
                if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) { result.Add(a); break; }
        }
        return result;
    }

    /// <summary>Resolve an asset's address to its <see cref="ManifestAsset"/>, throwing a clear error
    /// if it is not present. Convenience for the public AssetPackage API.</summary>
    public ManifestAsset ConvertLocationToAsset(string location)
    {
        if (TryGetAssetByAddress(location, out var a)) return a;
        throw new KeyNotFoundException($"Asset address not found in package '{PackageName}': {location}");
    }

    /// <summary>Direct dependency <see cref="BundleInfo"/> for an asset (its own bundle plus any
    /// extra DependentBundleIds). Does NOT include transitive bundle dependencies of those bundles;
    /// use <see cref="GetAllBundleDependencies"/> / the AssetBundleManifest wrapper for closure.</summary>
    public IReadOnlyList<BundleInfo> GetDirectAssetDependencies(ManifestAsset asset)
    {
        EnsureIndexed();
        var result = new List<BundleInfo> { BundleList[asset.BundleId] };
        foreach (int id in asset.DependentBundleIds)
            if (id >= 0 && id < BundleList.Count)
                result.Add(BundleList[id]);
        return result;
    }

    /// <summary>Transitive closure of bundle dependencies for an asset (its own bundle + extras +
    /// all of their recursive direct dependencies). Includes the asset's own bundle as the first element.</summary>
    public IReadOnlyList<BundleInfo> GetAllAssetDependencies(ManifestAsset asset)
    {
        EnsureIndexed();
        var visited = new HashSet<int>();
        var order = new List<int>();
        VisitBundle(asset.BundleId, visited, order);
        foreach (int id in asset.DependentBundleIds)
            VisitBundle(id, visited, order);

        var result = new List<BundleInfo>(order.Count);
        foreach (int id in order) result.Add(BundleList[id]);
        return result;
    }

    /// <summary>Transitive closure of bundle dependencies for a bundle.</summary>
    public IReadOnlyList<BundleInfo> GetAllBundleDependencies(BundleInfo bundle)
    {
        EnsureIndexed();
        int id = BundleList.IndexOf(bundle);
        var visited = new HashSet<int>();
        var order = new List<int>();
        VisitBundle(id, visited, order);
        var result = new List<BundleInfo>(order.Count);
        foreach (int i in order) result.Add(BundleList[i]);
        return result;
    }

    private void VisitBundle(int id, HashSet<int> visited, List<int> order)
    {
        if (id < 0 || id >= BundleList.Count || !visited.Add(id)) return;
        // Dependencies first (post-order) so a bundle is loaded before its dependents need it.
        foreach (int depId in BundleList[id].DependentBundleIds)
            VisitBundle(depId, visited, order);
        order.Add(id);
    }

    private void EnsureIndexed()
    {
        if (!_indexed) BuildIndexes();
    }
}
