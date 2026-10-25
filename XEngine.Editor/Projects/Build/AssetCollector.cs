using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XEngine.Editor.Build;

/// <summary>
/// Collects all assets needed for a build based on scene dependencies and Resources/ folders.
/// </summary>
public static class AssetCollector
{
    public sealed record AssetRetentionEdge(Guid From, Guid To, string Role);

    public sealed record AssetRetentionReason(
        string RootKind,
        Guid RootGuid,
        IReadOnlyList<AssetRetentionEdge> Chain);

    public struct CollectionResult
    {
        public HashSet<Guid> AllAssets;
        public Dictionary<string, Guid> ResourcesMap; // load path -> guid
        public Dictionary<Guid, AssetRetentionReason> RetentionReasons;
    }

    /// <summary>
    /// Collect all assets needed for a set of scenes.
    /// Walks dependencies transitively and includes all Resources/ folder assets.
    /// </summary>
    public static CollectionResult Collect(List<Guid> sceneGuids, bool dependenciesOnly)
        => Collect(sceneGuids, dependenciesOnly, settingsGuids: null);

    public static CollectionResult Collect(
        List<Guid> sceneGuids,
        bool dependenciesOnly,
        IReadOnlyList<Guid>? settingsGuids)
    {
        var db = EditorAssetBackend.Instance;
        if (db == null)
        {
            return new CollectionResult
            {
                AllAssets = new(),
                ResourcesMap = new(),
                RetentionReasons = new(),
            };
        }

        var allAssets = new HashSet<Guid>();
        var resourcesMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var resourceGuids = new List<Guid>();

        if (dependenciesOnly)
        {
            var roots = new List<Guid>(sceneGuids);
            if (settingsGuids != null)
                for (int i = 0; i < settingsGuids.Count; i++)
                    if (settingsGuids[i] != Guid.Empty)
                        roots.Add(settingsGuids[i]);
            allAssets = db.Dependencies.GetTransitiveDependencies(roots);
            foreach (var root in roots)
                allAssets.Add(root);
        }
        else
        {
            // All assets
            foreach (var entry in db.GetAllEntries())
            {
                allAssets.Add(entry.Guid);
                if (entry.SubAssets != null)
                    foreach (var sub in entry.SubAssets)
                        allAssets.Add(sub.Guid);
            }
        }

        // Always include Resources/ folder assets regardless of dependency mode
        foreach (var entry in db.GetAllEntries())
        {
            if (IsResourcesAsset(entry.Path))
            {
                allAssets.Add(entry.Guid);
                resourceGuids.Add(entry.Guid);
                if (entry.SubAssets != null)
                    foreach (var sub in entry.SubAssets)
                        allAssets.Add(sub.Guid);

                // Build the load path: everything after the last "Resources/" segment, no extension
                string loadPath = GetResourceLoadPath(entry.Path);
                if (!string.IsNullOrEmpty(loadPath))
                {
                    if (resourcesMap.ContainsKey(loadPath))
                        Runtime.Debug.LogWarning($"[Build] Duplicate Resources load path '{loadPath}': '{entry.Path}' overrides another asset.");
                    resourcesMap[loadPath] = entry.Guid;
                }
            }
        }

        // Resources assets are build entry points just like scenes - in DependenciesOnly mode their
        // own transitive dependencies must be collected too, or they ship with broken references.
        if (dependenciesOnly && resourceGuids.Count > 0)
            allAssets.UnionWith(db.Dependencies.GetTransitiveDependencies(resourceGuids));

        // Ensure sub-assets of all collected parents are included, and walk what those sub-assets
        // themselves reference too - not just their GUID. Repeats until nothing new turns up, since
        // a newly pulled-in dependency can itself be a parent with its own sub-assets.
        int previousCount;
        do
        {
            previousCount = allAssets.Count;

            var newSubAssetGuids = new List<Guid>();
            foreach (var entry in db.GetAllEntries())
            {
                if (!allAssets.Contains(entry.Guid) || entry.SubAssets == null) continue;
                foreach (var sub in entry.SubAssets)
                    if (allAssets.Add(sub.Guid))
                        newSubAssetGuids.Add(sub.Guid);
            }

            if (newSubAssetGuids.Count > 0)
                allAssets.UnionWith(db.Dependencies.GetTransitiveDependencies(newSubAssetGuids));

        } while (allAssets.Count > previousCount);

        // Exclude editor-only assets (importers flag non-shippable types; plus anything under an
        // Editor/ folder) by default - UNLESS something that's actually shipping still depends on it,
        // in which case it ships anyway (a normal runtime asset can legitimately reference something
        // that happens to live under Editor/ tooling). Walked to a fixed point since the dependency
        // itself might also be editor-only (e.g. two files under Editor/ referencing each other, where
        // neither should ship unless something outside Editor/ needs the chain).
        var editorOnlyImporters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var editorOnly = new HashSet<Guid>();
        foreach (var entry in db.GetAllEntries())
        {
            if (!IsEditorOnly(entry, editorOnlyImporters)) continue;
            editorOnly.Add(entry.Guid);
            if (entry.SubAssets != null)
                foreach (var sub in entry.SubAssets)
                    editorOnly.Add(sub.Guid);
        }

        var neededEditorOnly = new HashSet<Guid>();
        bool grew;
        do
        {
            grew = false;
            foreach (var guid in editorOnly)
            {
                if (!allAssets.Contains(guid) || neededEditorOnly.Contains(guid)) continue;
                foreach (var dependent in db.Dependencies.GetDependents(guid))
                {
                    if (!allAssets.Contains(dependent)) continue;
                    if (editorOnly.Contains(dependent) && !neededEditorOnly.Contains(dependent)) continue;
                    neededEditorOnly.Add(guid);
                    grew = true;
                    break;
                }
            }
        } while (grew);

        allAssets.ExceptWith(editorOnly.Where(g => !neededEditorOnly.Contains(g)));
        foreach (var kv in resourcesMap.Where(kv => editorOnly.Contains(kv.Value) && !neededEditorOnly.Contains(kv.Value)).ToList())
            resourcesMap.Remove(kv.Key);

        return new CollectionResult
        {
            AllAssets = allAssets,
            ResourcesMap = resourcesMap,
            RetentionReasons = BuildRetentionReasons(
                db,
                allAssets,
                dependenciesOnly,
                sceneGuids,
                resourceGuids,
                settingsGuids),
        };
    }

