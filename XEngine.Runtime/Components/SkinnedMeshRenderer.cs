// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Renders a skinned mesh deformed by bone Transforms.
/// Bones are referenced by path (relative to this GO's root)
/// Paths like "Armature/Hips/Spine" are resolved via Transform.Find().
/// Supports multiple materials via submeshes.
/// Bone matrices are uploaded via a float texture (no uniform array size limit).
/// </summary>
[AddComponentMenu("Rendering/Skinned Mesh Renderer")]
[ComponentIcon("\uf1b3")] // Cubes
[GenerateSerializer]
public partial class SkinnedMeshRenderer : MonoBehaviour
{
    /// <summary>The mesh to render (may contain submeshes).</summary>
    public AssetRef<Mesh> SharedMesh;

    /// <summary>Materials array one per submesh. If fewer materials than submeshes, last material is reused.</summary>
    public List<AssetRef<Material>> Materials = new();

    /// <summary>Path to the root bone, relative to this GO's hierarchy root.</summary>
    [SerializeField]
    public string? RootBonePath;

    /// <summary>
    /// Paths to all bone Transforms, indexed to match Mesh.BindPoses.
    /// Each path is relative to this GO's hierarchy root (e.g. "Armature/Hips/Spine").
    /// </summary>
    [SerializeField]
    public string[]? BonePaths;

    public Color MainColor = Color.White;

    /// <summary>Index into <c>Scene.BakedLighting.Lightmaps</c>, or -1 if not lightmapped. Assigned by the bake.</summary>
    [HideInInspector] public int LightmapIndex = -1;

    /// <summary>UV2 → atlas transform: <c>uv2 * xy + zw</c>. Assigned by the lightmap bake.</summary>
    [HideInInspector] public Float4 LightmapScaleOffset = new(1, 1, 0, 0);

    /// <summary>Which rendering layers this renderer belongs to. Independent of <see cref="GameObject.LayerIndex"/>.</summary>
    public RenderingLayerMask RenderingLayers = RenderingLayerMask.Everything;

    // Resolved at runtime
    [System.NonSerialized] private Transform? _rootBone;
    [System.NonSerialized] private Transform?[]? _bones;
    [System.NonSerialized] private Float4x4[]? _skinMatrices;
    [System.NonSerialized] private bool _resolved;
    [System.NonSerialized] private Mesh? _resolvedMesh;
    [System.NonSerialized] private string[]? _resolvedMeshNames;
    [System.NonSerialized] private Float4x4[]? _resolvedMeshBinds;
    [System.NonSerialized] private string[]? _resolvedPaths;

    // Skinning is only recomputed when the skeleton actually moves. The dirty check is the sum of every
    // bone's Transform.Version plus the renderer's - versions are monotonic, so an unchanged sum means a
    // static pose (skip the whole skin loop + texture upload). This also dedups redundant per-camera
    // OnRenderCollect calls in the same frame (nothing changed between cameras).
    [System.NonSerialized] private ulong _lastSkeletonVersion = ulong.MaxValue;
    [System.NonSerialized] private AABB _cachedBounds;
    // Reused per-recompute memo so bones sharing ancestors don't re-walk the chain to root each:
    // O(bones + depth) world-matrix builds instead of O(bones * depth).
    [System.NonSerialized] private readonly Dictionary<Transform, Float4x4> _worldMemo = new();

    // XENGINE_SKIN_DIAG=1 one-shot per-renderer skinning audit (see DumpSkinDiagnostics).
    [System.NonSerialized] private static readonly HashSet<int> _diagLogged = new();

