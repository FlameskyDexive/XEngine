using System;

namespace XEngine.VoxelWorld;

/// <summary>One cell inside a 16³ chunk. Type 0 is empty air.</summary>
public struct Voxel : IEquatable<Voxel>
{
    public const ushort EmptyTypeIndex = 0;
    public const byte FullOpaque = 15;

    public ushort TypeIndex;
    public byte Opaque;

    public bool IsEmpty => TypeIndex == EmptyTypeIndex;

    public static Voxel Empty => default;

    public static Voxel Solid(ushort typeIndex, byte opaque = FullOpaque) =>
        new() { TypeIndex = typeIndex, Opaque = opaque };

    public bool Equals(Voxel other) => TypeIndex == other.TypeIndex && Opaque == other.Opaque;
    public override bool Equals(object? obj) => obj is Voxel other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(TypeIndex, Opaque);
}
