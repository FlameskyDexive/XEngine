using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Prefabs;

/// <summary>
/// Editor-side prefab operations: create, instantiate, break, apply, revert.
/// </summary>
public static class PrefabUtility
{
    // ================================================================
    //  Creation
    // ================================================================

    /// <summary>
    /// Save a GameObject hierarchy as a new .prefab file and convert the source to a prefab instance.
    /// </summary>
    /// <param name="source">The GameObject to save as a prefab.</param>
    /// <param name="relativeSavePath">Path relative to the Assets folder (e.g., "Prefabs/Enemy.prefab").</param>
    /// <returns>True if successful.</returns>
    // ================================================================
    //  Play-mode guard (prowl-sync M4.6, upstream 762faac2/c6135ae6)
    // ================================================================

    /// <summary>
    /// Prefab assets must not be written while the editor is in Play Mode: the play-mode copy of
    /// the scene holds temporary instance state that would be baked into the asset. Guarding the
    /// asset-writing entry points here keeps every caller safe. Deduplicated per operation so an
    /// offending call site does not spam the console every frame.
    /// </summary>
    private static bool EnsureNotPlaying(string operation)
    {
        if (!Application.IsPlaying) return true;
        Runtime.Debug.LogErrorOnce($"Prefab.PlayMode.{operation}",
            $"[Prefab] {operation} was attempted while the editor is in Play Mode. Exit Play Mode first — play-mode changes are temporary and must not be written into assets.");
        return false;
    }

    public static bool CreatePrefab(GameObject source, string relativeSavePath)
    {
        if (source == null || Project.Current == null) return false;
        if (!EnsureNotPlaying(nameof(CreatePrefab))) return false;

        // Clear any existing prefab data so we serialize a clean prefab source
        source.ClearPrefabDataRecursive();

        // Serialize the GO tree
        var savedId = source.AssetID;
        source.AssetID = Guid.Empty;
        var echo = Serializer.Serialize(typeof(object), source);
        source.AssetID = savedId;

        if (echo == null) return false;

        // Write the .prefab file
        string absolutePath = Path.Combine(Project.Current.AssetsPath, relativeSavePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, echo.WriteToString());

        // Ensure meta file exists so asset DB picks it up with a stable GUID.
        var meta = MetaFile.EnsureMeta(absolutePath, nameof(Importers.PrefabImporter));
        if (meta.Guid == Guid.Empty) return false;

        // Stamp the source GO as a prefab instance
        StampAsPrefabInstance(source, meta.Guid);

        EditorSceneManager.MarkDirty();
        Runtime.Debug.Log($"[Prefab] Created prefab: {relativeSavePath}");

        return true;
    }

    // ================================================================
    //  Instantiation
    // ================================================================

    /// <summary>
    /// Instantiate a prefab from its asset GUID.
    /// Returns a GameObject ready to be added to a scene.
    /// </summary>
    public static GameObject? InstantiatePrefab(Guid prefabGuid)
    {
        var prefab = AssetDatabase.Get(prefabGuid) as PrefabAsset;
        if (prefab == null)
        {
            Runtime.Debug.LogWarning($"[Prefab] Failed to load prefab asset {prefabGuid}");
            return null;
        }
        return prefab.Instantiate();
    }

    // ================================================================
    //  Break
    // ================================================================

    /// <summary>
    /// Break a prefab instance removes all prefab tracking data.
    /// The GameObject becomes a plain non-prefab object.
    /// </summary>
    public static void BreakPrefabInstance(GameObject go)
    {
        // Capture prefab state for undo
        var prefabId = go.PrefabAssetId;
        var overrides = go.PrefabOverrides.ToList();
        var compCount = go.PrefabComponentCount;
        var childCount = go.PrefabChildCount;
        var goRef = go;

        Undo.RegisterAction("Break Prefab Instance",
            undo: () =>
            {
                // Re-stamp as prefab instance
                StampAsPrefabInstance(goRef, prefabId);
                goRef.PrefabOverrides = overrides;
                goRef.PrefabComponentCount = compCount;
                goRef.PrefabChildCount = childCount;
            },
            redo: () => goRef.ClearPrefabDataRecursive());

        go.ClearPrefabDataRecursive();
        EditorSceneManager.MarkDirty();
    }

    /// <summary>
    /// Unity's "Unpack Prefab": clear prefab tracking on this instance's own subtree while nested
    /// prefab roots inside it (different PrefabAssetId) stay instances. "Unpack Completely" is
    /// <see cref="BreakPrefabInstance"/>.
    /// </summary>
    public static void UnpackPrefabInstance(GameObject go)
    {
        Guid rootPrefabId = go.PrefabAssetId;
        if (rootPrefabId == Guid.Empty) return;

        var affected = new List<GameObject>();
        CollectSamePrefabSubtree(go, rootPrefabId, affected);

        var snapshots = new List<(GameObject go, Guid prefabId, List<PropertyOverride> overrides, int compCount, int childCount)>();
        foreach (var g in affected)
            snapshots.Add((g, g.PrefabAssetId, g.PrefabOverrides.ToList(), g.PrefabComponentCount, g.PrefabChildCount));

        Undo.RegisterAction("Unpack Prefab",
            undo: () =>
            {
                foreach (var s in snapshots)
                {
                    s.go.PrefabAssetId = s.prefabId;
                    s.go.PrefabOverrides = s.overrides;
                    s.go.PrefabComponentCount = s.compCount;
                    s.go.PrefabChildCount = s.childCount;
                }
            },
            redo: () =>
            {
                foreach (var s in snapshots)
                    s.go.ClearPrefabData();
            });

        foreach (var g in affected)
            g.ClearPrefabData();
        EditorSceneManager.MarkDirty();
    }

