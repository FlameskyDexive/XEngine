using System;
using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Animation.Humanoid;
using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.GpuAnimation;

/// <summary>Bake request: a character (skeleton root with a SkinnedMeshRenderer below it) plus clips.</summary>
public sealed class GpuAnimationBakeRequest
{
    /// <summary>Character root GameObject. Must contain a <see cref="SkinnedMeshRenderer"/> in its subtree.</summary>
    public GameObject? Target;

    /// <summary>Clips to bake into the texture atlas (one row range each).</summary>
    public List<AnimationClip> Clips = new();

    /// <summary>Avatar for humanoid clips (enables the muscle-space sampling path). Optional.</summary>
    public Avatar? Avatar;

    /// <summary>Target samples per second. Frame counts default to the nearest power of two of fps * duration.</summary>
    public float Fps = 30f;

    /// <summary>Force a frame count for every clip (null = derive per clip).</summary>
    public int? FrameCountOverride;

    /// <summary>Bake skinned normals into a second texture (required for lit crowd shaders).</summary>
    public bool BakeNormals = true;

    /// <summary>Half-precision (RGBA16F) atlas — halves GPU memory; exact 32F when false.</summary>
    public bool HalfPrecision;

    /// <summary>Pin the clip's root-motion bone back to the reference (bind) pose so crowds animate in place.</summary>
    public bool PinRootToReferencePose = true;

    /// <summary>Optional progress callback in [0,1].</summary>
    public Action<float>? Progress;
}

public sealed class GpuAnimationBakeResult
{
    public BakedGpuAnimation Asset = null!;
    public Mesh VatMesh = null!;
}

