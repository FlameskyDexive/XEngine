// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using Xunit;

namespace XEngine.Vector.Tests;

public class MathAndBatchTests
{
    [Fact]
    public void PublicVectorAbi_RemainsTightlyPacked()
    {
        Assert.Equal(8, Marshal.SizeOf<Float2>());
        Assert.Equal(12, Marshal.SizeOf<Float3>());
        Assert.Equal(16, Marshal.SizeOf<Float4>());
        Assert.Equal(0, Marshal.OffsetOf<Float3>(nameof(Float3.X)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<Float3>(nameof(Float3.Y)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<Float3>(nameof(Float3.Z)).ToInt32());

        Assert.Equal(8, Marshal.SizeOf<UInt2>());
        Assert.Equal(12, Marshal.SizeOf<UInt3>());
        Assert.Equal(16, Marshal.SizeOf<UInt4>());
        Assert.Equal(1, Marshal.SizeOf<Bool2>());
        Assert.Equal(1, Marshal.SizeOf<Bool3>());
        Assert.Equal(1, Marshal.SizeOf<Bool4>());
        Assert.Equal(4, Marshal.SizeOf<Half2>());
        Assert.Equal(6, Marshal.SizeOf<Half3>());
        Assert.Equal(8, Marshal.SizeOf<Half4>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(31)]
    [InlineData(256)]
    [InlineData(4096)]
    public void AddAndMultiply_HandleScalarAndIntrinsicTails(int count)
    {
        var left = new Float3[count];
        var right = new Float3[count];
        var result = new Float3[count];
        for (int i = 0; i < count; i++)
        {
            left[i] = new Float3(i + 0.25f, i + 0.5f, i + 0.75f);
            right[i] = new Float3(i + 1f, i + 2f, i + 3f);
        }

        BatchMath.Add(left, right, result);
        for (int i = 0; i < count; i++)
            Assert.Equal(left[i] + right[i], result[i]);

        BatchMath.Multiply(result, right, result);
        for (int i = 0; i < count; i++)
            Assert.Equal((left[i] + right[i]) * right[i], result[i]);
    }

    [Fact]
    public void BatchOperations_AllowIdentityOverlapAndRejectPartialOverlap()
    {
        var values = new Float3[5];
        var addends = new Float3[4];
        for (int i = 0; i < values.Length; i++)
            values[i] = new Float3(i);
        for (int i = 0; i < addends.Length; i++)
            addends[i] = Float3.One;

        BatchMath.Add(values.AsSpan(0, 4), addends, values.AsSpan(0, 4));
        Assert.Equal(new Float3(1f), values[0]);

        Assert.Throws<ArgumentException>(() =>
            BatchMath.Add(values.AsSpan(0, 4), addends, values.AsSpan(1, 4)));
    }

    [Fact]
    public void DotNormalizeTransformAndBounds_PreserveScalarSemantics()
    {
        Float3[] source =
        {
            new Float3(3f, 4f, 0f),
            Float3.Zero,
            new Float3(float.NaN, 0f, 0f),
        };
        var normalized = new Float3[source.Length];
        var dots = new float[source.Length];

        BatchMath.Normalize(source, normalized);
        BatchMath.Dot(source, source, dots);

        Assert.Equal(new Float3(0.6f, 0.8f, 0f), normalized[0]);
        Assert.Equal(Float3.Zero, normalized[1]);
        Assert.Equal(Float3.Zero, normalized[2]);
        Assert.Equal(25f, dots[0]);
        Assert.True(float.IsNaN(dots[2]));

        var matrix = new Float4x4(Float3x3.Identity, new Float3(10f, 20f, 30f));
        BatchMath.TransformPoints(source.AsSpan(0, 2), matrix, normalized.AsSpan(0, 2));
        Assert.Equal(new Float3(13f, 24f, 30f), normalized[0]);
        Assert.Equal(new Float3(10f, 20f, 30f), normalized[1]);

        Assert.True(BatchMath.TryReduceAabb(source.AsSpan(0, 2), out AABB bounds));
        Assert.Equal(Float3.Zero, bounds.Min);
        Assert.Equal(new Float3(3f, 4f, 0f), bounds.Max);
        Assert.False(BatchMath.TryReduceAabb(ReadOnlySpan<Float3>.Empty, out _));
    }

    [Fact]
    public void BatchAdd_SteadyStateDoesNotAllocate()
    {
        var left = new float[4096];
        var right = new float[4096];
        var result = new float[4096];
        BatchMath.Add(left, right, result);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            BatchMath.Add(left, right, result);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    [Fact]
    public void HalfBitCastSelectShuffleAndHash_HaveStableResults()
    {
        var half = new Half4(1f, -2f, float.PositiveInfinity, float.NaN);
        Float4 roundTrip = half.ToFloat4();
        Assert.Equal(1f, roundTrip.X);
        Assert.Equal(-2f, roundTrip.Y);
        Assert.True(float.IsPositiveInfinity(roundTrip.Z));
        Assert.True(float.IsNaN(roundTrip.W));

        Float3 source = new(-0f, 1f, float.NaN);
        Assert.Equal(Maths.BitCastToUInt(source), Maths.BitCastToUInt(Maths.BitCastToFloat(Maths.BitCastToUInt(source))));

        var selected = Maths.Select(Float3.Zero, Float3.One, new Bool3(true, false, true));
        Assert.Equal(new Float3(1f, 0f, 1f), selected);
        Assert.True(Maths.Any(new Bool4(false, false, true, false)));
        Assert.False(Maths.All(new Bool4(true, true, false, true)));

        var shuffled = Maths.Shuffle(new Float4(1f, 2f, 3f, 4f), new Float4(5f, 6f, 7f, 8f), 7, 0, 5, 2);
        Assert.Equal(new Float4(8f, 1f, 6f, 3f), shuffled);
        Assert.Equal(Maths.Hash(source), Maths.Hash(source));
    }

    [Fact]
    public void NonSquareAndAffineTransforms_FollowColumnMajorConvention()
    {
        var matrix = new Float2x3(1f, 2f, 3f, 4f, 5f, 6f);
        Assert.Equal(new Float2(14f, 32f), matrix * new Float3(1f, 2f, 3f));
        Assert.Equal(matrix, matrix.Transpose().Transpose());

        var rigid = new RigidTransform2D(Maths.PI * 0.5f, new Float2(10f, 20f));
        Float2 transformed = rigid.TransformPoint(Float2.UnitX);
        Assert.InRange(transformed.X, 9.9999f, 10.0001f);
        Assert.InRange(transformed.Y, 20.9999f, 21.0001f);
        Float2 restored = rigid.Inverse().TransformPoint(transformed);
        Assert.InRange(restored.X, 0.9999f, 1.0001f);
        Assert.InRange(restored.Y, -0.0001f, 0.0001f);

        var affine = new AffineTransform3D(Float3x3.Identity, new Float3(4f, 5f, 6f));
        Assert.Equal(new Float3(5f, 7f, 9f), affine.TransformPoint(new Float3(1f, 2f, 3f)));
        Assert.Equal(new Float3(1f, 2f, 3f), affine.Inverse().TransformPoint(new Float3(5f, 7f, 9f)));
    }

    [Fact]
    public void FastMaths_IsExplicitAndWithinEstimateTolerance()
    {
        float reciprocal = FastMaths.ReciprocalEstimate(7f);
        float reciprocalSqrt = FastMaths.ReciprocalSqrtEstimate(7f);
        Assert.InRange(Maths.Abs(reciprocal - (1f / 7f)), 0f, 0.001f);
        Assert.InRange(Maths.Abs(reciprocalSqrt - (1f / Maths.Sqrt(7f))), 0f, 0.001f);
        Assert.Equal(10f, FastMaths.MultiplyAdd(2f, 3f, 4f));
    }

    [Fact]
    public void NormalizeSafeClampSaturateAndSkinning_PreserveScalarSemantics()
    {
        Float3[] source =
        {
            new Float3(3f, 4f, 0f),
            Float3.Zero,
            new Float3(-2f, 3f, 4f),
        };
        var normalized = new Float3[source.Length];
        BatchMath.NormalizeSafe(source, Float3.UnitY, normalized);
        Assert.Equal(new Float3(0.6f, 0.8f, 0f), normalized[0]);
        Assert.Equal(Float3.UnitY, normalized[1]);

        var clamped = new Float3[source.Length];
        BatchMath.Clamp(source, 0f, 1f, clamped);
        Assert.Equal(new Float3(1f, 1f, 0f), clamped[0]);
        Assert.Equal(Float3.Zero, clamped[1]);
        Assert.Equal(new Float3(0f, 1f, 1f), clamped[2]);

        var saturated = new Float3[1];
        BatchMath.Saturate(new Float3[] { new(-0.5f, 0.25f, 1.5f) }, saturated);
        Assert.Equal(new Float3(0f, 0.25f, 1f), saturated[0]);

        var skinned = new Float3[1];
        BatchMath.SkinPositions(
            new Float3[] { Float3.Zero },
            new Float4[] { new(0.5f, 0.5f, 0f, 0f) },
            new UInt4[] { new(0, 1, 0, 0) },
            new Float4x4[]
            {
                new(Float3x3.Identity, new Float3(10f, 0f, 0f)),
                new(Float3x3.Identity, new Float3(0f, 20f, 0f)),
            },
            skinned);
        Assert.Equal(new Float3(5f, 10f, 0f), skinned[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(256)]
    public void Saturate_HandlesScalarAndIntrinsicTails(int count)
    {
        var source = new float[count];
        var destination = new float[count];
        for (int i = 0; i < count; i++)
            source[i] = i - count / 2f;
        BatchMath.Saturate(source, destination);
        for (int i = 0; i < count; i++)
            Assert.Equal(Maths.Saturate(source[i]), destination[i]);
    }

    [Fact]
    public void AoSoALanePacks_RoundTripWithoutBecomingPublicVectorTypes()
    {
        Type? packedType = typeof(Float3).Assembly.GetType("XEngine.Vector.Float3Lane4");
        Assert.NotNull(packedType);
        Assert.False(packedType!.IsPublic);
        var values = new Float3[]
        {
            new(1f, 2f, 3f),
            new(4f, 5f, 6f),
            new(7f, 8f, 9f),
            new(10f, 11f, 12f),
            new(13f, 14f, 15f),
            new(16f, 17f, 18f),
            new(19f, 20f, 21f),
            new(22f, 23f, 24f),
        };
        var packed = Float3Lane8.Load(values);
        var restored = new Float3[8];
        packed.Store(restored);
        Assert.Equal(values, restored);
    }

    [Fact]
    public void ActiveBackend_IsHardwareDispatched()
    {
        Assert.Contains(BatchMath.ActiveBackend, new[]
        {
            BatchMathBackend.Scalar,
            BatchMathBackend.Sse,
            BatchMathBackend.Avx2,
            BatchMathBackend.AdvSimd,
        });
    }
}
