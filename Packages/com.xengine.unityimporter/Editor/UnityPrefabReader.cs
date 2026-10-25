using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.UnityImporter.Editor;

/// <summary>A Transform node from a Unity prefab, with resolved GameObject name and local TRS.</summary>
public sealed class PrefabTransform
{
    public long Id;
    public long GameObjectId;
    public string Name = string.Empty;
    public bool ActiveSelf = true;
    public float[] Position = { 0, 0, 0 };
    public float[] Rotation = { 0, 0, 0, 1 }; // xyzw
    public float[] Scale = { 1, 1, 1 };
    public PrefabTransform? Father;
    public List<PrefabTransform> Children = new();

    public IEnumerable<PrefabTransform> SelfAndChildren
    {
        get
        {
            yield return this;
            foreach (PrefabTransform child in Children)
                foreach (PrefabTransform c in child.SelfAndChildren)
                    yield return c;
        }
    }
}

/// <summary>A SkinnedMeshRenderer component (class 137) from a Unity prefab.</summary>
public sealed class PrefabSkinnedMesh
{
    public PrefabTransform Node = null!;
    public string MeshGuid = string.Empty;
    public long MeshFileId;
    public List<string> MaterialGuids = new();
    public List<long> BoneTransformIds = new();
    public long RootBoneId;
    public bool Enabled = true;
}

/// <summary>Reads a Unity .prefab: GameObject names, Transform hierarchy, and SkinnedMeshRenderers.</summary>
public sealed class UnityPrefabData
{
    public Dictionary<long, PrefabTransform> Transforms = new();
    public PrefabTransform? Root;
    public List<PrefabSkinnedMesh> SkinnedMeshes = new();

    public static UnityPrefabData Read(string path)
    {
        List<UnityYamlDocument> docs = UnityYaml.ParseFile(path);
        var data = new UnityPrefabData();

        // GameObject id → name.
        var names = new Dictionary<long, string>();
        var activeStates = new Dictionary<long, bool>();
        foreach (UnityYamlDocument doc in docs)
        {
            if (doc.ClassId != 1) continue;
            names[doc.FileId] = doc.Root.ScalarOf("m_Name") ?? string.Empty;
            activeStates[doc.FileId] = (doc.Root.ScalarOf("m_IsActive") ?? "1") != "0";
        }

        // Transforms.
        foreach (UnityYamlDocument doc in docs)
        {
            if (doc.ClassId != 4) continue;
            UnityYamlNode root = doc.Root;
            var t = new PrefabTransform
            {
                Id = doc.FileId,
                GameObjectId = root["m_GameObject"]?.LongOf("fileID") ?? 0,
                Name = names.TryGetValue(root["m_GameObject"]?.LongOf("fileID") ?? 0, out string? n) ? n : string.Empty,
                ActiveSelf = !activeStates.TryGetValue(root["m_GameObject"]?.LongOf("fileID") ?? 0, out bool active) || active,
                Position = ReadXYZ(root["m_LocalPosition"]),
                Rotation = ReadQuat(root["m_LocalRotation"]),
                Scale = ReadXYZ(root["m_LocalScale"]),
            };
            data.Transforms[t.Id] = t;
        }

        // Link hierarchy.
        foreach (UnityYamlDocument doc in docs)
        {
            if (doc.ClassId != 4) continue;
            PrefabTransform t = data.Transforms[doc.FileId];
            long father = doc.Root["m_Father"]?.LongOf("fileID") ?? 0;
            if (father != 0 && data.Transforms.TryGetValue(father, out PrefabTransform? parent))
            {
                t.Father = parent;
                parent.Children.Add(t);
            }
            foreach (UnityYamlNode child in doc.Root["m_Children"]?.List ?? new List<UnityYamlNode>())
            {
                long childId = child.LongOf("fileID");
                if (childId != 0 && data.Transforms.TryGetValue(childId, out PrefabTransform? childT) && childT.Father == null)
                    childT.Father = t;
            }
        }

        foreach (PrefabTransform candidate in data.Transforms.Values)
        {
            if (candidate.Father == null) { data.Root = candidate; break; }
        }

        // SkinnedMeshRenderers.
        foreach (UnityYamlDocument doc in docs)
        {
            if (doc.ClassId != 137) continue;
            UnityYamlNode root = doc.Root;
            long goId = root["m_GameObject"]?.LongOf("fileID") ?? 0;
            PrefabTransform? node = null;
            foreach (PrefabTransform candidate in data.Transforms.Values)
            {
                if (candidate.GameObjectId == goId) { node = candidate; break; }
            }
            if (node == null) continue;

            var smr = new PrefabSkinnedMesh
            {
                Node = node,
                MeshGuid = root["m_Mesh"]?.ScalarOf("guid") ?? string.Empty,
                MeshFileId = root["m_Mesh"]?.LongOf("fileID") ?? 0,
                RootBoneId = root["m_RootBone"]?.LongOf("fileID") ?? 0,
                Enabled = (root.ScalarOf("m_Enabled") ?? "1") != "0",
            };
            foreach (UnityYamlNode mat in root["m_Materials"]?.List ?? new List<UnityYamlNode>())
            {
                string? guid = mat.ScalarOf("guid");
                if (!string.IsNullOrEmpty(guid)) smr.MaterialGuids.Add(guid);
            }
            foreach (UnityYamlNode bone in root["m_Bones"]?.List ?? new List<UnityYamlNode>())
            {
                long boneId = bone.LongOf("fileID");
                if (boneId != 0) smr.BoneTransformIds.Add(boneId);
            }
            data.SkinnedMeshes.Add(smr);
        }

        return data;
    }

    private static float[] ReadXYZ(UnityYamlNode? node) => node == null
        ? new float[] { 0, 0, 0 }
        : new[] { node.FloatOf("x"), node.FloatOf("y"), node.FloatOf("z") };

    private static float[] ReadQuat(UnityYamlNode? node) => node == null
        ? new float[] { 0, 0, 0, 1 }
        : new[] { node.FloatOf("x"), node.FloatOf("y"), node.FloatOf("z"), node.FloatOf("w") };
}