/// <summary>
/// Headless vertex-animation-texture baker. Samples clips through the engine's playable API
/// (generic or humanoid routing, mirroring <c>Animator.CreateClipState</c>), runs forward
/// kinematics from the sampled pose, and CPU-skins every vertex with the exact math the
/// GPU skinning shader uses (4 influences, 1-based bone indices, unnormalized weights,
/// &lt;0.01 total-weight pass-through) so baked texels match live rendering.
/// Positions are baked in the character root's local space (bake with the character at
/// identity; the per-instance crowd matrix supplies world placement).
/// </summary>
public static class GpuAnimationBaker
{
    public static GpuAnimationBakeResult Bake(GpuAnimationBakeRequest request)
    {
        if (request.Target == null) throw new InvalidOperationException("Bake request has no target GameObject.");
        if (request.Clips.Count == 0) throw new InvalidOperationException("Bake request has no clips.");

        var smr = request.Target.GetComponentInChildren<SkinnedMeshRenderer>()
            ?? throw new InvalidOperationException($"No SkinnedMeshRenderer under '{request.Target.Name}'.");
        Mesh mesh = smr.SharedMesh.Res
            ?? throw new InvalidOperationException($"SkinnedMeshRenderer '{request.Target.Name}' has no mesh.");
        Float3[]? vertices = mesh.Vertices;
        if (vertices == null || vertices.Length == 0)
            throw new InvalidOperationException($"Mesh '{mesh.Name}' has no CPU-readable vertices.");

        var binding = SkeletonBinding.Build(request.Target.Transform);
        var indexByTransform = new Dictionary<Transform, int>(binding.BoneCount);
        for (int i = 0; i < binding.BoneCount; i++)
            if (binding.Bones[i] != null && !indexByTransform.ContainsKey(binding.Bones[i]!))
                indexByTransform[binding.Bones[i]!] = i;

        int vertexCount = vertices.Length;
        int width = GpuAnimMath.NextPowerOfTwo(vertexCount);
        bool hasWeights = mesh.BoneIndices != null && mesh.BoneWeights != null;
        bool bakeNormals = request.BakeNormals && mesh.Normals != null;

        // Frame layout: one row range per clip, stacked into the atlas.
        var layout = new (AnimationClip Clip, int Frames, int Start)[request.Clips.Count];
        int totalFrames = 0;
        for (int c = 0; c < request.Clips.Count; c++)
        {
            int frames = request.FrameCountOverride ?? DefaultFrameCount(request.Clips[c], request.Fps);
            layout[c] = (request.Clips[c], frames, totalFrames);
            totalFrames += frames;
        }

        var asset = new BakedGpuAnimation(request.Target.Name + ".gpuanim")
        {
            TextureWidth = width,
            TextureHeight = totalFrames,
            VertexCount = vertexCount,
            HasNormals = bakeNormals,
            Precision = request.HalfPrecision ? GpuAnimationPrecision.Half16 : GpuAnimationPrecision.Float32,
            Positions = new float[width * totalFrames * 4],
            Normals = bakeNormals ? new float[width * totalFrames * 4] : Array.Empty<float>(),
            FrameBoundsMin = new float[totalFrames * 3],
            FrameBoundsMax = new float[totalFrames * 3],
        };

        var boneWorld = new Float4x4[binding.BoneCount];
        var boneWorldComputed = new bool[binding.BoneCount];
        Transform?[] smrBones = smr.Bones ?? Array.Empty<Transform?>();
        Float4x4[] bindPoses = mesh.BindPoses ?? Array.Empty<Float4x4>();
        var skin = new Float4x4[bindPoses.Length];
        int totalSamples = Math.Max(1, totalFrames);

        for (int c = 0; c < layout.Length; c++)
        {
            var (clip, frames, start) = layout[c];

            // Same routing rule as Animator.CreateClipState: humanoid clips only decode through
            // HumanoidClipPlayable + Avatar (their mapped-bone curves are baked into muscle space).
            AnimationClipPlayable playable = clip.IsHumanoid && request.Avatar != null
                ? new HumanoidClipPlayable(clip, request.Avatar)
                : new AnimationClipPlayable(clip);
            playable.Bind(binding);
            playable.Play();
            playable.Weight = 1f; // full weight; ProcessFrame early-outs at Weight <= 0

            float dt = clip.Duration > 0f ? clip.Duration / frames : 0f;

            for (int f = 0; f < frames; f++)
            {
                float t = f * dt;
                playable.SetTime(t);
                playable.ProcessFrame(new PlayableContext(0f, t, isScrubbing: true));
                Pose? pose = playable.Output;
                if (pose == null) throw new InvalidOperationException($"Clip '{clip.Name}' produced no pose at t={t}.");

                if (request.PinRootToReferencePose && !string.IsNullOrEmpty(clip.RootBonePath))
                {
                    int rootIdx = binding.IndexOfBonePath(clip.RootBonePath);
                    if (rootIdx >= 0)
                        pose.Bones[rootIdx].LocalPosition = binding.ReferencePose[rootIdx].LocalPosition;
                }

                Array.Clear(boneWorldComputed, 0, boneWorldComputed.Length);
                ComputeBoneWorld(binding, indexByTransform, pose, boneWorld, boneWorldComputed);
                for (int i = 0; i < skin.Length; i++)
                {
                    Transform? bone = i < smrBones.Length ? smrBones[i] : null;
                    skin[i] = bone != null
                        ? WorldOfBone(binding, indexByTransform, pose, boneWorld, boneWorldComputed, bone) * bindPoses[i]
                        : Float4x4.Identity;
                }

                int row = start + f;
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (int v = 0; v < vertexCount; v++)
                {
                    Float3 pos = SkinPosition(mesh, vertices, hasWeights, v, skin);
                    int i4 = asset.TexelIndex(row, v);
                    asset.Positions[i4] = pos.X;
                    asset.Positions[i4 + 1] = pos.Y;
                    asset.Positions[i4 + 2] = pos.Z;
                    asset.Positions[i4 + 3] = 1f;

                    if (bakeNormals)
                    {
                        Float3 nrm = SkinNormal(mesh, hasWeights, v, skin);
                        asset.Normals[i4] = nrm.X;
                        asset.Normals[i4 + 1] = nrm.Y;
                        asset.Normals[i4 + 2] = nrm.Z;
                        asset.Normals[i4 + 3] = 1f;
                    }

                    if (pos.X < minX) minX = pos.X;
                    if (pos.Y < minY) minY = pos.Y;
                    if (pos.Z < minZ) minZ = pos.Z;
                    if (pos.X > maxX) maxX = pos.X;
                    if (pos.Y > maxY) maxY = pos.Y;
                    if (pos.Z > maxZ) maxZ = pos.Z;
                }
                asset.FrameBoundsMin[row * 3] = minX;
                asset.FrameBoundsMin[row * 3 + 1] = minY;
                asset.FrameBoundsMin[row * 3 + 2] = minZ;
                asset.FrameBoundsMax[row * 3] = maxX;
                asset.FrameBoundsMax[row * 3 + 1] = maxY;
                asset.FrameBoundsMax[row * 3 + 2] = maxZ;

                request.Progress?.Invoke((row + 1) / (float)totalSamples);
            }

            asset.Clips.Add(new GpuAnimationClipInfo
            {
                Name = clip.Name,
                FrameStart = start,
                FrameCount = frames,
                AnimLength = clip.Duration,
                Fps = request.Fps,
            });
        }

        Mesh vatMesh = CreateVatMesh(mesh, width, asset);
        return new GpuAnimationBakeResult { Asset = asset, VatMesh = vatMesh };
    }