    private static void CollectSamePrefabSubtree(GameObject go, Guid prefabId, List<GameObject> result)
    {
        result.Add(go);
        foreach (var child in go.Children)
        {
            // A different PrefabAssetId marks a nested prefab root — it stays an instance;
            // an empty one is a plain added child with nothing to clear.
            if (child.PrefabAssetId != prefabId) continue;
            CollectSamePrefabSubtree(child, prefabId, result);
        }
    }

    // ================================================================
    //  Apply / Revert
    // ================================================================

    /// <summary>
    /// Apply all overrides from this instance back to its prefab asset.
    /// Only operates within the nesting boundary of this GO's PrefabAssetId.
    /// </summary>
    public static void ApplyOverrides(GameObject instanceRoot)
    {
        if (!instanceRoot.IsPrefabInstance) return;
        if (!EnsureNotPlaying(nameof(ApplyOverrides))) return;

        var db = EditorAssetBackend.Instance;
        if (db == null || Project.Current == null) return;

        var entry = db.GetEntry(instanceRoot.PrefabAssetId);
        if (entry == null)
        {
            Runtime.Debug.LogWarning("[Prefab] Cannot apply prefab asset not found.");
            return;
        }

        // Capture old prefab file for undo
        string absolutePath = Path.Combine(Project.Current.AssetsPath, entry.Path);
        string? oldFileContent = File.Exists(absolutePath) ? File.ReadAllText(absolutePath) : null;
        var oldOverrides = instanceRoot.PrefabOverrides.ToList();
        var prefabGuid = instanceRoot.PrefabAssetId;
        var goRef = instanceRoot;

        // Serialize the instance tree with prefab data stripped
        var cleanCopy = CloneWithoutPrefabData(instanceRoot);
        if (cleanCopy == null) return;

        var echo = Serializer.Serialize(typeof(object), cleanCopy);
        if (echo == null) return;

        // Write to the .prefab file
        File.WriteAllText(absolutePath, echo.WriteToString());

        // Clear overrides on this instance
        ClearOverridesWithinBoundary(instanceRoot, instanceRoot.PrefabAssetId);

        // Reimport and refresh invalidate source cache first
        _sourceCache.Remove(instanceRoot.PrefabAssetId);
        db.Reimport(entry.Guid);
        RefreshAllInstances(instanceRoot.PrefabAssetId);

        Undo.RegisterAction("Apply Prefab Overrides",
            undo: () =>
            {
                // Restore old prefab file
                if (oldFileContent != null) File.WriteAllText(absolutePath, oldFileContent);
                _sourceCache.Remove(prefabGuid);
                db.Reimport(entry.Guid);
                // Restore overrides on instance
                goRef.PrefabOverrides = oldOverrides;
                RefreshAllInstances(prefabGuid);
            },
            redo: () => ApplyOverrides(goRef));

        EditorSceneManager.MarkDirty();
        Runtime.Debug.Log($"[Prefab] Applied overrides to {entry.Path}");
    }

    /// <summary>
    /// Revert all overrides on this instance, restoring it to match the prefab source.
    /// </summary>
    public static void RevertOverrides(GameObject instanceRoot)
    {
        if (!instanceRoot.IsPrefabInstance) return;

        var prefab = AssetDatabase.Get(instanceRoot.PrefabAssetId) as PrefabAsset;
        if (prefab == null)
        {
            Runtime.Debug.LogWarning("[Prefab] Cannot revert prefab asset not found.");
            return;
        }

        // Capture old state for undo
        var oldSerialized = Serializer.Serialize(typeof(object), instanceRoot);
        var parentId = instanceRoot.Parent?.Identifier ?? Guid.Empty;
        var siblingIdx = instanceRoot.Parent != null ? instanceRoot.Parent.Children.IndexOf(instanceRoot) : -1;
        var prefabGuid = instanceRoot.PrefabAssetId;

        // Instantiate fresh from prefab
        var fresh = prefab.Instantiate();
        if (fresh == null) return;

        // Preserve identifiers so undo records stay valid
        CopyIdentifiers(instanceRoot, fresh);

        fresh.Transform.Position = instanceRoot.Transform.Position;
        fresh.Transform.Rotation = instanceRoot.Transform.Rotation;
        fresh.Transform.LocalScale = instanceRoot.Transform.LocalScale;
        fresh.Name = instanceRoot.Name;

        var scene = instanceRoot.Scene;
        var parent = instanceRoot.Parent;
        var rootIdx = parent == null && scene != null ? scene.GetRootIndex(instanceRoot) : -1;

        if (scene != null)
        {
            scene.Remove(instanceRoot);
            instanceRoot.Dispose();
            scene.Add(fresh);
            if (parent != null)
            {
                fresh.SetParent(parent);
                if (siblingIdx >= 0) fresh.SetSiblingIndex(siblingIdx);
            }
            else if (rootIdx >= 0)
            {
                scene.SetRootIndex(fresh, rootIdx);
            }
        }

        // Register undo that swaps fresh back to old
        var freshId = fresh.Identifier;
        Undo.RegisterAction("Revert Prefab Overrides",
            undo: () =>
            {
                var s = Scene.Current;
                if (s == null) return;
                var current = FindByIdentifier(s, freshId);
                if (current == null) return;

                var restored = Serializer.Deserialize<GameObject>(oldSerialized);
                if (restored == null) return;
                Undo.RestoreIdentifiers(restored, oldSerialized);

                var p = current.Parent;
                s.Remove(current);
                current.Dispose();
                s.Add(restored);
                if (p != null) restored.SetParent(p);
                Selection.Select(restored);
            },
            redo: () =>
            {
                var s = Scene.Current;
                if (s == null) return;
                // Re-revert: find by old identifier, replace with fresh prefab
                var pf = AssetDatabase.Get(prefabGuid) as PrefabAsset;
                if (pf == null) return;
                // Find the old-state GO by its identifier
                var oldGo = FindByIdentifier(s, oldSerialized.Get("Identifier")?.StringValue != null
                    && Guid.TryParse(oldSerialized.Get("Identifier")?.StringValue, out var oid) ? oid : Guid.Empty);
                if (oldGo == null) return;

                var f2 = pf.Instantiate();
                if (f2 == null) return;
                f2.Transform.Position = oldGo.Transform.Position;
                f2.Transform.Rotation = oldGo.Transform.Rotation;
                f2.Transform.LocalScale = oldGo.Transform.LocalScale;
                f2.Name = oldGo.Name;
                var p2 = oldGo.Parent;
                s.Remove(oldGo);
                oldGo.Dispose();
                s.Add(f2);
                if (p2 != null) f2.SetParent(p2);
                Selection.Select(f2);
            });

        Selection.Select(fresh);
        EditorSceneManager.MarkDirty();
    }

