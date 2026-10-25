// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Converts HLSL's separate b/t/s register namespaces into one collision-free physical
/// binding namespace. Vulkan uses the flattened numbers as descriptor-set 0 bindings.
/// WebGPU uses the same plan as <c>@group</c> / <c>@binding</c> (group = physical space).
/// D3D12 keeps the original logical registers but uses the same deterministic entry
/// ordering when building root parameters.
/// </summary>
internal sealed class ShaderDescriptorLayoutPlan
{
    public int BufferBindingBase { get; }
    public int TextureBindingBase { get; }
    public int SamplerBindingBase { get; }
    public ShaderDescriptorBinding[] Bindings { get; }
    public ShaderBindGroupPlan[] BindGroups { get; }

    private ShaderDescriptorLayoutPlan(
        int bufferBindingBase,
        int textureBindingBase,
        int samplerBindingBase,
        ShaderDescriptorBinding[] bindings,
        ShaderBindGroupPlan[] bindGroups)
    {
        BufferBindingBase = bufferBindingBase;
        TextureBindingBase = textureBindingBase;
        SamplerBindingBase = samplerBindingBase;
        Bindings = bindings;
        BindGroups = bindGroups;
    }

    public static ShaderDescriptorLayoutPlan Create(ShaderBindingLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        bool hasExplicitPhysicalLocations = ValidatePhysicalLocations(layout);
        int bufferSpan = GetRegisterSpan(layout.Buffers);
        int textureSpan = GetRegisterSpan(layout.Textures);
        int bufferBase = 0;
        int textureBase = bufferSpan;
        int samplerBase = bufferSpan + textureSpan;
        int count = hasExplicitPhysicalLocations
            ? CountPhysicalBindings(layout.Buffers) + CountPhysicalBindings(layout.Textures) + CountPhysicalBindings(layout.Samplers)
            : layout.Buffers.Length + layout.Textures.Length + layout.Samplers.Length;
        var bindings = new ShaderDescriptorBinding[count];
        int index = 0;

        AddBindings(layout.Buffers, bufferBase, hasExplicitPhysicalLocations, bindings, ref index);
        AddBindings(layout.Textures, textureBase, hasExplicitPhysicalLocations, bindings, ref index);
        AddBindings(layout.Samplers, samplerBase, hasExplicitPhysicalLocations, bindings, ref index);

        return new ShaderDescriptorLayoutPlan(
            bufferBase,
            textureBase,
            samplerBase,
            bindings,
            BuildBindGroups(bindings));
    }

