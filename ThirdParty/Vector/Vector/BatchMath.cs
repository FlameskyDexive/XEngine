// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace XEngine.Vector;

/// <summary>Hardware backend selected for component-wise batch operations.</summary>
public enum BatchMathBackend
{
    Scalar,
    Sse,
    Avx2,
    AdvSimd,
}

/// <summary>
/// Allocation-free operations over tightly packed vector spans. Input and output may be the same
/// span, but partially overlapping spans are rejected because their results are order-dependent.
/// </summary>
public static class BatchMath
{
    /// <summary>Minimum number of logical elements required before an intrinsic kernel is used.</summary>
    public const int SimdThreshold = 256;

    public static BatchMathBackend ActiveBackend
    {
        get
        {
#if NET8_0_OR_GREATER
            if (Avx2.IsSupported)
                return BatchMathBackend.Avx2;
            if (Sse.IsSupported)
                return BatchMathBackend.Sse;
            if (AdvSimd.IsSupported)
                return BatchMathBackend.AdvSimd;
#endif
            return BatchMathBackend.Scalar;
        }
    }

    public static void Add(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination)
    {
        ValidateBinary(left, right, destination);
        AddComponents(left, right, destination, left.Length);
    }

    public static void Add(ReadOnlySpan<Float2> left, ReadOnlySpan<Float2> right, Span<Float2> destination)
    {
        ValidateBinary(left, right, destination);
        AddComponents(AsFloats(left), AsFloats(right), AsFloats(destination), left.Length);
    }

    public static void Add(ReadOnlySpan<Float3> left, ReadOnlySpan<Float3> right, Span<Float3> destination)
    {
        ValidateBinary(left, right, destination);
        AddComponents(AsFloats(left), AsFloats(right), AsFloats(destination), left.Length);
    }

    public static void Add(ReadOnlySpan<Float4> left, ReadOnlySpan<Float4> right, Span<Float4> destination)
    {
        ValidateBinary(left, right, destination);
        AddComponents(AsFloats(left), AsFloats(right), AsFloats(destination), left.Length);
    }

    public static void Multiply(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination)
    {
        ValidateBinary(left, right, destination);
        MultiplyComponents(left, right, destination, left.Length);
    }

    public static void Multiply(ReadOnlySpan<Float2> left, ReadOnlySpan<Float2> right, Span<Float2> destination)
    {
        ValidateBinary(left, right, destination);
        MultiplyComponents(AsFloats(left), AsFloats(right), AsFloats(destination), left.Length);
    }

    public static void Multiply(ReadOnlySpan<Float3> left, ReadOnlySpan<Float3> right, Span<Float3> destination)
    {
        ValidateBinary(left, right, destination);
        MultiplyComponents(AsFloats(left), AsFloats(right), AsFloats(destination), left.Length);
    }

    public static void Multiply(ReadOnlySpan<Float4> left, ReadOnlySpan<Float4> right, Span<Float4> destination)
    {
        ValidateBinary(left, right, destination);
        MultiplyComponents(AsFloats(left), AsFloats(right), AsFloats(destination), left.Length);
    }

    public static void Dot(ReadOnlySpan<Float2> left, ReadOnlySpan<Float2> right, Span<float> destination)
    {
        ValidateDot(left, right, destination);
        for (int i = 0; i < left.Length; i++)
            destination[i] = Float2.Dot(left[i], right[i]);
    }

    public static void Dot(ReadOnlySpan<Float3> left, ReadOnlySpan<Float3> right, Span<float> destination)
    {
        ValidateDot(left, right, destination);
        for (int i = 0; i < left.Length; i++)
            destination[i] = Float3.Dot(left[i], right[i]);
    }

    public static void Dot(ReadOnlySpan<Float4> left, ReadOnlySpan<Float4> right, Span<float> destination)
    {
        ValidateDot(left, right, destination);
        for (int i = 0; i < left.Length; i++)
            destination[i] = Float4.Dot(left[i], right[i]);
    }

    public static void Normalize(ReadOnlySpan<Float2> source, Span<Float2> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
            destination[i] = Float2.Normalize(source[i]);
    }

    public static void Normalize(ReadOnlySpan<Float3> source, Span<Float3> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
            destination[i] = Float3.Normalize(source[i]);
    }

    public static void Normalize(ReadOnlySpan<Float4> source, Span<Float4> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
            destination[i] = Float4.Normalize(source[i]);
    }

