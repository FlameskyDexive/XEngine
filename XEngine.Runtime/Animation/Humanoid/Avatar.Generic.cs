// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using XEngine.Vector;

namespace XEngine.Animation.Humanoid;

/// <summary>Zero preserves the meaning of existing Avatar assets with no type field.</summary>
public enum AvatarRigKind { Humanoid = 0, Generic = 1 }

public partial class Avatar
{
    public AvatarRigKind RigKind = AvatarRigKind.Humanoid;
    public bool IsHumanoid => RigKind == AvatarRigKind.Humanoid;

    /// <summary>One Generic transform, including sockets and non-skinned nodes, in source order.</summary>
    public sealed class SkeletonNode
    {
        /// <summary>Model-relative full path. "." identifies the model transform itself.</summary>
        public string Path = ".";
        /// <summary>Index of the direct parent, preceding this node; -1 only for the model root.</summary>
        public int ParentIndex = -1;
        public Float3 LocalPosition;
        public Quaternion LocalRotation = Quaternion.Identity;
        public Float3 LocalScale = Float3.One;
    }

    /// <summary>Generic reference hierarchy. No Humanoid roles, muscles or limb measurements.</summary>
    public List<SkeletonNode> GenericSkeleton = new();

    /// <summary>
    /// Snapshots the entire model hierarchy without animation or skin-palette dependencies.
    /// Empty rootMotionBonePath describes no motion root; "." describes the model transform.
    /// This descriptor does not select runtime root-motion policy or alter clip curves.
    /// </summary>
    public static Avatar BuildGeneric(Transform modelRoot, string rootMotionBonePath = "", string name = "Avatar")
    {
        ArgumentNullException.ThrowIfNull(modelRoot);
        var avatar = new Avatar { Name = name, RigKind = AvatarRigKind.Generic,
            RootMotionBonePath = rootMotionBonePath ?? "" };
        try
        {
            CollectGeneric(modelRoot, ".", -1, avatar.GenericSkeleton);
            string? problems = avatar.ValidateGeneric();
            if (problems != null) throw new ArgumentException(problems, nameof(rootMotionBonePath));
            return avatar;
        }
        catch { avatar.Dispose(); throw; }
    }

    private static void CollectGeneric(Transform node, string path, int parent, List<SkeletonNode> result)
    {
        int index = result.Count;
        result.Add(new SkeletonNode { Path = path, ParentIndex = parent,
            LocalPosition = node.LocalPosition, LocalRotation = node.LocalRotation, LocalScale = node.LocalScale });
        for (int i = 0; i < node.ChildCount; i++)
        {
            Transform child = node.GetChild(i);
            string name = child.GameObject.Name;
            if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
                throw new ArgumentException("Generic skeleton node has an unrepresentable path component: " + name);
            CollectGeneric(child, path == "." ? name : path + "/" + name, index, result);
        }
    }

    private string? ValidateGeneric()
    {
        if (Mappings == null || Mappings.Count != 0) return "Generic Avatar must not contain Humanoid mappings";
        if (GenericSkeleton == null || GenericSkeleton.Count == 0) return "Generic Avatar has no reference hierarchy";
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < GenericSkeleton.Count; i++)
        {
            SkeletonNode node = GenericSkeleton[i];
            if (node == null || !IsGenericPath(node.Path)) return "invalid Generic skeleton path at index " + i;
            if (!paths.Add(node.Path)) return "duplicate Generic skeleton path: " + node.Path;
            if (i == 0)
            {
                if (node.Path != "." || node.ParentIndex != -1) return "Generic hierarchy must begin with model root '.'";
            }
            else
            {
                if ((uint)node.ParentIndex >= (uint)i) return "invalid Generic parent index for " + node.Path;
                int slash = node.Path.LastIndexOf('/');
                string expectedParent = slash < 0 ? "." : node.Path[..slash];
                if (node.Path == "." || GenericSkeleton[node.ParentIndex].Path != expectedParent)
                    return "Generic parent path mismatch for " + node.Path;
            }
            if (!Finite(node.LocalPosition) || !Finite(node.LocalScale)
                || !float.IsFinite(node.LocalRotation.X) || !float.IsFinite(node.LocalRotation.Y)
                || !float.IsFinite(node.LocalRotation.Z) || !float.IsFinite(node.LocalRotation.W)
                || Quaternion.LengthSquared(node.LocalRotation) < 1e-12f)
                return "invalid Generic reference TRS for " + node.Path;
        }
        if (!string.IsNullOrEmpty(RootMotionBonePath) && !paths.Contains(RootMotionBonePath))
            return "Generic root path is missing: " + RootMotionBonePath;
        return null;
    }

    private static bool Finite(Float3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsGenericPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path == ".") return true;
        if (path.Contains('\\')) return false;
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/') continue;
            int length = i - start;
            if (length == 0 || (length == 1 && path[start] == '.')
                || (length == 2 && path[start] == '.' && path[start + 1] == '.')) return false;
            start = i + 1;
        }
        return true;
    }
}