    private static GameObject? FindByIdentifier(Scene scene, Guid id)
    {
        foreach (var root in scene.RootObjects)
        {
            var found = root.FindChildByIdentifier(id);
            if (found != null) return found;
        }
        return null;
    }

    // ================================================================
    //  Override Detection
    // ================================================================

    /// <summary>
    /// Apply a single override from an instance to the prefab source.
    /// </summary>
    public static void ApplySingleOverride(GameObject instanceGO, PropertyOverride ov)
    {
        if (!instanceGO.IsPrefabInstance) return;
        if (!EnsureNotPlaying(nameof(ApplySingleOverride))) return;

        var db = EditorAssetBackend.Instance;
        if (db == null || Project.Current == null) return;

        var entry = db.GetEntry(instanceGO.PrefabAssetId);
        if (entry == null) return;

        // Load the prefab source, apply the single field, save back
        var prefab = Runtime.AssetDatabase.Get(instanceGO.PrefabAssetId) as PrefabAsset;
        if (prefab?.GameObjectData == null) return;

        // Capture old prefab file content for undo
        string absolutePath = System.IO.Path.Combine(Project.Current.AssetsPath, entry.Path);
        string? oldFileContent = System.IO.File.Exists(absolutePath) ? System.IO.File.ReadAllText(absolutePath) : null;
        var ovPath = ov.Path;
        var ovValue = ov.Value;
        var prefabGuid = instanceGO.PrefabAssetId;
        // Overrides live on the prefab instance root.
        var goRef = GetPrefabInstanceRoot(instanceGO) ?? instanceGO;

        // Keep the file's identifiers intact across this load-modify-save (fresh ids here would
        // rewrite every source identity on each single-override apply).
        var source = GameObject.DeserializePreservingIdentifiers(prefab.GameObjectData);
        if (source == null) return;

        // Apply the override value to the source (path ids belong to the instance tree).
        ParseOverridePath(source, ov.Path, out var target, out string fieldPath, goRef);
        if (target != null && !string.IsNullOrEmpty(fieldPath))
            ApplyFieldValue(target, fieldPath, ov.Value);

        // Save back to the .prefab file
        var echo = Serializer.Serialize(typeof(object), source);
        if (echo != null)
        {
            System.IO.File.WriteAllText(absolutePath, echo.WriteToString());
            _sourceCache.Remove(instanceGO.PrefabAssetId);
            db.Reimport(entry.Guid);
        }

        // Remove this override from the instance (stored on the root)
        goRef.PrefabOverrides.Remove(ov);

        Undo.RegisterAction("Apply Single Override",
            undo: () =>
            {
                // Restore old prefab file
                if (oldFileContent != null) System.IO.File.WriteAllText(absolutePath, oldFileContent);
                _sourceCache.Remove(prefabGuid);
                db.Reimport(entry.Guid);
                // Re-add the override to the instance
                goRef.PrefabOverrides.Add(new PropertyOverride { Path = ovPath, Value = ovValue });
                RefreshAllInstances(prefabGuid);
            },
            redo: () =>
            {
                // Re-apply
                ApplySingleOverride(goRef, new PropertyOverride { Path = ovPath, Value = ovValue });
            });

        // Refresh other instances to pick up the change
        RefreshAllInstances(instanceGO.PrefabAssetId);

        EditorSceneManager.MarkDirty();
    }

