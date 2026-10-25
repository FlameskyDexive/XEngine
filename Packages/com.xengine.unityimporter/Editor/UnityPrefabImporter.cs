using System;
using System.Collections.Generic;
using System.IO;
using XEngine.Echo;
using XEngine.Editor;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;


namespace XEngine.UnityImporter.Editor;

/// <summary>
/// Claims <c>.prefab</c> files whose content is Unity serialized YAML and converts the
/// GameObject/Transform hierarchy + SkinnedMeshRenderers into a native <see cref="Model"/> asset
/// (same shape the glTF importer produces, so <see cref="Model.Instantiate"/> and the normal
/// drag-into-scene flow work unchanged). Cross-references to meshes/materials are resolved
/// through the copy-time guid sidecar. Native XEngine .prefab files are delegated to the
/// engine's own <see cref="PrefabImporter"/>.
/// </summary>
[ImporterFor(".prefab", Priority = 100)]
public sealed class UnityPrefabImporter : AssetImporter
{
    public override int Version => 7;

    public override bool CanImport(string absolutePath) => true; // owns .prefab; sniffs in Import and delegates

    public override bool Import(ImportContext ctx)
    {
        if (!UnityAssetSniffer.IsUnityYaml(ctx.AbsolutePath))
            return new PrefabImporter().Import(ctx);

        try
        {
            UnityPrefabData prefab = UnityPrefabData.Read(ctx.AbsolutePath);
            if (prefab.Root == null)
                throw new InvalidDataException("prefab has no root transform.");

            // 1. Build the live GO hierarchy (straight TRS pass-through).
            var transforms = new Dictionary<long, GameObject>(prefab.Transforms.Count);
            foreach (PrefabTransform node in prefab.Root.SelfAndChildren)
            {
                var go = new GameObject(string.IsNullOrEmpty(node.Name) ? "Node" : node.Name);
                go.Transform.LocalPosition = new Float3(node.Position[0], node.Position[1], node.Position[2]);
                go.Transform.LocalRotation = new Quaternion(node.Rotation[0], node.Rotation[1], node.Rotation[2], node.Rotation[3]);
                go.Transform.LocalScale = new Float3(node.Scale[0], node.Scale[1], node.Scale[2]);
                transforms[node.Id] = go;
            }
            foreach (PrefabTransform node in prefab.Root.SelfAndChildren)
            {
                if (node.Father != null && transforms.TryGetValue(node.Father.Id, out GameObject? parent))
                    transforms[node.Id].SetParent(parent, worldPositionStays: false);
            }
            GameObject root = transforms[prefab.Root.Id];

            // 2. SkinnedMeshRenderers: mesh/material refs via the Unity-guid sidecar, bones by path.
            int wired = 0, skipped = 0;
            foreach (PrefabSkinnedMesh smr in prefab.SkinnedMeshes)
            {
                if (!smr.Enabled || !transforms.TryGetValue(smr.Node.Id, out GameObject? go))
                {
                    skipped++;
                    continue;
                }

                // Unity prefab mesh refs point either at a Unity .mesh file (main asset) or at a
                // mesh sub-asset inside an imported .fbx. The engine imports .fbx natively
                // (meshes become name-addressed sub-assets), and ripped prefabs keep the
                // convention that the SMR's own GameObject is named after its mesh, so FBX meshes
                // resolve by node name with a single-mesh fallback.
                Guid meshGuid = ResolveEngineMeshGuid(ctx.AbsolutePath, smr.MeshGuid, smr.Node.Name, out Guid meshFileGuid);
                // Always depend on the FBX file when the sidecar mapped it, even if the named
                // mesh sub-asset is not ready yet (prefab often imports before the FBX). Skipping
                // without this edge left Anbi/Corin with 0 renderers after the first pass.
                if (meshFileGuid != Guid.Empty)
                    ctx.AddDependency(meshFileGuid, "Mesh");
                if (meshGuid == Guid.Empty)
                {
                    string why = meshFileGuid == Guid.Empty
                        ? "could not be resolved via unity-guid-map.json"
                        : $"FBX is mapped but has no mesh sub-asset named '{smr.Node.Name}' yet";
                    Debug.LogWarning($"[UnityImport] Prefab '{Path.GetFileName(ctx.AbsolutePath)}' node '{smr.Node.Name}': " +
                                     $"mesh guid {smr.MeshGuid} {why} — renderer skipped.");
                    skipped++;
                    continue;
                }

                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.SharedMesh = new AssetRef<Mesh>(meshGuid);
                if (smr.MaterialGuids.Count == 0)
                {
                    Debug.LogWarning($"[UnityImport] Prefab '{Path.GetFileName(ctx.AbsolutePath)}' node '{smr.Node.Name}': " +
                                     "m_Materials has no guid entries — renderer will not draw.");
                }
                foreach (string materialGuid in smr.MaterialGuids)
                {
                    Guid engineGuid = ResolveEngineGuid(ctx.AbsolutePath, materialGuid);
                    if (engineGuid == Guid.Empty)
                    {
                        Debug.LogWarning($"[UnityImport] Prefab '{Path.GetFileName(ctx.AbsolutePath)}' node '{smr.Node.Name}': " +
                                         $"material guid {materialGuid} could not be resolved via unity-guid-map.json.");
                        renderer.Materials.Add(new AssetRef<Material>());
                        continue;
                    }
                    renderer.Materials.Add(new AssetRef<Material>(engineGuid));
                    ctx.AddDependency(engineGuid, "Material");
                }

                var bones = new List<Transform>(smr.BoneTransformIds.Count);
                foreach (long boneId in smr.BoneTransformIds)
                    bones.Add(transforms.TryGetValue(boneId, out GameObject? boneGo) ? boneGo.Transform : root.Transform);
                Transform? rootBone = smr.RootBoneId != 0 && transforms.TryGetValue(smr.RootBoneId, out GameObject? rootBoneGo)
                    ? rootBoneGo.Transform
                    : (bones.Count > 0 ? bones[0] : null);
                var boneArray = new Transform[bones.Count];
                for (int b = 0; b < bones.Count; b++) boneArray[b] = bones[b];
                renderer.SetBones(boneArray, rootBone);
                wired++;
            }

            // 3. Serialize the hierarchy into a PrefabAsset main asset; AssetRefs serialize as GUIDs and
            //    the tracking context forwards them as typed dependency edges.
            var model = new PrefabAsset { Name = Path.GetFileNameWithoutExtension(ctx.AbsolutePath) };
            var serializationContext = ImportHelper.CreateTrackingContext();
            model.GameObjectData = Serializer.Serialize(typeof(object), root, serializationContext);
            foreach (var dep in serializationContext.Dependencies)
                ctx.AddDependency(dep.Guid, dep.Role);

            ctx.SetMainAsset(model);
            Debug.Log($"[UnityImport] Imported Unity prefab '{Path.GetFileName(ctx.AbsolutePath)}': " +
                      $"{transforms.Count} nodes, {wired} skinned renderers ({skipped} skipped).");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UnityImport] Failed to import Unity prefab '{ctx.AbsolutePath}': {ex.Message}");
            return false;
        }
    }

    internal static Guid ResolveEngineGuid(string assetPath, string unityGuid)
    {
        if (!TryResolveMappedEntry(assetPath, unityGuid, out _, out var entry) || entry == null)
            return Guid.Empty;
        return entry.Guid;
    }

    /// <summary>
    /// Resolves a Unity mesh reference to an engine guid. Non-FBX targets resolve to the file's
    /// main asset (Unity .mesh imports are single-asset). An .fbx target resolves to the named
    /// mesh sub-asset (falling back to the file's only mesh) because the engine imports FBX
    /// natively with meshes as sub-assets. Multi-mesh FBXs never fall back to the first mesh —
    /// that collapsed every character SkinnedMeshRenderer onto one dummy. <paramref name="fileGuid"/>
    /// receives the referenced file's own asset guid (Guid.Empty when unresolved) for dependency ordering.
    /// </summary>
    internal static Guid ResolveEngineMeshGuid(string assetPath, string unityGuid, string meshName, out Guid fileGuid)
    {
        fileGuid = Guid.Empty;
        if (!TryResolveMappedEntry(assetPath, unityGuid, out string relative, out var entry) || entry == null)
            return Guid.Empty;

        fileGuid = entry.Guid;
        bool fromFbx = string.Equals(Path.GetExtension(relative), ".fbx", StringComparison.OrdinalIgnoreCase);
        if (!fromFbx)
            return entry.Guid;

        if (entry.SubAssets is not { Length: > 0 })
            return Guid.Empty;

        Guid named = Guid.Empty;
        Guid first = Guid.Empty;
        int meshCount = 0;
        foreach (var sub in entry.SubAssets)
        {
            if (sub.Type != typeof(Mesh)) continue;
            meshCount++;
            if (first == Guid.Empty) first = sub.Guid;
            if (!string.IsNullOrEmpty(meshName)
                && string.Equals(sub.Name, meshName, StringComparison.OrdinalIgnoreCase))
                named = sub.Guid;
        }
        if (named != Guid.Empty) return named;
        return meshCount == 1 ? first : Guid.Empty;
    }

    private static bool TryResolveMappedEntry(string assetPath, string unityGuid,
        out string relative, out AssetEntry? entry)
    {
        relative = string.Empty;
        entry = null;
        string? absolute = UnityGuidMap.ResolveToAbsolutePath(assetPath, unityGuid);
        if (absolute == null) return false;

        EditorAssetBackend? database = EditorAssetBackend.Instance;
        string? assetsRoot = Project.Current?.AssetsPath;
        if (database == null || string.IsNullOrEmpty(assetsRoot)) return false;

        string assetsFull = Path.GetFullPath(assetsRoot);
        string absoluteFull = Path.GetFullPath(absolute);
        string prefix = assetsFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (!absoluteFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !absoluteFull.Equals(assetsFull, StringComparison.OrdinalIgnoreCase))
            return false;

        relative = EditorAssetBackend.NormalizePath(Path.GetRelativePath(assetsFull, absoluteFull));
        entry = database.GetEntry(relative);
        return entry != null;
    }
}
