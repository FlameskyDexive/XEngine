// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Animation;

/// <summary>
/// One resolved blend-shape write target: the renderer and the index of the shape on its mesh.
/// </summary>
public readonly struct BlendShapeTarget
{
    public readonly SkinnedMeshRenderer? Renderer;
    public readonly int ShapeIndex;

    public BlendShapeTarget(SkinnedMeshRenderer? renderer, int shapeIndex)
    {
        Renderer = renderer;
        ShapeIndex = shapeIndex;
    }
}

/// <summary>
/// Resolves animation-clip bone paths and blend-shape (path, name) keys to compact integer indices
/// into a <see cref="Pose"/>, and remembers the live <see cref="Transform"/> / renderer each index
/// writes to. Built once per <see cref="Animator"/> enable by walking the GameObject hierarchy;
/// looked up by integer at runtime (no per-frame string dictionary hit).
/// </summary>
/// <remarks>
/// <para>
/// Path convention matches the legacy <c>AnimationComponent</c> and the importer: bone paths are
/// slash-separated and relative to the animation root, <em>not</em> including the root's own name
/// (so a direct child of root has path <c>"Hips"</c>, not <c>"Armature/Hips"</c>). A blend-shape
/// whose renderer <em>is</em> the root has an empty path <c>""</c>.
/// </para>
/// <para>
/// <see cref="Build"/> is a cold path (called on enable / skeleton rebuild); allocations there are
/// fine. Runtime lookups (<see cref="IndexOfBonePath"/> / <see cref="IndexOfBlendShape"/>) are
/// dictionary reads used only at clip-bind time, not per frame.
/// </para>
/// </remarks>
public sealed class SkeletonBinding
{
    private readonly Dictionary<string, int> _bonePathToIndex;
    private readonly Dictionary<(string path, string shape), int> _blendShapeKeyToIndex;

    /// <summary>Bone write targets, indexed parallel to <see cref="Pose.Bones"/>. Null entries are unresolved.</summary>
    public readonly Transform?[] Bones;

    /// <summary>Bone paths, indexed parallel to <see cref="Bones"/> (for mask subtree matching).</summary>
    public readonly string[] Paths;

    /// <summary>
    /// Destination skeleton's immutable local reference pose, captured when the binding is built
    /// and indexed parallel to <see cref="Bones"/>. Clip switches must read this snapshot instead
    /// of the live transforms, which may already contain the previous state's animated pose.
    /// </summary>
    public readonly BonePose[] ReferencePose;

    /// <summary>Blend-shape write targets, indexed parallel to <see cref="Pose.BlendShapeWeights"/>.</summary>
    public readonly BlendShapeTarget[] BlendShapes;

    public readonly Transform Root;
    // Common ancestor of the skinned renderers' roots. Only its ancestor chain can carry
    // whole-character travel; descendant weapon/attachment motion must remain authored.
    internal readonly Transform? MotionRoot;
    // Animator configures these before graph evaluation. New root policies pin each leaf's
    // own pose before blending, so a Disabled state's motion cannot be erased by another state.
    internal bool PinRootMotionPerState;
    internal bool ExtractRootMotion;
    internal bool PlanarRootMotion;
    internal bool RotateRootMotion;
    internal Transform? RootMotionBody;
    internal readonly bool HasSkinnedRenderer;
    public int BoneCount => Bones.Length;
    public int BlendShapeCount => BlendShapes.Length;

    private SkeletonBinding(
        Dictionary<string, int> bonePathToIndex,
        Transform?[] bones,
        string[] paths,
        BonePose[] referencePose,
        BlendShapeTarget[] blendShapes,
        Dictionary<(string, string), int> blendShapeKeyToIndex,
        Transform root, Transform? motionRoot)
    {
        _bonePathToIndex = bonePathToIndex;
        Bones = bones;
        Paths = paths;
        ReferencePose = referencePose;
        BlendShapes = blendShapes;
        _blendShapeKeyToIndex = blendShapeKeyToIndex;
        Root = root;
        MotionRoot = motionRoot;
        HasSkinnedRenderer = root.GameObject.GetComponentInChildren<SkinnedMeshRenderer>(includeSelf: true, includeInactive: true) != null;
    }

    /// <summary>The path of the bone at <paramref name="index"/>, or empty if out of range.</summary>
    public string GetBonePath(int index)
        => (uint)index < (uint)Paths.Length ? Paths[index] : string.Empty;

