// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.Runtime.Resources;

/// <summary>
/// What a PrefabAsset was created from. Model imports produce prefabs too (prowl-sync M4.1,
/// upstream 9d976099 removed the separate Model asset type); their read-only posture comes from here.
/// </summary>
public enum PrefabInstanceType
{
    /// <summary>Authored prefab — fully editable.</summary>
    Prefab,
    /// <summary>Produced by a model import (.obj/.fbx/...). Read-only as a whole; re-import instead.</summary>
    Model,
}

/// <summary>
/// A prefab asset containing a serialized GameObject hierarchy.
/// The GO tree is stored as raw EchoObject data and instantiated on demand.
/// </summary>
public class PrefabAsset : EngineObject
{
    [SerializeField]
    private PrefabInstanceType _instanceType = PrefabInstanceType.Prefab;

    /// <summary>What produced this prefab (authored prefab vs model import).</summary>
    public PrefabInstanceType InstanceType
    {
        get { EnsureNotDisposed(); return _instanceType; }
        set { EnsureNotDisposed(); _instanceType = value; }
    }

    /// <summary>
    /// Model imports and other non-authored prefabs are read-only as a whole: edit the source file
    /// and re-import instead of editing the asset.
    /// </summary>
    public bool IsReadOnly => InstanceType != PrefabInstanceType.Prefab;

    /// <summary>The serialized GameObject hierarchy in Echo format.</summary>
    [SerializeField]
    public EchoObject GameObjectData;

    /// <summary>
    /// Instantiate a live GameObject from this prefab.
    /// The returned GO has PrefabAssetId set to this asset's GUID.
    /// Add it to a scene to make it active.
    /// </summary>
    public GameObject? Instantiate()
    {
        EnsureNotDisposed();
        if (GameObjectData == null) return null;

        // Identifiers come through intact and are traded for fresh ones by StampPrefabId, which
        // records where each object came from (SourceIdentifier) on the way. Every instance of
        // this asset therefore owns unique identities — two live instances never share one, which
        // is what ID-keyed lookups and the editor's ID-keyed UI require — while the recorded
        // pairing keeps override addressing and source matching working. (prowl-sync upstream,
        // GameObject.Instantiate/StampPrefabId.)
        var clone = GameObject.DeserializePreservingIdentifiers(GameObjectData);
        if (clone == null) return null;

        // Stamp all GOs with this prefab's asset ID
        StampPrefabId(clone, AssetID);

        return clone;
    }

    /// <summary>
    /// Stamps PrefabAssetId on a GO and its children, records which serialized node each object
    /// came in as, and hands out fresh identities.
    /// Skips children that already have a different PrefabAssetId (nested prefab instances).
    /// </summary>
    private static void StampPrefabId(GameObject go, Guid prefabAssetId)
    {
        if (go.IsPrefabInstance && go.PrefabAssetId != prefabAssetId)
            return; // a nested instance, with a pairing of its own

        go.PrefabAssetId = prefabAssetId;
        go.SetSourceIdentifier(go.Identifier);
        go.SetIdentifier(Guid.NewGuid());

        // Count without LINQ (PR0001): GetComponents returns IEnumerable; one materialized pass
        // serves both the count and the source-pairing stamp.
        var comps = new List<MonoBehaviour>();
        foreach (var c in go.GetComponents<MonoBehaviour>())
            comps.Add(c);
        go.PrefabComponentCount = comps.Count;
        go.PrefabChildCount = go.Children.Count;

        foreach (MonoBehaviour comp in comps)
        {
            // The identifier the component came in under is the asset's, which it records as
            // where it came from before taking an identity of its own.
            comp.SetSourceIdentifier(comp.Identifier);
            comp.Identifier = Guid.NewGuid();
        }

        foreach (var child in go.Children)
        {
            // Don't overwrite nested prefab instances
            if (child.IsPrefabInstance && child.PrefabAssetId != prefabAssetId)
                continue;
            StampPrefabId(child, prefabAssetId);
        }
    }
}