    /// <summary>Nearest power of two of round(fps * duration), at least 1 (chenjd's Mathf.ClosestPowerOfTwo).</summary>
    public static int DefaultFrameCount(AnimationClip clip, float fps)
    {
        float duration = clip.Duration > 0f ? clip.Duration : 0f;
        int v = Math.Max(1, (int)MathF.Round(fps * duration));
        int up = GpuAnimMath.NextPowerOfTwo(v);
        int down = up >> 1;
        return down >= 1 && v - down < up - v ? down : up;
    }

    /// <summary>Forward kinematics over the sampled pose: world[i] = parentWorld * TRS(pose[i]).</summary>
    private static void ComputeBoneWorld(SkeletonBinding binding, Dictionary<Transform, int> indexByTransform, Pose pose, Float4x4[] world, bool[] computed)
    {
        for (int i = 0; i < binding.BoneCount; i++)
            WorldOfIndex(binding, indexByTransform, pose, world, computed, i);
    }

    private static Float4x4 WorldOfIndex(SkeletonBinding binding, Dictionary<Transform, int> indexByTransform, Pose pose, Float4x4[] world, bool[] computed, int index)
    {
        if (computed[index])
            return world[index];

        computed[index] = true;
        Transform? t = binding.Bones[index];
        if (t == null)
        {
            world[index] = Float4x4.Identity;
            return world[index];
        }

        Float4x4 local = Float4x4.CreateTRS(
            LocalPositionOf(pose, binding, index),
            LocalRotationOf(pose, binding, index),
            LocalScaleOf(pose, binding, index));
        Transform? parent = t.Parent;
        Float4x4 parentWorld;
        if (parent != null && indexByTransform.TryGetValue(parent, out int parentIndex))
            parentWorld = WorldOfIndex(binding, indexByTransform, pose, world, computed, parentIndex);
        else if (parent != null)
            parentWorld = LiveWorld(parent);
        else
            parentWorld = Float4x4.Identity;

        world[index] = parentWorld * local;
        return world[index];
    }

    private static Float4x4 WorldOfBone(SkeletonBinding binding, Dictionary<Transform, int> indexByTransform, Pose pose, Float4x4[] world, bool[] computed, Transform bone)
        => indexByTransform.TryGetValue(bone, out int index)
            ? WorldOfIndex(binding, indexByTransform, pose, world, computed, index)
            : LiveWorld(bone);

    /// <summary>World matrix from live Transform locals (prefab bind pose); used for bones outside the binding.</summary>
    private static Float4x4 LiveWorld(Transform t)
    {
        Float4x4 local = Float4x4.CreateTRS(t.LocalPosition, t.LocalRotation, t.LocalScale);
        return t.Parent != null ? LiveWorld(t.Parent) * local : local;
    }

    // Pose.Clear() zeroes every channel; the engine's AnimationPlayableOutput.Apply only writes
    // channels flagged in BonePose.Channels and leaves the rest at the bind pose. FK must do the
    // same or unanimated bones collapse (zero quaternion / zero scale).

    private static Float3 LocalPositionOf(Pose pose, SkeletonBinding binding, int index)
        => (pose.Bones[index].Channels & PoseChannel.Position) != 0
            ? pose.Bones[index].LocalPosition
            : binding.ReferencePose[index].LocalPosition;

    private static Quaternion LocalRotationOf(Pose pose, SkeletonBinding binding, int index)
        => (pose.Bones[index].Channels & PoseChannel.Rotation) != 0
            ? pose.Bones[index].LocalRotation
            : binding.ReferencePose[index].LocalRotation;

    private static Float3 LocalScaleOf(Pose pose, SkeletonBinding binding, int index)
        => (pose.Bones[index].Channels & PoseChannel.Scale) != 0
            ? pose.Bones[index].LocalScale
            : binding.ReferencePose[index].LocalScale;

    /// <summary>Shader-exact position skinning (VertexAttributes.glsl GetSkinnedPosition).</summary>
    private static Float3 SkinPosition(Mesh mesh, Float3[] vertices, bool hasWeights, int v, Float4x4[] skin)
    {
        if (!hasWeights) return vertices[v];
        Float4 idx = mesh.BoneIndices![v];
        Float4 wgt = mesh.BoneWeights![v];
        float w0 = wgt.X, w1 = wgt.Y, w2 = wgt.Z, w3 = wgt.W;
        float total = w0 + w1 + w2 + w3;
        if (total < 0.01f) return vertices[v];

        Float3 p = vertices[v];
        Float3 acc = Float3.Zero;
        AccumulatePosition(ref acc, skin, (int)MathF.Round(idx.X), w0, p);
        AccumulatePosition(ref acc, skin, (int)MathF.Round(idx.Y), w1, p);
        AccumulatePosition(ref acc, skin, (int)MathF.Round(idx.Z), w2, p);
        AccumulatePosition(ref acc, skin, (int)MathF.Round(idx.W), w3, p);
        return acc;
    }