    public int GetPhysicalBinding(ShaderBindingSlot slot)
    {
        if (slot.HasPhysicalLocation)
        {
            if (slot.PhysicalBindings.Length != 1)
                throw new InvalidOperationException($"Shader resource '{slot.Name}' has more than one physical binding.");
            return slot.PhysicalBinding;
        }

        return slot.Kind switch
        {
            ShaderBindingKind.Buffer or ShaderBindingKind.StructuredBuffer or ShaderBindingKind.StorageBuffer =>
                BufferBindingBase + slot.Slot,
            ShaderBindingKind.Texture or ShaderBindingKind.StorageTexture => TextureBindingBase + slot.Slot,
            ShaderBindingKind.Sampler => SamplerBindingBase + slot.Slot,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
    }

    public int GetPhysicalGroup(ShaderBindingSlot slot)
    {
        if (slot.HasPhysicalLocation)
        {
            if (slot.PhysicalBindings.Length != 1)
                throw new InvalidOperationException($"Shader resource '{slot.Name}' has more than one physical binding.");
            return slot.PhysicalSpace;
        }

        return 0;
    }

    public void EnsureSingleBindGroup(int group)
    {
        for (int i = 0; i < Bindings.Length; i++)
        {
            ShaderDescriptorBinding binding = Bindings[i];
            if (binding.PhysicalGroup != group)
            {
                throw new NotSupportedException(
                    $"Vulkan descriptor set {binding.PhysicalGroup} for shader resource '{binding.Slot.Name}' is unsupported; " +
                    $"XEngine currently binds descriptor set {group} only.");
            }
        }
    }

    private static bool ValidatePhysicalLocations(ShaderBindingLayout layout)
    {
        int bindingCount = layout.Buffers.Length + layout.Textures.Length + layout.Samplers.Length;
        if (bindingCount == 0)
            return false;

        int explicitCount = CountExplicit(layout.Buffers)
            + CountExplicit(layout.Textures)
            + CountExplicit(layout.Samplers);
        if (explicitCount == 0)
            return false;
        if (explicitCount != bindingCount)
            throw new InvalidOperationException("Shader binding layouts must specify physical locations for every binding or none.");

        var occupiedBindings = new HashSet<(int Group, int Binding)>();
        ValidatePhysicalLocations(layout.Buffers, occupiedBindings);
        ValidatePhysicalLocations(layout.Textures, occupiedBindings);
        ValidatePhysicalLocations(layout.Samplers, occupiedBindings);
        return true;
    }

    private static int CountExplicit(ShaderBindingSlot[] slots)
    {
        int count = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].HasPhysicalLocation)
                count++;
        }
        return count;
    }

    private static int CountPhysicalBindings(ShaderBindingSlot[] slots)
    {
        int count = 0;
        for (int i = 0; i < slots.Length; i++)
            count += slots[i].PhysicalBindings.Length;
        return count;
    }

    private static void ValidatePhysicalLocations(
        ShaderBindingSlot[] slots,
        HashSet<(int Group, int Binding)> occupiedBindings)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            for (int physicalIndex = 0; physicalIndex < slot.PhysicalBindings.Length; physicalIndex++)
            {
                ShaderPhysicalBinding physical = slot.PhysicalBindings[physicalIndex];
                if (!occupiedBindings.Add((physical.Space, physical.Binding)))
                {
                    throw new InvalidOperationException(
                        $"Shader binding @group({physical.Space}) @binding({physical.Binding}) is assigned to more than one shader resource.");
                }
            }
        }
    }

    private static int GetRegisterSpan(ShaderBindingSlot[] slots)
    {
        int maxSlot = -1;
        for (int i = 0; i < slots.Length; i++)
            maxSlot = Math.Max(maxSlot, slots[i].Slot);
        return maxSlot + 1;
    }

    private static void AddBindings(
        ShaderBindingSlot[] slots,
        int bindingBase,
        bool hasExplicitPhysicalLocations,
        ShaderDescriptorBinding[] destination,
        ref int index)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            if (!hasExplicitPhysicalLocations)
            {
                destination[index++] = new ShaderDescriptorBinding(slot, bindingBase + slot.Slot, physicalGroup: 0, slot.Stages);
                continue;
            }

            for (int physicalIndex = 0; physicalIndex < slot.PhysicalBindings.Length; physicalIndex++)
            {
                ShaderPhysicalBinding physical = slot.PhysicalBindings[physicalIndex];
                destination[index++] = new ShaderDescriptorBinding(
                    slot,
                    physical.Binding,
                    physical.Space,
                    physical.Stages);
            }
        }
    }

    private static ShaderBindGroupPlan[] BuildBindGroups(ShaderDescriptorBinding[] bindings)
    {
        if (bindings.Length == 0)
            return [];

        var grouped = new Dictionary<int, List<ShaderDescriptorBinding>>();
        for (int i = 0; i < bindings.Length; i++)
        {
            ShaderDescriptorBinding binding = bindings[i];
            if (!grouped.TryGetValue(binding.PhysicalGroup, out List<ShaderDescriptorBinding>? list))
            {
                list = [];
                grouped[binding.PhysicalGroup] = list;
            }
            list.Add(binding);
        }

        int[] groups = new int[grouped.Count];
        grouped.Keys.CopyTo(groups, 0);
        Array.Sort(groups);

        var plans = new ShaderBindGroupPlan[groups.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            List<ShaderDescriptorBinding> list = grouped[groups[i]];
            list.Sort(static (left, right) => left.PhysicalBinding.CompareTo(right.PhysicalBinding));
            plans[i] = new ShaderBindGroupPlan(groups[i], list.ToArray());
        }

        return plans;
    }
}

internal readonly struct ShaderDescriptorBinding
{
    public ShaderBindingSlot Slot { get; }
    public int PhysicalBinding { get; }
    public int PhysicalGroup { get; }
    public ShaderStageMask Stages { get; }

    public ShaderDescriptorBinding(ShaderBindingSlot slot, int physicalBinding)
        : this(slot, physicalBinding, physicalGroup: 0, slot.Stages)
    {
    }

    public ShaderDescriptorBinding(
        ShaderBindingSlot slot,
        int physicalBinding,
        int physicalGroup,
        ShaderStageMask stages)
    {
        Slot = slot;
        PhysicalBinding = physicalBinding;
        PhysicalGroup = physicalGroup;
        Stages = stages;
    }
}

internal readonly struct ShaderBindGroupPlan
{
    public int Group { get; }
    public ShaderDescriptorBinding[] Bindings { get; }

    public ShaderBindGroupPlan(int group, ShaderDescriptorBinding[] bindings)
    {
        Group = group;
        Bindings = bindings;
    }
}