    /// <summary>
    /// One-shot per-renderer skinning audit, enabled by XENGINE_SKIN_DIAG=1. Measures the four
    /// classic shredding causes directly: unresolved bone paths (identity matrices frozen in mesh
    /// space), bindpose count mismatch, vertex bone-index overflow, and per-bone matrix scale /
    /// translation anomalies (unit mismatches, e.g. cm skeleton vs m bindposes).
    /// </summary>
    private void DumpSkinDiagnostics(Mesh mesh)
    {
        if (_bones == null || _skinMatrices == null) return;
        int nullBones = 0;
        int bindPoseMissing = 0;
        var unresolved = new List<string>();
        for (int i = 0; i < _bones.Length; i++)
        {
            if (_bones[i] == null)
            {
                nullBones++;
                string path = BonePaths != null && i < BonePaths.Length ? BonePaths[i] : $"<index {i}>";
                unresolved.Add(path ?? "<null path>");
            }
            if (mesh.BindPoses == null || i >= mesh.BindPoses.Length)
                bindPoseMissing++;
        }

        float maxDet = 0f, minDet = float.MaxValue, maxT = 0f;
        int worstDet = -1, worstT = -1;
        for (int i = 0; i < _skinMatrices.Length; i++)
        {
            ref readonly Float4x4 m = ref _skinMatrices[i];
            Float3 c0 = new(m.c0.X, m.c0.Y, m.c0.Z);
            Float3 c1 = new(m.c1.X, m.c1.Y, m.c1.Z);
            Float3 c2 = new(m.c2.X, m.c2.Y, m.c2.Z);
            float det = Float3.Dot(Float3.Cross(c0, c1), c2); // = scale^2 for uniform scale
            if (det > maxDet) { maxDet = det; worstDet = i; }
            if (det < minDet) minDet = det;
            float t = Float3.Length(new Float3(m.c3.X, m.c3.Y, m.c3.Z));
            if (t > maxT) { maxT = t; worstT = i; }
        }

        int maxIndex = 0;
        if (mesh.HasBoneIndices && mesh.isReadable)
        {
            Float4[] idx = mesh.BoneIndices;
            for (int i = 0; i < idx.Length; i++)
            {
                Float4 v = idx[i];
                maxIndex = Math.Max(maxIndex, (int)MathF.Max(MathF.Max(v.X, v.Y), MathF.Max(v.Z, v.W)));
            }
        }

        string name = GameObject.Name;
        Debug.LogWarning($"[SkinDiag] '{name}' mesh='{mesh.Name}' verts={mesh.VertexCount} " +
            $"bones={_bones.Length} bindPoses={mesh.BindPoses?.Length ?? 0} nullBones={nullBones} " +
            $"bindPoseMissing={bindPoseMissing} texW={_boneTexture?.Width} " +
            $"maxVertexBoneIndex={maxIndex} (1-based, limit={_bones.Length})");

        // Bone ORDER alignment: vertex data references mesh bone order; BonePaths[i] must name the
        // same bone as mesh.BoneNames[i]. A v13 re-import that reordered bones with a scene still
        // carrying old-order paths renders valid matrices on the WRONG bones (sail/shred).
        string[]? meshBoneNames = mesh.BoneNames;
        if (meshBoneNames != null && BonePaths != null)
        {
            int mismatch = 0;
            int pairs = Math.Min(Math.Min(meshBoneNames.Length, BonePaths.Length), _bones.Length);
            var examples = new List<string>();
            for (int i = 0; i < pairs; i++)
            {
                string meshBone = meshBoneNames[i] ?? "";
                string pathBone = BonePaths[i] ?? "";
                int slash = pathBone.LastIndexOf('/');
                if (slash >= 0) pathBone = pathBone[(slash + 1)..];
                if (!string.Equals(meshBone, pathBone, StringComparison.OrdinalIgnoreCase))
                {
                    mismatch++;
                    if (examples.Count < 6)
                        examples.Add($"#{i}: mesh='{meshBone}' path='{pathBone}'");
                }
            }
            Debug.LogWarning($"[SkinDiag] '{name}' boneNameOrderMismatches={mismatch}/{pairs}" +
                (examples.Count > 0 ? " e.g. " + string.Join("; ", examples) : ""));
        }
        for (int i = 0; i < unresolved.Count && i < 8; i++)
            Debug.LogWarning($"[SkinDiag]   !! unresolved bone path: {unresolved[i]}");
        if (unresolved.Count > 8)
        {
            for (int i = 8; i < unresolved.Count; i++)
                Debug.LogWarning($"[SkinDiag]   !! unresolved bone path: {unresolved[i]}");
        }
        Debug.LogWarning($"[SkinDiag] '{name}' matrices: det(max)={maxDet:0.####} det(min)={minDet:0.####} " +
            $"(expect ~1) |t|max={maxT:0.###}m at bone[{worstT}] det-extreme bone[{worstDet}] " +
            $"rootWorld={Transform.LocalToWorldMatrix.Translation}");
    }

    // Bone matrix texture each bone is 4 RGBA32F texels (one per matrix row)
    [System.NonSerialized] private Texture2D? _boneTexture;
    [System.NonSerialized] private int _boneTextureSize; // number of bones the texture was allocated for
    [System.NonSerialized] private PropertyState[]? _propCache; // reused per submesh (same pattern as MeshRenderer)
    // One cached renderable per submesh (see MeshRenderable.Reset for the lifetime contract).
    [System.NonSerialized] private SkinnedMeshRenderable[]? _renderableCache;

    /// <summary>
    /// Per-blend-shape weights (0-100). Length tracks the mesh's blend-shape count.
    /// Serialized so hand-posed weights persist; animation drives these at runtime.
    /// </summary>
    [SerializeField, HideInInspector]
    private float[]? _blendShapeWeights;

    // Active-morph-layer weight texture (rebuilt per frame from current weights): one texel per
    // active layer = (layerIndex, weight, 0, 0). Reused across frames.
    [System.NonSerialized] private Texture2D? _morphWeightTexture;
    [System.NonSerialized] private int _morphWeightCapacity;
    [System.NonSerialized] private Float4[]? _morphWeightData; // reused upload buffer, sized to capacity
    [System.NonSerialized] private readonly List<Float4> _activeMorphLayers = new();

