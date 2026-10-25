// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XEngine.Vector;

[Serializable, StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct Half2 : IEquatable<Half2>
{
    public ushort XBits;
    public ushort YBits;
    public float X { readonly get => HalfBits.ToSingle(XBits); set => XBits = HalfBits.FromSingle(value); }
    public float Y { readonly get => HalfBits.ToSingle(YBits); set => YBits = HalfBits.FromSingle(value); }
    public Half2(float value) : this(value, value) { }
    public Half2(float x, float y) { XBits = HalfBits.FromSingle(x); YBits = HalfBits.FromSingle(y); }
    public Half2(Float2 value) : this(value.X, value.Y) { }
    public readonly Float2 ToFloat2() => new(X, Y);
    public readonly bool Equals(Half2 other) => XBits == other.XBits && YBits == other.YBits;
    public override readonly bool Equals(object? obj) => obj is Half2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(XBits, YBits);
    public static bool operator ==(Half2 a, Half2 b) => a.Equals(b);
    public static bool operator !=(Half2 a, Half2 b) => !a.Equals(b);
}

[Serializable, StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct Half3 : IEquatable<Half3>
{
    public ushort XBits;
    public ushort YBits;
    public ushort ZBits;
    public float X { readonly get => HalfBits.ToSingle(XBits); set => XBits = HalfBits.FromSingle(value); }
    public float Y { readonly get => HalfBits.ToSingle(YBits); set => YBits = HalfBits.FromSingle(value); }
    public float Z { readonly get => HalfBits.ToSingle(ZBits); set => ZBits = HalfBits.FromSingle(value); }
    public Half3(float value) : this(value, value, value) { }
    public Half3(float x, float y, float z) { XBits = HalfBits.FromSingle(x); YBits = HalfBits.FromSingle(y); ZBits = HalfBits.FromSingle(z); }
    public Half3(Float3 value) : this(value.X, value.Y, value.Z) { }
    public readonly Float3 ToFloat3() => new(X, Y, Z);
    public readonly bool Equals(Half3 other) => XBits == other.XBits && YBits == other.YBits && ZBits == other.ZBits;
    public override readonly bool Equals(object? obj) => obj is Half3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(XBits, YBits, ZBits);
    public static bool operator ==(Half3 a, Half3 b) => a.Equals(b);
    public static bool operator !=(Half3 a, Half3 b) => !a.Equals(b);
}

[Serializable, StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct Half4 : IEquatable<Half4>
{
    public ushort XBits;
    public ushort YBits;
    public ushort ZBits;
    public ushort WBits;
    public float X { readonly get => HalfBits.ToSingle(XBits); set => XBits = HalfBits.FromSingle(value); }
    public float Y { readonly get => HalfBits.ToSingle(YBits); set => YBits = HalfBits.FromSingle(value); }
    public float Z { readonly get => HalfBits.ToSingle(ZBits); set => ZBits = HalfBits.FromSingle(value); }
    public float W { readonly get => HalfBits.ToSingle(WBits); set => WBits = HalfBits.FromSingle(value); }
    public Half4(float value) : this(value, value, value, value) { }
    public Half4(float x, float y, float z, float w)
    {
        XBits = HalfBits.FromSingle(x); YBits = HalfBits.FromSingle(y);
        ZBits = HalfBits.FromSingle(z); WBits = HalfBits.FromSingle(w);
    }
    public Half4(Float4 value) : this(value.X, value.Y, value.Z, value.W) { }
    public readonly Float4 ToFloat4() => new(X, Y, Z, W);
    public readonly bool Equals(Half4 other)
        => XBits == other.XBits && YBits == other.YBits && ZBits == other.ZBits && WBits == other.WBits;
    public override readonly bool Equals(object? obj) => obj is Half4 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(XBits, YBits, ZBits, WBits);
    public static bool operator ==(Half4 a, Half4 b) => a.Equals(b);
    public static bool operator !=(Half4 a, Half4 b) => !a.Equals(b);
}

internal static class HalfBits
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort FromSingle(float value)
    {
        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
        uint sign = (bits >> 16) & 0x8000u;
        uint exponent = (bits >> 23) & 0xffu;
        uint mantissa = bits & 0x7fffffu;

        if (exponent == 0xffu)
            return (ushort)(sign | (mantissa == 0 ? 0x7c00u : 0x7e00u));

        int halfExponent = (int)exponent - 127 + 15;
        if (halfExponent >= 31)
            return (ushort)(sign | 0x7c00u);
        if (halfExponent <= 0)
        {
            if (halfExponent < -10)
                return (ushort)sign;
            mantissa = (mantissa | 0x800000u) >> (1 - halfExponent);
            uint rounded = (mantissa + 0x0fffu + ((mantissa >> 13) & 1u)) >> 13;
            return (ushort)(sign | rounded);
        }

        uint halfMantissa = (mantissa + 0x0fffu + ((mantissa >> 13) & 1u)) >> 13;
        if (halfMantissa == 0x400u)
        {
            halfMantissa = 0;
            halfExponent++;
            if (halfExponent >= 31)
                return (ushort)(sign | 0x7c00u);
        }
        return (ushort)(sign | ((uint)halfExponent << 10) | halfMantissa);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToSingle(ushort value)
    {
        uint sign = ((uint)value & 0x8000u) << 16;
        uint exponent = ((uint)value >> 10) & 0x1fu;
        uint mantissa = (uint)value & 0x3ffu;
        uint bits;
        if (exponent == 0)
        {
            if (mantissa == 0)
                bits = sign;
            else
            {
                int e = -14;
                while ((mantissa & 0x400u) == 0) { mantissa <<= 1; e--; }
                mantissa &= 0x3ffu;
                bits = sign | ((uint)(e + 127) << 23) | (mantissa << 13);
            }
        }
        else if (exponent == 31)
        {
            bits = sign | 0x7f800000u | (mantissa << 13);
        }
        else
        {
            bits = sign | ((exponent + 112u) << 23) | (mantissa << 13);
        }
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }
}