    private static void AccumulatePosition(ref Float3 acc, Float4x4[] skin, int oneBased, float w, Float3 p)
    {
        if (oneBased <= 0 || w <= 0f || oneBased > skin.Length) return;
        Float4x4 m = skin[oneBased - 1];
        acc.X += (m.c0.X * p.X + m.c1.X * p.Y + m.c2.X * p.Z + m.c3.X) * w;
        acc.Y += (m.c0.Y * p.X + m.c1.Y * p.Y + m.c2.Y * p.Z + m.c3.Y) * w;
        acc.Z += (m.c0.Z * p.X + m.c1.Z * p.Y + m.c2.Z * p.Z + m.c3.Z) * w;
    }

    /// <summary>Shader-exact normal skinning (VertexAttributes.glsl GetSkinnedNormal): plain upper-left 3×3, final normalize.</summary>
    private static Float3 SkinNormal(Mesh mesh, bool hasWeights, int v, Float4x4[] skin)
    {
        Float3 n = mesh.Normals![v];
        if (!hasWeights) return n;
        Float4 idx = mesh.BoneIndices![v];
        Float4 wgt = mesh.BoneWeights![v];
        float w0 = wgt.X, w1 = wgt.Y, w2 = wgt.Z, w3 = wgt.W;
        float total = w0 + w1 + w2 + w3;
        if (total < 0.01f) return n;

        Float3 acc = Float3.Zero;
        AccumulateNormal(ref acc, skin, (int)MathF.Round(idx.X), w0, n);
        AccumulateNormal(ref acc, skin, (int)MathF.Round(idx.Y), w1, n);
        AccumulateNormal(ref acc, skin, (int)MathF.Round(idx.Z), w2, n);
        AccumulateNormal(ref acc, skin, (int)MathF.Round(idx.W), w3, n);
        float len = MathF.Sqrt(acc.X * acc.X + acc.Y * acc.Y + acc.Z * acc.Z);
        return len > 1e-8f ? new Float3(acc.X / len, acc.Y / len, acc.Z / len) : n;
    }

    private static void AccumulateNormal(ref Float3 acc, Float4x4[] skin, int oneBased, float w, Float3 n)
    {
        if (oneBased <= 0 || w <= 0f || oneBased > skin.Length) return;
        Float4x4 m = skin[oneBased - 1];
        acc.X += (m.c0.X * n.X + m.c1.X * n.Y + m.c2.X * n.Z) * w;
        acc.Y += (m.c0.Y * n.X + m.c1.Y * n.Y + m.c2.Y * n.Z) * w;
        acc.Z += (m.c0.Z * n.X + m.c1.Z * n.Y + m.c2.Z * n.Z) * w;
    }

    /// <summary>Static clone of the skinned mesh: UV2 encodes the vertex texel center, bone data stripped.</summary>
    private static Mesh CreateVatMesh(Mesh mesh, int width, BakedGpuAnimation asset)
    {
        int vertexCount = mesh.Vertices!.Length;
        var clone = new Mesh { Name = mesh.Name + "_VAT" };
        clone.Vertices = (Float3[])mesh.Vertices.Clone();
        // Unset streams come back as empty arrays (not null) — only clone populated ones.
        if (mesh.Normals != null && mesh.Normals.Length == vertexCount) clone.Normals = (Float3[])mesh.Normals.Clone();
        if (mesh.Tangents != null && mesh.Tangents.Length == vertexCount) clone.Tangents = (Float4[])mesh.Tangents.Clone();
        if (mesh.UV != null && mesh.UV.Length == vertexCount) clone.UV = (Float2[])mesh.UV.Clone();
        var uv2 = new Float2[vertexCount];
        for (int v = 0; v < vertexCount; v++)
            uv2[v] = new Float2(GpuAnimMath.VertexU(v, width), v); // x = texel center u, y = raw vertex index
        clone.UV2 = uv2;
        clone.Indices = (uint[])mesh.Indices.Clone();
        if (mesh.SubMeshCount > 1)
        {
            clone.SetSubMeshCount(mesh.SubMeshCount);
            for (int s = 0; s < mesh.SubMeshCount; s++)
                clone.SetSubMesh(s, mesh.GetSubMesh(s));
        }
        // Mesh.bounds is engine-computed (bind pose); animated-space culling uses the renderable's
        // explicit AABB from BakedGpuAnimation.GetUnionBounds instead.
        clone.RecalculateBounds();
        return clone;
    }
}
