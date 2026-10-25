// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

namespace XEngine.Vector;

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct Float2x3 : IEquatable<Float2x3>
{
    public Float2 c0;
    public Float2 c1;
    public Float2 c2;

    public Float2x3(Float2 col0, Float2 col1, Float2 col2) { c0 = col0; c1 = col1; c2 = col2; }
    public Float2x3(float m00, float m01, float m02, float m10, float m11, float m12)
        : this(new Float2(m00, m10), new Float2(m01, m11), new Float2(m02, m12)) { }
    public float this[int row, int column]
    {
        readonly get => column switch { 0 => c0[row], 1 => c1[row], 2 => c2[row], _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        set { if (column == 0) c0[row] = value; else if (column == 1) c1[row] = value; else if (column == 2) c2[row] = value; else throw new ArgumentOutOfRangeException(nameof(column)); }
    }
    public static Float2 operator *(Float2x3 matrix, Float3 vector)
        => matrix.c0 * vector.X + matrix.c1 * vector.Y + matrix.c2 * vector.Z;
    public readonly Float3x2 Transpose() => new(
        new Float3(c0.X, c1.X, c2.X),
        new Float3(c0.Y, c1.Y, c2.Y));
    public readonly bool Equals(Float2x3 other) => c0 == other.c0 && c1 == other.c1 && c2 == other.c2;
    public override readonly bool Equals(object? obj) => obj is Float2x3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(c0, c1, c2);
    public static bool operator ==(Float2x3 left, Float2x3 right) => left.Equals(right);
    public static bool operator !=(Float2x3 left, Float2x3 right) => !left.Equals(right);
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct Float3x2 : IEquatable<Float3x2>
{
    public Float3 c0;
    public Float3 c1;

    public Float3x2(Float3 col0, Float3 col1) { c0 = col0; c1 = col1; }
    public Float3x2(float m00, float m01, float m10, float m11, float m20, float m21)
        : this(new Float3(m00, m10, m20), new Float3(m01, m11, m21)) { }
    public float this[int row, int column]
    {
        readonly get => column switch { 0 => c0[row], 1 => c1[row], _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        set { if (column == 0) c0[row] = value; else if (column == 1) c1[row] = value; else throw new ArgumentOutOfRangeException(nameof(column)); }
    }
    public static Float3 operator *(Float3x2 matrix, Float2 vector) => matrix.c0 * vector.X + matrix.c1 * vector.Y;
    public readonly Float2x3 Transpose() => new(
        new Float2(c0.X, c1.X),
        new Float2(c0.Y, c1.Y),
        new Float2(c0.Z, c1.Z));
    public readonly bool Equals(Float3x2 other) => c0 == other.c0 && c1 == other.c1;
    public override readonly bool Equals(object? obj) => obj is Float3x2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(c0, c1);
    public static bool operator ==(Float3x2 left, Float3x2 right) => left.Equals(right);
    public static bool operator !=(Float3x2 left, Float3x2 right) => !left.Equals(right);
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct Float2x4 : IEquatable<Float2x4>
{
    public Float2 c0;
    public Float2 c1;
    public Float2 c2;
    public Float2 c3;

    public Float2x4(Float2 col0, Float2 col1, Float2 col2, Float2 col3) { c0 = col0; c1 = col1; c2 = col2; c3 = col3; }
    public Float2x4(float m00, float m01, float m02, float m03, float m10, float m11, float m12, float m13)
        : this(new Float2(m00, m10), new Float2(m01, m11), new Float2(m02, m12), new Float2(m03, m13)) { }
    public float this[int row, int column]
    {
        readonly get => column switch { 0 => c0[row], 1 => c1[row], 2 => c2[row], 3 => c3[row], _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        set { if (column == 0) c0[row] = value; else if (column == 1) c1[row] = value; else if (column == 2) c2[row] = value; else if (column == 3) c3[row] = value; else throw new ArgumentOutOfRangeException(nameof(column)); }
    }
    public static Float2 operator *(Float2x4 matrix, Float4 vector)
        => matrix.c0 * vector.X + matrix.c1 * vector.Y + matrix.c2 * vector.Z + matrix.c3 * vector.W;
    public readonly Float4x2 Transpose() => new(
        new Float4(c0.X, c1.X, c2.X, c3.X),
        new Float4(c0.Y, c1.Y, c2.Y, c3.Y));
    public readonly bool Equals(Float2x4 other) => c0 == other.c0 && c1 == other.c1 && c2 == other.c2 && c3 == other.c3;
    public override readonly bool Equals(object? obj) => obj is Float2x4 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(c0, c1, c2, c3);
    public static bool operator ==(Float2x4 left, Float2x4 right) => left.Equals(right);
    public static bool operator !=(Float2x4 left, Float2x4 right) => !left.Equals(right);
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct Float4x2 : IEquatable<Float4x2>
{
    public Float4 c0;
    public Float4 c1;

    public Float4x2(Float4 col0, Float4 col1) { c0 = col0; c1 = col1; }
    public Float4x2(float m00, float m01, float m10, float m11, float m20, float m21, float m30, float m31)
        : this(new Float4(m00, m10, m20, m30), new Float4(m01, m11, m21, m31)) { }
    public float this[int row, int column]
    {
        readonly get => column switch { 0 => c0[row], 1 => c1[row], _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        set { if (column == 0) c0[row] = value; else if (column == 1) c1[row] = value; else throw new ArgumentOutOfRangeException(nameof(column)); }
    }
    public static Float4 operator *(Float4x2 matrix, Float2 vector) => matrix.c0 * vector.X + matrix.c1 * vector.Y;
    public readonly Float2x4 Transpose() => new(
        new Float2(c0.X, c1.X),
        new Float2(c0.Y, c1.Y),
        new Float2(c0.Z, c1.Z),
        new Float2(c0.W, c1.W));
    public readonly bool Equals(Float4x2 other) => c0 == other.c0 && c1 == other.c1;
    public override readonly bool Equals(object? obj) => obj is Float4x2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(c0, c1);
    public static bool operator ==(Float4x2 left, Float4x2 right) => left.Equals(right);
    public static bool operator !=(Float4x2 left, Float4x2 right) => !left.Equals(right);
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct Float3x4 : IEquatable<Float3x4>
{
    public Float3 c0;
    public Float3 c1;
    public Float3 c2;
    public Float3 c3;

    public Float3x4(Float3 col0, Float3 col1, Float3 col2, Float3 col3) { c0 = col0; c1 = col1; c2 = col2; c3 = col3; }
    public Float3x4(
        float m00, float m01, float m02, float m03,
        float m10, float m11, float m12, float m13,
        float m20, float m21, float m22, float m23)
        : this(new Float3(m00, m10, m20), new Float3(m01, m11, m21), new Float3(m02, m12, m22), new Float3(m03, m13, m23)) { }
    public float this[int row, int column]
    {
        readonly get => column switch { 0 => c0[row], 1 => c1[row], 2 => c2[row], 3 => c3[row], _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        set { if (column == 0) c0[row] = value; else if (column == 1) c1[row] = value; else if (column == 2) c2[row] = value; else if (column == 3) c3[row] = value; else throw new ArgumentOutOfRangeException(nameof(column)); }
    }
    public static Float3 operator *(Float3x4 matrix, Float4 vector)
        => matrix.c0 * vector.X + matrix.c1 * vector.Y + matrix.c2 * vector.Z + matrix.c3 * vector.W;
    public readonly Float4x3 Transpose() => new(
        new Float4(c0.X, c1.X, c2.X, c3.X),
        new Float4(c0.Y, c1.Y, c2.Y, c3.Y),
        new Float4(c0.Z, c1.Z, c2.Z, c3.Z));
    public readonly bool Equals(Float3x4 other) => c0 == other.c0 && c1 == other.c1 && c2 == other.c2 && c3 == other.c3;
    public override readonly bool Equals(object? obj) => obj is Float3x4 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(c0, c1, c2, c3);
    public static bool operator ==(Float3x4 left, Float3x4 right) => left.Equals(right);
    public static bool operator !=(Float3x4 left, Float3x4 right) => !left.Equals(right);
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct Float4x3 : IEquatable<Float4x3>
{
    public Float4 c0;
    public Float4 c1;
    public Float4 c2;

    public Float4x3(Float4 col0, Float4 col1, Float4 col2) { c0 = col0; c1 = col1; c2 = col2; }
    public Float4x3(
        float m00, float m01, float m02,
        float m10, float m11, float m12,
        float m20, float m21, float m22,
        float m30, float m31, float m32)
        : this(new Float4(m00, m10, m20, m30), new Float4(m01, m11, m21, m31), new Float4(m02, m12, m22, m32)) { }
    public float this[int row, int column]
    {
        readonly get => column switch { 0 => c0[row], 1 => c1[row], 2 => c2[row], _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        set { if (column == 0) c0[row] = value; else if (column == 1) c1[row] = value; else if (column == 2) c2[row] = value; else throw new ArgumentOutOfRangeException(nameof(column)); }
    }
    public static Float4 operator *(Float4x3 matrix, Float3 vector)
        => matrix.c0 * vector.X + matrix.c1 * vector.Y + matrix.c2 * vector.Z;
    public readonly Float3x4 Transpose() => new(
        new Float3(c0.X, c1.X, c2.X),
        new Float3(c0.Y, c1.Y, c2.Y),
        new Float3(c0.Z, c1.Z, c2.Z),
        new Float3(c0.W, c1.W, c2.W));
    public readonly bool Equals(Float4x3 other) => c0 == other.c0 && c1 == other.c1 && c2 == other.c2;
    public override readonly bool Equals(object? obj) => obj is Float4x3 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(c0, c1, c2);
    public static bool operator ==(Float4x3 left, Float4x3 right) => left.Equals(right);
    public static bool operator !=(Float4x3 left, Float4x3 right) => !left.Equals(right);
}
