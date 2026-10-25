using System.Collections.Generic;

using Xunit;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.GpuAnimation.Tests;

/// <summary>
/// CPU-side per-instance API: BuildInstancePayloads must map every GpuAnimatedInstance field
/// into the engine InstanceData payload (CustomData = clip/phase/speed, Color = tint, rows =
/// TRS) and BuildClipTableData must encode the atlas layout. Device-free.
/// </summary>
public sealed class GpuAnimationInstanceApiTests
{
    private static BakedGpuAnimation BuildTwoClipAsset()
    {
        var asset = new BakedGpuAnimation("api.gpuanim")
        {
            TextureWidth = 4,
            TextureHeight = 12,
            VertexCount = 3,
            HasNormals = false,
            Positions = new float[4 * 12 * 4],
            FrameBoundsMin = new float[12 * 3],
            FrameBoundsMax = new float[12 * 3],
        };
        for (int r = 0; r < 12; r++)
        {
            asset.FrameBoundsMax[r * 3] = 2f;
            asset.FrameBoundsMax[r * 3 + 1] = 3f;
            asset.FrameBoundsMax[r * 3 + 2] = 1f;
        }
        asset.Clips.Add(new GpuAnimationClipInfo { Name = "A", FrameStart = 0, FrameCount = 8, AnimLength = 2f, Fps = 4f });
        asset.Clips.Add(new GpuAnimationClipInfo { Name = "B", FrameStart = 8, FrameCount = 4, AnimLength = 1f, Fps = 4f });
        return asset;
    }

    [Fact]
    public void BuildInstancePayloads_MapsAllFields()
    {
        using var asset = BuildTwoClipAsset();
        var instances = new List<GpuAnimatedInstance>();

        var a = GpuAnimatedInstance.Place(new Float3(10f, 0f, 20f), phase: 0.25f, speed: 2f, clip: 1, headingDegrees: 90f);
        a.Tint = new Float4(1f, 0.5f, 0.25f, 0.75f);
        a.Scale = new Float3(2f, 2f, 2f);
        instances.Add(a);
        instances.Add(GpuAnimatedInstance.Place(Float3.Zero)); // defaults: clip 0, phase 0, speed 1

        var (transforms, colors, custom, bounds) = GpuAnimatedCrowd.BuildInstancePayloads(asset, instances);

        // CustomData = (clip, phase, speed, 0) — exactly what the shader unpacks.
        Assert.Equal(1f, custom[0].X, 5);
        Assert.Equal(0.25f, custom[0].Y, 5);
        Assert.Equal(2f, custom[0].Z, 5);
        Assert.Equal(0f, custom[0].W, 5);
        Assert.Equal(0f, custom[1].X, 5);
        Assert.Equal(1f, custom[1].Z, 5);

        // Tint passes through as the instance color channel.
        Assert.Equal(0.5f, colors[0].Y, 5);
        Assert.Equal(0.75f, colors[0].W, 5);
        Assert.Equal(1f, colors[1].X, 5);

        // TRS: instance A rotated 90° heading and scaled 2x at (10,0,20); union bounds cover both.
        Float4x4 trsA = transforms[0];
        Assert.Equal(10f, trsA.c3.X, 4);
        Assert.Equal(20f, trsA.c3.Z, 4);
        // Rotated +Z basis: after 90° yaw the local +X maps to world -Z (or +Z depending on
        // convention) — the column must be unit-length either way.
        float colLen = Float3.Length(new Float3(trsA.c0.X, trsA.c0.Y, trsA.c0.Z));
        Assert.Equal(2f, colLen, 4); // scale 2 baked into the basis columns

        // Bounds: local (0..2, 0..3, 0..1) scaled 2 => (0..4, 0..6, 0..2); A's 90° yaw maps the
        // local X extent onto -Z (engine yaw convention): x' ∈ [10,12], z' ∈ [16,20].
        Assert.Equal(0f, bounds.Min.X, 4);
        Assert.Equal(12f, bounds.Max.X, 4);
        Assert.Equal(0f, bounds.Min.Z, 4);
        Assert.Equal(20f, bounds.Max.Z, 4);
        Assert.Equal(6f, bounds.Max.Y, 4);
    }

    [Fact]
    public void BuildClipTableData_EncodesAtlasLayout()
    {
        using var asset = BuildTwoClipAsset();
        Float4[] table = GpuAnimatedCrowd.BuildClipTableData(asset);
        Assert.Equal(2, table.Length);
        Assert.Equal(0f, table[0].X, 5); // clip A: start row 0
        Assert.Equal(8f, table[0].Y, 5); // 8 frames
        Assert.Equal(2f, table[0].Z, 5); // 2 s loop
        Assert.Equal(8f, table[1].X, 5); // clip B starts at row 8
        Assert.Equal(4f, table[1].Y, 5);
        Assert.Equal(1f, table[1].Z, 5);
    }

    [Fact]
    public void Place_Defaults_ArePlayReady()
    {
        GpuAnimatedInstance inst = GpuAnimatedInstance.Place(new Float3(1, 2, 3));
        Assert.Equal(0, inst.Clip);
        Assert.Equal(0f, inst.Phase, 5);
        Assert.Equal(1f, inst.Speed, 5);
        Assert.Equal(1f, inst.Tint.X, 5);
        Assert.Equal(1f, inst.Tint.W, 5);
        Assert.Equal(1f, inst.Scale.X, 5);
    }
}
