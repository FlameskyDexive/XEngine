// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Utils;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.GUI;

/// <summary>
/// Handles copy, paste, and duplicate operations for GameObjects using Echo serialization.
/// Copies are placed on the system clipboard as Echo text, integrating with Paper's
/// text field clipboard so that pasting into a text field yields the serialized data.
/// </summary>
public static class GameObjectClipboard
{
    private const string ClipboardHeader = "XEngineGameObjects:";

    // Pending cut: identifiers the next Paste will MOVE instead of instantiating (Unity's Cut).
    private static readonly List<Guid> _cutIds = new();

    /// <summary>Bumped whenever the pending-cut set changes, so row caches can invalidate.</summary>
    public static int CutVersion { get; private set; }

    /// <summary>True while a cut is pending and the next paste will move objects instead of copying.</summary>
    public static bool HasCutObjects => _cutIds.Count > 0;

    /// <summary>True when this object was cut and its moving paste is still pending (rows dim it).</summary>
    public static bool IsCut(Guid identifier) => _cutIds.Contains(identifier);

    /// <summary>Anything for Paste to act on — a pending cut or our serialized clipboard payload.</summary>
    public static bool CanPaste
        => HasCutObjects || (Input.Clipboard is { } text && text.StartsWith(ClipboardHeader, StringComparison.Ordinal));

    /// <summary>Unity's Cut: remember the objects; the next paste MOVES them instead of copying.</summary>
    public static void Cut(IEnumerable<GameObject> gameObjects)
    {
        _cutIds.Clear();
        foreach (var go in FilterToRoots(gameObjects))
            _cutIds.Add(go.Identifier);
        CutVersion++;
    }

    /// <summary>Drain the pending cut set — the paste that moves the objects consumes it exactly once.</summary>
    public static List<Guid> TakeCutIds()
    {
        var ids = new List<Guid>(_cutIds);
        _cutIds.Clear();
        CutVersion++;
        return ids;
    }

    /// <summary>
    /// Deep-copy the given GameObjects to the system clipboard as serialized Echo text.
    /// Filters out children whose ancestors are also in the selection to avoid duplicates.
    /// </summary>
    public static void Copy(IEnumerable<GameObject> gameObjects)
    {
        var roots = FilterToRoots(gameObjects);
        var list = new List<EchoObject>();
        foreach (var go in roots)
        {
            var echo = Serializer.Serialize(typeof(object), go);
            if (echo != null)
                list.Add(echo);
        }

        if (list.Count == 0) return;

        // Wrap in a compound with a header tag so we can identify our clipboard content
        var root = EchoObject.NewList();
        foreach (var item in list)
            root.ListAdd(item);

        string text = ClipboardHeader + root.WriteToString();
        Input.Clipboard = text;
    }

