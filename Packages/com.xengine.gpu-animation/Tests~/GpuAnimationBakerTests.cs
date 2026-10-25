using System;

using Xunit;

using XEngine.Animation;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.GpuAnimation.Tests;

/// <summary>
/// Exact assertions for the headless bake pipeline on a synthetic two-bone rig: sampled pose →
/// FK → CPU skinning (shader-exact math) → texel layout → atlas stacking → UV2 mesh clone.
/// Translation clips assert hand-computed values (convention free); the rotation clip asserts
/// against the engine's own Quaternion/CreateTRS math (shares only the convention, not the code).
/// </summary>
public sealed class GpuAnimationBakerTests
{
    private static AnimationCurve Linear(float v0, float v1, float tEnd)
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, v0), new KeyFrame(tEnd, v1) });
        c.SmoothTangents(CurveTangent.Linear);
        return c;
    }

    private static AnimationCurve Constant(float v) => new(new[] { new KeyFrame(0, v) });

    private sealed class Rig : IDisposable
    {
        public GameObject Root = new("Armature");
        public GameObject Hips = new("Hips");
        public GameObject Spine = new("Spine");
        public Mesh Mesh = null!;
        public SkinnedMeshRenderer Smr = null!;

        public static Rig BuildTwoBone(Float4[]? boneIndices = null, Float4[]? boneWeights = null)
        {
            var rig = new Rig();
            rig.Hips.SetParent(rig.Root, worldPositionStays: false);
            rig.Spine.SetParent(rig.Hips, worldPositionStays: false);

            var mesh = new Mesh { Name = "TwoBone" };
            mesh.Vertices = new[] { new Float3(0, 0, 0), new Float3(1, 0, 0), new Float3(2, 0, 0) };
            mesh.Normals = new[] { new Float3(1, 0, 0), new Float3(1, 0, 0), new Float3(1, 0, 0) };
            mesh.Indices = new uint[] { 0, 1, 2 };
            mesh.BoneIndices = boneIndices ?? new[] { new Float4(1, 0, 0, 0), new Float4(2, 0, 0, 0), new Float4(1, 2, 0, 0) };
            mesh.BoneWeights = boneWeights ?? new[] { new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(0.5f, 0.5f, 0, 0) };
            mesh.BindPoses = new[] { Float4x4.Identity, Float4x4.Identity };
            rig.Mesh = mesh;

            rig.Smr = rig.Root.AddComponent<SkinnedMeshRenderer>();
            rig.Smr.SharedMesh = new AssetRef<Mesh>(mesh);
            rig.Smr.SetBones(new[] { rig.Hips.Transform, rig.Spine.Transform }, rig.Root.Transform);
            return rig;
        }

        public void Dispose()
        {
            Mesh?.Dispose();
        }
    }

    private static AnimationClip MakePosClip(string name, string bone, float endX, float duration = 1f, string? rootBonePath = null)
    {
        var clip = new AnimationClip { Name = name };
        clip.Duration = duration;
        clip.Wrap = AnimationWrapMode.Loop;
        clip.RootBonePath = rootBonePath;
        var boneCurves = new AnimationClip.AnimBone { BoneName = bone };
        boneCurves.PosX = Linear(0f, endX, duration);
        boneCurves.PosY = Constant(0f);
        boneCurves.PosZ = Constant(0f);
        clip.AddBone(boneCurves);
        return clip;
    }

    private static AnimationClip MakeConstantRotClip(string name, string bone, Quaternion rotation)
    {
        var clip = new AnimationClip { Name = name };
        clip.Duration = 1f;
        clip.Wrap = AnimationWrapMode.Loop;
        var boneCurves = new AnimationClip.AnimBone { BoneName = bone };
        boneCurves.PosX = Constant(0f);
        boneCurves.PosY = Constant(0f);
        boneCurves.PosZ = Constant(0f);
        boneCurves.RotX = Constant(rotation.X);
        boneCurves.RotY = Constant(rotation.Y);
        boneCurves.RotZ = Constant(rotation.Z);
        boneCurves.RotW = Constant(rotation.W);
        clip.AddBone(boneCurves);
        return clip;
    }

    [Fact]
    public void Bake_Translations_MatchHandComputedSkinning()
    {
        using var rig = Rig.BuildTwoBone();
        var request = new GpuAnimationBakeRequest
        {
            Target = rig.Root,
            Clips = { MakePosClip("HipsMove", "Hips", 1f), MakePosClip("SpineMove", "Hips/Spine", 1f) },
            Fps = 4f,
            BakeNormals = false,
        };
        var result = GpuAnimationBaker.Bake(request);
        var asset = result.Asset;

        // 3 vertices -> width 4; two clips x 4 frames -> height 8.
        Assert.Equal(4, asset.TextureWidth);
        Assert.Equal(8, asset.TextureHeight);
        Assert.Equal(2, asset.Clips.Count);
        Assert.Equal(0, asset.Clips[0].FrameStart);
        Assert.Equal(4, asset.Clips[0].FrameCount);
        Assert.Equal(4, asset.Clips[1].FrameStart);
        Assert.Equal(1f, asset.Clips[0].AnimLength, 5);
        Assert.Equal(4f, asset.Clips[0].Fps, 5);

        // Clip 1 (Hips 0->1 over 1s, 4 frames at t = 0, .25, .5, .75): the whole rig translates
        // rigidly (identity bind poses) — vertex k ends up at x = t + k.
        for (int f = 0; f < 4; f++)
        {
            float t = f * 0.25f;
            asset.GetPosition(f, 0, out var v0);
            asset.GetPosition(f, 1, out var v1);
            asset.GetPosition(f, 2, out var v2);
            Assert.Equal(t + 0f, v0.X, 5);
            Assert.Equal(t + 1f, v1.X, 5);
            Assert.Equal(t + 2f, v2.X, 5);
            Assert.Equal(0f, v0.Z, 5);
        }

        // Clip 2 (Spine leaf 0->1, path "Hips/Spine"): hips-bound vertex frozen, spine vertex
        // rides the spine translation, blended vertex averages the two bones.
        int row = 4 + 3; // t = 0.75
        asset.GetPosition(row, 0, out var s0);
        asset.GetPosition(row, 1, out var s1);
        asset.GetPosition(row, 2, out var s2);
        Assert.Equal(0f, s0.X, 5);
        Assert.Equal(1.75f, s1.X, 5);
        Assert.Equal(2f + 0.5f * 0.75f, s2.X, 5);

        // Alpha channel is 1 everywhere; padding column 3 stays zero.
        for (int r = 0; r < 8; r++)
        {
            Assert.Equal(1f, asset.Positions[asset.TexelIndex(r, 0) + 3], 5);
            int pad = asset.TexelIndex(r, 3);
            Assert.Equal(0f, asset.Positions[pad], 5);
            Assert.Equal(0f, asset.Positions[pad + 1], 5);
            Assert.Equal(0f, asset.Positions[pad + 2], 5);
        }

        // Frame bounds: clip 1 frame 2 spans [t+0, t+2].
        Assert.Equal(0.5f, asset.FrameBoundsMin[2 * 3], 5);
        Assert.Equal(2.5f, asset.FrameBoundsMax[2 * 3], 5);
        // Union across both clips: [0, 0.75 + 2].
        asset.GetUnionBounds(out var min, out var max);
        Assert.Equal(0f, min.X, 5);
        Assert.Equal(2.75f, max.X, 5);
    }

    [Fact]
    public void Bake_Rotation_MatchesEngineQuaternionMath_AndRotatesNormals()
    {
        using var rig = Rig.BuildTwoBone();
        Quaternion q = Quaternion.FromEuler(0f, 90f, 0f);
        var request = new GpuAnimationBakeRequest
        {
            Target = rig.Root,
            Clips = { MakeConstantRotClip("SpineRot", "Hips/Spine", q) }, // bone paths are binding-relative full paths
            Fps = 4f,
            BakeNormals = true,
        };
        var result = GpuAnimationBaker.Bake(request);
        var asset = result.Asset;
        Assert.True(asset.HasNormals);

        // Expected via the engine's own matrix math (ground truth for the convention only):
        // Spine world = TRS(0, q, 1); hips-bound vertex untouched.
        Float4x4 m = Float4x4.CreateTRS(Float3.Zero, q, Float3.One);
        Float3 expectedV1 = new(
            m.c0.X * 1f + m.c1.X * 0f + m.c2.X * 0f + m.c3.X,
            m.c0.Y * 1f + m.c1.Y * 0f + m.c2.Y * 0f + m.c3.Y,
            m.c0.Z * 1f + m.c1.Z * 0f + m.c2.Z * 0f + m.c3.Z);
        Float3 expectedN1 = Float3.Normalize(new(
            m.c0.X * 1f + m.c1.X * 0f + m.c2.X * 0f,
            m.c0.Y * 1f + m.c1.Y * 0f + m.c2.Y * 0f,
            m.c0.Z * 1f + m.c1.Z * 0f + m.c2.Z * 0f));

        for (int f = 0; f < 4; f++)
        {
            asset.GetPosition(f, 0, out var v0);   // hips bone: unaffected by spine rotation
            asset.GetPosition(f, 1, out var v1);   // spine bone at (1,0,0): rotated
            asset.GetNormal(f, 1, out var n1);
            Assert.Equal(0f, v0.X, 5);
            Assert.Equal(expectedV1.X, v1.X, 4);
            Assert.Equal(expectedV1.Y, v1.Y, 4);
            Assert.Equal(expectedV1.Z, v1.Z, 4);
            Assert.Equal(expectedN1.X, n1.X, 4);
            Assert.Equal(expectedN1.Y, n1.Y, 4);
            Assert.Equal(expectedN1.Z, n1.Z, 4);
        }
        // |rotated vertex| == 1 (rotation preserves length).
        asset.GetPosition(0, 1, out var len);
        Assert.Equal(1f, Float3.Length(len), 4);
    }

    [Fact]
    public void Bake_RootBonePath_PinsRootToReferencePose()
    {
        using var rig = Rig.BuildTwoBone();
        var request = new GpuAnimationBakeRequest
        {
            Target = rig.Root,
            Clips = { MakePosClip("RootMove", "Hips", 1f, rootBonePath: "Hips") },
            Fps = 4f,
            BakeNormals = false,
        };
        var pinned = GpuAnimationBaker.Bake(request).Asset;

        request.PinRootToReferencePose = false;
        var free = GpuAnimationBaker.Bake(request).Asset;

        pinned.GetPosition(3, 0, out var pinnedPos);
        Assert.Equal(0f, pinnedPos.X, 5); // pinned: every frame back at bind pose
        free.GetPosition(3, 0, out var freePos);
        Assert.Equal(0.75f, freePos.X, 5); // unpinned: root travel carries the rig
    }

    [Fact]
    public void Bake_ZeroWeightVertex_PassesThrough()
    {
        // Vertex 2 has all-zero weights -> stays at its bind position regardless of bone motion.
        using var rig = Rig.BuildTwoBone(
            boneIndices: new[] { new Float4(1, 0, 0, 0), new Float4(2, 0, 0, 0), new Float4(0, 0, 0, 0) },
            boneWeights: new[] { new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(0, 0, 0, 0) });
        var request = new GpuAnimationBakeRequest
        {
            Target = rig.Root,
            Clips = { MakePosClip("HipsMove", "Hips", 1f) },
            Fps = 4f,
            BakeNormals = false,
        };
        var asset = GpuAnimationBaker.Bake(request).Asset;

        asset.GetPosition(3, 0, out var v0);
        asset.GetPosition(3, 2, out var v2);
        Assert.Equal(0.75f, v0.X, 5);  // skinned bone vertex moved
        Assert.Equal(2f, v2.X, 5);     // zero-weight vertex frozen at bind position
        Assert.Equal(0f, v2.Z, 5);
    }

    [Fact]
    public void Bake_VatMeshClone_EncodesTexelUAndStripsBoneData()
    {
        using var rig = Rig.BuildTwoBone();
        var request = new GpuAnimationBakeRequest
        {
            Target = rig.Root,
            Clips = { MakePosClip("HipsMove", "Hips", 1f) },
            Fps = 4f,
            BakeNormals = true,
        };
        var result = GpuAnimationBaker.Bake(request);
        var vat = result.VatMesh;

        Assert.Equal(3, vat.Vertices.Length);
        Assert.Equal(3, vat.UV2!.Length);
        Assert.Equal(0.5f / 4f, vat.UV2[0].X, 6);
        Assert.Equal(1.5f / 4f, vat.UV2[1].X, 6);
        Assert.Equal(2.5f / 4f, vat.UV2[2].X, 6);
        Assert.Equal(0f, vat.UV2[0].Y, 6); // y carries the raw vertex index for texelFetch
        Assert.Equal(1f, vat.UV2[1].Y, 6);
        Assert.Equal(2f, vat.UV2[2].Y, 6);
        Assert.Empty(vat.BoneIndices); // bone data stripped (unset streams read back as empty arrays)
        Assert.Empty(vat.BoneWeights);
        Assert.Empty(vat.UV);
        Assert.Equal(3, vat.Indices.Length);
        Assert.Equal(2f, vat.bounds.Max.X, 5); // RecalculateBounds over bind-pose vertices [0..2]
        Assert.True(vat.Normals != null && vat.Normals.Length == 3);
    }

    [Fact]
    public void DefaultFrameCount_NearestPowerOfTwo_CatParity()
    {
        // chenjd cat: 30 fps * 4.6667 s = 140 -> closest power of two 128.
        var catClip = new AnimationClip { Name = "Cat_Idle1", Duration = 4.6666667f };
        Assert.Equal(128, GpuAnimationBaker.DefaultFrameCount(catClip, 30f));

        var shortClip = new AnimationClip { Name = "Short", Duration = 0.041f };
        Assert.Equal(1, GpuAnimationBaker.DefaultFrameCount(shortClip, 30f)); // round(1.23) = 1

        var midClip = new AnimationClip { Name = "Mid", Duration = 0.5f };
        Assert.Equal(16, GpuAnimationBaker.DefaultFrameCount(midClip, 30f)); // 15 -> nearest 16
    }

    [Fact]
    public void Bake_HumanoidClipWithoutAvatar_FallsBackToGeneric_NoCrash()
    {
        using var rig = Rig.BuildTwoBone();
        var clip = MakePosClip("Move", "Hips", 1f);
        clip.IsHumanoid = true; // muscle-space clip, no avatar supplied -> generic fallback path
        var request = new GpuAnimationBakeRequest
        {
            Target = rig.Root,
            Clips = { clip },
            Fps = 4f,
            BakeNormals = false,
            Avatar = null,
        };
        var asset = GpuAnimationBaker.Bake(request).Asset;
        Assert.Single(asset.Clips);
        // Generic fallback still samples the clip's generic curves (the muscle curves a real
        // humanoid bake would carry are simply absent here): frames advance normally.
        asset.GetPosition(0, 1, out var f0);
        asset.GetPosition(2, 1, out var f2);
        Assert.Equal(1f, f0.X, 5);   // t=0: hips 0 -> v1 = 0 + 1
        Assert.Equal(1.5f, f2.X, 5); // t=0.5: hips 0.5 -> v1 = 0.5 + 1
    }
}