    private static Dictionary<Guid, AssetRetentionReason> BuildRetentionReasons(
        EditorAssetBackend db,
        HashSet<Guid> allAssets,
        bool dependenciesOnly,
        IReadOnlyList<Guid> sceneGuids,
        IReadOnlyList<Guid> resourceGuids,
        IReadOnlyList<Guid>? settingsGuids)
    {
        var reasons = new Dictionary<Guid, AssetRetentionReason>();
        var queue = new Queue<Guid>();

        var sortedScenes = new List<Guid>(sceneGuids);
        sortedScenes.Sort(CompareGuid);
        for (int i = 0; i < sortedScenes.Count; i++)
            AddRoot(sortedScenes[i], "build-scene", sortedScenes[i]);

        var sortedResources = new List<Guid>(resourceGuids);
        sortedResources.Sort(CompareGuid);
        for (int i = 0; i < sortedResources.Count; i++)
            AddRoot(sortedResources[i], "resources", sortedResources[i]);

        if (settingsGuids != null)
        {
            var sortedSettings = new List<Guid>(settingsGuids);
            sortedSettings.Sort(CompareGuid);
            for (int i = 0; i < sortedSettings.Count; i++)
                if (sortedSettings[i] != Guid.Empty)
                    AddRoot(sortedSettings[i], "player-settings", sortedSettings[i]);
        }

        if (!dependenciesOnly)
        {
            var sortedAll = new List<Guid>(allAssets);
            sortedAll.Sort(CompareGuid);
            for (int i = 0; i < sortedAll.Count; i++)
                AddRoot(sortedAll[i], "all-assets", sortedAll[i]);
        }

        while (queue.Count > 0)
        {
            Guid current = queue.Dequeue();
            IReadOnlyCollection<DependencyEdge> dependencies = db.Dependencies.GetDependenciesWithRoles(current);
            var sortedDependencies = new List<DependencyEdge>(dependencies);
            sortedDependencies.Sort(static (left, right) =>
            {
                int target = CompareGuid(left.Target, right.Target);
                return target != 0 ? target : StringComparer.Ordinal.Compare(left.Role, right.Role);
            });
            for (int i = 0; i < sortedDependencies.Count; i++)
            {
                DependencyEdge dependency = sortedDependencies[i];
                if (!allAssets.Contains(dependency.Target))
                    continue;
                AddEdge(current, dependency.Target, dependency.Role);
            }

            AssetEntry? entry = db.GetEntry(current);
            if (entry?.SubAssets is not { Length: > 0 })
                continue;

            var subAssets = new List<SubAssetEntry>(entry.SubAssets);
            subAssets.Sort(static (left, right) => CompareGuid(left.Guid, right.Guid));
            for (int i = 0; i < subAssets.Count; i++)
            {
                Guid subAsset = subAssets[i].Guid;
                if (allAssets.Contains(subAsset))
                    AddEdge(current, subAsset, "SubAsset");
            }
        }

        // Any item added by an importer-specific closure but not reachable through the persisted
        // graph remains visible in the report instead of being silently called a direct root.
        var remaining = new List<Guid>(allAssets);
        remaining.Sort(CompareGuid);
        for (int i = 0; i < remaining.Count; i++)
        {
            Guid guid = remaining[i];
            if (!reasons.ContainsKey(guid))
                reasons.Add(guid, new AssetRetentionReason("collector-closure", guid, Array.Empty<AssetRetentionEdge>()));
        }

        return reasons;

        void AddRoot(Guid guid, string rootKind, Guid rootGuid)
        {
            if (!allAssets.Contains(guid) || reasons.ContainsKey(guid))
                return;
            reasons.Add(guid, new AssetRetentionReason(rootKind, rootGuid, Array.Empty<AssetRetentionEdge>()));
            queue.Enqueue(guid);
        }

        void AddEdge(Guid from, Guid to, string role)
        {
            if (reasons.ContainsKey(to))
                return;
            AssetRetentionReason parent = reasons[from];
            var chain = new List<AssetRetentionEdge>(parent.Chain.Count + 1);
            for (int i = 0; i < parent.Chain.Count; i++)
                chain.Add(parent.Chain[i]);
            chain.Add(new AssetRetentionEdge(from, to, role));
            reasons.Add(to, new AssetRetentionReason(parent.RootKind, parent.RootGuid, chain));
            queue.Enqueue(to);
        }
    }

