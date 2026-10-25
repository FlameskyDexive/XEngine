// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.Metal;

/// <summary>
/// M3 explicit Metal bind-points. v1 uses <c>setVertexBuffer</c> / <c>setFragmentBuffer</c> /
/// <c>setFragmentTexture</c> / <c>setFragmentSamplerState</c> with per-kind indices.
/// Argument buffers are a later optimization; this mapping never folds resources into one.
/// </summary>
internal enum MetalBindKind
{
    Buffer,
    Texture,
    Sampler,
}

internal readonly struct MetalBindPoint : IEquatable<MetalBindPoint>
{
    public MetalBindKind Kind { get; }
    public int Index { get; }
    public ShaderStageMask Stages { get; }

    public MetalBindPoint(MetalBindKind kind, int index, ShaderStageMask stages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Kind = kind;
        Index = index;
        Stages = stages == ShaderStageMask.None
            ? ShaderStageMask.Vertex | ShaderStageMask.Fragment
            : stages;
    }

    public bool Vertex => (Stages & ShaderStageMask.Vertex) != 0;
    public bool Fragment => (Stages & ShaderStageMask.Fragment) != 0;

    public bool Equals(MetalBindPoint other) =>
        Kind == other.Kind && Index == other.Index && Stages == other.Stages;

    public override bool Equals(object? obj) => obj is MetalBindPoint other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Index, Stages);
}

internal static class MetalBindPoints
{
    public static MetalBindPoint ToBindPoint(ShaderDescriptorBinding binding)
    {
        ShaderBindingSlot slot = binding.Slot;
        int index = slot.HasPhysicalLocation ? binding.PhysicalBinding : slot.Slot;
        MetalBindKind kind = slot.Kind switch
        {
            ShaderBindingKind.Buffer or ShaderBindingKind.StructuredBuffer or ShaderBindingKind.StorageBuffer =>
                MetalBindKind.Buffer,
            ShaderBindingKind.Texture or ShaderBindingKind.StorageTexture => MetalBindKind.Texture,
            ShaderBindingKind.Sampler => MetalBindKind.Sampler,
            _ => throw new ArgumentOutOfRangeException(nameof(binding), slot.Kind, "Unsupported Metal bind kind."),
        };
        return new MetalBindPoint(kind, index, binding.Stages);
    }

    public static MetalBindPoint[] ToBindPoints(ShaderDescriptorLayoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var points = new MetalBindPoint[plan.Bindings.Length];
        for (int i = 0; i < plan.Bindings.Length; i++)
            points[i] = ToBindPoint(plan.Bindings[i]);
        return points;
    }

    public static MetalBindPoint Find(ShaderDescriptorLayoutPlan plan, string name)
    {
        ArgumentNullException.ThrowIfNull(plan);
        for (int i = 0; i < plan.Bindings.Length; i++)
        {
            if (plan.Bindings[i].Slot.Name == name)
                return ToBindPoint(plan.Bindings[i]);
        }

        throw new InvalidOperationException($"Plan has no binding named '{name}'.");
    }
}