    /// <summary>
    /// Revert a single override load the source value and write it back to the instance field.
    /// </summary>
    public static void RevertSingleOverride(GameObject instanceGO, string overridePath)
    {
        if (!instanceGO.IsPrefabInstance) return;

        var source = GetCachedPrefabSource(instanceGO.PrefabAssetId);
        if (source == null) return;

        // Overrides live on the prefab instance root, with root-relative paths, so resolve and
        // mutate against the root rather than whichever GO the inspector happened to pass in.
        var root = GetPrefabInstanceRoot(instanceGO) ?? instanceGO;

        // Find the source value via the path (path ids belong to the instance tree).
        ParseOverridePath(source, overridePath, out var sourceTarget, out string sourceFieldPath, root);
        if (sourceTarget == null || string.IsNullOrEmpty(sourceFieldPath)) return;

        // Read the source value
        var sourceField = GetFieldByPath(sourceTarget, sourceFieldPath);
        if (sourceField == null) return;

        // Find the instance target
        ParseOverridePath(root, overridePath, out var instanceTarget, out string instanceFieldPath);
        if (instanceTarget == null) return;

        // Capture old instance value for undo
        var oldInstanceValue = GetFieldValue(instanceTarget, instanceFieldPath);
        var oldInstanceEcho = Serializer.Serialize(sourceField.FieldType, oldInstanceValue);
        var removedOverrides = root.PrefabOverrides.Where(o => o.Path == overridePath).ToList();
        var goRef = root;
        var path = overridePath;

        // Copy source value to instance
        var sourceValue = GetFieldValue(sourceTarget, sourceFieldPath);
        SetFieldValue(instanceTarget, instanceFieldPath, sourceValue);

        // Remove the override entry
        root.PrefabOverrides.RemoveAll(o => o.Path == overridePath);

        Undo.RegisterAction("Revert Single Override",
            undo: () =>
            {
                // Restore old instance value
                ParseOverridePath(goRef, path, out var undoTarget, out string undoFieldPath);
                if (undoTarget != null && oldInstanceEcho != null)
                    ApplyFieldValue(undoTarget, undoFieldPath, oldInstanceEcho);
                // Re-add removed overrides
                goRef.PrefabOverrides.AddRange(removedOverrides);
            },
            redo: () =>
            {
                RevertSingleOverride(goRef, path);
            });

        EditorSceneManager.MarkDirty();
    }

