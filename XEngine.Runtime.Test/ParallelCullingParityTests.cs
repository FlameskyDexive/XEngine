// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Jobs;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// P7 stage C: the chunked parallel frustum cull must produce a bit-identical mask and an
/// identical culled count to the serial path, for every renderable count and every frustum.
/// The serial reference is the same code path with <see cref="JobSystem.Enabled"/> off, so a
/// divergence can only come from the parallel split itself.
/// </summary>
[Collection("JobSystem")]
public class ParallelCullingParityTests : IDisposable
{
    private readonly bool _wasEnabled = JobSystem.Enabled;

    public void Dispose() => JobSystem.Enabled = _wasEnabled;

    private sealed class FakeRenderable(AABB bounds, bool renderable, int layer) : IRenderable
    {
        public Material GetMaterial() => null!;
        public int GetLayer() => layer;
        public Float3 GetPosition() => bounds.Center;
        public void GetRenderingData(
            ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData)
            => throw new NotSupportedException();
        public void GetCullingData(out bool isRenderable, out AABB outBounds)
        {
            isRenderable = renderable;
            outBounds = bounds;
        }
    }

    private sealed class TestPipeline : RenderPipeline
    {
        internal override void Render(Camera camera, in RenderingData data) { }
    }

    private static List<IRenderable> BuildScene(int count, int seed)
    {
        var rng = new Random(seed);
        var list = new List<IRenderable>(count);
        for (int i = 0; i < count; i++)
        {
            var center = new Float3(
                (float)(rng.NextDouble() * 400.0 - 200.0),
                (float)(rng.NextDouble() * 120.0 - 60.0),
                (float)(rng.NextDouble() * 400.0 - 200.0));
            float half = (float)(rng.NextDouble() * 3.0 + 0.25);
            var bounds = new AABB(center - new Float3(half, half, half), center + new Float3(half, half, half));
            // A slice of the scene is deliberately non-renderable and a slice sits on other
            // layers, so the mask exercises both cull reasons in every chunk.
            bool renderable = rng.Next(16) != 0;
            int layer = rng.Next(4);
            list.Add(new FakeRenderable(bounds, renderable, layer));
        }
        return list;
    }

    /// <summary>LayerMask has no bit constructor; build one by setting the bits we want.</summary>
    private static LayerMask MaskOf(uint bits)
    {
        var mask = LayerMask.Nothing;
        for (int i = 0; i < 32; i++)
            if ((bits & (1u << i)) != 0)
                mask.SetLayer(i);
        return mask;
    }

    private static Frustum FrustumLookingDown(float distance, float aspect = 1.6f)
    {
        Float4x4 view = Float4x4.CreateLookAt(new Float3(0f, 25f, distance), Float3.Zero, Float3.UnitY);
        Float4x4 projection = Float4x4.CreatePerspectiveFov(1.0472f, aspect, 0.3f, 500f);
        return Frustum.FromMatrix(projection * view);
    }

    private static (bool[] Mask, int Culled, int Drawn) Cull(
        List<IRenderable> scene, Frustum? frustum, LayerMask mask, bool parallel)
    {
        JobSystem.Enabled = parallel;
        var pipeline = new TestPipeline(); // fresh instance => fresh world-bounds cache
        RenderStats.BeginFrame();
        bool[] culled = pipeline.CullRenderables(scene, frustum, mask);
        RenderStats.EndFrame();
        var copy = new bool[scene.Count];
        Array.Copy(culled, copy, scene.Count);
        int culledCount = 0;
        for (int i = 0; i < copy.Length; i++)
            if (copy[i]) culledCount++;
        return (copy, culledCount, scene.Count - culledCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(511)]      // just under the parallel threshold
    [InlineData(512)]      // exactly at it
    [InlineData(513)]
    [InlineData(1024)]     // exact multiple of the chunk size
    [InlineData(1025)]     // ragged last chunk
    [InlineData(4096)]
    [InlineData(20_000)]
    public void Parallel_Cull_Matches_Serial_Bit_For_Bit(int count)
    {
        List<IRenderable> scene = BuildScene(count, seed: 1234 + count);
        Frustum frustum = FrustumLookingDown(140f);
        var mask = MaskOf(0b0101u); // layers 0 and 2 visible, 1 and 3 culled by mask

        (bool[] serialMask, int serialCulled, int serialDrawn) = Cull(scene, frustum, mask, parallel: false);
        (bool[] parallelMask, int parallelCulled, int parallelDrawn) = Cull(scene, frustum, mask, parallel: true);

        Assert.Equal(serialCulled, parallelCulled);
        Assert.Equal(serialDrawn, parallelDrawn);
        Assert.Equal(serialMask.Length, parallelMask.Length);
        for (int i = 0; i < serialMask.Length; i++)
        {
            if (serialMask[i] != parallelMask[i])
                Assert.Fail($"Cull mask diverged at index {i} (serial={serialMask[i]}, parallel={parallelMask[i]}, count={count}).");
        }
        // Sanity: the fixture must actually cull some and keep some, or the parity check is vacuous.
        if (count >= 512)
        {
            Assert.True(serialCulled > 0, "Fixture culled nothing.");
            Assert.True(serialDrawn > 0, "Fixture drew nothing.");
        }
    }

    [Fact]
    public void Parallel_Cull_Matches_Serial_Without_A_Frustum()
    {
        List<IRenderable> scene = BuildScene(3000, seed: 77);
        var mask = MaskOf(0b0011u);

        (bool[] serialMask, int serialCulled, _) = Cull(scene, null, mask, parallel: false);
        (bool[] parallelMask, int parallelCulled, _) = Cull(scene, null, mask, parallel: true);

        Assert.Equal(serialCulled, parallelCulled);
        Assert.Equal(serialMask, parallelMask);
    }

    [Fact]
    public void Parallel_Cull_Matches_Serial_Across_Many_Frustums()
    {
        // Mirrors the real frame shape: one main cull plus cascade/point-face culls over the
        // same renderable list, all reusing the cached world bounds.
        List<IRenderable> scene = BuildScene(6000, seed: 5150);
        var mask = LayerMask.Everything;

        for (int i = 0; i < 8; i++)
        {
            Frustum frustum = FrustumLookingDown(40f + i * 35f, aspect: 1f + i * 0.15f);
            (bool[] serialMask, int serialCulled, _) = Cull(scene, frustum, mask, parallel: false);
            (bool[] parallelMask, int parallelCulled, _) = Cull(scene, frustum, mask, parallel: true);
            Assert.Equal(serialCulled, parallelCulled);
            Assert.Equal(serialMask, parallelMask);
        }
    }

    [Fact]
    public void Parallel_Cull_Is_Stable_Across_Repeats()
    {
        // Recycling job batches between culls must not leak a chunk's result into the next cull.
        List<IRenderable> scene = BuildScene(9000, seed: 909);
        Frustum frustum = FrustumLookingDown(120f);
        var mask = MaskOf(0b0111u);

        (bool[] reference, int referenceCulled, _) = Cull(scene, frustum, mask, parallel: false);
        for (int round = 0; round < 30; round++)
        {
            (bool[] parallelMask, int parallelCulled, _) = Cull(scene, frustum, mask, parallel: true);
            Assert.Equal(referenceCulled, parallelCulled);
            Assert.Equal(reference, parallelMask);
        }
    }
}
