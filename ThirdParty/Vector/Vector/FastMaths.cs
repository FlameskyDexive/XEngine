// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Vector;

/// <summary>
/// Explicit non-strict math operations. Callers opt in to fused or estimate semantics; the regular
/// <see cref="Maths"/> and vector APIs retain their scalar behavior.
/// </summary>
public static class FastMaths
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float MultiplyAdd(float left, float right, float addend)
    {
#if NET8_0_OR_GREATER
        return MathF.FusedMultiplyAdd(left, right, addend);
#else
        return left * right + addend;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double MultiplyAdd(double left, double right, double addend)
    {
#if NET8_0_OR_GREATER
        return Math.FusedMultiplyAdd(left, right, addend);
#else
        return left * right + addend;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReciprocalEstimate(float value)
    {
#if NET8_0_OR_GREATER
        return MathF.ReciprocalEstimate(value);
#else
        return 1f / value;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReciprocalSqrtEstimate(float value)
    {
#if NET8_0_OR_GREATER
        return MathF.ReciprocalSqrtEstimate(value);
#else
        return 1f / (float)Math.Sqrt(value);
#endif
    }

    public static Float2 MultiplyAdd(Float2 left, Float2 right, Float2 addend)
        => new(MultiplyAdd(left.X, right.X, addend.X), MultiplyAdd(left.Y, right.Y, addend.Y));

    public static Float3 MultiplyAdd(Float3 left, Float3 right, Float3 addend)
        => new(
            MultiplyAdd(left.X, right.X, addend.X),
            MultiplyAdd(left.Y, right.Y, addend.Y),
            MultiplyAdd(left.Z, right.Z, addend.Z));

    public static Float4 MultiplyAdd(Float4 left, Float4 right, Float4 addend)
        => new(
            MultiplyAdd(left.X, right.X, addend.X),
            MultiplyAdd(left.Y, right.Y, addend.Y),
            MultiplyAdd(left.Z, right.Z, addend.Z),
            MultiplyAdd(left.W, right.W, addend.W));

    public static Float2 ReciprocalEstimate(Float2 value)
        => new(ReciprocalEstimate(value.X), ReciprocalEstimate(value.Y));

    public static Float3 ReciprocalEstimate(Float3 value)
        => new(ReciprocalEstimate(value.X), ReciprocalEstimate(value.Y), ReciprocalEstimate(value.Z));

    public static Float4 ReciprocalEstimate(Float4 value)
        => new(
            ReciprocalEstimate(value.X),
            ReciprocalEstimate(value.Y),
            ReciprocalEstimate(value.Z),
            ReciprocalEstimate(value.W));

    public static Float2 ReciprocalSqrtEstimate(Float2 value)
        => new(ReciprocalSqrtEstimate(value.X), ReciprocalSqrtEstimate(value.Y));

    public static Float3 ReciprocalSqrtEstimate(Float3 value)
        => new(
            ReciprocalSqrtEstimate(value.X),
            ReciprocalSqrtEstimate(value.Y),
            ReciprocalSqrtEstimate(value.Z));

    public static Float4 ReciprocalSqrtEstimate(Float4 value)
        => new(
            ReciprocalSqrtEstimate(value.X),
            ReciprocalSqrtEstimate(value.Y),
            ReciprocalSqrtEstimate(value.Z),
            ReciprocalSqrtEstimate(value.W));
}
