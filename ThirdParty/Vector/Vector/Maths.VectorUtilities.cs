// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Vector;

public static partial class Maths
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BitCastToUInt(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float BitCastToFloat(uint value) => BitConverter.Int32BitsToSingle(unchecked((int)value));

    public static UInt2 BitCastToUInt(Float2 value) => new(BitCastToUInt(value.X), BitCastToUInt(value.Y));
    public static UInt3 BitCastToUInt(Float3 value) => new(BitCastToUInt(value.X), BitCastToUInt(value.Y), BitCastToUInt(value.Z));
    public static UInt4 BitCastToUInt(Float4 value) => new(BitCastToUInt(value.X), BitCastToUInt(value.Y), BitCastToUInt(value.Z), BitCastToUInt(value.W));
    public static Float2 BitCastToFloat(UInt2 value) => new(BitCastToFloat(value.X), BitCastToFloat(value.Y));
    public static Float3 BitCastToFloat(UInt3 value) => new(BitCastToFloat(value.X), BitCastToFloat(value.Y), BitCastToFloat(value.Z));
    public static Float4 BitCastToFloat(UInt4 value) => new(BitCastToFloat(value.X), BitCastToFloat(value.Y), BitCastToFloat(value.Z), BitCastToFloat(value.W));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool All(Bool2 value) => value.All;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool All(Bool3 value) => value.All;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool All(Bool4 value) => value.All;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Any(Bool2 value) => value.Any;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Any(Bool3 value) => value.Any;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Any(Bool4 value) => value.Any;

    public static Float2 Select(Float2 whenFalse, Float2 whenTrue, Bool2 mask)
        => new(mask.X ? whenTrue.X : whenFalse.X, mask.Y ? whenTrue.Y : whenFalse.Y);

    public static Float3 Select(Float3 whenFalse, Float3 whenTrue, Bool3 mask)
        => new(
            mask.X ? whenTrue.X : whenFalse.X,
            mask.Y ? whenTrue.Y : whenFalse.Y,
            mask.Z ? whenTrue.Z : whenFalse.Z);

    public static Float4 Select(Float4 whenFalse, Float4 whenTrue, Bool4 mask)
        => new(
            mask.X ? whenTrue.X : whenFalse.X,
            mask.Y ? whenTrue.Y : whenFalse.Y,
            mask.Z ? whenTrue.Z : whenFalse.Z,
            mask.W ? whenTrue.W : whenFalse.W);

    public static UInt2 Select(UInt2 whenFalse, UInt2 whenTrue, Bool2 mask)
        => new(mask.X ? whenTrue.X : whenFalse.X, mask.Y ? whenTrue.Y : whenFalse.Y);

    public static UInt3 Select(UInt3 whenFalse, UInt3 whenTrue, Bool3 mask)
        => new(
            mask.X ? whenTrue.X : whenFalse.X,
            mask.Y ? whenTrue.Y : whenFalse.Y,
            mask.Z ? whenTrue.Z : whenFalse.Z);

    public static UInt4 Select(UInt4 whenFalse, UInt4 whenTrue, Bool4 mask)
        => new(
            mask.X ? whenTrue.X : whenFalse.X,
            mask.Y ? whenTrue.Y : whenFalse.Y,
            mask.Z ? whenTrue.Z : whenFalse.Z,
            mask.W ? whenTrue.W : whenFalse.W);

    /// <summary>Selects four components from the concatenated component stream (left, right).</summary>
    public static Float4 Shuffle(Float4 left, Float4 right, int x, int y, int z, int w)
        => new(
            SelectShuffleComponent(left, right, x),
            SelectShuffleComponent(left, right, y),
            SelectShuffleComponent(left, right, z),
            SelectShuffleComponent(left, right, w));

    public static uint Hash(Float2 value)
    {
        uint hash = 2166136261u;
        hash = (hash ^ BitCastToUInt(value.X)) * 16777619u;
        return (hash ^ BitCastToUInt(value.Y)) * 16777619u;
    }

    public static uint Hash(Float3 value)
    {
        uint hash = Hash(new Float2(value.X, value.Y));
        return (hash ^ BitCastToUInt(value.Z)) * 16777619u;
    }

    public static uint Hash(Float4 value)
    {
        uint hash = Hash(new Float3(value.X, value.Y, value.Z));
        return (hash ^ BitCastToUInt(value.W)) * 16777619u;
    }

    public static uint Hash(UInt2 value)
    {
        uint hash = (2166136261u ^ value.X) * 16777619u;
        return (hash ^ value.Y) * 16777619u;
    }

    public static uint Hash(UInt3 value)
    {
        uint hash = Hash(new UInt2(value.X, value.Y));
        return (hash ^ value.Z) * 16777619u;
    }

    public static uint Hash(UInt4 value)
    {
        uint hash = Hash(new UInt3(value.X, value.Y, value.Z));
        return (hash ^ value.W) * 16777619u;
    }

    private static float SelectShuffleComponent(Float4 left, Float4 right, int index)
        => index switch
        {
            0 => left.X,
            1 => left.Y,
            2 => left.Z,
            3 => left.W,
            4 => right.X,
            5 => right.Y,
            6 => right.Z,
            7 => right.W,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Shuffle index must be in the range 0 through 7."),
        };
}
