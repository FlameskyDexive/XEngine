using System;
using System.Linq;

using Xunit;

using XEngine.Echo;

namespace XEngine.GpuAnimation.Tests;

public sealed class GpuAnimationAssetTests
{
    private static BakedGpuAnimation BuildSampleAsset()
    {
        const int width = 4;    // next pow2 of 3 vertices
        const int height = 12;  // clip A rows 0-7, clip B rows 8-11
        var asset = new BakedGpuAnimation("Sample.gpuanim")
        {
            TextureWidth = width,
            TextureHeight = height,
            VertexCount = 3,
            HasNormals = true,
            Precision = GpuAnimationPrecision.Float32,
        };
        var positions = new float[width * height * 4];
        var normals = new float[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int i = (row * width + col) * 4;
                positions[i] = row * 100f + col;
                positions[i + 1] = row * 100f + col + 0.1f;
                positions[i + 2] = row * 100f + col + 0.25f;
                positions[i + 3] = 1f;
                normals[i] = -(row * 100f + col);
                normals[i + 1] = 0f;
                normals[i + 2] = 1f;
                normals[i + 3] = 1f;
            }
        }
        asset.Positions = positions;
        asset.Normals = normals;
        asset.FrameBoundsMin = new float[height * 3];
        asset.FrameBoundsMax = new float[height * 3];
        for (int row = 0; row < height; row++)
        {
            asset.FrameBoundsMin[row * 3] = row * 100f;
            asset.FrameBoundsMin[row * 3 + 1] = row * 100f;
            asset.FrameBoundsMin[row * 3 + 2] = row * 100f;
            asset.FrameBoundsMax[row * 3] = row * 100f + 3f;
            asset.FrameBoundsMax[row * 3 + 1] = row * 100f + 3.1f;
            asset.FrameBoundsMax[row * 3 + 2] = row * 100f + 3.25f;
        }
        asset.Clips.Add(new GpuAnimationClipInfo { Name = "Walk", FrameStart = 0, FrameCount = 8, AnimLength = 8f / 30f, Fps = 30f });
        asset.Clips.Add(new GpuAnimationClipInfo { Name = "Idle", FrameStart = 8, FrameCount = 4, AnimLength = 4f / 30f, Fps = 30f });
        return asset;
    }

    [Fact]
    public void EchoRoundtrip_PreservesAllDataExactly()
    {
        var asset = BuildSampleAsset();
        EchoObject echo = Serializer.Serialize(typeof(object), asset);
        string text = echo.WriteToString();
        EchoObject readBack = EchoObject.ReadFromString(text);
        var clone = Serializer.Deserialize<BakedGpuAnimation>(readBack);
        Assert.NotNull(clone);

        Assert.Equal("Sample.gpuanim", clone!.Name);
        Assert.Equal(4, clone.TextureWidth);
        Assert.Equal(12, clone.TextureHeight);
        Assert.Equal(3, clone.VertexCount);
        Assert.True(clone.HasNormals);
        Assert.Equal(GpuAnimationPrecision.Float32, clone.Precision);
        Assert.Equal(2, clone.Clips.Count);

        var walk = clone.Clips[0];
        Assert.Equal("Walk", walk.Name);
        Assert.Equal(0, walk.FrameStart);
        Assert.Equal(8, walk.FrameCount);
        Assert.Equal(8f / 30f, walk.AnimLength);
        var idle = clone.Clips[1];
        Assert.Equal("Idle", idle.Name);
        Assert.Equal(8, idle.FrameStart);
        Assert.Equal(4, idle.FrameCount);

        // Base64-backed float arrays must round-trip bit-exact.
        Assert.Equal(asset.Positions.Length, clone.Positions.Length);
        Assert.Equal(asset.Normals.Length, clone.Normals.Length);
        for (int i = 0; i < asset.Positions.Length; i++)
        {
            Assert.Equal(asset.Positions[i].ToString("R"), clone.Positions[i].ToString("R"));
            Assert.Equal(asset.Normals[i].ToString("R"), clone.Normals[i].ToString("R"));
        }
        Assert.True(asset.FrameBoundsMin.SequenceEqual(clone.FrameBoundsMin));
        Assert.True(asset.FrameBoundsMax.SequenceEqual(clone.FrameBoundsMax));
    }

    [Fact]
    public void TexelIndexing_AndUnionBounds()
    {
        var asset = BuildSampleAsset();
        Assert.Equal(0, asset.TexelIndex(0, 0));
        Assert.Equal(4, asset.TexelIndex(0, 1));
        Assert.Equal(16, asset.TexelIndex(1, 0));
        Assert.Equal((8 * 4 + 2) * 4, asset.TexelIndex(8, 2));

        asset.GetPosition(5, 2, out var pos);
        Assert.Equal(502f, pos.X);
        Assert.Equal(502.1f, pos.Y);
        Assert.Equal(502.25f, pos.Z);
        asset.GetNormal(5, 2, out var nrm);
        Assert.Equal(-502f, nrm.X);

        asset.GetUnionBounds(out var min, out var max);
        Assert.Equal(0f, min.X);
        Assert.Equal(11 * 100f + 3.25f, max.Z);
    }

    [Fact]
    public void ClipMath_WrapsInsideOwnSegment()
    {
        var clip = new GpuAnimationClipInfo { Name = "Walk", FrameStart = 4, FrameCount = 8, AnimLength = 2f, Fps = 4f };

        // t=0 → first row, no blend.
        GpuAnimMath.SampleRows(GpuAnimMath.ContinuousRow(clip, 0f), clip, out int r0, out int r1, out float b);
        Assert.Equal(4, r0);
        Assert.Equal(5, r1);
        Assert.Equal(0f, b, 5);

        // Halfway → row 8, next row 9.
        GpuAnimMath.SampleRows(GpuAnimMath.ContinuousRow(clip, 1f), clip, out r0, out r1, out b);
        Assert.Equal(8, r0);
        Assert.Equal(9, r1);
        Assert.Equal(0f, b, 5);

        // Loop wrap: last frame blends into the clip's first row (4), not the atlas row below.
        GpuAnimMath.SampleRows(GpuAnimMath.ContinuousRow(clip, 2f - 0.001f), clip, out r0, out r1, out b);
        Assert.Equal(11, r0);
        Assert.Equal(4, r1);
        Assert.True(b > 0.99f);

        // Phase offset of half a loop.
        GpuAnimMath.SampleRows(GpuAnimMath.ContinuousRow(clip, 0f, phase: 0.5f), clip, out r0, out r1, out b);
        Assert.Equal(8, r0);

        // Speed 2× reaches the loop point in half the time.
        GpuAnimMath.SampleRows(GpuAnimMath.ContinuousRow(clip, 0.5f, speed: 2f), clip, out r0, out r1, out b);
        Assert.Equal(8, r0);

        // U/V texel centers.
        Assert.Equal(0.375f, GpuAnimMath.VertexU(1, 4), 6);
        Assert.Equal(0.5f, GpuAnimMath.RowV(5, 11), 6);
    }

    [Fact]
    public void NextPowerOfTwo_Cases()
    {
        Assert.Equal(1, GpuAnimMath.NextPowerOfTwo(1));
        Assert.Equal(2, GpuAnimMath.NextPowerOfTwo(2));
        Assert.Equal(4, GpuAnimMath.NextPowerOfTwo(3));
        Assert.Equal(2048, GpuAnimMath.NextPowerOfTwo(1025));
    }
}
