// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

namespace XEngine.Vector;

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct RigidTransform2D : IEquatable<RigidTransform2D>
{
    /// <summary>Normalized complex rotation stored as (cos, sin).</summary>
    public Float2 Rotation;
    public Float2 Translation;

    public static RigidTransform2D Identity => new(Float2.UnitX, Float2.Zero);
    public RigidTransform2D(float radians, Float2 translation)
        : this(new Float2(Maths.Cos(radians), Maths.Sin(radians)), translation) { }
    public RigidTransform2D(Float2 rotation, Float2 translation)
    {
        Rotation = Float2.NormalizeSafe(rotation, Float2.UnitX);
        Translation = translation;
    }
    public readonly Float2 TransformVector(Float2 value)
        => new(Rotation.X * value.X - Rotation.Y * value.Y, Rotation.Y * value.X + Rotation.X * value.Y);
    public readonly Float2 TransformPoint(Float2 value) => TransformVector(value) + Translation;
    public readonly RigidTransform2D Inverse()
    {
        Float2 inverseRotation = new(Rotation.X, -Rotation.Y);
        RigidTransform2D inverse = new(inverseRotation, Float2.Zero);
        inverse.Translation = -inverse.TransformVector(Translation);
        return inverse;
    }
    public readonly bool Equals(RigidTransform2D other) => Rotation == other.Rotation && Translation == other.Translation;
    public override readonly bool Equals(object? obj) => obj is RigidTransform2D other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Rotation, Translation);
    public static bool operator ==(RigidTransform2D left, RigidTransform2D right) => left.Equals(right);
    public static bool operator !=(RigidTransform2D left, RigidTransform2D right) => !left.Equals(right);
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct RigidTransform3D : IEquatable<RigidTransform3D>
{
    public Quaternion Rotation;
    public Float3 Translation;

    public static RigidTransform3D Identity => new(Quaternion.Identity, Float3.Zero);
    public RigidTransform3D(Quaternion rotation, Float3 translation)
    {
        Rotation = Quaternion.NormalizeSafe(rotation, Quaternion.Identity);
        Translation = translation;
    }
    public readonly Float3 TransformVector(Float3 value) => Rotation * value;
    public readonly Float3 TransformPoint(Float3 value) => Rotation * value + Translation;
    public readonly RigidTransform3D Inverse()
    {
        Quaternion inverseRotation = Quaternion.Inverse(Rotation);
        return new RigidTransform3D(inverseRotation, inverseRotation * -Translation);
    }
    public readonly Float4x4 ToMatrix() => new(Rotation, Translation);
    public readonly bool Equals(RigidTransform3D other) => Rotation == other.Rotation && Translation == other.Translation;
    public override readonly bool Equals(object? obj) => obj is RigidTransform3D other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Rotation, Translation);
    public static bool operator ==(RigidTransform3D left, RigidTransform3D right) => left.Equals(right);
    public static bool operator !=(RigidTransform3D left, RigidTransform3D right) => !left.Equals(right);
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct AffineTransform2D : IEquatable<AffineTransform2D>
{
    public Float2x2 Linear;
    public Float2 Translation;

    public static AffineTransform2D Identity => new(Float2x2.Identity, Float2.Zero);
    public AffineTransform2D(Float2x2 linear, Float2 translation) { Linear = linear; Translation = translation; }
    public AffineTransform2D(RigidTransform2D rigid)
        : this(new Float2x2(
            rigid.Rotation.X, -rigid.Rotation.Y,
            rigid.Rotation.Y, rigid.Rotation.X), rigid.Translation) { }
    public readonly Float2 TransformVector(Float2 value) => Linear * value;
    public readonly Float2 TransformPoint(Float2 value) => Linear * value + Translation;
    public readonly AffineTransform2D Inverse()
    {
        Float2x2 inverseLinear = Linear.Invert();
        return new AffineTransform2D(inverseLinear, inverseLinear * -Translation);
    }
    public readonly Float3x3 ToMatrix() => new(
        new Float3(Linear.c0, 0f),
        new Float3(Linear.c1, 0f),
        new Float3(Translation, 1f));
    public readonly bool Equals(AffineTransform2D other) => Linear == other.Linear && Translation == other.Translation;
    public override readonly bool Equals(object? obj) => obj is AffineTransform2D other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Linear, Translation);
    public static bool operator ==(AffineTransform2D left, AffineTransform2D right) => left.Equals(right);
    public static bool operator !=(AffineTransform2D left, AffineTransform2D right) => !left.Equals(right);
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct AffineTransform3D : IEquatable<AffineTransform3D>
{
    public Float3x3 Linear;
    public Float3 Translation;

    public static AffineTransform3D Identity => new(Float3x3.Identity, Float3.Zero);
    public AffineTransform3D(Float3x3 linear, Float3 translation) { Linear = linear; Translation = translation; }
    public AffineTransform3D(RigidTransform3D rigid) : this(Float3x3.FromQuaternion(rigid.Rotation), rigid.Translation) { }
    public readonly Float3 TransformVector(Float3 value) => Linear * value;
    public readonly Float3 TransformPoint(Float3 value) => Linear * value + Translation;
    public readonly AffineTransform3D Inverse()
    {
        Float3x3 inverseLinear = Linear.Invert();
        return new AffineTransform3D(inverseLinear, inverseLinear * -Translation);
    }
    public readonly Float4x4 ToMatrix() => new(Linear, Translation);
    public readonly bool Equals(AffineTransform3D other) => Linear == other.Linear && Translation == other.Translation;
    public override readonly bool Equals(object? obj) => obj is AffineTransform3D other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Linear, Translation);
    public static bool operator ==(AffineTransform3D left, AffineTransform3D right) => left.Equals(right);
    public static bool operator !=(AffineTransform3D left, AffineTransform3D right) => !left.Equals(right);
}
