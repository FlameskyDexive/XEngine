// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Backends.WebGPU.Native;
using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.WebGPU;

/// <summary>
/// Maps a <see cref="ShaderDescriptorLayoutPlan"/> onto wgpu bind-group layouts
/// (<c>@group</c> / <c>@binding</c>).
/// </summary>
internal static class WebGPUBindGroupLayouts
{
    public static WGPUBindGroupLayoutEntry ToLayoutEntry(ShaderDescriptorBinding binding)
    {
        ShaderBindingSlot slot = binding.Slot;
        var entry = new WGPUBindGroupLayoutEntry
        {
            binding = (uint)binding.PhysicalBinding,
            visibility = ToVisibility(binding.Stages),
            bindingArraySize = slot.Count > 1 ? (uint)slot.Count : 0u,
        };

        switch (slot.Kind)
        {
            case ShaderBindingKind.Buffer:
                entry.buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform };
                break;
            case ShaderBindingKind.StructuredBuffer:
                entry.buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage };
                break;
            case ShaderBindingKind.StorageBuffer:
                entry.buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage };
                break;
            case ShaderBindingKind.Texture:
                entry.texture = new WGPUTextureBindingLayout
                {
                    // Unfilterable-float accepts both filterable and non-filterable float views
                    // (wgpu binding validation), which the engine needs: Load()-fetched data
                    // textures like morph deltas and the bone matrix texture are RGBA32F
                    // (non-filterable) while material textures are filterable — one layout per
                    // shader must bind both.
                    sampleType = WGPUTextureSampleType.UnfilterableFloat,
                    viewDimension = ToViewDimension(slot.ResourceShape),
                    multisampled = IsMultisampled(slot.ResourceShape) ? 1u : 0u,
                };
                break;
            case ShaderBindingKind.StorageTexture:
                entry.storageTexture = new WGPUStorageTextureBindingLayout
                {
                    access = WGPUStorageTextureAccess.WriteOnly,
                    format = WGPUTextureFormat.RGBA8Unorm,
                    viewDimension = ToViewDimension(slot.ResourceShape),
                };
                break;
            case ShaderBindingKind.Sampler:
                entry.sampler = new WGPUSamplerBindingLayout { type = WGPUSamplerBindingType.Filtering };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(binding), slot.Kind, "Unsupported WebGPU bind-group entry kind.");
        }

        return entry;
    }

    public static unsafe nint[] CreateNativeLayouts(nint device, ShaderDescriptorLayoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (device == 0)
            throw new ArgumentOutOfRangeException(nameof(device));

        var layouts = new nint[plan.BindGroups.Length];
        for (int groupIndex = 0; groupIndex < plan.BindGroups.Length; groupIndex++)
        {
            ShaderBindGroupPlan group = plan.BindGroups[groupIndex];
            var entries = new WGPUBindGroupLayoutEntry[group.Bindings.Length];
            for (int i = 0; i < group.Bindings.Length; i++)
                entries[i] = ToLayoutEntry(group.Bindings[i]);

            fixed (WGPUBindGroupLayoutEntry* entryPtr = entries)
            {
                var descriptor = new WGPUBindGroupLayoutDescriptor
                {
                    entryCount = (nuint)entries.Length,
                    entries = entryPtr,
                };
                nint layout = WebGPUNative.wgpuDeviceCreateBindGroupLayout(device, &descriptor);
                if (layout == 0)
                {
                    Release(layouts, groupIndex);
                    throw new InvalidOperationException(
                        $"wgpuDeviceCreateBindGroupLayout returned null for @group({group.Group}).");
                }

                layouts[groupIndex] = layout;
            }
        }

        return layouts;
    }

    public static void Release(nint[] layouts) => Release(layouts, layouts.Length);

    private static void Release(nint[] layouts, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (layouts[i] == 0)
                continue;
            WebGPUNative.wgpuBindGroupLayoutRelease(layouts[i]);
            layouts[i] = 0;
        }
    }

    private static WGPUShaderStage ToVisibility(ShaderStageMask stages)
    {
        WGPUShaderStage visibility = WGPUShaderStage.None;
        if ((stages & ShaderStageMask.Vertex) != 0)
            visibility |= WGPUShaderStage.Vertex;
        if ((stages & ShaderStageMask.Fragment) != 0)
            visibility |= WGPUShaderStage.Fragment;
        if ((stages & ShaderStageMask.Compute) != 0)
            visibility |= WGPUShaderStage.Compute;
        return visibility == WGPUShaderStage.None
            ? WGPUShaderStage.Vertex | WGPUShaderStage.Fragment
            : visibility;
    }

    private static bool IsMultisampled(ShaderResourceShape shape) =>
        shape is ShaderResourceShape.Texture2DMultisample or ShaderResourceShape.Texture2DMultisampleArray;

    private static WGPUTextureViewDimension ToViewDimension(ShaderResourceShape shape) => shape switch
    {
        ShaderResourceShape.Texture1D => WGPUTextureViewDimension.D1,
        ShaderResourceShape.Texture1DArray => WGPUTextureViewDimension.D2Array,
        ShaderResourceShape.Texture2DArray or ShaderResourceShape.Texture2DMultisampleArray => WGPUTextureViewDimension.D2Array,
        ShaderResourceShape.Texture3D => WGPUTextureViewDimension.D3,
        ShaderResourceShape.TextureCube => WGPUTextureViewDimension.Cube,
        ShaderResourceShape.TextureCubeArray => WGPUTextureViewDimension.CubeArray,
        _ => WGPUTextureViewDimension.D2,
    };
}
