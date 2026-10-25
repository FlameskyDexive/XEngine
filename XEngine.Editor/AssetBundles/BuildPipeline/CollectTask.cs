// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using XEngine.Editor.AssetBundles.Collector;

using EditorAssetBackend = XEngine.Editor.EditorAssetBackend;
using AssetEntry = XEngine.Editor.AssetEntry;
using SubAssetEntry = XEngine.Editor.SubAssetEntry;

namespace XEngine.Editor.AssetBundles.BuildPipeline;

/// <summary>
/// Phase 1: walk the collector configuration, resolve each collector against the asset database, and
/// assign every asset a bundle (per its PackRule) + an address (per its AddressRule). Output:
/// <see cref="BundleBuildCollection"/> in the context.
/// </summary>
public sealed class CollectTask : IBuildTask
{
    public int Version => 1;
    public string Name => "Collect";

    /// <summary>Carrier for collect-phase warnings (e.g. empty package) surfaced into the build summary.</summary>
    public sealed class CollectTaskWarnings : IBuildContextObject
    {
        public System.Collections.Generic.List<string> Warnings;
        public CollectTaskWarnings(System.Collections.Generic.List<string> warnings) { Warnings = warnings; }
    }

    public BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null)
    {
        var content = context.Get<BundleBuildContent>();
        var db = content.AssetDatabase;
        if (db == null) return BuildReturnCode.Error;

        if (content.PrecollectedCollection != null)
        {
            bool precollectedAddressesOk = ValidateAddresses(content.PrecollectedCollection, content.Package.EnableAddressable);
            context.Set(content.PrecollectedCollection);
            progress?.Report(new BuildTaskProgress(
                Name,
                1f,
                $"{content.PrecollectedCollection.BundleNames.Count} bundles, " +
                $"{content.PrecollectedCollection.AssetToBundle.Count} assets (preflight)"));
            return precollectedAddressesOk ? BuildReturnCode.Success : BuildReturnCode.Error;
        }

        var collection = new BundleBuildCollection();
        context.Set(collection);

        foreach (var group in content.Package.Groups)
        {
            if (!group.Enabled) continue;
            foreach (var collector in group.Collectors)
            {
                CollectCollector(collection, collector, group, content.Package, db);
            }
        }

        bool addressesOk = ValidateAddresses(collection, content.Package.EnableAddressable);

        if (collection.BundleNames.Count == 0)
        {
            // Surface an empty-package warning so callers see why no bundles were produced. The
            // WriteManifest task carries it into the build summary.
            context.Set(new CollectTaskWarnings(new System.Collections.Generic.List<string>
            {
                $"Package '{content.Package.PackageName}' produced no bundles (no collectors matched any assets).",
            }));
        }

        progress?.Report(new BuildTaskProgress(Name, 1f, $"{collection.BundleNames.Count} bundles, {collection.AssetToBundle.Count} assets"));
        if (!addressesOk)
        {
            context.Set(new CollectTaskWarnings(new List<string>(collection.AddressErrors)));
            return BuildReturnCode.Error;
        }
        return BuildReturnCode.Success;
    }

    private static void CollectCollector(BundleBuildCollection c, AssetBundleCollector collector, AssetBundleCollectorGroup group, AssetBundleCollectorPackage package, EditorAssetBackend db)
    {
        if (string.IsNullOrEmpty(collector.CollectPath)) return;

        var collected = new List<(Guid guid, AssetEntry entry, SubAssetEntry? sub)>();
        foreach (var entry in db.GetAllEntries())
        {
            if (!PathMatches(entry.Path, collector.CollectPath)) continue;
            if (IsIgnoredExtension(package, entry.Path)) continue;
            if (!TypeFilterMatches(entry, collector.AssetTypeFilter)) continue;

            if (collector.FilterRule == CollectorFilterRule.MainAssets || collector.FilterRule == CollectorFilterRule.IncludeSubAssets)
                collected.Add((entry.Guid, entry, null));
            if (collector.FilterRule == CollectorFilterRule.IncludeSubAssets || collector.FilterRule == CollectorFilterRule.SubAssetsOnly)
            {
                if (entry.SubAssets != null)
                    foreach (var sub in entry.SubAssets)
                        collected.Add((sub.Guid, entry, sub));
            }
        }
        if (collected.Count == 0) return;

        // Keep the authored collector roots separate from the transitive closure below. Build
        // pipelines use this distinction to verify that a startup scene was explicitly assigned to
        // an AB collector; merely arriving as a dependency of another collector is not enough to
        // make scene loading/addressing deterministic.
        for (int i = 0; i < collected.Count; i++)
            c.ExplicitRootAssets.Add(collected[i].guid);

        // A collector selects roots, but a bundle must also carry the assets those roots reference.
        // Expand the graph here so a scene collector does not silently omit a material/texture (or
        // a prefab's transitive dependencies) that AssetCollector already proved is build-reachable.
        // Roots stay in database order for compatibility; dependencies are appended in GUID order so
        // the resulting bundle/index is reproducible across database enumeration order.
        var collectedGuids = new HashSet<Guid>();
        for (int i = 0; i < collected.Count; i++)
            collectedGuids.Add(collected[i].guid);

        var rootGuids = new Guid[collected.Count];
        for (int i = 0; i < collected.Count; i++)
            rootGuids[i] = collected[i].guid;

        var dependencies = db.Dependencies.GetTransitiveDependencies(rootGuids);
        var sortedDependencies = new List<Guid>(dependencies);
        sortedDependencies.Sort(CompareGuid);
        for (int i = 0; i < sortedDependencies.Count; i++)
        {
            Guid dependencyGuid = sortedDependencies[i];
            if (!collectedGuids.Add(dependencyGuid)) continue;

            AssetEntry? dependencyEntry = db.GetEntry(dependencyGuid);
            if (dependencyEntry != null)
            {
                collected.Add((dependencyGuid, dependencyEntry, null));
                continue;
            }

            // Sub-assets have their own GUID and cache blob, but are indexed through their parent
            // AssetEntry. Keep the sub-asset as the collected item so the bundle writer can emit
            // its typed cache without pulling in the parent or sibling sub-assets implicitly.
            if (!db.TryGetParentGuid(dependencyGuid, out Guid parentGuid)) continue;
            AssetEntry? parentEntry = db.GetEntry(parentGuid);
            if (parentEntry?.SubAssets == null) continue;

            for (int subIndex = 0; subIndex < parentEntry.SubAssets.Length; subIndex++)
            {
                SubAssetEntry sub = parentEntry.SubAssets[subIndex];
                if (sub.Guid != dependencyGuid) continue;
                collected.Add((dependencyGuid, parentEntry, sub));
                break;
            }
        }

        // Explicit collectors own friendly addresses even when an earlier scene collector
        // already included the same asset as a dependency.
        for (int i = 0; i < rootGuids.Length; i++)
            c.AssetAddress[rootGuids[i]] = DeriveAddress(collected[i], collector, package);

        var packGroups = new List<(string bundleName, List<(Guid guid, AssetEntry entry, SubAssetEntry? sub)> items)>();
        var packRule = collector.PackRule == CollectorPackRule.InheritGroup ? group.PackRule : collector.PackRule;
        switch (packRule)
        {
            case CollectorPackRule.PackByGroup:
                packGroups.Add((MakeName(package.PackageName, group.GroupName, "group"), collected));
                break;
            case CollectorPackRule.PackTogether:
                packGroups.Add((MakeName(package.PackageName, group.GroupName, collector.CollectPath), collected));
                break;
            case CollectorPackRule.PackSeparately:
                foreach (var item in collected)
                    packGroups.Add((MakeName(package.PackageName, group.GroupName, item.entry.Path), new List<(Guid, AssetEntry, SubAssetEntry?)> { item }));
                break;
            case CollectorPackRule.PackByDirectory:
                foreach (var grp in collected.GroupBy(x => Path.GetDirectoryName(x.entry.Path) ?? ""))
                    packGroups.Add((MakeName(package.PackageName, group.GroupName, grp.Key), grp.ToList()));
                break;
        }

        foreach (var pg in packGroups)
        {
            // A shared dependency may already have been assigned by another collector. Keep the
            // first explicit/implicit assignment and let ComputeDependencyTask record the cross-
            // bundle edge instead of duplicating the payload in multiple bundles.
            var newItems = new List<(Guid guid, AssetEntry entry, SubAssetEntry? sub)>(pg.items.Count);
            foreach (var item in pg.items)
                if (!c.AssetToBundle.ContainsKey(item.guid))
                    newItems.Add(item);
            if (newItems.Count == 0) continue;

            string? groupBundleName = packRule == CollectorPackRule.PackByGroup
                ? MakeSafeName(pg.bundleName, package.Groups.IndexOf(group)) + "_group" : null;
            int idx = groupBundleName == null ? -1 : c.BundleNames.IndexOf(groupBundleName);
            if (idx < 0)
            {
                idx = c.BundleNames.Count;
                c.BundleNames.Add(groupBundleName ?? MakeSafeName(pg.bundleName, idx));
                c.BundleAssets.Add(new List<Guid>(newItems.Count));
            }
            foreach (var item in newItems)
            {
                c.BundleAssets[idx].Add(item.guid);
                c.AssetToBundle[item.guid] = idx;
                c.AssetAddress.TryAdd(item.guid, item.guid.ToString());
                c.AssetPath[item.guid] = item.sub == null
                    ? item.entry.Path
                    : $"{item.entry.Path}#{item.sub.Name}";
                c.AssetTags[item.guid] = CombineTags(collector.Tags, group.Tags);
            }
        }
    }

    private static int CompareGuid(Guid left, Guid right)
        => StringComparer.Ordinal.Compare(left.ToString("N"), right.ToString("N"));

    internal static bool ValidateAddresses(BundleBuildCollection collection, bool enabled)
    {
        collection.AddressErrors.Clear();
        if (!enabled) return true;
        var addresses = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in collection.AssetAddress)
        {
            if (addresses.TryGetValue(pair.Value, out Guid previous) && previous != pair.Key)
            {
                string message =
                    $"Duplicate Addressable name '{pair.Value}': '{collection.AssetPath.GetValueOrDefault(previous)}' and '{collection.AssetPath.GetValueOrDefault(pair.Key)}'. Rename the asset or change its collector address rule.";
                collection.AddressErrors.Add(message);
                XEngine.Runtime.Debug.LogError("[AssetBundles] " + message);
                continue;
            }
            addresses[pair.Value] = pair.Key;
        }
        return collection.AddressErrors.Count == 0;
    }

    private static bool PathMatches(string entryPath, string collectPath)
    {
        string ep = NormalizeCollectorPath(entryPath);
        string cp = NormalizeCollectorPath(collectPath);
        // EditorAssetBackend stores project assets relative to Assets/ (for example,
        // "Resources/Hero.asset"), while settings authored from the project tree may retain the
        // visible "Assets/" prefix. Treat both spellings as the same collector path. Package
        // assets intentionally keep their Packages/ mount prefix and are never matched by an
        // Assets-root collector.
        if (cp.Length == 0)
            return !ep.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(ep, cp, StringComparison.OrdinalIgnoreCase)) return true;
        return ep.StartsWith(cp + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCollectorPath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring("Assets/".Length);
        return normalized;
    }

    private static bool TypeFilterMatches(AssetEntry entry, string typeFilter)
    {
        if (string.IsNullOrEmpty(typeFilter)) return true;
        return string.Equals(entry.ImporterType, typeFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeName(string packageName, string groupName, string path)
    {
        string p = path.Replace('\\', '/').Trim('/');
        return $"{packageName}/{groupName}/{p}";
    }

    private static string MakeSafeName(string name, int index)
    {
        string safe = name.Replace('/', '_').Replace('\\', '_');
        if (safe.Length > 60) safe = safe.Substring(0, 60);
        return $"b{index:D4}_{safe}";
    }

    private static string DeriveAddress((Guid guid, AssetEntry entry, SubAssetEntry? sub) item, AssetBundleCollector collector, AssetBundleCollectorPackage package)
    {
        if (!package.EnableAddressable || collector.AddressRule == CollectorAddressRule.NoAddress)
            return item.guid.ToString();

        string address = collector.AddressRule switch
        {
            CollectorAddressRule.ByPath => RelPathNoExt(item.entry.Path, collector.CollectPath, item.sub?.Name),
            CollectorAddressRule.ByFileName => item.sub?.Name ?? Path.GetFileNameWithoutExtension(item.entry.Path),
            CollectorAddressRule.ByGuid => item.guid.ToString(),
            CollectorAddressRule.ByAssetPath => RelPathNoExt(item.entry.Path, "", item.sub?.Name),
            _ => item.guid.ToString(),
        };
        return package.LocationToLower ? address.ToLowerInvariant() : address;
    }

    /// <summary>Package-level extension deny list ("png,meta" or ".png,.meta") — matched entries
    /// are excluded from every collector in the package (YooAsset's file-ignore-rule analog).</summary>
    private static bool IsIgnoredExtension(AssetBundleCollectorPackage package, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(package.IgnoreExtensions)) return false;
        foreach (var raw in package.IgnoreExtensions.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string ext = raw.StartsWith('.') ? raw : "." + raw;
            if (entryPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string RelPathNoExt(string entryPath, string collectPath, string? subName)
    {
        string ep = NormalizeCollectorPath(entryPath);
        string cp = NormalizeCollectorPath(collectPath);
        string rel = cp.Length == 0
            ? ep
            : ep.StartsWith(cp + "/", StringComparison.OrdinalIgnoreCase)
                ? ep.Substring(cp.Length + 1)
                : Path.GetFileName(ep);
        int dot = rel.LastIndexOf('.');
        if (dot >= 0) rel = rel.Substring(0, dot);
        return string.IsNullOrEmpty(subName) ? rel : $"{rel}/{subName}";
    }

    private static List<string> CombineTags(List<string> collectorTags, List<string> groupTags)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in collectorTags) if (!string.IsNullOrEmpty(t)) set.Add(t);
        foreach (var t in groupTags) if (!string.IsNullOrEmpty(t)) set.Add(t);
        return new List<string>(set);
    }
}