    /// <summary>Returns the bone index for <paramref name="path"/>, or -1 if the path doesn't resolve.</summary>
    public int IndexOfBonePath(string path)
        => _bonePathToIndex.TryGetValue(path, out int i) ? i : -1;

    /// <summary>Returns the blend-shape index for the given (path, name) key, or -1 if unresolved.</summary>
    public int IndexOfBlendShape(string rendererPath, string shapeName)
        => _blendShapeKeyToIndex.TryGetValue((rendererPath, shapeName), out int i) ? i : -1;

    /// <summary>
    /// Walks the hierarchy under <paramref name="root"/> and builds the binding: every descendant
    /// Transform becomes a bone index keyed by its path-from-root, and every blend shape on every
    /// <see cref="SkinnedMeshRenderer"/> in the subtree becomes a blend-shape index keyed by
    /// (renderer-path, shape-name). The root itself is excluded from the bone table (its path would
    /// be empty); a blend-shape on the root itself keeps path <c>""</c>.
    /// </summary>
    public static SkeletonBinding Build(Transform root)
    {
        var pathToIndex = new Dictionary<string, int>();
        var bones = new List<Transform?>();
        var paths = new List<string>();

        // Bones: children of root, recursing. Root itself is intentionally not added.
        var children = root.GameObject.Children;
        for (int i = 0; i < children.Count; i++)
            WalkBones(children[i].Transform, string.Empty, pathToIndex, bones, paths);

        // Blend shapes: every node in the subtree (including root).
        var bsTargets = new List<BlendShapeTarget>();
        var bsKeyToIndex = new Dictionary<(string, string), int>();
        Transform? motionRoot = null;
        WalkBlendShapes(root, root, bsTargets, bsKeyToIndex, ref motionRoot);

        Transform?[] boneArray = bones.ToArray();
        var referencePose = new BonePose[boneArray.Length];
        for (int i = 0; i < boneArray.Length; i++)
        {
            Transform? bone = boneArray[i];
            if (bone == null) continue;
            referencePose[i] = new BonePose
            {
                LocalPosition = bone.LocalPosition,
                LocalRotation = bone.LocalRotation,
                LocalScale = bone.LocalScale,
                Channels = PoseChannel.All,
            };
        }

        return new SkeletonBinding(
            pathToIndex,
            boneArray,
            paths.ToArray(),
            referencePose,
            bsTargets.ToArray(),
            bsKeyToIndex,
            root, motionRoot);
    }

    private static void WalkBones(Transform t, string parentPath, Dictionary<string, int> map, List<Transform?> bones, List<string> paths)
    {
        string name = t.GameObject.Name;
        string path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;

        // First occurrence wins (matches legacy TryAdd semantics).
        if (!map.ContainsKey(path))
        {
            map[path] = bones.Count;
            bones.Add(t);
            paths.Add(path);
        }

        var children = t.GameObject.Children;
        for (int i = 0; i < children.Count; i++)
            WalkBones(children[i].Transform, path, map, bones, paths);
    }

    private static void WalkBlendShapes(
        Transform t, Transform root,
        List<BlendShapeTarget> targets, Dictionary<(string, string), int> map,
        ref Transform? motionRoot)
    {
        var smr = t.GameObject.GetComponent<SkinnedMeshRenderer>();
        if (smr != null)
        {
            Transform? skinRoot = smr.RootBone;
            if (skinRoot != null && IsAncestorOf(root, skinRoot))
            {
                if (motionRoot == null) motionRoot = skinRoot;
                else
                    while (motionRoot != null && !IsAncestorOf(motionRoot, skinRoot))
                        motionRoot = motionRoot.Parent;
            }
            var mesh = smr.SharedMesh.Res;
            if (mesh != null)
            {
                string rendererPath = Transform.GetRelativePath(t, root);
                int count = mesh.BlendShapeCount;
                for (int i = 0; i < count; i++)
                {
                    string shapeName = mesh.GetBlendShapeName(i);
                    var key = (rendererPath, shapeName);
                    if (!map.ContainsKey(key))
                    {
                        map[key] = targets.Count;
                        targets.Add(new BlendShapeTarget(smr, i));
                    }
                }
            }
        }

        var children = t.GameObject.Children;
        for (int i = 0; i < children.Count; i++)
            WalkBlendShapes(children[i].Transform, root, targets, map, ref motionRoot);
    }

    internal static bool IsAncestorOf(Transform ancestor, Transform descendant)
    {
        for (Transform? current = descendant; current != null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
