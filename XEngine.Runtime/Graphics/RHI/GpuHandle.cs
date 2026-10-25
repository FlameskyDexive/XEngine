// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics.CodeAnalysis;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Opaque backend-owned resource identity. Zero is always invalid.
/// Concrete backends map this to GL names, Vulkan handles, or D3D12 IDs.
/// </summary>
public readonly struct GpuHandle : IEquatable<GpuHandle>
{
    public static readonly GpuHandle Invalid = default;

    public ulong Value { get; }

    public GpuHandle(ulong value) => Value = value;

    public bool IsValid => Value != 0;

    public bool Equals(GpuHandle other) => Value == other.Value;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is GpuHandle other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => IsValid ? $"GpuHandle(0x{Value:X})" : "GpuHandle(Invalid)";

    public static bool operator ==(GpuHandle left, GpuHandle right) => left.Value == right.Value;
    public static bool operator !=(GpuHandle left, GpuHandle right) => left.Value != right.Value;

    public static explicit operator ulong(GpuHandle handle) => handle.Value;
    public static explicit operator GpuHandle(ulong value) => new(value);
}
