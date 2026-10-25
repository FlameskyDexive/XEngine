// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// A Unity-5-style dependency-query wrapper over a <see cref="PackageManifest"/>. Exposes the
/// GetAllDependencies / GetDirectDependencies / hash / crc API that game code and the loader layer
/// expect, so callers do not need to know about manifest internals.
/// </summary>
public sealed class AssetBundleManifest
{
    private readonly PackageManifest _manifest;

    public AssetBundleManifest(PackageManifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    /// <summary>The underlying package manifest.</summary>
    public PackageManifest Package => _manifest;

    /// <summary>All bundle names in the manifest.</summary>
    public IEnumerable<string> GetAllAssetBundles()
    {
        foreach (var b in _manifest.BundleList) yield return b.BundleName;
    }

    /// <summary>Direct dependencies of the named bundle (one level).</summary>
    public string[] GetDirectDependencies(string bundleName)
    {
        if (!_manifest.TryGetBundleByName(bundleName, out var bundle))
            throw new KeyNotFoundException($"Bundle not found: {bundleName}");
        var ids = bundle.DependentBundleIds;
        var result = new string[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            result[i] = _manifest.BundleList[ids[i]].BundleName;
        return result;
    }

    /// <summary>Transitive closure of dependencies of the named bundle (does not include itself).</summary>
    public string[] GetAllDependencies(string bundleName)
    {
        if (!_manifest.TryGetBundleByName(bundleName, out var bundle))
            throw new KeyNotFoundException($"Bundle not found: {bundleName}");
        var deps = _manifest.GetAllBundleDependencies(bundle);
        // GetAllBundleDependencies includes the bundle itself as the last element; exclude it.
        var result = new List<string>(deps.Count);
        foreach (var d in deps)
            if (!string.Equals(d.BundleName, bundleName, StringComparison.Ordinal))
                result.Add(d.BundleName);
        return result.ToArray();
    }

    public BundleInfo? GetBundleInfo(string bundleName)
        => _manifest.TryGetBundleByName(bundleName, out var b) ? b : null;

    public BundleHash GetAssetBundleHash(string bundleName)
        => _manifest.TryGetBundleByName(bundleName, out var b) ? b.FileHash : throw new KeyNotFoundException($"Bundle not found: {bundleName}");

    public uint GetAssetBundleCrc(string bundleName)
        => _manifest.TryGetBundleByName(bundleName, out var b) ? b.FileCrc : throw new KeyNotFoundException($"Bundle not found: {bundleName}");

    /// <summary>Bundles that directly reference <paramref name="bundleName"/> (reverse edges).</summary>
    public string[] GetAllReferencingBundles(string bundleName)
    {
        if (!_manifest.TryGetBundleByName(bundleName, out var bundle))
            throw new KeyNotFoundException($"Bundle not found: {bundleName}");
        var ids = bundle.ReferrerBundleIds;
        var result = new string[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            result[i] = _manifest.BundleList[ids[i]].BundleName;
        return result;
    }
}
