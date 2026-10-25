// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// CPU-side correctness of the clustered light list builder (P3.2). The load-bearing invariant:
/// for every world position P and every light whose bounding sphere contains P, the cluster that
/// the shader's lookup maps P to must list that light. Missing a light would darken pixels
/// relative to the BVH path; extra lights are harmless (zero contribution beyond range).
/// </summary>
public class ClusterLightListTests
{
    private sealed class StubLight : IRenderableLight
    {
        public int GetLightID() => 0;
        public int GetLayer() => 0;
        public LightType GetLightType() => LightType.Point;
        public Float3 GetLightPosition() => Float3.Zero;
        public Float3 GetLightDirection() => -Float3.UnitY;
        public bool DoCastShadows() => false;
        public ForwardLightData GetForwardLightData() => default;
    }

    private static ForwardLightData PointAt(Float3 pos, float range)
        => new()
        {
            Type = LightType.Point,
            Position = pos,
            Direction = -Float3.UnitY,
            Color = new Float3(1, 1, 1),
            Intensity = 1f,
            Range = range,
            RenderingLayerMask = 0xFFu,
        };

    private const float Near = 0.3f;
    private const float Far = 500f;

    private static readonly Float3 s_eye = new(0f, 2f, -10f);
    private static readonly Float3 s_forward = Float3.UnitZ;

    private static Float4x4 ViewProjection()
    {
        Float4x4 view = Float4x4.CreateLookTo(s_eye, s_forward, Float3.UnitY);
        Float4x4 proj = Float4x4.CreatePerspectiveFov(60f * MathF.PI / 180f, 16f / 9f, Near, Far);
        return proj * view;
    }

    /// <summary>Mirror of the shader's cluster lookup for a world position. Returns -1 when the
    /// position projects behind the eye (the shader returns an empty header there).</summary>
    private static int ClusterOf(Float3 p, in Float4x4 vp, float sliceNear, float slicesPerLog2)
    {
        Float4 clip = vp * new Float4(p.X, p.Y, p.Z, 1f);
        if (clip.W <= 1e-6f)
            return -1;
        float nx = clip.X / clip.W;
        float ny = clip.Y / clip.W;
        int tx = ClusterLightList.TileOf(nx, ClusterLightList.TilesX);
        int ty = ClusterLightList.TileOf(ny, ClusterLightList.TilesY);
        float viewZ = Float3.Dot(p - s_eye, s_forward);
        int slice = ClusterLightList.SliceOf(viewZ, sliceNear, slicesPerLog2);
        return (slice * ClusterLightList.TilesY + ty) * ClusterLightList.TilesX + tx;
    }

    private static bool ClusterListsLight(ClusterLightList clusters, int cluster, int encoded)
    {
        (int offset, int count) = clusters.HeaderForTest(cluster);
        for (int i = 0; i < count; i++)
            if (clusters.EntryForTest(offset + i) == encoded)
                return true;
        return false;
    }

    [Fact]
    public void NoLights_LeavesListDisabled()
    {
        var clusters = new ClusterLightList();
        var staticBvh = new LightBVH();
        var dynamicBvh = new LightBVH();
        Float4x4 vp = ViewProjection();

        clusters.BuildForTest(staticBvh, dynamicBvh, in vp, s_eye, s_forward, Near, Far);

        Assert.False(clusters.Enabled);
        Assert.Equal(0, clusters.EntryCount);
    }

    [Fact]
    public void LightBehindCamera_IsCulled()
    {
        var clusters = new ClusterLightList();
        var staticBvh = new LightBVH();
        var dynamicBvh = new LightBVH();
        dynamicBvh.Add(new StubLight(), PointAt(s_eye - s_forward * 50f, 5f));
        dynamicBvh.Sync();
        Float4x4 vp = ViewProjection();

        clusters.BuildForTest(staticBvh, dynamicBvh, in vp, s_eye, s_forward, Near, Far);

        Assert.True(clusters.Enabled);
        Assert.Equal(0, clusters.EntryCount);
    }

    [Fact]
    public void LightBeyondFarPlane_IsCulled()
    {
        var clusters = new ClusterLightList();
        var staticBvh = new LightBVH();
        var dynamicBvh = new LightBVH();
        dynamicBvh.Add(new StubLight(), PointAt(s_eye + s_forward * (Far + 100f), 5f));
        dynamicBvh.Sync();
        Float4x4 vp = ViewProjection();

        clusters.BuildForTest(staticBvh, dynamicBvh, in vp, s_eye, s_forward, Near, Far);

        Assert.True(clusters.Enabled);
        Assert.Equal(0, clusters.EntryCount);
    }

    [Fact]
    public void StaticAndDynamic_EncodeWithDistinctSigns()
    {
        var clusters = new ClusterLightList();
        var staticBvh = new LightBVH();
        var dynamicBvh = new LightBVH();
        // Both lights sit dead center in front of the camera.
        Float3 pos = s_eye + s_forward * 20f;
        staticBvh.Add(new StubLight(), PointAt(pos, 4f));
        dynamicBvh.Add(new StubLight(), PointAt(pos + new Float3(0.5f, 0f, 0f), 4f));
        staticBvh.Sync();
        dynamicBvh.Sync();
        Float4x4 vp = ViewProjection();

        clusters.BuildForTest(staticBvh, dynamicBvh, in vp, s_eye, s_forward, Near, Far);

        Assert.True(clusters.Enabled);
        int cluster = ClusterOf(pos, in vp, clusters.SliceNear, clusters.SlicesPerLog2Z);
        Assert.True(cluster >= 0);
        Assert.True(ClusterListsLight(clusters, cluster, +1), "static slot 0 must encode as +1");
        Assert.True(ClusterListsLight(clusters, cluster, -1), "dynamic slot 0 must encode as -1");
    }