    public static void NormalizeSafe(ReadOnlySpan<Float2> source, Float2 defaultValue, Span<Float2> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
            destination[i] = Float2.NormalizeSafe(source[i], defaultValue);
    }

    public static void NormalizeSafe(ReadOnlySpan<Float3> source, Float3 defaultValue, Span<Float3> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
            destination[i] = Float3.NormalizeSafe(source[i], defaultValue);
    }

    public static void NormalizeSafe(ReadOnlySpan<Float4> source, Float4 defaultValue, Span<Float4> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
            destination[i] = Float4.NormalizeSafe(source[i], defaultValue);
    }

    public static void Clamp(ReadOnlySpan<float> source, float min, float max, Span<float> destination)
    {
        ValidateUnary(source, destination);
        ClampComponents(source, min, max, destination, source.Length);
    }

    public static void Clamp(ReadOnlySpan<Float2> source, float min, float max, Span<Float2> destination)
    {
        ValidateUnary(source, destination);
        ClampComponents(AsFloats(source), min, max, AsFloats(destination), source.Length);
    }

    public static void Clamp(ReadOnlySpan<Float3> source, float min, float max, Span<Float3> destination)
    {
        ValidateUnary(source, destination);
        ClampComponents(AsFloats(source), min, max, AsFloats(destination), source.Length);
    }

    public static void Clamp(ReadOnlySpan<Float4> source, float min, float max, Span<Float4> destination)
    {
        ValidateUnary(source, destination);
        ClampComponents(AsFloats(source), min, max, AsFloats(destination), source.Length);
    }

    public static void Clamp(ReadOnlySpan<Float3> source, Float3 min, Float3 max, Span<Float3> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
            destination[i] = Maths.Clamp(source[i], min, max);
    }

    public static void Saturate(ReadOnlySpan<float> source, Span<float> destination)
        => Clamp(source, 0f, 1f, destination);

    public static void Saturate(ReadOnlySpan<Float2> source, Span<Float2> destination)
        => Clamp(source, 0f, 1f, destination);

    public static void Saturate(ReadOnlySpan<Float3> source, Span<Float3> destination)
        => Clamp(source, 0f, 1f, destination);

    public static void Saturate(ReadOnlySpan<Float4> source, Span<Float4> destination)
        => Clamp(source, 0f, 1f, destination);

    public static void TransformPoints(ReadOnlySpan<Float3> source, in Float4x4 matrix, Span<Float3> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
            destination[i] = Float4x4.TransformPoint(source[i], matrix);
    }

    public static void TransformVectors(ReadOnlySpan<Float3> source, in Float4x4 matrix, Span<Float3> destination)
    {
        ValidateUnary(source, destination);
        for (int i = 0; i < source.Length; i++)
        {
            Float3 value = source[i];
            destination[i] = matrix.c0.XYZ * value.X + matrix.c1.XYZ * value.Y + matrix.c2.XYZ * value.Z;
        }
    }

    /// <summary>Applies four-matrix linear blend skinning to a position stream.</summary>
    public static void SkinPositions(
        ReadOnlySpan<Float3> positions,
        ReadOnlySpan<Float4> weights,
        ReadOnlySpan<UInt4> boneIndices,
        ReadOnlySpan<Float4x4> boneMatrices,
        Span<Float3> destination)
    {
        if (weights.Length != positions.Length || boneIndices.Length != positions.Length || destination.Length != positions.Length)
            ThrowLengthMismatch();
        ValidateOverlap(positions, destination);
        RejectAnyByteOverlap(weights, destination);
        RejectAnyByteOverlap(boneIndices, destination);
        RejectAnyByteOverlap(boneMatrices, destination);

        for (int i = 0; i < positions.Length; i++)
        {
            Float3 position = positions[i];
            Float4 weight = weights[i];
            UInt4 indices = boneIndices[i];
            ValidateBoneIndex(indices.X, boneMatrices.Length);
            ValidateBoneIndex(indices.Y, boneMatrices.Length);
            ValidateBoneIndex(indices.Z, boneMatrices.Length);
            ValidateBoneIndex(indices.W, boneMatrices.Length);

            Float3 skinned = Float4x4.TransformPoint(position, boneMatrices[(int)indices.X]) * weight.X;
            skinned += Float4x4.TransformPoint(position, boneMatrices[(int)indices.Y]) * weight.Y;
            skinned += Float4x4.TransformPoint(position, boneMatrices[(int)indices.Z]) * weight.Z;
            skinned += Float4x4.TransformPoint(position, boneMatrices[(int)indices.W]) * weight.W;
            destination[i] = skinned;
        }
    }