    /// <summary>Number of blend shapes on the shared mesh (0 if none).</summary>
    public int BlendShapeCount => SharedMesh.Res?.BlendShapeCount ?? 0;

    /// <summary>Index of a blend shape by name, or -1 if not found.</summary>
    public int GetBlendShapeIndex(string name) => SharedMesh.Res?.GetBlendShapeIndex(name) ?? -1;

    /// <summary>The blend shape's name, or empty if out of range.</summary>
    public string GetBlendShapeName(int index) => SharedMesh.Res?.GetBlendShapeName(index) ?? string.Empty;

    /// <summary>Get a blend-shape weight (0-100). Returns 0 for invalid indices.</summary>
    public float GetBlendShapeWeight(int index)
    {
        if (_blendShapeWeights == null || index < 0 || index >= _blendShapeWeights.Length) return 0f;
        return _blendShapeWeights[index];
    }

    /// <summary>Set a blend-shape weight (0-100, may exceed for over/under-shooting morphs).</summary>
    public void SetBlendShapeWeight(int index, float weight)
    {
        int count = BlendShapeCount;
        if (index < 0 || index >= count) return;
        EnsureWeightsArray(count);
        _blendShapeWeights![index] = weight;
    }

    /// <summary>Set a blend-shape weight by name.</summary>
    public void SetBlendShapeWeight(string name, float weight)
    {
        int index = GetBlendShapeIndex(name);
        if (index >= 0) SetBlendShapeWeight(index, weight);
    }

    private void EnsureWeightsArray(int count)
    {
        if (_blendShapeWeights != null && _blendShapeWeights.Length == count) return;
        var resized = new float[count];
        if (_blendShapeWeights != null)
            Array.Copy(_blendShapeWeights, resized, Math.Min(_blendShapeWeights.Length, count));
        _blendShapeWeights = resized;
    }

    /// <summary>Get the resolved root bone Transform.</summary>
    public Transform? RootBone
    {
        get { Resolve(SharedMesh.Res); return _rootBone; }
    }

    /// <summary>Get the resolved bone Transforms array.</summary>
    public Transform?[]? Bones
    {
        get { Resolve(SharedMesh.Res); return _bones; }
    }

    /// <summary>
    /// Set bone references from live Transforms. Computes paths relative to the hierarchy root.
    /// Call this during import when you have the live GO hierarchy.
    /// </summary>
    public void SetBones(Transform[] boneTransforms, Transform? rootBone)
    {
        // During import, hierarchy is not yet reparented root is correct
        Transform hierarchyRoot = Transform;
        while (hierarchyRoot.Parent != null) hierarchyRoot = hierarchyRoot.Parent;

        BonePaths = new string[boneTransforms.Length];
        for (int i = 0; i < boneTransforms.Length; i++)
        {
            if (boneTransforms[i] != null)
                BonePaths[i] = Transform.GetRelativePath(boneTransforms[i], hierarchyRoot);
        }

        RootBonePath = rootBone == null ? null : rootBone == hierarchyRoot ? "."
            : Transform.GetRelativePath(rootBone, hierarchyRoot);

        // Also cache resolved references immediately
        _bones = new Transform?[boneTransforms.Length];
        Array.Copy(boneTransforms, _bones, boneTransforms.Length);
        _rootBone = rootBone;
        _resolved = true;
        RememberResolvedMesh(SharedMesh.ResWeak);
        _lastSkeletonVersion = ulong.MaxValue;
    }

    /// <summary>
    /// Find the correct search root for bone path resolution.
    /// Walks up from this GO to find the topmost ancestor, then tries to resolve.
    /// If paths don't resolve from the absolute root (e.g. after reparenting),
    /// tries progressively lower ancestors until paths work.
    /// </summary>
    internal Transform? FindSearchRoot()
    {
        // Collect ancestors from this GO up to root
        var ancestors = new System.Collections.Generic.List<Transform>();
        Transform current = Transform;
        while (current != null)
        {
            ancestors.Add(current);
            current = current.Parent;
        }

        // Try from topmost ancestor first (original import behavior)
        // If that fails (reparented), try each ancestor going down until paths resolve
        // The palette's paths establish its coordinate root independently of the optional
        // Root Bone reference. Editing/clearing that reference must not move the palette.
        if (BonePaths != null)
            for (int i = ancestors.Count - 1; i >= 0; i--)
                for (int b = 0; b < BonePaths.Length; b++)
                    if (!string.IsNullOrEmpty(BonePaths[b]) && ancestors[i].Find(BonePaths[b]) != null)
                        return ancestors[i];

        if (!string.IsNullOrEmpty(RootBonePath) && RootBonePath != ".")
            for (int i = ancestors.Count - 1; i >= 0; i--)
                if (ancestors[i].Find(RootBonePath) != null) return ancestors[i];

        return ancestors[^1]; // fallback
    }

