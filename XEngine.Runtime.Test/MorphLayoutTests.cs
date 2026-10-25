// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Xunit;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test;

/// <summary>
/// Verifies the GPU-free morph-target math in <see cref="MorphLayout"/>: texture tiling plans,
/// texel fill placement, and the weights → active-layer resolution the shader contract depends on
/// (single-frame scaling, frame clamping, between-frame interpolation).
/// </summary>
public class MorphLayoutTests
{
    private static BlendShapeFrame Frame(float weight, Float3[]? deltas = null, Float3[]? normals = null, Float3[]? tangents = null)
        => new() { Weight = weight, DeltaVertices = deltas ?? Array.Empty<Float3>(), DeltaNormals = normals, DeltaTangents = tangents };

    // ─────────────────────────── ComputePlan ───────────────────────────

    [Fact]
    public void ComputePlan_AssignsLayerOffsetsInShapeOrder()
    {
        var shapes = new[]
        {
            new BlendShape { Name = "A", Frames = new[] { Frame(100f) } },
            new BlendShape { Name = "B", Frames = new[] { Frame(50f), Frame(100f) } },
        };

        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 10, maxTextureSize: 1024);

        Assert.Equal(3, plan.LayerCount);
        Assert.Equal(0, plan.LayerOffsets[0]);
        Assert.Equal(1, plan.LayerOffsets[1]);
        Assert.False(plan.Overflows);
    }

    [Fact]
    public void ComputePlan_TilesLinearlyInto2D()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(100f), Frame(100f) } } };

        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 10, maxTextureSize: 1024);

        Assert.Equal(10, plan.TexWidth);
        Assert.Equal(2, plan.TexHeight);

        // Last texel of layer 1 must land in row 1.
        int idx = plan.TexelIndex(1, 9);
        Assert.Equal(19, idx);
        Assert.Equal((9, 1), plan.TexelCoord(idx));
        // First texel of layer 0 stays at the origin.
        Assert.Equal((0, 0), plan.TexelCoord(0));
    }

    [Fact]
    public void ComputePlan_CapsWidthAtMaxTextureSize()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(100f) } } };

        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 5000, maxTextureSize: 4096);

        Assert.Equal(4096, plan.TexWidth);
        Assert.Equal(2, plan.TexHeight); // ceil(5000 / 4096)
    }

    [Fact]
    public void ComputePlan_OverflowFlaggedWhenHeightExceedsMax()
    {
        // 9 layers x 8 verts into a max-size-8 device → 9 rows > 8.
        var frames = new BlendShapeFrame[9];
        for (int i = 0; i < frames.Length; i++) frames[i] = Frame(100f);
        var shapes = new[] { new BlendShape { Name = "A", Frames = frames } };

        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 8, maxTextureSize: 8);

        Assert.True(plan.Overflows);
    }

    [Fact]
    public void ComputePlan_NoShapesOrFrames_YieldsEmptyNonOverflowingPlan()
    {
        var empty = MorphLayout.ComputePlan(Array.Empty<BlendShape>(), vertexCount: 4, maxTextureSize: 1024);
        Assert.Equal(0, empty.LayerCount);
        Assert.False(empty.Overflows);

        var noFrames = new[] { new BlendShape { Name = "A", Frames = Array.Empty<BlendShapeFrame>() } };
        var plan = MorphLayout.ComputePlan(noFrames, vertexCount: 4, maxTextureSize: 1024);
        Assert.Equal(0, plan.LayerCount);
        Assert.False(plan.Overflows);
    }

    [Fact]
    public void ComputePlan_DetectsNormalAndTangentStreams()
    {
        var shapes = new[]
        {
            new BlendShape { Name = "N", Frames = new[] { Frame(100f, normals: Array.Empty<Float3>()) } },
            new BlendShape { Name = "T", Frames = new[] { Frame(100f, tangents: Array.Empty<Float3>()) } },
        };

        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 4, maxTextureSize: 1024);

        Assert.True(plan.AnyNormals);
        Assert.True(plan.AnyTangents);
    }

    // ─────────────────────────── BuildArrays ───────────────────────────

    [Fact]
    public void BuildArrays_DeltasLandAtTheirLayerTexels()
    {
        var shapes = new[]
        {
            new BlendShape { Name = "A", Frames = new[] { Frame(100f, new[] { new Float3(1, 0, 0), new Float3(2, 0, 0) }) } },
            new BlendShape { Name = "B", Frames = new[] { Frame(50f, new[] { new Float3(0, 5, 0) }), Frame(100f, new[] { new Float3(0, 0, 7) }) } },
        };
        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 3, maxTextureSize: 1024);

        var (pos, nrm, tan) = MorphLayout.BuildArrays(shapes, plan);

        Assert.Null(nrm);
        Assert.Null(tan);

        // Shape A layer 0 (offset 0): 2 deltas then a zero tail.
        Assert.Equal(new Float4(1, 0, 0, 0), pos[0]);
        Assert.Equal(new Float4(2, 0, 0, 0), pos[1]);
        Assert.Equal(Float4.Zero, pos[2]);
        // Shape B frame 0 → layer 1 (offset 1).
        Assert.Equal(new Float4(0, 5, 0, 0), pos[3]);
        // Shape B frame 1 → layer 2 (offset 2).
        Assert.Equal(new Float4(0, 0, 7, 0), pos[6]);
        // Texel rows between layer blocks: nothing spilled into layer 1's remaining texels.
        Assert.Equal(Float4.Zero, pos[4]);
        Assert.Equal(Float4.Zero, pos[5]);
    }

    [Fact]
    public void BuildArrays_BuildsNormalAndTangentArraysOnlyWhenFramesHaveThem()
    {
        var shapes = new[]
        {
            new BlendShape { Name = "A", Frames = new[] { Frame(100f, new[] { new Float3(1, 0, 0) },
                normals: new[] { new Float3(0, 1, 0) }) } },
        };
        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 2, maxTextureSize: 1024);

        var (pos, nrm, tan) = MorphLayout.BuildArrays(shapes, plan);

        Assert.NotNull(nrm);
        Assert.Null(tan);
        Assert.Equal(new Float4(0, 1, 0, 0), nrm![0]);
        Assert.Equal(Float4.Zero, nrm[1]); // tail zero
    }

    [Fact]
    public void BuildArrays_ShorterDeltaArraysLeaveTailZeroed()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(100f, new[] { new Float3(1, 1, 1) }) } } };
        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 4, maxTextureSize: 1024);

        var (pos, _, _) = MorphLayout.BuildArrays(shapes, plan);

        Assert.Equal(new Float4(1, 1, 1, 0), pos[0]);
        Assert.Equal(Float4.Zero, pos[1]);
        Assert.Equal(Float4.Zero, pos[3]);
    }

    // ─────────────────────────── ResolveLayers ───────────────────────────

    [Fact]
    public void ResolveLayers_ZeroWeights_YieldNoLayers()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(100f) } } };
        var results = new List<Float4>();

        MorphLayout.ResolveLayers(shapes, new float[] { 0f, 0f }, plan(1, shapes), results);

        Assert.Empty(results);
    }

    [Fact]
    public void ResolveLayers_NullWeights_YieldNoLayers()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(100f) } } };
        var results = new List<Float4> { new(9, 9, 9, 9) }; // stale data must be cleared

        MorphLayout.ResolveLayers(shapes, null, plan(1, shapes), results);

        Assert.Empty(results);
    }

    [Fact]
    public void ResolveLayers_SingleFrame_ScalesByFrameWeight_AllowingOvershoot()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(50f) } } };

        var half = resolve(shapes, new float[] { 25f });
        var full = resolve(shapes, new float[] { 100f });

        var halfLayer = Assert.Single(half);
        Assert.Equal(0, (int)halfLayer.X);
        Assert.Equal(0.5f, halfLayer.Y);
        var fullLayer = Assert.Single(full);
        Assert.Equal(2f, fullLayer.Y); // 100/50 — overshoot past the authored frame is kept
    }

    [Fact]
    public void ResolveLayers_BelowFirstFrame_ScalesFirstFrameDown()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(25f), Frame(75f) } } };

        var layers = resolve(shapes, new float[] { 12.5f });

        var layer = Assert.Single(layers);
        Assert.Equal(0, (int)layer.X);
        Assert.Equal(0.5f, layer.Y);
    }

    [Fact]
    public void ResolveLayers_AtOrAboveLastFrame_ClampsToFull()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(25f), Frame(75f) } } };

        var atLast = resolve(shapes, new float[] { 75f });
        var beyondLast = resolve(shapes, new float[] { 120f });

        var a = Assert.Single(atLast);
        Assert.Equal(1, (int)a.X);
        Assert.Equal(1f, a.Y);
        var b = Assert.Single(beyondLast);
        Assert.Equal(1f, b.Y); // no overshoot layer beyond the final frame
    }

    [Fact]
    public void ResolveLayers_BetweenFrames_InterpolatesNeighbouringFrames()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(25f), Frame(75f) } } };

        // w=50 → t=(50-25)/50=0.5 → layer0 0.5 + layer1 0.5.
        var mid = resolve(shapes, new float[] { 50f });
        Assert.Equal(2, mid.Count);
        Assert.Equal(0.5f, mid[0].Y);
        Assert.Equal(1, (int)mid[1].X);
        Assert.Equal(0.5f, mid[1].Y);

        // w=40 → t=0.3 → layer0 0.7 + layer1 0.3.
        var early = resolve(shapes, new float[] { 40f });
        Assert.Equal(2, early.Count);
        Assert.Equal(0.7f, early[0].Y, 4);
        Assert.Equal(0.3f, early[1].Y, 4);
    }

    [Fact]
    public void ResolveLayers_MultiShape_UsesPerShapeLayerOffsets()
    {
        var shapes = new[]
        {
            new BlendShape { Name = "A", Frames = new[] { Frame(100f) } },
            new BlendShape { Name = "B", Frames = new[] { Frame(100f) } },
        };

        var layers = resolve(shapes, new float[] { 100f, 50f });

        Assert.Equal(2, layers.Count);
        Assert.Equal(0, (int)layers[0].X);
        Assert.Equal(1f, layers[0].Y);
        Assert.Equal(1, (int)layers[1].X);
        Assert.Equal(0.5f, layers[1].Y);
    }

    [Fact]
    public void ResolveLayers_TinyWeights_AreSkipped()
    {
        var shapes = new[] { new BlendShape { Name = "A", Frames = new[] { Frame(100f) } } };

        var layers = resolve(shapes, new float[] { 5e-5f });

        Assert.Empty(layers);
    }

    [Fact]
    public void ResolveLayers_WeightsShorterThanShapes_MissingAreZero()
    {
        var shapes = new[]
        {
            new BlendShape { Name = "A", Frames = new[] { Frame(100f) } },
            new BlendShape { Name = "B", Frames = new[] { Frame(100f) } },
        };

        var layers = resolve(shapes, new float[] { 100f });

        var layer = Assert.Single(layers);
        Assert.Equal(0, (int)layer.X);
    }

    [Fact]
    public void ResolveLayers_EmptyFrames_YieldNoLayers()
    {
        var shapes = new[]
        {
            new BlendShape { Name = "A", Frames = Array.Empty<BlendShapeFrame>() },
            new BlendShape { Name = "B", Frames = new[] { Frame(100f) } },
        };

        var layers = resolve(shapes, new float[] { 100f, 100f });

        var layer = Assert.Single(layers);
        Assert.Equal(0, (int)layer.X); // shape B collapses to offset 0 — no layers were reserved for A
    }

    private static int[] plan(int length, BlendShape[] shapes)
    {
        var plan = MorphLayout.ComputePlan(shapes, vertexCount: 1, maxTextureSize: 1024);
        Assert.Equal(length, plan.LayerOffsets.Length);
        return plan.LayerOffsets;
    }

    private static List<Float4> resolve(BlendShape[] shapes, float[] weights)
    {
        var results = new List<Float4>();
        MorphLayout.ResolveLayers(shapes, weights, plan(shapes.Length, shapes), results);
        return results;
    }
}
