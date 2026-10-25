// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI;

/// <summary>Kind of resource bound at a shader binding slot.</summary>
public enum ShaderBindingKind
{
    Texture,
    Buffer,
    Sampler,
    StorageTexture,
    StructuredBuffer,
    StorageBuffer,
}

[Flags]
public enum ShaderStageMask : uint
{
    None = 0,
    Vertex = 1 << 0,
    Fragment = 1 << 1,
    Geometry = 1 << 2,
    Compute = 1 << 3,
    Hull = 1 << 4,
    Domain = 1 << 5,
    AllGraphics = Vertex | Fragment | Geometry | Hull | Domain,
}

public enum ShaderResourceShape
{
    Unknown,
    Texture1D,
    Texture1DArray,
    Texture2D,
    Texture2DArray,
    Texture2DMultisample,
    Texture2DMultisampleArray,
    Texture3D,
    TextureCube,
    TextureCubeArray,
    TextureBuffer,
    StructuredBuffer,
    ByteAddressBuffer,
}

public enum ShaderScalarType
{
    Unknown,
    Bool,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float16,
    Float32,
    Float64,
    IntPtr,
    UIntPtr,
}

public readonly record struct ShaderUniformField(
    string Name,
    int Offset,
    int Size,
    ShaderScalarType ScalarType,
    byte Rows,
    byte Columns,
    int ElementCount,
    int ArrayStride,
    int MatrixStride);

public readonly record struct ShaderPhysicalBinding(
    int Binding,
    int Space,
    ShaderStageMask Stages);