    private const BindingFlags InstanceFields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static bool TraverseToParent(object target, string[] parts, out object parent)
    {
        parent = target;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var field = parent.GetType().GetField(parts[i], InstanceFields);
            if (field == null) return false;
            var next = field.GetValue(parent);
            if (next == null) return false;
            parent = next;
        }
        return true;
    }

    private static FieldInfo? GetFieldByPath(object target, string fieldPath)
    {
        string[] parts = fieldPath.Split('.');
        if (!TraverseToParent(target, parts, out var parent)) return null;
        return parent.GetType().GetField(parts[^1], InstanceFields);
    }

    private static object? GetFieldValue(object target, string fieldPath)
    {
        string[] parts = fieldPath.Split('.');
        if (!TraverseToParent(target, parts, out var parent)) return null;
        var field = parent.GetType().GetField(parts[^1], InstanceFields);
        return field?.GetValue(parent);
    }

    private static void SetFieldValue(object target, string fieldPath, object? value)
    {
        string[] parts = fieldPath.Split('.');
        if (!TraverseToParent(target, parts, out var parent)) return;
        parent.GetType().GetField(parts[^1], InstanceFields)?.SetValue(parent, value);
    }

    /// <summary>Check if a specific property path is overridden on a GameObject.</summary>
    public static bool IsPropertyOverridden(GameObject go, string path)
    {
        if (!go.IsPrefabInstance) return false;
        // Overrides are stored on the instance root with root-relative paths.
        return (GetPrefabInstanceRoot(go) ?? go).PrefabOverrides.Any(o => o.Path == path);
    }

    /// <summary>Check if a prefab instance has any overrides at all.</summary>
    public static bool HasAnyOverrides(GameObject go)
    {
        if (!go.IsPrefabInstance) return false;
        return (GetPrefabInstanceRoot(go) ?? go).PrefabOverrides.Count > 0;
    }

    // ================================================================
    //  Instance Refresh
    // ================================================================

    /// <summary>
    /// Refresh all instances of a prefab in the current scene after the prefab asset changes.
    /// Re-instantiates from the updated source and re-applies each instance's overrides.
    /// </summary>
    public static void RefreshAllInstances(Guid prefabGuid)
    {
        var scene = Scene.Current;
        if (scene == null) return;

        // Find all instance roots for this prefab
        var roots = scene.AllObjects
            .Where(go => go.PrefabAssetId == prefabGuid && IsInstanceRoot(go))
            .ToList();

        var prefab = AssetDatabase.Get(prefabGuid) as PrefabAsset;
        if (prefab == null) return;

        var selectedGO = Selection.GetSelected<GameObject>().FirstOrDefault();
        GameObject? newSelection = null;

        foreach (var root in roots)
        {
            var savedOverrides = root.PrefabOverrides.ToList();
            var savedName = root.Name;
            var pos = root.Transform.Position;
            var rot = root.Transform.Rotation;
            var scale = root.Transform.LocalScale;
            var parent = root.Parent;
            var siblingIdx = root.GetSiblingIndex() ?? -1;
            var rootIdx = parent == null ? scene.GetRootIndex(root) : -1;

            var fresh = prefab.Instantiate();
            if (fresh == null) continue;

            // Preserve identifiers from the old instance so undo records stay valid
            CopyIdentifiers(root, fresh);

            fresh.PrefabOverrides = savedOverrides;
            fresh.Name = savedName;
            ApplyPropertyOverridesToInstance(fresh, savedOverrides);
            fresh.Transform.Position = pos;
            fresh.Transform.Rotation = rot;
            fresh.Transform.LocalScale = scale;

            scene.Remove(root);
            root.Dispose();
            scene.Add(fresh);
            if (parent != null)
            {
                fresh.SetParent(parent);
                if (siblingIdx >= 0) fresh.SetSiblingIndex(siblingIdx);
            }
            else if (rootIdx >= 0)
            {
                scene.SetRootIndex(fresh, rootIdx);
            }

            if (selectedGO == root)
                newSelection = fresh;
        }

        if (newSelection != null)
            Selection.Select(newSelection);
    }

    // ================================================================
    //  Nesting Helpers
    // ================================================================

    /// <summary>
    /// Find the prefab instance root by walking up the parent chain.
    /// The root is the highest ancestor with the same PrefabAssetId.
    /// </summary>
    public static GameObject? GetPrefabInstanceRoot(GameObject go)
    {
        if (!go.IsPrefabInstance) return null;

        Guid prefabId = go.PrefabAssetId;
        GameObject root = go;

        while (root.Parent != null && root.Parent.IsValid() && root.Parent.PrefabAssetId == prefabId)
            root = root.Parent;

        return root;
    }

    /// <summary>True if this GO is a prefab instance root (not just a child within a prefab).</summary>
    public static bool IsInstanceRoot(GameObject go)
    {
        if (!go.IsPrefabInstance) return false;
        // Root if parent is null, or parent has a different PrefabAssetId
        return go.Parent == null || !go.Parent.IsValid() || go.Parent.PrefabAssetId != go.PrefabAssetId;
    }

    /// <summary>True if this GO is a nested prefab root (different PrefabAssetId from parent).</summary>
    public static bool IsNestedPrefabRoot(GameObject go)
    {
        if (!go.IsPrefabInstance) return false;
        return go.Parent != null && go.Parent.IsValid() && go.Parent.IsPrefabInstance
            && go.Parent.PrefabAssetId != go.PrefabAssetId;
    }

    // ================================================================
    //  Internal Helpers
    // ================================================================

    /// <summary>
    /// Re-applies stored property overrides to a freshly instantiated GO tree.
    /// Parses index-based paths to find target GO/component/field.
    /// </summary>
    private static void ApplyPropertyOverridesToInstance(GameObject root, List<PropertyOverride> overrides)
    {
        foreach (var ov in overrides)
        {
            try
            {
                // Parse the path to find the target
                ParseOverridePath(root, ov.Path, out var targetObj, out string fieldPath);
                if (targetObj == null || string.IsNullOrEmpty(fieldPath))
                {
                    // The structure shifted (component/child added, removed or reordered) so this
                    // index-based path no longer resolves. Skip rather than mis-applying the value.
                    Runtime.Debug.LogWarning($"[Prefab] Override path '{ov.Path}' no longer resolves on the instance; skipping.");
                    continue;
                }

                // Validate the field still exists on the resolved target before writing, so a path
                // that now points at a different component type doesn't silently land on the wrong field.
                if (GetFieldByPath(targetObj, fieldPath) == null)
                {
                    Runtime.Debug.LogWarning($"[Prefab] Override '{ov.Path}' has no matching field on the current instance; skipping.");
                    continue;
                }

                ApplyFieldValue(targetObj, fieldPath, ov.Value);
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"[Prefab] Failed to apply override '{ov.Path}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Parse an override path into a target object and remaining field path. Identifier-addressed
    /// segments carry the ids of the tree the path was RECORDED on; when resolving against the
    /// other half of the pairing (an override written from the instance being applied to the
    /// asset source), pass that instance in <paramref name="identityRoot"/> so the segments
    /// translate through the recorded SourceIdentifier pairing.
    /// </summary>
    private static void ParseOverridePath(GameObject root, string path, out object? target, out string fieldPath,
        GameObject? identityRoot = null)
    {
        target = null;
        fieldPath = "";

        var parts = path.Split('.');
        GameObject currentGO = root;
        int i = 0;

        // Walk GO path segments: identifier form ("i{guid}", prowl-sync M4.2) and the legacy
        // index form ("g0, g1, ...").
        while (i < parts.Length && parts[i].StartsWith('i') && parts[i].Length == 33
               && Guid.TryParseExact(parts[i].AsSpan(1), "N", out Guid goId))
        {
            currentGO = currentGO.FindChildByIdentifier(goId, deep: true);
            if (currentGO == null && identityRoot != null)
            {
                // The id belongs to the instance tree — translate to the source node via pairing.
                var instanceGO = identityRoot.FindChildByIdentifier(goId, deep: true);
                if (instanceGO != null && instanceGO.SourceIdentifier != Guid.Empty)
                    currentGO = root.FindChildByIdentifier(instanceGO.SourceIdentifier, deep: true);
            }
            if (currentGO == null) return;
            i++;
        }
        while (i < parts.Length && parts[i].StartsWith('g'))
        {
            if (!int.TryParse(parts[i].AsSpan(1), out int childIdx) || childIdx < 0 || childIdx >= currentGO.Children.Count)
                return;
            currentGO = currentGO.Children[childIdx];
            i++;
        }

        if (i >= parts.Length) return;

        if (parts[i] == "$")
        {
            // GO-level field
            target = currentGO;
            fieldPath = string.Join(".", parts.Skip(i + 1));
        }
        else if (parts[i].StartsWith('c') && parts[i].Length != 32)
        {
            // Legacy component-by-index segment. A 32-character component GUID can
            // also start with 'c'; let the identifier branch below resolve it.
            if (!int.TryParse(parts[i].AsSpan(1), out int compIdx)) return;
            var comps = currentGO.GetComponents<MonoBehaviour>().ToList();
            if (compIdx >= comps.Count) return;
            target = comps[compIdx];
            fieldPath = string.Join(".", parts.Skip(i + 1));
        }
        else if (parts[i].Length == 32 && Guid.TryParseExact(parts[i].AsSpan(0), "N", out Guid compId))
        {
            // Identifier-addressed component segment (prowl-sync M4.2). Resolve on this tree;
            // when the id belongs to the paired tree, translate through SourceIdentifier.
            target = currentGO.GetComponentByIdentifier(compId);
            if (target == null && identityRoot != null)
            {
                var instanceComp = FindComponentDeep(identityRoot, compId);
                if (instanceComp != null && instanceComp.SourceIdentifier != Guid.Empty)
                {
                    var sourceComp = currentGO.GetComponentByIdentifier(instanceComp.SourceIdentifier);
                    // A pairing must not silently redirect onto a different component type.
                    if (sourceComp != null && sourceComp.GetType() == instanceComp.GetType())
                        target = sourceComp;
                }
            }
            if (target == null) return;
            fieldPath = string.Join(".", parts.Skip(i + 1));
        }
    }

    private static MonoBehaviour? FindComponentDeep(GameObject root, Guid identifier)
    {
        var comp = root.GetComponentByIdentifier(identifier);
        if (comp != null) return comp;
        foreach (var child in root.Children)
        {
            comp = FindComponentDeep(child, identifier);
            if (comp != null) return comp;
        }
        return null;
    }

    private static void ApplyFieldValue(object target, string fieldPath, EchoObject value)
    {
        string[] parts = fieldPath.Split('.');
        if (!TraverseToParent(target, parts, out var parent)) return;

        var finalFieldInfo = parent.GetType().GetField(parts[^1], InstanceFields);
        if (finalFieldInfo == null) return;

        var deserialized = Serializer.Deserialize(value, finalFieldInfo.FieldType);

        // Null is a valid override for reference fields (e.g. clearing an object reference).
        // Only skip for non-nullable value types where null means deserialization failed.
        bool fieldAllowsNull = !finalFieldInfo.FieldType.IsValueType || Nullable.GetUnderlyingType(finalFieldInfo.FieldType) != null;
        if (deserialized != null || fieldAllowsNull)
            finalFieldInfo.SetValue(parent, deserialized);
    }

    // ================================================================
    //  Automatic Override Detection (comparison-based)
    // ================================================================

    /// <summary>
    /// Compare a component's current state against its prefab source and update overrides.
    /// Uses index-based paths. Called after each component is drawn in the inspector.
    /// </summary>
    public static void DetectComponentOverrides(GameObject instanceGO, MonoBehaviour instanceComp)
    {
        if (!instanceGO.IsPrefabInstance) return;

        var source = GetCachedPrefabSource(instanceGO.PrefabAssetId);
        if (source == null) return;

        // Build the GO path from instance root
        string goPath = BuildGOPath(instanceGO);

        // Find the matching source GO by identifier path (ids recorded on the instance tree).
        var instanceRoot = GetPrefabInstanceRoot(instanceGO) ?? instanceGO;
        var sourceGO = ResolveGOPath(source, goPath, instanceRoot);
        if (sourceGO == null) return;

        // Find the matching source component. Source pairing first (upstream StampPrefabId model):
        // the instance records which asset component it came from in SourceIdentifier, so the match
        // survives reordering and insertion even though live identities are per-instance. Raw
        // identifier equality covers pre-fix instances that still share the asset's ids; index is
        // the last resort.
        MonoBehaviour? sourceComp = instanceComp.SourceIdentifier != Guid.Empty
            ? sourceGO.GetComponentByIdentifier(instanceComp.SourceIdentifier)
            : null;
        bool byIdentifier = sourceComp != null && sourceComp.GetType() == instanceComp.GetType();
        if (!byIdentifier)
            sourceComp = sourceGO.GetComponentByIdentifier(instanceComp.Identifier);
        byIdentifier = sourceComp != null && sourceComp.GetType() == instanceComp.GetType();
        if (!byIdentifier)
        {
            // Find matching component by index (all components, not just same type)
            instanceGO.GetComponents(s_instanceComps);
            int compIndex = s_instanceComps.IndexOf(instanceComp);
            if (compIndex < 0) return;

            sourceGO.GetComponents(s_sourceComps);
            if (compIndex >= s_sourceComps.Count) return;

            sourceComp = s_sourceComps[compIndex];
            if (sourceComp.GetType() != instanceComp.GetType())
            {
                Runtime.Debug.LogWarning($"[Prefab] Component type mismatch at index {compIndex}: instance={instanceComp.GetType().Name}, source={sourceComp.GetType().Name}");
                return;
            }
        }

        // Build path prefix. Identifier-addressed when the source component matched by id; the
        // index form remains only for legacy instances whose ids no longer line up.
        string compSegment = byIdentifier
            ? instanceComp.Identifier.ToString("N")
            : $"c{s_instanceComps.IndexOf(instanceComp)}";
        string pathPrefix = string.IsNullOrEmpty(goPath)
            ? compSegment
            : $"{goPath}.{compSegment}";

        // Compare fields. Overrides are stored on the instance root (paths are root-relative) so
        // that apply/revert/refresh - which operate on the root - can see overrides on any child.
        var root = GetPrefabInstanceRoot(instanceGO) ?? instanceGO;
        CompareFields(instanceComp, sourceComp, pathPrefix, root.PrefabOverrides);
    }

    /// <summary>
    /// Detect GO-level overrides (Name, Tag, Layer, Enabled, Transform).
    /// </summary>
    public static void DetectGOOverrides(GameObject instanceGO)
    {
        if (!instanceGO.IsPrefabInstance) return;

        var source = GetCachedPrefabSource(instanceGO.PrefabAssetId);
        if (source == null) return;

        string goPath = BuildGOPath(instanceGO);
        var sourceGO = ResolveGOPath(source, goPath, GetPrefabInstanceRoot(instanceGO) ?? instanceGO);
        if (sourceGO == null) return;

        string pathPrefix = string.IsNullOrEmpty(goPath) ? "$" : $"{goPath}.$";
        // Stored on the instance root (root-relative paths) so refresh/apply can find child overrides.
        var overrides = (GetPrefabInstanceRoot(instanceGO) ?? instanceGO).PrefabOverrides;

        // Compare GO-level fields (excluding Name and Transform those are per-instance)
        CompareField(pathPrefix, "TagIndex", instanceGO.TagIndex, sourceGO.TagIndex, overrides);
        CompareField(pathPrefix, "LayerIndex", instanceGO.LayerIndex, sourceGO.LayerIndex, overrides);
        CompareField(pathPrefix, "Enabled", instanceGO.Enabled, sourceGO.Enabled, overrides);
        // Name and Transform (Position/Rotation/Scale) are intentionally NOT tracked -
        // they are per-instance values that don't constitute overrides.
    }

    private static readonly List<MonoBehaviour> s_instanceComps = new();
    private static readonly List<MonoBehaviour> s_sourceComps = new();

    // Fields that should never be compared for prefab overrides
    private static readonly HashSet<string> _skipFields = new()
    {
        "_identifier", "_enabledInHierarchy", "_go",
        "_hasStarted", "_hasBeenEnabled", "_executeAlwaysCached",
        "HideFlags"
    };

    private static void CompareFields(object instance, object source, string pathPrefix, List<PropertyOverride> overrides)
    {
        var fields = PropertyGridUtils.GetSerializableFields(instance.GetType());
        foreach (var field in fields)
        {
            if (_skipFields.Contains(field.Name)) continue;

            var instanceVal = field.GetValue(instance);
            var sourceVal = field.GetValue(source);
            string path = $"{pathPrefix}.{field.Name}";

            var instanceEcho = Serializer.Serialize(field.FieldType, instanceVal);
            var sourceEcho = Serializer.Serialize(field.FieldType, sourceVal);

            bool areSame = (instanceEcho?.WriteToString() ?? "") == (sourceEcho?.WriteToString() ?? "");

            var existing = overrides.FirstOrDefault(o => o.Path == path);
            if (!areSame)
            {
                if (existing != null)
                    existing.Value = instanceEcho!;
                else if (instanceEcho != null)
                    overrides.Add(new PropertyOverride { Path = path, Value = instanceEcho });
            }
            else if (existing != null)
            {
                overrides.Remove(existing);
            }
        }
    }

    private static void CompareField<T>(string pathPrefix, string fieldName, T instanceVal, T sourceVal, List<PropertyOverride> overrides)
    {
        string path = $"{pathPrefix}.{fieldName}";
        bool areSame = EqualityComparer<T>.Default.Equals(instanceVal, sourceVal);

        var existing = overrides.FirstOrDefault(o => o.Path == path);
        if (!areSame)
        {
            var serialized = Serializer.Serialize(typeof(T), instanceVal);
            if (existing != null)
                existing.Value = serialized!;
            else if (serialized != null)
                overrides.Add(new PropertyOverride { Path = path, Value = serialized });
        }
        else if (existing != null)
        {
            overrides.Remove(existing);
        }
    }

    // ================================================================
    //  GO Path Helpers
    // ================================================================

    /// <summary>Build index path from prefab instance root to this GO. Empty string = root.</summary>
    public static string BuildGOPath(GameObject go)
    {
        var root = GetPrefabInstanceRoot(go);
        if (root == null || root == go) return "";

        // Identifier-addressed segments (prowl-sync M4.2, upstream e0c2eb8e/5d6dd959): instances and
        // prefab sources share identifiers, so overrides survive reordering, insertion and removal.
        var parts = new List<string>();
        var current = go;
        while (current != root && current.Parent != null)
        {
            parts.Insert(0, $"i{current.Identifier:N}");
            current = current.Parent;
        }
        return string.Join(".", parts);
    }

    /// <summary>
    /// Resolve an address path to a GO in the source tree. Supports identifier segments
    /// ("i{guid}") and the legacy index form ("g0.g2") for overrides stored before M4.2.
    /// Identifier segments carry the ids of the tree the path was recorded on; when resolving
    /// against the paired tree (an instance path against the asset source), pass that instance
    /// in <paramref name="identityRoot"/> so unmatched ids translate through SourceIdentifier.
    /// </summary>
    public static GameObject? ResolveGOPath(GameObject root, string path, GameObject? identityRoot = null)
    {
        if (string.IsNullOrEmpty(path)) return root;
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (part.Length == 33 && part[0] == 'i' && Guid.TryParseExact(part.AsSpan(1), "N", out Guid id))
            {
                // Identifier segment: search this node and its subtree.
                current = current.FindChildByIdentifier(id, deep: true);
                if (current == null && identityRoot != null)
                {
                    // The id belongs to the instance tree — translate to the source node via pairing.
                    var instanceGO = identityRoot.FindChildByIdentifier(id, deep: true);
                    if (instanceGO != null && instanceGO.SourceIdentifier != Guid.Empty)
                        current = root.FindChildByIdentifier(instanceGO.SourceIdentifier, deep: true);
                }
                if (current == null) return null;
                continue;
            }
            if (!part.StartsWith('g') || !int.TryParse(part.AsSpan(1), out int idx))
                return null;
            if (idx < 0 || idx >= current.Children.Count) return null;
            current = current.Children[idx];
        }
        return current;
    }

    // Cache the deserialized prefab source for comparison (per prefab GUID)
    // Cache key includes the asset file's mtime (prowl-sync M4.2): a re-import or any file rewrite
    // must invalidate the cached source even when the frame counter is not advancing (headless
    // tests, tools) — a stale source silently mis-applies overrides against old structure/values.
    private static readonly Dictionary<Guid, (GameObject go, long frame, long mtime)> _sourceCache = new();

    private static GameObject? GetCachedPrefabSource(Guid prefabGuid)
    {
        long frame = Runtime.Time.FrameCount;

        long mtime = 0;
        var entry = EditorAssetBackend.Instance?.GetEntry(prefabGuid);
        if (entry != null) mtime = entry.LastModifiedTicks;

        if (_sourceCache.TryGetValue(prefabGuid, out var cached) && cached.frame == frame && cached.mtime == mtime)
            return cached.go;

        var prefab = Runtime.AssetDatabase.Get(prefabGuid) as PrefabAsset;
        if (prefab?.GameObjectData == null) return null;

        // The comparison source must carry the asset file's ids — that is the pairing key
        // instance matching (CopyIdentifiers) works against.
        var source = GameObject.DeserializePreservingIdentifiers(prefab.GameObjectData);
        if (source != null)
            _sourceCache[prefabGuid] = (source, frame, mtime);

        return source;
    }

    /// <summary>
    /// Copy identifiers from an old GO tree to a fresh one (matched by source pairing, then
    /// identifier, then structure order). Preserves GO and component identifiers so undo records,
    /// selection, etc. stay valid across a prefab revert/refresh swap.
    /// </summary>
    private static void CopyIdentifiers(GameObject oldGO, GameObject freshGO)
    {
        // Fresh instances carry per-instance identities and record where each node came from in
        // SourceIdentifier; instances rebuilt from the SAME asset source therefore pair through
        // SourceIdentifier even though their live ids differ. Identifier equality stays as the
        // fallback (pre-fix instances share the asset's ids; some flows rebuild with ids kept),
        // and structure order is the last resort. Source pairing is what keeps identities pinned
        // to the right component/child when the source structure shifted between rebuilds —
        // index matching would swap identities exactly when a component is inserted or removed.
        // The old/fresh pair reaching this point was already matched by the caller (source
        // pairing, id equality, or structure order), so the fresh node adopts the old identity.
        freshGO.SetIdentifier(oldGO.Identifier);

        var oldComps = oldGO.GetComponents().ToArray();
        var freshComps = freshGO.GetComponents().ToArray();
        foreach (var oc in oldComps)
        {
            // Source pairing first: same recorded asset component.
            if (oc.SourceIdentifier != Guid.Empty)
            {
                foreach (var fc in freshComps)
                    if (fc.SourceIdentifier == oc.SourceIdentifier)
                    { fc.Identifier = oc.Identifier; goto nextComp; }
            }

            // Fresh already carries this id straight from the source data — nothing to do.
            foreach (var fc in freshComps)
                if (fc.Identifier == oc.Identifier) goto nextComp;

            // Legacy fallback: hand the old id to the first fresh component that no old component
            // claims, so pre-M4.2 instances keep their identities where the structure lines up.
            foreach (var fc in freshComps)
            {
                bool claimedByOtherOld = false;
                foreach (var oc2 in oldComps)
                    if (oc2 != oc && oc2.Identifier == fc.Identifier) { claimedByOtherOld = true; break; }
                if (claimedByOtherOld) continue;

                fc.Identifier = oc.Identifier;
                break;
            }
            nextComp: ;
        }

        // Children: source pairing first, then identifier, then structure order for leftovers.
        var unmatchedOld = new List<GameObject>();
        foreach (var oc in oldGO.Children)
        {
            GameObject? match = null;
            if (oc.SourceIdentifier != Guid.Empty)
                foreach (var fc in freshGO.Children)
                    if (fc.SourceIdentifier == oc.SourceIdentifier) { match = fc; break; }
            if (match == null)
                foreach (var fc in freshGO.Children)
                    if (fc.Identifier == oc.Identifier) { match = fc; break; }
            if (match != null) CopyIdentifiers(oc, match);
            else unmatchedOld.Add(oc);
        }
        var freshCopy = freshGO.Children.ToArray();
        for (int i = 0; i < Math.Min(unmatchedOld.Count, freshCopy.Length); i++)
        {
            // Skip fresh children whose ids already matched an old child above.
            bool claimed = false;
            foreach (var oc in oldGO.Children)
                if (oc.Identifier == freshCopy[i].Identifier) { claimed = true; break; }
            if (!claimed) CopyIdentifiers(unmatchedOld[i], freshCopy[i]);
        }
    }

    private static void StampAsPrefabInstance(GameObject go, Guid prefabGuid)
    {
        go.PrefabAssetId = prefabGuid;
        foreach (var child in go.Children)
        {
            // Don't overwrite nested prefab instances
            if (child.IsPrefabInstance && child.PrefabAssetId != prefabGuid)
                continue;
            StampAsPrefabInstance(child, prefabGuid);
        }
    }

    private static GameObject? CloneWithoutPrefabData(GameObject source)
    {
        // Serialize the source
        var savedId = source.AssetID;
        source.AssetID = Guid.Empty;
        var echo = Serializer.Serialize(typeof(object), source);
        source.AssetID = savedId;
        if (echo == null) return null;

        // Deserialize a clean copy — keeping the source tree's identifiers so the asset written
        // back below carries one coherent identity set instead of fresh random ones.
        var clone = GameObject.DeserializePreservingIdentifiers(echo);
        if (clone == null) return null;

        // Strip prefab data from the clone
        StripPrefabDataWithinBoundary(clone, source.PrefabAssetId);
        return clone;
    }

    private static void StripPrefabDataWithinBoundary(GameObject go, Guid boundaryPrefabId)
    {
        if (go.PrefabAssetId == boundaryPrefabId)
        {
            go.ClearPrefabData();
            foreach (var child in go.Children)
                StripPrefabDataWithinBoundary(child, boundaryPrefabId);
        }
        // Nested prefab children keep their own prefab data
    }

    private static void ClearOverridesWithinBoundary(GameObject go, Guid boundaryPrefabId)
    {
        if (go.PrefabAssetId == boundaryPrefabId)
        {
            go.PrefabOverrides.Clear();
            foreach (var child in go.Children)
                ClearOverridesWithinBoundary(child, boundaryPrefabId);
        }
    }
}