    [Fact]
    public void EveryPointInsideALightSphere_FindsThatLightInItsCluster()
    {
        var clusters = new ClusterLightList();
        var staticBvh = new LightBVH();
        var dynamicBvh = new LightBVH();
        var rng = new Random(12345);

        // 64 random point lights scattered through the frustum-ish volume in front of the camera.
        var lightPos = new Float3[64];
        var lightRange = new float[64];
        for (int i = 0; i < 64; i++)
        {
            lightPos[i] = new Float3(
                (float)(rng.NextDouble() * 120.0 - 60.0),
                (float)(rng.NextDouble() * 40.0 - 10.0),
                (float)(rng.NextDouble() * 180.0 - 30.0));
            lightRange[i] = (float)(rng.NextDouble() * 14.0 + 1.0);
            var data = PointAt(lightPos[i], lightRange[i]);
            if ((i & 1) == 0) staticBvh.Add(new StubLight(), data);
            else dynamicBvh.Add(new StubLight(), data);
        }
        staticBvh.Sync();
        dynamicBvh.Sync();
        Float4x4 vp = ViewProjection();

        clusters.BuildForTest(staticBvh, dynamicBvh, in vp, s_eye, s_forward, Near, Far);
        Assert.True(clusters.Enabled);

        // Sample random surface-like points inside the view frustum: pick NDC + view depth, then
        // unproject along the corresponding world position by brute force (sample world points and
        // keep the ones that project inside the frustum).
        int tested = 0;
        for (int n = 0; n < 20000 && tested < 4000; n++)
        {
            var p = new Float3(
                (float)(rng.NextDouble() * 140.0 - 70.0),
                (float)(rng.NextDouble() * 50.0 - 15.0),
                (float)(rng.NextDouble() * 200.0 - 40.0));

            Float4 clip = vp * new Float4(p.X, p.Y, p.Z, 1f);
            if (clip.W <= 1e-4f)
                continue;
            float nx = clip.X / clip.W;
            float ny = clip.Y / clip.W;
            if (nx < -1f || nx > 1f || ny < -1f || ny > 1f)
                continue;
            float viewZ = Float3.Dot(p - s_eye, s_forward);
            if (viewZ < Near || viewZ > Far)
                continue;
            tested++;

            int cluster = ClusterOf(p, in vp, clusters.SliceNear, clusters.SlicesPerLog2Z);
            Assert.True(cluster >= 0);

            // Every light whose sphere strictly contains the point must be listed. (Points near
            // the sphere boundary contribute ~0 light; the builder still covers them via its
            // conservative rects, but we assert with a small interior margin to keep the test
            // independent of float rounding at the exact boundary.)
            for (int i = 0; i < 64; i++)
            {
                Float3 d = p - lightPos[i];
                float distSq = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
                float r = lightRange[i] * 0.999f;
                if (distSq >= r * r)
                    continue;
                int slot = i >> 1; // interleaved add order: even -> static, odd -> dynamic
                int encoded = (i & 1) == 0 ? slot + 1 : -(slot + 1);
                Assert.True(
                    ClusterListsLight(clusters, cluster, encoded),
                    $"light {i} (slot {slot}) missing from cluster {cluster} for point {p}");
            }
        }

        Assert.True(tested >= 1000, $"expected at least 1000 in-frustum samples, got {tested}");
    }

    [Fact]
    public void FullScreenLight_CoversEveryCluster()
    {
        var clusters = new ClusterLightList();
        var staticBvh = new LightBVH();
        var dynamicBvh = new LightBVH();
        // Huge sphere surrounding the camera past the far plane: corners project behind the
        // eye (full-screen xy) and the z range clamps to every slice.
        dynamicBvh.Add(new StubLight(), PointAt(s_eye, 800f));
        dynamicBvh.Sync();
        Float4x4 vp = ViewProjection();

        clusters.BuildForTest(staticBvh, dynamicBvh, in vp, s_eye, s_forward, Near, Far);

        Assert.True(clusters.Enabled);
        Assert.Equal(ClusterLightList.ClusterCount, clusters.EntryCount);
        for (int c = 0; c < ClusterLightList.ClusterCount; c++)
        {
            (int offset, int count) = clusters.HeaderForTest(c);
            Assert.Equal(1, count);
            Assert.Equal(-1, clusters.EntryForTest(offset));
        }
    }

    [Fact]
    public void Disable_TurnsOffWithoutTouchingTexture()
    {
        var clusters = new ClusterLightList();
        var staticBvh = new LightBVH();
        var dynamicBvh = new LightBVH();
        dynamicBvh.Add(new StubLight(), PointAt(s_eye + s_forward * 10f, 3f));
        dynamicBvh.Sync();
        Float4x4 vp = ViewProjection();

        clusters.BuildForTest(staticBvh, dynamicBvh, in vp, s_eye, s_forward, Near, Far);
        Assert.True(clusters.Enabled);

        clusters.Disable();
        Assert.False(clusters.Enabled);
        Assert.Equal(0, clusters.EntryCount);
    }
}