    /// <summary>
    /// Paste GameObjects from the system clipboard into the scene.
    /// Returns the list of newly created GameObjects (already added to the scene and selected).
    /// </summary>
    public static List<GameObject> Paste(GameObject? parent = null)
    {
        var results = new List<GameObject>();

        string? text = Input.Clipboard;
        if (string.IsNullOrEmpty(text) || !text.StartsWith(ClipboardHeader))
            return results;

        string echoText = text[ClipboardHeader.Length..];

        try
        {
            var root = EchoObject.ReadFromString(echoText);
            if (root == null || root.TagType != EchoType.List) return results;

            var scene = Scene.Current;
            if (scene == null) return results;

            foreach (var item in root.List)
            {
                var go = DeserializeCloneWithFreshIdentity(item);
                if (go == null) continue;

                go.Name = UniqueNames.ForGameObjectSibling(go.Name, parent, scene);
                scene.Add(go);
                if (parent != null)
                    // Keep the deserialized local transform (see Duplicate); don't preserve world pos.
                    go.SetParent(parent, worldPositionStays: false);
                results.Add(go);
            }

            if (results.Count > 0)
            {
                Selection.Clear();
                foreach (var go in results)
                    Selection.AddToSelection(go);
                EditorSceneManager.MarkDirty();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to paste GameObjects: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Deep-duplicate the given GameObjects in-place using Echo serialization.
    /// Filters out children whose ancestors are also in the selection to avoid duplicates.
    /// Returns the list of newly created duplicates (already added to the scene and selected).
    /// </summary>
    public static List<GameObject> Duplicate(IEnumerable<GameObject> gameObjects)
    {
        var results = new List<GameObject>();
        var scene = Scene.Current;
        if (scene == null) return results;

        foreach (var source in FilterToRoots(gameObjects))
        {
            try
            {
                var echo = Serializer.Serialize(typeof(object), source);
                if (echo == null) continue;

                var clone = DeserializeCloneWithFreshIdentity(echo);
                if (clone == null) continue;

                clone.Name = UniqueNames.ForGameObjectSibling(source.Name, source.Parent, scene);
                scene.Add(clone);
                if (source.Parent != null)
                    // Keep the clone's copied local transform; don't preserve world position (the
                    // clone is briefly a root, so that path would reinterpret its local pos as world).
                    clone.SetParent(source.Parent, worldPositionStays: false);
                results.Add(clone);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to duplicate '{source.Name}': {ex.Message}");
            }
        }

        if (results.Count > 0)
        {
            Selection.Clear();
            foreach (var go in results)
                Selection.AddToSelection(go);
            EditorSceneManager.MarkDirty();
        }

        return results;
    }

    // ------------------------------------------------------------------
    //  Fresh-identity cloning
    // ------------------------------------------------------------------

    /// <summary>
    /// Deserialize a clipboard GO tree and give every node a fresh identity. Prefab override
    /// paths address the live instance ids (i{guid} GO segments, {guid} component segments), so
    /// they are remapped onto the new ones — a duplicated/pasted prefab instance keeps its
    /// overrides instead of leaving them dangling on the original's identities.
    /// </summary>
    private static GameObject? DeserializeCloneWithFreshIdentity(EchoObject echo)
    {
        // Identifiers come through intact so the old→new pairing can be built, then every node
        // takes a fresh one — two live objects must never share an identity (it is what the
        // hierarchy rows, undo lookups and identifier search key on).
        var clone = GameObject.DeserializePreservingIdentifiers(echo);
        if (clone == null) return null;

        var goMap = new Dictionary<Guid, Guid>();
        var compMap = new Dictionary<Guid, Guid>();
        AssignFreshIdentifiers(clone, goMap, compMap);
        RemapOverridePaths(clone, goMap, compMap);
        return clone;
    }

    private static void AssignFreshIdentifiers(GameObject go, Dictionary<Guid, Guid> goMap, Dictionary<Guid, Guid> compMap)
    {
        Guid oldId = go.Identifier;
        Guid newId = Guid.NewGuid();
        go.SetIdentifier(newId);
        goMap[oldId] = newId;

        foreach (var comp in go.GetComponents<MonoBehaviour>())
        {
            Guid oldCompId = comp.Identifier;
            Guid newCompId = Guid.NewGuid();
            comp.Identifier = newCompId;
            compMap[oldCompId] = newCompId;
        }

        foreach (var child in go.Children)
            AssignFreshIdentifiers(child, goMap, compMap);
    }

    private static void RemapOverridePaths(GameObject go, Dictionary<Guid, Guid> goMap, Dictionary<Guid, Guid> compMap)
    {
        if (go.PrefabOverrides is { Count: > 0 } overrides)
        {
            foreach (var ov in overrides)
            {
                if (string.IsNullOrEmpty(ov.Path)) continue;
                var parts = ov.Path.Split('.');
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (part.Length == 33 && part[0] == 'i' && Guid.TryParseExact(part.AsSpan(1), "N", out Guid goSeg)
                        && goMap.TryGetValue(goSeg, out Guid newGo))
                        parts[i] = $"i{newGo:N}";
                    else if (part.Length == 32 && Guid.TryParseExact(part.AsSpan(0), "N", out Guid compSeg)
                        && compMap.TryGetValue(compSeg, out Guid newComp))
                        parts[i] = $"{newComp:N}";
                }
                ov.Path = string.Join('.', parts);
            }
        }

        foreach (var child in go.Children)
            RemapOverridePaths(child, goMap, compMap);
    }

    /// <summary>
    /// Filter a selection to only include root-level objects whose ancestors are
    /// NOT also in the selection. This prevents duplicating a child that's already
    /// included inside a selected parent's hierarchy.
    /// </summary>
    private static List<GameObject> FilterToRoots(IEnumerable<GameObject> gameObjects)
    {
        var set = new HashSet<GameObject>(gameObjects);
        var roots = new List<GameObject>();

        foreach (var go in set)
        {
            bool ancestorSelected = false;
            var parent = go.Parent;
            while (parent != null)
            {
                if (set.Contains(parent))
                {
                    ancestorSelected = true;
                    break;
                }
                parent = parent.Parent;
            }
            if (!ancestorSelected)
                roots.Add(go);
        }

        return roots;
    }
}