    private void Resolve(Mesh? mesh)
    {
        var names = mesh?.BoneNames; var binds = mesh?.BindPoses;
        if (_resolved && ReferenceEquals(mesh, _resolvedMesh) && ReferenceEquals(names, _resolvedMeshNames)
            && ReferenceEquals(binds, _resolvedMeshBinds) && ReferenceEquals(BonePaths, _resolvedPaths)) return;
        _resolved = true;
        RememberResolvedMesh(mesh);
        _lastSkeletonVersion = ulong.MaxValue;
        _skinMatrices = null;

        _rootBone = null;
        _bones = null;

        Transform? searchRoot = FindSearchRoot();
        if (searchRoot == null) return;

        // Resolve root bone by path
        if (!string.IsNullOrEmpty(RootBonePath))
            _rootBone = RootBonePath == "." ? searchRoot : searchRoot.Find(RootBonePath);

        // Resolve bone array by paths
        var paths = names != null && binds != null && names.Length == binds.Length
            && CanFollowMeshPalette(names, searchRoot) ? names : BonePaths;
        if (paths != null)
        {
            _bones = new Transform?[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                if (ReferenceEquals(paths, names) && string.IsNullOrEmpty(paths[i])) _bones[i] = searchRoot;
                else if (!string.IsNullOrEmpty(paths[i])) _bones[i] = searchRoot.Find(paths[i]);
            }
        }
    }

    private void RememberResolvedMesh(Mesh? mesh)
    {
        _resolvedMesh = mesh; _resolvedMeshNames = mesh?.BoneNames;
        _resolvedMeshBinds = mesh?.BindPoses; _resolvedPaths = BonePaths;
    }