    public static bool TryReduceAabb(ReadOnlySpan<Float3> points, out AABB bounds)
    {
        if (points.IsEmpty)
        {
            bounds = default;
            return false;
        }

        Float3 min = points[0];
        Float3 max = min;
        for (int i = 1; i < points.Length; i++)
        {
            Float3 point = points[i];
            min.X = Maths.Min(min.X, point.X);
            min.Y = Maths.Min(min.Y, point.Y);
            min.Z = Maths.Min(min.Z, point.Z);
            max.X = Maths.Max(max.X, point.X);
            max.Y = Maths.Max(max.Y, point.Y);
            max.Z = Maths.Max(max.Z, point.Z);
        }

        bounds = new AABB(min, max);
        return true;
    }

    public static AABB ReduceAabb(ReadOnlySpan<Float3> points)
    {
        if (!TryReduceAabb(points, out AABB bounds))
            ThrowEmptyPoints();
        return bounds;
    }

    private static ReadOnlySpan<float> AsFloats<T>(ReadOnlySpan<T> values) where T : struct
        => MemoryMarshal.Cast<T, float>(values);

    private static Span<float> AsFloats<T>(Span<T> values) where T : struct
        => MemoryMarshal.Cast<T, float>(values);

    private static void AddComponents(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination, int elementCount)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (elementCount >= SimdThreshold)
            i = AddIntrinsic(left, right, destination);
#endif
        for (; i < left.Length; i++)
            destination[i] = left[i] + right[i];
    }

    private static void MultiplyComponents(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination, int elementCount)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (elementCount >= SimdThreshold)
            i = MultiplyIntrinsic(left, right, destination);
#endif
        for (; i < left.Length; i++)
            destination[i] = left[i] * right[i];
    }

    private static void ClampComponents(ReadOnlySpan<float> source, float min, float max, Span<float> destination, int elementCount)
    {
        int i = 0;
#if NET8_0_OR_GREATER
        if (elementCount >= SimdThreshold)
            i = ClampIntrinsic(source, min, max, destination);
#endif
        for (; i < source.Length; i++)
            destination[i] = Maths.Clamp(source[i], min, max);
    }