/// <summary>
/// One named binding slot. Names match the current string-keyed uniform / texture /
/// buffer lookups so OpenGL reflection and modern descriptor sets stay compatible.
/// </summary>
public readonly struct ShaderBindingSlot
{
    public const int UnspecifiedPhysicalLocation = -1;

    public ShaderBindingKind Kind { get; }
    public int Slot { get; }
    public string Name { get; }
    public int Space { get; }
    public int Count { get; }
    public ShaderStageMask Stages { get; }
    public ShaderResourceShape ResourceShape { get; }
    public int ByteSize { get; }
    public ShaderUniformField[] UniformFields { get; }
    public ShaderPhysicalBinding[] PhysicalBindings { get; }
    public int PhysicalBinding => HasPhysicalLocation ? PhysicalBindings[0].Binding : UnspecifiedPhysicalLocation;
    public int PhysicalSpace => HasPhysicalLocation ? PhysicalBindings[0].Space : UnspecifiedPhysicalLocation;
    public bool HasPhysicalLocation => PhysicalBindings.Length != 0;

    public ShaderBindingSlot(
        ShaderBindingKind kind,
        int slot,
        string name,
        int space = 0,
        int count = 1,
        ShaderStageMask stages = ShaderStageMask.AllGraphics,
        ShaderResourceShape resourceShape = ShaderResourceShape.Unknown,
        int byteSize = 0,
        ShaderUniformField[]? uniformFields = null,
        int physicalBinding = UnspecifiedPhysicalLocation,
        int physicalSpace = UnspecifiedPhysicalLocation,
        ShaderPhysicalBinding[]? physicalBindings = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfNegative(space);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(byteSize);
        if ((physicalBinding == UnspecifiedPhysicalLocation) != (physicalSpace == UnspecifiedPhysicalLocation))
            throw new ArgumentException("Physical binding and space must either both be specified or both be omitted.");
        if (physicalBinding < UnspecifiedPhysicalLocation)
            throw new ArgumentOutOfRangeException(nameof(physicalBinding));
        if (physicalSpace < UnspecifiedPhysicalLocation)
            throw new ArgumentOutOfRangeException(nameof(physicalSpace));
        if (physicalBindings is { Length: > 0 } && physicalBinding != UnspecifiedPhysicalLocation)
            throw new ArgumentException("Physical bindings must be supplied either as one location or as an array, not both.");

        Kind = kind;
        Slot = slot;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Space = space;
        Count = count;
        Stages = stages;
        ResourceShape = resourceShape;
        ByteSize = byteSize;
        UniformFields = uniformFields ?? Array.Empty<ShaderUniformField>();
        PhysicalBindings = physicalBindings ?? (physicalBinding == UnspecifiedPhysicalLocation
            ? []
            : [new ShaderPhysicalBinding(physicalBinding, physicalSpace, stages)]);
        ValidatePhysicalBindings(PhysicalBindings);
    }

    internal ShaderBindingSlot WithStages(ShaderStageMask stages) => new(
        Kind,
        Slot,
        Name,
        Space,
        Count,
        stages,
        ResourceShape,
        ByteSize,
        UniformFields,
        physicalBindings: PhysicalBindings);

    internal ShaderBindingSlot WithPhysicalLocation(int binding, int space) => new(
        Kind,
        Slot,
        Name,
        Space,
        Count,
        Stages,
        ResourceShape,
        ByteSize,
        UniformFields,
        binding,
        space);

    internal ShaderBindingSlot WithPhysicalBindings(
        ShaderPhysicalBinding[] physicalBindings,
        int? byteSize = null) => new(
        Kind,
        Slot,
        Name,
        Space,
        Count,
        Stages,
        ResourceShape,
        byteSize ?? ByteSize,
        UniformFields,
        physicalBindings: physicalBindings);

    internal ShaderBindingSlot MergeStagesAndPhysicalBindings(ShaderBindingSlot other)
    {
        var merged = new ShaderPhysicalBinding[PhysicalBindings.Length + other.PhysicalBindings.Length];
        int count = 0;
        AddPhysicalBindings(PhysicalBindings, merged, ref count);
        AddPhysicalBindings(other.PhysicalBindings, merged, ref count);
        if (count != merged.Length)
            Array.Resize(ref merged, count);

        return new ShaderBindingSlot(
            Kind,
            Slot,
            Name,
            Space,
            Count,
            Stages | other.Stages,
            ResourceShape,
            ByteSize,
            UniformFields,
            physicalBindings: merged);
    }

    private static void AddPhysicalBindings(
        ShaderPhysicalBinding[] source,
        ShaderPhysicalBinding[] destination,
        ref int count)
    {
        for (int sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
        {
            ShaderPhysicalBinding candidate = source[sourceIndex];
            int existingIndex = -1;
            for (int index = 0; index < count; index++)
            {
                if (destination[index].Binding == candidate.Binding
                    && destination[index].Space == candidate.Space)
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                ShaderPhysicalBinding existing = destination[existingIndex];
                destination[existingIndex] = existing with { Stages = existing.Stages | candidate.Stages };
            }
            else
            {
                destination[count++] = candidate;
            }
        }
    }

    private static void ValidatePhysicalBindings(ShaderPhysicalBinding[] bindings)
    {
        for (int i = 0; i < bindings.Length; i++)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bindings[i].Binding);
            ArgumentOutOfRangeException.ThrowIfNegative(bindings[i].Space);
            if (bindings[i].Stages == ShaderStageMask.None)
                throw new ArgumentException("Physical shader bindings must name at least one stage.", nameof(bindings));
        }
    }
}

/// <summary>
/// Declares texture, buffer, and sampler binding slots for a shader / pipeline.
/// Names are retained for reflection compatibility with the existing name-based
/// uniform system (<c>PropertyState</c> / <c>GraphicsProgram.uniformLocations</c>).
/// </summary>
public sealed class ShaderBindingLayout
{
    public ShaderBindingSlot[] Textures { get; init; } = Array.Empty<ShaderBindingSlot>();
    public ShaderBindingSlot[] Buffers { get; init; } = Array.Empty<ShaderBindingSlot>();
    public ShaderBindingSlot[] Samplers { get; init; } = Array.Empty<ShaderBindingSlot>();

    /// <summary>Find a binding by name across all kinds, or null if missing.</summary>
    public ShaderBindingSlot? FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        for (int i = 0; i < Textures.Length; i++)
        {
            if (Textures[i].Name == name)
                return Textures[i];
        }

        for (int i = 0; i < Buffers.Length; i++)
        {
            if (Buffers[i].Name == name)
                return Buffers[i];
        }

        for (int i = 0; i < Samplers.Length; i++)
        {
            if (Samplers[i].Name == name)
                return Samplers[i];
        }

        return null;
    }
}
