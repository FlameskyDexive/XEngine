// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Resolves the Inspector animation-preview avatar and builds a debug-skeleton fallback
/// from clip bone paths when no Model/Prefab is available (M17b).
/// </summary>
public static class AnimationPreviewAvatar
{
    public static bool IsPreviewable(EngineObject? asset)
    {
        if (asset is Mesh or Material or PrefabAsset or Texture2D
            or AnimationClip or XEngine.Animation.AnimatorController
            or XEngine.Animation.BlendTree or XEngine.Animation.AvatarMask)
            return true;
        return asset != null && asset.GetType().Name == "TimelineAsset";
    }

    public static bool IsAnimationPreview(EngineObject? asset)
        => asset is AnimationClip or XEngine.Animation.AnimatorController
            or XEngine.Animation.BlendTree or XEngine.Animation.AvatarMask
            || (asset != null && asset.GetType().Name == "TimelineAsset");

    public static EngineObject? ResolveAvatar(Guid previewAssetGuid)
    {
        var db = EditorAssetBackend.Instance;
        if (db != null && previewAssetGuid != Guid.Empty
            && db.TryGetParentGuid(previewAssetGuid, out Guid parentGuid))
        {
            EngineObject? parent = AssetDatabase.Get(parentGuid);
            if (parent is PrefabAsset)
                return parent;
        }

        if (Guid.TryParse(EditorSettings.Instance.LastAnimationPreviewAvatarGuid, out Guid last)
            && last != Guid.Empty)
        {
            EngineObject? loaded = AssetDatabase.Get(last);
            if (loaded is PrefabAsset)
                return loaded;
        }

        return null;
    }

    public static void RememberAvatar(EngineObject avatar)
    {
        if (avatar.AssetID == Guid.Empty) return;
        EditorSettings.Instance.LastAnimationPreviewAvatarGuid = avatar.AssetID.ToString("N");
        EditorSettings.Instance.Save();
    }

    public static bool TryAcceptDrop(object? payload, out EngineObject? avatar)
    {
        avatar = null;
            if (payload is not AssetDragPayload drag) return false;
        EngineObject? loaded = AssetDatabase.Get(drag.AssetGuid);
        if (loaded is not PrefabAsset) return false;
        avatar = loaded;
        RememberAvatar(loaded);
        return true;
    }

    /// <summary>
    /// Builds an empty GameObject hierarchy whose child names match <paramref name="clip"/> bone
    /// paths, so <see cref="Animator"/> can write TRS. Tiny cubes mark each bone for orbit preview.
    /// </summary>
    public static GameObject BuildSkeletonSubject(AnimationClip clip)
    {
        var root = new GameObject("PreviewSkeleton");
        var nodes = new Dictionary<string, GameObject>(StringComparer.Ordinal) { [""] = root };

        var bones = clip.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            string path = bones[i].BoneName ?? "";
            if (string.IsNullOrEmpty(path)) continue;
            EnsureBonePath(nodes, path);
        }

        return root;
    }

    private static GameObject EnsureBonePath(Dictionary<string, GameObject> nodes, string path)
    {
        if (nodes.TryGetValue(path, out GameObject? existing))
            return existing;

        int slash = path.LastIndexOf('/');
        string parentPath = slash < 0 ? "" : path[..slash];
        string name = slash < 0 ? path : path[(slash + 1)..];
        GameObject parent = EnsureBonePath(nodes, parentPath);

        var go = new GameObject(name);
        go.SetParent(parent);
        AttachBoneMarker(go);
        nodes[path] = go;
        return go;
    }

    private static Mesh? s_boneMesh;
    private static Material? s_boneMat;

    private static void AttachBoneMarker(GameObject go)
    {
        try
        {
            s_boneMesh ??= Mesh.CreateCube(new Float3(0.04f, 0.04f, 0.04f));
            s_boneMat ??= new Material(Shader.LoadDefault(DefaultShader.Unlit));
            var mr = go.AddComponent<MeshRenderer>();
            mr.Mesh = s_boneMesh;
            mr.Material = s_boneMat;
        }
        catch
        {
            // Headless tests have no GPU shader compiler; hierarchy still animates via Transforms.
        }
    }
}