    private static int CompareGuid(Guid left, Guid right) =>
        StringComparer.Ordinal.Compare(left.ToString("N"), right.ToString("N"));

    /// <summary>
    /// An asset is editor-only (excluded from build packaging by default - see <see cref="Collect"/>'s
    /// "still needed" check for the dependency override) when it lives under an Editor/ folder, or when
    /// its importer declares <see cref="Importers.AssetImporter.IsEditorOnlyAsset"/> (scripts, plugins,
    /// assembly definitions - things that aren't real runtime data assets).
    /// </summary>
    /// <param name="importerCache">Per-collection cache of importer-type-name -> editor-only.</param>
    public static bool IsEditorOnly(AssetEntry entry, Dictionary<string, bool> importerCache)
    {
        var segments = entry.Path.Split('/', '\\');
        if (segments.Any(s => s.Equals("Editor", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (string.IsNullOrEmpty(entry.ImporterType))
            return false;

        if (!importerCache.TryGetValue(entry.ImporterType, out bool editorOnly))
        {
            editorOnly = EditorRegistries.CreateImporterByName(entry.ImporterType)?.IsEditorOnlyAsset ?? false;
            importerCache[entry.ImporterType] = editorOnly;
        }
        return editorOnly;
    }

    /// <summary>Check if an asset path is under a Resources/ folder.</summary>
    private static bool IsResourcesAsset(string relativePath)
    {
        var segments = relativePath.Split('/', '\\');
        return segments.Any(s => s.Equals("Resources", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get the load path for a Resources asset.
    /// "Art/Resources/Textures/Grass.png" -> "Textures/Grass"
    /// </summary>
    private static string GetResourceLoadPath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        int idx = normalized.LastIndexOf("/Resources/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // Check if it starts with "Resources/"
            if (normalized.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase))
                idx = -1; // will add "/Resources/".Length below
            else
                return "";
        }

        string afterResources = normalized[(idx + "/Resources/".Length)..];
        // Remove extension
        int dotIdx = afterResources.LastIndexOf('.');
        if (dotIdx >= 0)
            afterResources = afterResources[..dotIdx];

        return afterResources;
    }
}