#if NET8_0_OR_GREATER
    private static int AddIntrinsic(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination)
    {
        ref float leftRef = ref MemoryMarshal.GetReference(left);
        ref float rightRef = ref MemoryMarshal.GetReference(right);
        ref float destinationRef = ref MemoryMarshal.GetReference(destination);
        int i = 0;

        if (Avx2.IsSupported)
        {
            for (; i <= left.Length - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<float> result = Avx.Add(
                    Vector256.LoadUnsafe(ref leftRef, (nuint)i),
                    Vector256.LoadUnsafe(ref rightRef, (nuint)i));
                result.StoreUnsafe(ref destinationRef, (nuint)i);
            }
            return i;
        }

        if (Sse.IsSupported)
        {
            for (; i <= left.Length - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> result = Sse.Add(
                    Vector128.LoadUnsafe(ref leftRef, (nuint)i),
                    Vector128.LoadUnsafe(ref rightRef, (nuint)i));
                result.StoreUnsafe(ref destinationRef, (nuint)i);
            }
            return i;
        }

        if (AdvSimd.IsSupported)
        {
            for (; i <= left.Length - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> result = AdvSimd.Add(
                    Vector128.LoadUnsafe(ref leftRef, (nuint)i),
                    Vector128.LoadUnsafe(ref rightRef, (nuint)i));
                result.StoreUnsafe(ref destinationRef, (nuint)i);
            }
        }

        return i;
    }

    private static int MultiplyIntrinsic(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination)
    {
        ref float leftRef = ref MemoryMarshal.GetReference(left);
        ref float rightRef = ref MemoryMarshal.GetReference(right);
        ref float destinationRef = ref MemoryMarshal.GetReference(destination);
        int i = 0;

        if (Avx2.IsSupported)
        {
            for (; i <= left.Length - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<float> result = Avx.Multiply(
                    Vector256.LoadUnsafe(ref leftRef, (nuint)i),
                    Vector256.LoadUnsafe(ref rightRef, (nuint)i));
                result.StoreUnsafe(ref destinationRef, (nuint)i);
            }
            return i;
        }

        if (Sse.IsSupported)
        {
            for (; i <= left.Length - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> result = Sse.Multiply(
                    Vector128.LoadUnsafe(ref leftRef, (nuint)i),
                    Vector128.LoadUnsafe(ref rightRef, (nuint)i));
                result.StoreUnsafe(ref destinationRef, (nuint)i);
            }
            return i;
        }

        if (AdvSimd.IsSupported)
        {
            for (; i <= left.Length - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> result = AdvSimd.Multiply(
                    Vector128.LoadUnsafe(ref leftRef, (nuint)i),
                    Vector128.LoadUnsafe(ref rightRef, (nuint)i));
                result.StoreUnsafe(ref destinationRef, (nuint)i);
            }
        }

        return i;
    }

    private static int ClampIntrinsic(ReadOnlySpan<float> source, float min, float max, Span<float> destination)
    {
        ref float sourceRef = ref MemoryMarshal.GetReference(source);
        ref float destinationRef = ref MemoryMarshal.GetReference(destination);
        int i = 0;

        if (Avx2.IsSupported)
        {
            Vector256<float> minVec = Vector256.Create(min);
            Vector256<float> maxVec = Vector256.Create(max);
            for (; i <= source.Length - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<float> value = Vector256.LoadUnsafe(ref sourceRef, (nuint)i);
                Avx.Min(maxVec, Avx.Max(minVec, value)).StoreUnsafe(ref destinationRef, (nuint)i);
            }
            return i;
        }

        if (Sse.IsSupported)
        {
            Vector128<float> minVec = Vector128.Create(min);
            Vector128<float> maxVec = Vector128.Create(max);
            for (; i <= source.Length - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> value = Vector128.LoadUnsafe(ref sourceRef, (nuint)i);
                Sse.Min(maxVec, Sse.Max(minVec, value)).StoreUnsafe(ref destinationRef, (nuint)i);
            }
            return i;
        }

        if (AdvSimd.IsSupported)
        {
            Vector128<float> minVec = Vector128.Create(min);
            Vector128<float> maxVec = Vector128.Create(max);
            for (; i <= source.Length - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> value = Vector128.LoadUnsafe(ref sourceRef, (nuint)i);
                AdvSimd.Min(maxVec, AdvSimd.Max(minVec, value)).StoreUnsafe(ref destinationRef, (nuint)i);
            }
        }

        return i;
    }
#endif

    private static void ValidateBinary<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right, Span<T> destination)
    {
        if (left.Length != right.Length || destination.Length != left.Length)
            ThrowLengthMismatch();
        ValidateOverlap(left, destination);
        ValidateOverlap(right, destination);
    }

    private static void ValidateUnary<T>(ReadOnlySpan<T> source, Span<T> destination)
    {
        if (source.Length != destination.Length)
            ThrowLengthMismatch();
        ValidateOverlap(source, destination);
    }

    private static void ValidateDot<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right, Span<float> destination)
        where T : struct
    {
        if (left.Length != right.Length || destination.Length != left.Length)
            ThrowLengthMismatch();
        RejectAnyByteOverlap(left, destination);
        RejectAnyByteOverlap(right, destination);
    }

    private static void ValidateOverlap<T>(ReadOnlySpan<T> source, Span<T> destination)
    {
        if (source.Overlaps(destination, out int elementOffset) &&
            (elementOffset != 0 || source.Length != destination.Length))
        {
            ThrowPartialOverlap();
        }
    }

    private static void RejectAnyByteOverlap<TSource, TDestination>(ReadOnlySpan<TSource> source, Span<TDestination> destination)
        where TSource : struct
        where TDestination : struct
    {
        if (MemoryMarshal.AsBytes(source).Overlaps(MemoryMarshal.AsBytes(destination)))
            ThrowPartialOverlap();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateBoneIndex(uint index, int boneCount)
    {
        if (index >= (uint)boneCount)
            ThrowBoneIndex(index, boneCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLengthMismatch()
        => throw new ArgumentException("Source and destination spans must have matching lengths.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPartialOverlap()
        => throw new ArgumentException("Spans may be identical or disjoint, but may not partially overlap.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEmptyPoints()
        => throw new ArgumentException("At least one point is required.", "points");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBoneIndex(uint index, int boneCount)
        => throw new ArgumentOutOfRangeException("boneIndices", index, $"Bone index must be less than {boneCount}.");
}
