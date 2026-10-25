// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace XEngine.Vector;

[Serializable]
public struct UInt2 : IEquatable<UInt2>, IFormattable
{
    public static UInt2 Zero => new(0u);
    public static UInt2 One => new(1u);
    public static UInt2 UnitX => new(1u, 0u);
    public static UInt2 UnitY => new(0u, 1u);

    public uint X;
    public uint Y;

    public UInt2(uint value) : this(value, value) { }
    public UInt2(uint x, uint y) { X = x; Y = y; }

    public uint this[int index]
    {
        readonly get => index switch { 0 => X, 1 => Y, _ => throw new IndexOutOfRangeException() };
        set { if (index == 0) X = value; else if (index == 1) Y = value; else throw new IndexOutOfRangeException(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Dot(UInt2 a, UInt2 b) => a.X * b.X + a.Y * b.Y;
    public static UInt2 operator +(UInt2 a, UInt2 b) => new(a.X + b.X, a.Y + b.Y);
    public static UInt2 operator -(UInt2 a, UInt2 b) => new(a.X - b.X, a.Y - b.Y);
    public static UInt2 operator *(UInt2 a, UInt2 b) => new(a.X * b.X, a.Y * b.Y);
    public static UInt2 operator /(UInt2 a, UInt2 b) => new(a.X / b.X, a.Y / b.Y);
    public static UInt2 operator %(UInt2 a, UInt2 b) => new(a.X % b.X, a.Y % b.Y);
    public static UInt2 operator &(UInt2 a, UInt2 b) => new(a.X & b.X, a.Y & b.Y);
    public static UInt2 operator |(UInt2 a, UInt2 b) => new(a.X | b.X, a.Y | b.Y);
    public static UInt2 operator ^(UInt2 a, UInt2 b) => new(a.X ^ b.X, a.Y ^ b.Y);
    public static UInt2 operator ~(UInt2 value) => new(~value.X, ~value.Y);
    public static UInt2 operator <<(UInt2 value, int shift) => new(value.X << shift, value.Y << shift);
    public static UInt2 operator >>(UInt2 value, int shift) => new(value.X >> shift, value.Y >> shift);
    public static bool operator ==(UInt2 a, UInt2 b) => a.Equals(b);
    public static bool operator !=(UInt2 a, UInt2 b) => !a.Equals(b);
    public readonly bool Equals(UInt2 other) => X == other.X && Y == other.Y;
    public override readonly bool Equals(object? obj) => obj is UInt2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => ToString("G", CultureInfo.CurrentCulture);
    public readonly string ToString(string? format, IFormatProvider? provider)
        => $"({X.ToString(format, provider)}, {Y.ToString(format, provider)})";
}

[Serializable]
public struct UInt3 : IEquatable<UInt3>, IFormattable
{
    public static UInt3 Zero => new(0u);
    public static UInt3 One => new(1u);
    public static UInt3 UnitX => new(1u, 0u, 0u);
    public static UInt3 UnitY => new(0u, 1u, 0u);
    public static UInt3 UnitZ => new(0u, 0u, 1u);

    public uint X;
    public uint Y;
    public uint Z;

    public UInt3(uint value) : this(value, value, value) { }
    public UInt3(uint x, uint y, uint z) { X = x; Y = y; Z = z; }
    public UInt3(UInt2 xy, uint z) : this(xy.X, xy.Y, z) { }

    public uint this[int index]
    {
        readonly get => index switch { 0 => X, 1 => Y, 2 => Z, _ => throw new IndexOutOfRangeException() };
        set
        {
            switch (index) { case 0: X = value; break; case 1: Y = value; break; case 2: Z = value; break; default: throw new IndexOutOfRangeException(); }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Dot(UInt3 a, UInt3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static UInt3 operator +(UInt3 a, UInt3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static UInt3 operator -(UInt3 a, UInt3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static UInt3 operator *(UInt3 a, UInt3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static UInt3 operator /(UInt3 a, UInt3 b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
    public static UInt3 operator %(UInt3 a, UInt3 b) => new(a.X % b.X, a.Y % b.Y, a.Z % b.Z);
    public static UInt3 operator &(UInt3 a, UInt3 b) => new(a.X & b.X, a.Y & b.Y, a.Z & b.Z);
    public static UInt3 operator |(UInt3 a, UInt3 b) => new(a.X | b.X, a.Y | b.Y, a.Z | b.Z);
    public static UInt3 operator ^(UInt3 a, UInt3 b) => new(a.X ^ b.X, a.Y ^ b.Y, a.Z ^ b.Z);
    public static UInt3 operator ~(UInt3 value) => new(~value.X, ~value.Y, ~value.Z);
    public static UInt3 operator <<(UInt3 value, int shift) => new(value.X << shift, value.Y << shift, value.Z << shift);
    public static UInt3 operator >>(UInt3 value, int shift) => new(value.X >> shift, value.Y >> shift, value.Z >> shift);
    public static bool operator ==(UInt3 a, UInt3 b) => a.Equals(b);
    public static bool operator !=(UInt3 a, UInt3 b) => !a.Equals(b);
    public readonly bool Equals(UInt3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override readonly bool Equals(object? obj) => obj is UInt3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override readonly string ToString() => ToString("G", CultureInfo.CurrentCulture);
    public readonly string ToString(string? format, IFormatProvider? provider)
        => $"({X.ToString(format, provider)}, {Y.ToString(format, provider)}, {Z.ToString(format, provider)})";
}

[Serializable]
public struct UInt4 : IEquatable<UInt4>, IFormattable
{
    public static UInt4 Zero => new(0u);
    public static UInt4 One => new(1u);
    public static UInt4 UnitX => new(1u, 0u, 0u, 0u);
    public static UInt4 UnitY => new(0u, 1u, 0u, 0u);
    public static UInt4 UnitZ => new(0u, 0u, 1u, 0u);
    public static UInt4 UnitW => new(0u, 0u, 0u, 1u);

    public uint X;
    public uint Y;
    public uint Z;
    public uint W;

    public UInt4(uint value) : this(value, value, value, value) { }
    public UInt4(uint x, uint y, uint z, uint w) { X = x; Y = y; Z = z; W = w; }
    public UInt4(UInt3 xyz, uint w) : this(xyz.X, xyz.Y, xyz.Z, w) { }

    public uint this[int index]
    {
        readonly get => index switch { 0 => X, 1 => Y, 2 => Z, 3 => W, _ => throw new IndexOutOfRangeException() };
        set
        {
            switch (index) { case 0: X = value; break; case 1: Y = value; break; case 2: Z = value; break; case 3: W = value; break; default: throw new IndexOutOfRangeException(); }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Dot(UInt4 a, UInt4 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
    public static UInt4 operator +(UInt4 a, UInt4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
    public static UInt4 operator -(UInt4 a, UInt4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
    public static UInt4 operator *(UInt4 a, UInt4 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W);
    public static UInt4 operator /(UInt4 a, UInt4 b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z, a.W / b.W);
    public static UInt4 operator %(UInt4 a, UInt4 b) => new(a.X % b.X, a.Y % b.Y, a.Z % b.Z, a.W % b.W);
    public static UInt4 operator &(UInt4 a, UInt4 b) => new(a.X & b.X, a.Y & b.Y, a.Z & b.Z, a.W & b.W);
    public static UInt4 operator |(UInt4 a, UInt4 b) => new(a.X | b.X, a.Y | b.Y, a.Z | b.Z, a.W | b.W);
    public static UInt4 operator ^(UInt4 a, UInt4 b) => new(a.X ^ b.X, a.Y ^ b.Y, a.Z ^ b.Z, a.W ^ b.W);
    public static UInt4 operator ~(UInt4 value) => new(~value.X, ~value.Y, ~value.Z, ~value.W);
    public static UInt4 operator <<(UInt4 value, int shift) => new(value.X << shift, value.Y << shift, value.Z << shift, value.W << shift);
    public static UInt4 operator >>(UInt4 value, int shift) => new(value.X >> shift, value.Y >> shift, value.Z >> shift, value.W >> shift);
    public static bool operator ==(UInt4 a, UInt4 b) => a.Equals(b);
    public static bool operator !=(UInt4 a, UInt4 b) => !a.Equals(b);
    public readonly bool Equals(UInt4 other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;
    public override readonly bool Equals(object? obj) => obj is UInt4 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    public override readonly string ToString() => ToString("G", CultureInfo.CurrentCulture);
    public readonly string ToString(string? format, IFormatProvider? provider)
        => $"({X.ToString(format, provider)}, {Y.ToString(format, provider)}, {Z.ToString(format, provider)}, {W.ToString(format, provider)})";
}