    private bool CanFollowMeshPalette(string[] names, Transform root)
    {
        // Source palette compaction/expansion/reorder preserves path identity. Explicit
        // different-rig mappings keep BonePaths. This scan occurs only on cache invalidation.
        bool subset = true, superset = true;
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] == null || (names[i].Length > 0 && root.Find(names[i]) == null)) return false;
            if (BonePaths != null && Array.IndexOf(BonePaths, names[i]) < 0) subset = false;
        }
        if (BonePaths != null)
            for (int i = 0; i < BonePaths.Length; i++)
                if (Array.IndexOf(names, BonePaths[i]) < 0) superset = false;
        return subset || superset;
    }

    public override void OnEnable()
    {
        OnValidate();
    }

    public override void OnValidate()
    {
        _resolved = false;
        _resolvedMesh = null; _resolvedMeshNames = null; _resolvedMeshBinds = null; _resolvedPaths = null;
        _rootBone = null;
        _bones = null;
        _lastSkeletonVersion = ulong.MaxValue;
    }

    /// <summary>
    /// Recompute the skinning matrices, world-space bounds and bone texture for the current pose.
    /// Called only when the skeleton has actually moved (see the version check in OnRenderCollect).
    /// Each bone's world matrix is built once via <see cref="WorldMatrixOf"/> (memoized so shared
    /// ancestors aren't re-walked) and reused for both the skin matrix and the bounds corner.
    /// </summary>
    private void RecomputeSkinning(Mesh mesh)
    {
        _worldMemo.Clear();
        Float4x4 worldToLocal = Transform.WorldToLocalMatrix;

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        bool anyBone = false;

        for (int i = 0; i < _bones!.Length; i++)
        {
            Transform? bone = _bones[i];
            if (bone != null && i < mesh.BindPoses!.Length)
            {
                Float4x4 boneWorld = WorldMatrixOf(bone);
                _skinMatrices![i] = worldToLocal * boneWorld * mesh.BindPoses[i];

                // Bone world position is the translation column of its world matrix (reuses the walk above).
                Float4 p = boneWorld.Translation;
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y; if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y; if (p.Z > maxZ) maxZ = p.Z;
                anyBone = true;
            }
            else
            {
                _skinMatrices![i] = Float4x4.Identity;
            }
        }

        // Pad by the mesh's max extent so vertices attached to edge bones aren't clipped.
        if (anyBone)
        {
            float padding = MathF.Max(MathF.Max(mesh.bounds.Size.X, mesh.bounds.Size.Y), mesh.bounds.Size.Z) * 0.5f;
            Float3 pad = new Float3(padding);
            _cachedBounds = new AABB(new Float3(minX, minY, minZ) - pad, new Float3(maxX, maxY, maxZ) + pad);
        }
        else
        {
            _cachedBounds = mesh.bounds.TransformBy(Transform.LocalToWorldMatrix);
        }

        UploadBoneTexture(_skinMatrices!);

        if (Environment.GetEnvironmentVariable("XENGINE_SKIN_DIAG") == "1"
            && _boneTexture != null && _diagLogged.Add(-InstanceID))
        {
            Debug.LogWarning($"[SkinDiag][tex] '{GameObject.Name}' boneTexture handle={(uint)_boneTexture.Handle.Handle} " +
                             $"width={_boneTexture.Width} bones={_skinMatrices!.Length}");
            // Attach-space audit: where do the first three bindposes / skin matrices sit, and where
            // do this mesh's raw vertices live? Faces/hairs/weapons are authored in their attach
            // bone's space; the bindpose must carry the bone→SMR offset or the mesh renders ~1 bone
            // length away (strips poking through the floor).
            string[]? boneNames = mesh.BoneNames;
            for (int i = 0; i < Math.Min(3, _skinMatrices.Length); i++)
            {
                Float4x4 bind = mesh.BindPoses != null && i < mesh.BindPoses.Length ? mesh.BindPoses[i] : default;
                ref readonly Float4x4 skin = ref _skinMatrices[i];
                string boneLabel = boneNames != null && i < boneNames.Length ? boneNames[i] : $"bone{i}";
                int slash = boneLabel.LastIndexOf('/');
                if (slash >= 0) boneLabel = boneLabel[(slash + 1)..];
                Float3 skinUp = new(skin.c1.X, skin.c1.Y, skin.c1.Z);
                Debug.LogWarning($"[SkinDiag][space] '{GameObject.Name}'[{i}] '{boneLabel}' " +
                    $"bindT=({bind.c3.X:0.###},{bind.c3.Y:0.###},{bind.c3.Z:0.###}) " +
                    $"skinT=({skin.c3.X:0.###},{skin.c3.Y:0.###},{skin.c3.Z:0.###}) " +
                    $"skinY=({skinUp.X:0.###},{skinUp.Y:0.###},{skinUp.Z:0.###})");
            }
            Float3 vmin = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Float3 vmax = new(float.MinValue, float.MinValue, float.MinValue);
            if (mesh.isReadable && mesh.Vertices is { Length: > 0 } rawVerts)
            {
                foreach (Float3 p in rawVerts)
                {
                    vmin = new Float3(MathF.Min(vmin.X, p.X), MathF.Min(vmin.Y, p.Y), MathF.Min(vmin.Z, p.Z));
                    vmax = new Float3(MathF.Max(vmax.X, p.X), MathF.Max(vmax.Y, p.Y), MathF.Max(vmax.Z, p.Z));
                }
                Debug.LogWarning($"[SkinDiag][space] '{GameObject.Name}' rawVertBBox=({vmin.X:0.##},{vmin.Y:0.##},{vmin.Z:0.##})..({vmax.X:0.##},{vmax.Y:0.##},{vmax.Z:0.##})");
            }
        }

        if (Environment.GetEnvironmentVariable("XENGINE_SKIN_DIAG") != "1") return;
        if (_diagLogged.Add(InstanceID))
        {
            DumpSkinDiagnostics(mesh);
            _diagRecomputeCount = 0;
        }
        else if (++_diagRecomputeCount % 30 == 0)
        {
            // Compact per-animation-sample line: worst matrix stats over time.
            float minDet = float.MaxValue, maxDet = 0f, maxT = 0f;
            for (int i = 0; i < _skinMatrices!.Length; i++)
            {
                ref readonly Float4x4 m = ref _skinMatrices[i];
                Float3 c0 = new(m.c0.X, m.c0.Y, m.c0.Z);
                Float3 c1 = new(m.c1.X, m.c1.Y, m.c1.Z);
                Float3 c2 = new(m.c2.X, m.c2.Y, m.c2.Z);
                float det = MathF.Abs(Float3.Dot(Float3.Cross(c0, c1), c2));
                if (det > maxDet) maxDet = det;
                if (det < minDet) minDet = det;
                maxT = MathF.Max(maxT, Float3.Length(new Float3(m.c3.X, m.c3.Y, m.c3.Z)));
            }
            Debug.LogWarning($"[SkinDiag] '{GameObject.Name}' recompute#{_diagRecomputeCount}: " +
                $"det=[{minDet:0.####}..{maxDet:0.####}] |t|max={maxT:0.###}m{DumpCpuSkinnedBounds(mesh)}{DumpBoneAxisDrift()}");
        }
    }

    /// <summary>
    /// XENGINE_SKIN_DIAG: where does SMR-local +Y point for the first bone's current world matrix?
    /// A ±90° drift means the animation feeds a root-orientation correction (FBX Z-up bake) that
    /// the scene skeleton already carries — tipping the whole rig.
    /// </summary>
    private string DumpBoneAxisDrift()
    {
        if (_bones == null || _bones.Length == 0 || _bones[0] == null)
            return string.Empty;
        Float4x4 worldToLocal = Transform.WorldToLocalMatrix;
        Float4x4 rel = worldToLocal * WorldMatrixOf(_bones[0]!);
        Float3 upNow = new(rel.c1.X, rel.c1.Y, rel.c1.Z); // rel * (0,1,0) = column 1
        return $" bone0Up=({upNow.X:0.###},{upNow.Y:0.###},{upNow.Z:0.###})";
    }

    /// <summary>
    /// XENGINE_SKIN_DIAG: CPU-skin every 16th vertex with the exact matrices uploaded this frame
    /// and return the resulting local-space bounding box. A healthy skinned mesh stays within
    /// roughly the bind-pose bounds (~1-2 m for characters); a huge box means the vertex→matrix
    /// mapping itself deforms wrongly, while a small box with a shredded on-screen render means
    /// the GPU consumes the data differently than the CPU (shader matrix layout).
    /// </summary>
    private string DumpCpuSkinnedBounds(Mesh mesh)
    {
        if (_skinMatrices == null || !mesh.isReadable || mesh.BoneIndices == null || mesh.BoneWeights == null)
            return string.Empty;
        Float3[]? verts = mesh.Vertices;
        if (verts == null || verts.Length == 0)
            return string.Empty;

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int v = 0; v < verts.Length; v += 16)
        {
            Float4 idx = mesh.BoneIndices[v];
            Float4 wgt = mesh.BoneWeights[v];
            Float3 acc = default;
            for (int k = 0; k < 4; k++)
            {
                float w = k switch { 0 => wgt.X, 1 => wgt.Y, 2 => wgt.Z, _ => wgt.W };
                int oneBased = (int)MathF.Round(k switch { 0 => idx.X, 1 => idx.Y, 2 => idx.Z, _ => idx.W });
                if (oneBased <= 0 || w <= 0f || oneBased > _skinMatrices.Length)
                    continue;
                ref readonly Float4x4 m = ref _skinMatrices[oneBased - 1];
                Float3 p = verts[v];
                acc += new Float3(
                    m.c0.X * p.X + m.c1.X * p.Y + m.c2.X * p.Z + m.c3.X,
                    m.c0.Y * p.X + m.c1.Y * p.Y + m.c2.Y * p.Z + m.c3.Y,
                    m.c0.Z * p.X + m.c1.Z * p.Y + m.c2.Z * p.Z + m.c3.Z) * w;
            }
            minX = MathF.Min(minX, acc.X); maxX = MathF.Max(maxX, acc.X);
            minY = MathF.Min(minY, acc.Y); maxY = MathF.Max(maxY, acc.Y);
            minZ = MathF.Min(minZ, acc.Z); maxZ = MathF.Max(maxZ, acc.Z);
        }
        return $" cpuSkinnedBBox=({minX:0.##},{minY:0.##},{minZ:0.##})..({maxX:0.##},{maxY:0.##},{maxZ:0.##})";
    }

    [System.NonSerialized] private int _diagRecomputeCount;

    /// <summary>
    /// World matrix of a Transform, memoized in <see cref="_worldMemo"/> for the current recompute.
    /// Mirrors <see cref="Transform.LocalToWorldMatrix"/> but avoids re-walking shared ancestor chains.
    /// </summary>
    private Float4x4 WorldMatrixOf(Transform t)
    {
        if (_worldMemo.TryGetValue(t, out Float4x4 cached))
            return cached;

        Float4x4 local = Float4x4.CreateTRS(t.LocalPosition, t.LocalRotation, t.LocalScale);
        Transform? parent = t.Parent;
        Float4x4 world = parent != null ? WorldMatrixOf(parent) * local : local;
        _worldMemo[t] = world;
        return world;
    }

    public override void OnDisable()
    {
        if (_boneTexture != null) _boneTexture.Dispose();
        _boneTexture = null;
        _boneTextureSize = 0;

        if (_morphWeightTexture != null) _morphWeightTexture.Dispose();
        _morphWeightTexture = null;
        _morphWeightCapacity = 0;
        _morphWeightData = null;
    }

    /// <summary>
    /// Ensures the bone matrix texture exists and is large enough for the given bone count.
    /// Each bone occupies 4 texels (one per matrix row) in a single-row RGBA32F texture.
    /// </summary>
    private void EnsureBoneTexture(int boneCount)
    {
        if (_boneTexture != null && _boneTextureSize >= boneCount)
            return;

        if (_boneTexture != null) _boneTexture.Dispose();

        // Width = boneCount * 4 (4 texels per mat4), Height = 1
        uint width = (uint)(boneCount * 4);
        _boneTexture = new Texture2D(width, 1, false, TextureImageFormat.Float4);
        _boneTexture.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
        Graphics.SetWrapS(_boneTexture.Handle, TextureWrap.ClampToEdge);
        Graphics.SetWrapT(_boneTexture.Handle, TextureWrap.ClampToEdge);
        _boneTextureSize = boneCount;
    }

    /// <summary>
    /// Uploads bone matrices to the texture. Each mat4 is stored as 4 consecutive RGBA32F texels.
    /// Layout: texel[bone*4+0] = column0 through texel[bone*4+3] = column3.
    /// </summary>
    private unsafe void UploadBoneTexture(Float4x4[] matrices)
    {
        int boneCount = matrices.Length;
        EnsureBoneTexture(boneCount);

        // Float4x4 is column-major (c0,c1,c2,c3). We store columns as texels.
        // Shader reconstructs: mat4(col0, col1, col2, col3)
        // Each Float4x4 is 4 Float4 columns laid out contiguously in memory (c0, c1, c2, c3)
        // So we can upload the whole array directly 4 texels per matrix, boneCount*4 texels total
        fixed (Float4x4* ptr = matrices)
        {
            _boneTexture!.SetDataPtr(ptr, 0, 0, (uint)(boneCount * 4), 1);
        }
    }

    // Set by PrepareBlendShapes each frame; consumed by ApplyBlendShapeProps per submesh.
    [System.NonSerialized] private bool _morphReady;
    [System.NonSerialized] private int _morphActiveCount;
    // ★ N8 — last uploaded layer list: when the resolved layers are identical to the previous
    // frame's (static body-customization poses), the weight texture upload is skipped entirely.
    [System.NonSerialized] private readonly List<Float4> _uploadedMorphLayers = new();

    /// <summary>
    /// Once per frame: builds the mesh's static delta textures (first use), resolves the current
    /// weights into active morph layers, and uploads the per-renderer weight texture. Cheap when idle.
    /// </summary>
    private void PrepareBlendShapes(Mesh mesh)
    {
        _morphReady = false;
        _morphActiveCount = 0;

        int shapeCount = mesh.BlendShapeCount;
        EnsureWeightsArray(shapeCount);

        // Skip all morph work (including building the delta textures) while every weight is zero,
        // so un-morphed meshes cost no VRAM or per-frame upload. The shader loop is a no-op then.
        bool anyActive = false;
        for (int s = 0; s < shapeCount; s++)
            if (MathF.Abs(_blendShapeWeights![s]) >= 1e-4f) { anyActive = true; break; }
        if (!anyActive)
            return;

        mesh.EnsureMorphTextures();
        if (mesh.MorphPositionTexture == null)
            return; // Morph textures unavailable (e.g. data too large) shader loop stays a no-op.

        // Resolve weights -> active (layer, weight) pairs, interpolating between frames by weight.
        // Pure math lives in MorphLayout (unit-testable without a GPU); the contract it must match
        // is documented there (single-frame scaling, frame clamping, between-frame interpolation).
        MorphLayout.ResolveLayers(mesh.BlendShapes, _blendShapeWeights, mesh.MorphLayerOffsets, _activeMorphLayers);

        _morphActiveCount = _activeMorphLayers.Count;

        // ★ N8 — upload only when the resolved layers changed. Static poses (the common
        // body-customization case: a slider is set once, then every frame re-resolves the same
        // layers) skip the texture upload completely.
        bool layersChanged = _uploadedMorphLayers.Count != _activeMorphLayers.Count;
        if (!layersChanged)
        {
            for (int i = 0; i < _activeMorphLayers.Count; i++)
            {
                Float4 uploaded = _uploadedMorphLayers[i];
                Float4 current = _activeMorphLayers[i];
                if (MathF.Abs(uploaded.X - current.X) > 1e-5f ||
                    MathF.Abs(uploaded.Y - current.Y) > 1e-5f)
                {
                    layersChanged = true;
                    break;
                }
            }
        }
        if (layersChanged || _morphWeightTexture == null)
        {
            UploadMorphWeightTexture();
            _uploadedMorphLayers.Clear();
            _uploadedMorphLayers.AddRange(_activeMorphLayers);
        }

        _morphReady = true;
    }

    /// <summary>Binds the morph uniforms onto a submesh's <see cref="PropertyState"/>. Cheap dictionary writes.</summary>
    private void ApplyBlendShapeProps(Mesh mesh, PropertyState props)
    {
        if (!_morphReady)
        {
            props.SetInt("morphActiveCount", 0);
            return;
        }

        Texture2D posTex = mesh.MorphPositionTexture!;
        props.SetTexture("morphPositionTexture", posTex);
        props.SetTexture("morphNormalTexture", mesh.MorphNormalTexture ?? posTex);
        props.SetTexture("morphTangentTexture", mesh.MorphTangentTexture ?? posTex);
        props.SetTexture("morphWeightTexture", _morphWeightTexture!);
        props.SetInt("morphActiveCount", _morphActiveCount);
        props.SetInt("morphTexWidth", mesh.MorphTexWidth);
        props.SetInt("morphVertexCount", mesh.VertexCount);
        props.SetInt("morphHasNormals", mesh.MorphHasNormals ? 1 : 0);
        props.SetInt("morphHasTangents", mesh.MorphHasTangents ? 1 : 0);
    }

    private void UploadMorphWeightTexture()
    {
        int count = Math.Max(1, _activeMorphLayers.Count); // keep a valid 1-wide texture even when idle
        if (_morphWeightTexture == null || _morphWeightCapacity < count)
        {
            if (_morphWeightTexture != null) _morphWeightTexture.Dispose();
            _morphWeightTexture = new Texture2D((uint)count, 1, false, TextureImageFormat.Float4);
            _morphWeightTexture.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
            Graphics.SetWrapS(_morphWeightTexture.Handle, TextureWrap.ClampToEdge);
            Graphics.SetWrapT(_morphWeightTexture.Handle, TextureWrap.ClampToEdge);
            _morphWeightCapacity = count;
            _morphWeightData = new Float4[count];
        }

        // Pack into the reusable buffer (sized to the texture). The tail beyond the active count is
        // never sampled (the shader loops only over morphActiveCount), so stale tail data is fine.
        var data = _morphWeightData!;
        for (int i = 0; i < _activeMorphLayers.Count; i++)
            data[i] = _activeMorphLayers[i];
        _morphWeightTexture!.SetData<Float4>(data.AsMemory(0, _morphWeightCapacity), 0, 0, (uint)_morphWeightCapacity, 1);
    }

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        var mesh = SharedMesh.Res;
        if (mesh == null || Materials.Count == 0) return;

        Resolve(mesh);

        AABB worldBounds;
        if (_bones != null && _bones.Length > 0 && mesh.BindPoses != null)
        {
            if (_skinMatrices == null || _skinMatrices.Length != _bones.Length)
            {
                _skinMatrices = new Float4x4[_bones.Length];
                _lastSkeletonVersion = ulong.MaxValue; // force a recompute after a bone-count change
            }

            // Dirty check: sum of monotonic Transform.Versions across every bone and the renderer's own
            // ancestor chain (so moving a shared character root, which bumps none of the bones, still
            // refreshes the world-space bounds used for culling). Unchanged sum => static pose, so the
            // cached skin matrices, bone texture and bounds from last frame are still valid.
            ulong version = 0;
            for (Transform? t = Transform; t != null; t = t.Parent)
                version += t.Version;
            for (int i = 0; i < _bones.Length; i++)
                if (_bones[i] != null) version += _bones[i]!.Version;

            if (version != _lastSkeletonVersion || _boneTexture == null)
            {
                _lastSkeletonVersion = version;
                RecomputeSkinning(mesh);
            }

            worldBounds = _cachedBounds;
        }
        else
        {
            worldBounds = mesh.bounds.TransformBy(Transform.LocalToWorldMatrix);
        }

        // Resolve blend-shape weights -> active morph layers + weight texture (once per frame).
        if (mesh.HasBlendShapes)
            PrepareBlendShapes(mesh);

        // Render each submesh with its material. CollectionsMarshal.AsSpan gives a ref to the
        // list's real backing elements (List<T>'s indexer would copy a value-type element, so
        // AssetRef<Material>.Res's internal caching would mutate a throwaway copy and never stick).
        int subCount = mesh.SubMeshCount;
        if (_propCache == null || _propCache.Length != subCount)
        {
            _propCache = new PropertyState[subCount];
            for (int i = 0; i < subCount; i++)
                _propCache[i] = new PropertyState();
            _renderableCache = new SkinnedMeshRenderable[subCount];
        }

        Float4x4 world = Transform.LocalToWorldMatrix;
        Float3 giAnchor = Float4x4.TransformPoint(mesh.bounds.Center, world);
        var materials = CollectionsMarshal.AsSpan(Materials);
        for (int s = 0; s < subCount; s++)
        {
            Material? mat = null;
            if (s < materials.Length)
                mat = materials[s].Res;
            else if (materials.Length > 0)
                mat = materials[^1].Res; // Reuse last material for extra submeshes

            if (mat == null) continue;

            PropertyState props = _propCache[s];
            props.Clear();
            props.SetInt("_ObjectID", InstanceID);
            props.SetColor("_MainColor", MainColor);
            LightmapBinding.Fill(props, GameObject.Scene, LightmapIndex, LightmapScaleOffset, giAnchor, mesh.HasUV2);
            if (_boneTexture != null)
            {
                props.SetTexture("boneMatrixTexture", _boneTexture);
                props.SetInt("boneCount", _skinMatrices?.Length ?? 0);
            }

            if (mesh.HasBlendShapes)
                ApplyBlendShapeProps(mesh, props);

            SkinnedMeshRenderable renderable = _renderableCache![s] ??= new SkinnedMeshRenderable(
                mesh, mat, world, GameObject.LayerIndex, worldBounds, props);
            renderable.Reset(
                mesh, mat, world,
                GameObject.LayerIndex, worldBounds, props, subMeshIndex: s,
                renderingLayerMask: RenderingLayers.Mask);
            renderable.SetOwner(GameObject);
            renderable.SkinMatrices = _skinMatrices;
            // ★ N7 — per-instance morph palette source for SkinnedInstanceBatch (same-frame data).
            renderable.MorphActiveLayers = _morphReady ? _activeMorphLayers : null;
            renderables.Add(renderable);
        }
    }
}
