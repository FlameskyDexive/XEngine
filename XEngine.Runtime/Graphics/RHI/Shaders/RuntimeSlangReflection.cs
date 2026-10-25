// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

using XEngine.Slang;

using SlangResourceAccess = XEngine.Slang.ResourceAccess;
using SlangResourceShape = XEngine.Slang.ResourceShape;
using SlangScalarType = XEngine.Slang.ScalarType;

namespace XEngine.Runtime.RHI.Shaders;

internal static class RuntimeSlangReflection
{
    public static SlangStageReflection ReflectLogical(
        ComponentType program,
        int targetIndex,
        ShaderStageMask stage,
        bool metalPerKindSlots = false)
    {
        ShaderReflection reflection = program.GetLayout(targetIndex, out _);
        var bindings = new List<ShaderBindingSlot>((int)reflection.ParameterCount);
        foreach (VariableLayoutReflection parameter in reflection.Parameters)
        {
            bindings.Add(ReflectBinding(parameter, stage));
        }

        ShaderBindingLayout bindingLayout = NormalizeBindings(bindings, metalPerKindSlots);
        ShaderVertexInputLayout vertexInputs = stage == ShaderStageMask.Vertex
            ? ReflectVertexInputs(reflection, null)
            : new ShaderVertexInputLayout();
        return new SlangStageReflection(bindingLayout, vertexInputs);
    }

    public static (SlangStageReflection Vertex, SlangStageReflection Fragment) AttachPhysicalLayouts(
        SlangStageReflection logicalVertex,
        SlangStageReflection logicalFragment,
        ComponentType physicalProgram,
        int targetIndex,
        bool metalPerKindSlots = false)
    {
        ShaderReflection physicalReflection = physicalProgram.GetLayout(targetIndex, out _);
        var physicalBindings = new List<ShaderBindingSlot>((int)physicalReflection.ParameterCount);
        foreach (VariableLayoutReflection parameter in physicalReflection.Parameters)
        {
            physicalBindings.Add(ReflectBinding(parameter, ShaderStageMask.AllGraphics));
        }
        var consumed = new bool[physicalBindings.Count];

        ShaderBindingLayout vertexBindings = new()
        {
            Buffers = AttachPhysicalLocations(logicalVertex.BindingLayout.Buffers, physicalBindings, consumed,
                metalPerKindSlots),
            Textures = AttachPhysicalLocations(logicalVertex.BindingLayout.Textures, physicalBindings, consumed,
                metalPerKindSlots),
            Samplers = AttachPhysicalLocations(logicalVertex.BindingLayout.Samplers, physicalBindings, consumed,
                metalPerKindSlots),
        };
        ShaderBindingLayout fragmentBindings = new()
        {
            Buffers = AttachPhysicalLocations(logicalFragment.BindingLayout.Buffers, physicalBindings, consumed,
                metalPerKindSlots),
            Textures = AttachPhysicalLocations(logicalFragment.BindingLayout.Textures, physicalBindings, consumed,
                metalPerKindSlots),
            Samplers = AttachPhysicalLocations(logicalFragment.BindingLayout.Samplers, physicalBindings, consumed,
                metalPerKindSlots),
        };
        SlangStageReflection vertex = new(
            vertexBindings,
            ReflectVertexInputs(physicalReflection, null, entryPointIndex: 0));
        SlangStageReflection fragment = new(fragmentBindings, logicalFragment.VertexInputLayout);

        for (int i = 0; i < consumed.Length; i++)
        {
            if (!consumed[i])
                throw new InvalidOperationException($"Physical reflection resource '{physicalBindings[i].Name}' has no DXIL logical binding.");
        }
        return (vertex, fragment);
    }

    public static ShaderBindingLayout AttachMetalPhysicalSlots(
        ShaderBindingLayout logical,
        bool compactIndices = false,
        bool compactSamplers = false)
    {
        var attached = new ShaderBindingLayout
        {
            Buffers = AttachMetalPhysicalSlots(logical.Buffers),
            Textures = AttachMetalPhysicalSlots(logical.Textures),
            Samplers = AttachMetalPhysicalSlots(logical.Samplers),
        };
        return compactIndices
            ? CompactMetalPhysicalSlots(attached)
            : compactSamplers ? CompactMetalSamplerPhysicalSlots(attached) : attached;
    }

    public static ShaderBindingLayout AttachPhysicalLayout(
        ShaderBindingLayout logical,
        ComponentType physicalProgram,
        int targetIndex,
        ShaderStageMask stage,
        bool metalPerKindSlots)
    {
        ShaderReflection physicalReflection = physicalProgram.GetLayout(targetIndex, out _);
        var physicalBindings = new List<ShaderBindingSlot>((int)physicalReflection.ParameterCount);
        foreach (VariableLayoutReflection parameter in physicalReflection.Parameters)
            physicalBindings.Add(ReflectBinding(parameter, stage));

        var consumed = new bool[physicalBindings.Count];
        ShaderBindingLayout attached = new()
        {
            Buffers = AttachPhysicalLocations(logical.Buffers, physicalBindings, consumed, metalPerKindSlots),
            Textures = AttachPhysicalLocations(logical.Textures, physicalBindings, consumed, metalPerKindSlots),
            Samplers = AttachPhysicalLocations(logical.Samplers, physicalBindings, consumed, metalPerKindSlots),
        };
        for (int i = 0; i < consumed.Length; i++)
        {
            if (!consumed[i])
                throw new InvalidOperationException(
                    $"Physical reflection resource '{physicalBindings[i].Name}' has no logical binding.");
        }
        return attached;
    }

    /// <summary>
    /// Attaches the argument indices Slang actually emitted into MSL. Slang's target reflection
    /// exposes logical register indices for Metal resources, while MSL may assign different
    /// indices after lowering and resource-kind partitioning.
    /// </summary>
    public static ShaderBindingLayout AttachMetalSourcePhysicalSlots(
        ShaderBindingLayout reflected,
        byte[] metalSource)
    {
        ArgumentNullException.ThrowIfNull(reflected);
        ArgumentNullException.ThrowIfNull(metalSource);

        string source = Encoding.UTF8.GetString(metalSource);
        return new ShaderBindingLayout
        {
            Buffers = AttachMetalSourcePhysicalSlots(reflected.Buffers, source, "buffer"),
            Textures = AttachMetalSourcePhysicalSlots(reflected.Textures, source, "texture"),
            Samplers = AttachMetalSourcePhysicalSlots(reflected.Samplers, source, "sampler"),
        };
    }

    /// <summary>
    /// Rewrites Slang's explicit Metal argument indices to a dense per-stage, per-kind layout.
    /// Metal samplers have a 0..15 slot limit on macOS and iOS, while portable HLSL register
    /// numbers can be sparse or larger (for example, engine-reserved s24/s29 resources).
    /// </summary>
    public static byte[] CompactMetalSourceIndices(byte[] metalSource, ShaderBindingLayout logical)
    {
        ArgumentNullException.ThrowIfNull(metalSource);
        ArgumentNullException.ThrowIfNull(logical);

        MetalIndexMaps maps = CreateMetalIndexMaps(logical);
        string source = Encoding.UTF8.GetString(metalSource);
        source = RewriteMetalAttributeByName(source, "buffer", maps.Buffers);
        source = RewriteMetalAttributeByName(source, "texture", maps.Textures);
        source = RewriteMetalAttributeByName(source, "sampler", maps.Samplers);
        return Encoding.UTF8.GetBytes(source);
    }

    /// <summary>
    /// Rewrites only Metal sampler arguments to dense per-stage indices. macOS keeps its
    /// established buffer and texture slots, but sampler arguments must fit Metal's 0..15 limit.
    /// </summary>
    public static byte[] CompactMetalSamplerSourceIndices(byte[] metalSource, ShaderBindingLayout logical)
    {
        ArgumentNullException.ThrowIfNull(metalSource);
        ArgumentNullException.ThrowIfNull(logical);

        Dictionary<string, int> samplerIndices = CreateMetalSamplerIndexMap(logical.Samplers);
        string source = Encoding.UTF8.GetString(metalSource);
        source = RewriteMetalAttributeByName(source, "sampler", samplerIndices);
        return Encoding.UTF8.GetBytes(source);
    }

    public static ShaderBindingLayout CompactMetalPhysicalSlots(ShaderBindingLayout layout)
    {
        MetalIndexMaps maps = CreateMetalIndexMaps(layout);
        return new ShaderBindingLayout
        {
            Buffers = Compact(layout.Buffers, maps.Buffers),
            Textures = Compact(layout.Textures, maps.Textures),
            Samplers = Compact(layout.Samplers, maps.Samplers),
        };
    }

    public static ShaderBindingLayout CompactMetalSamplerPhysicalSlots(ShaderBindingLayout layout)
    {
        Dictionary<string, int> samplerIndices = CreateMetalSamplerIndexMap(layout.Samplers);
        return new ShaderBindingLayout
        {
            Buffers = layout.Buffers,
            Textures = layout.Textures,
            Samplers = Compact(layout.Samplers, samplerIndices),
        };
    }

    private static ShaderBindingSlot[] Compact(ShaderBindingSlot[] slots, IReadOnlyDictionary<string, int> indices)
    {
        var result = new ShaderBindingSlot[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            int sourceBaseBinding = slot.HasPhysicalLocation ? slot.PhysicalBinding : slot.Slot;
            if (!indices.TryGetValue(slot.Name, out int compactBaseBinding))
                throw new InvalidOperationException($"Metal binding '{slot.Name}' has no compact physical index.");
            ShaderPhysicalBinding[] sourceBindings = slot.HasPhysicalLocation
                ? slot.PhysicalBindings
                : [new ShaderPhysicalBinding(slot.Slot, 0, slot.Stages)];
            var compactedBindings = new ShaderPhysicalBinding[sourceBindings.Length];
            for (int physicalIndex = 0; physicalIndex < sourceBindings.Length; physicalIndex++)
            {
                ShaderPhysicalBinding physical = sourceBindings[physicalIndex];
                compactedBindings[physicalIndex] = new ShaderPhysicalBinding(
                    checked(compactBaseBinding + physical.Binding - sourceBaseBinding),
                    physical.Space,
                    physical.Stages);
            }
            result[i] = slot.WithPhysicalBindings(compactedBindings);
        }
        return result;
    }

    private static ShaderBindingSlot[] AttachMetalSourcePhysicalSlots(
        ShaderBindingSlot[] slots,
        string source,
        string attribute)
    {
        var result = new List<ShaderBindingSlot>(slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            string resourceName = Regex.Escape(slot.Name);
            if (slot.Name.Length > 1 && slot.Name[0] == '_')
            {
                // Slang's MSL emitter adds a U prefix to some HLSL globals while leaving
                // material resources and other globals with their original underscore name.
                resourceName = $"(?:{resourceName}|U_{Regex.Escape(slot.Name[1..])})";
            }
            string pattern = $@"\b{resourceName}(?:_\d+)?\s*\[\[\s*{attribute}\s*\(\s*(\d+)\s*\)\s*\]\]";
            MatchCollection matches = Regex.Matches(source, pattern, RegexOptions.CultureInvariant);
            if (matches.Count == 0)
            {
                // The program reflection includes declarations shared by graphics stages even
                // when this entry point optimized the resource away. Do not expose a binding
                // that has no corresponding argument in this stage's emitted MSL.
                continue;
            }
            if (matches.Count != 1 || !int.TryParse(matches[0].Groups[1].Value, out int sourceIndex))
            {
                throw new InvalidOperationException(
                    $"Could not identify exactly one emitted Metal {attribute} argument for resource '{slot.Name}'.");
            }

            ShaderPhysicalBinding[] sourceBindings = slot.HasPhysicalLocation
                ? slot.PhysicalBindings
                : [new ShaderPhysicalBinding(slot.Slot, 0, slot.Stages)];
            var physicalBindings = new ShaderPhysicalBinding[sourceBindings.Length];
            for (int physicalIndex = 0; physicalIndex < sourceBindings.Length; physicalIndex++)
            {
                ShaderPhysicalBinding physical = sourceBindings[physicalIndex];
                physicalBindings[physicalIndex] = new ShaderPhysicalBinding(
                    sourceIndex,
                    physical.Space,
                    physical.Stages);
            }
            result.Add(slot.WithPhysicalBindings(physicalBindings));
        }
        return [.. result];
    }

    private static MetalIndexMaps CreateMetalIndexMaps(ShaderBindingLayout layout) => new(
        CreateMetalIndexMap(layout.Buffers),
        CreateMetalIndexMap(layout.Textures),
        CreateMetalIndexMap(layout.Samplers));

    private static Dictionary<string, int> CreateMetalIndexMap(ShaderBindingSlot[] slots)
    {
        var ordered = new List<(int Binding, int Count, string Name)>(slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            int binding = slot.HasPhysicalLocation ? slot.PhysicalBinding : slot.Slot;
            ordered.Add((binding, slot.Count, slot.Name));
        }
        ordered.Sort(static (left, right) =>
        {
            int byBinding = left.Binding.CompareTo(right.Binding);
            if (byBinding != 0)
                return byBinding;
            int byCount = left.Count.CompareTo(right.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(left.Name, right.Name);
        });

        var indices = new Dictionary<string, int>(ordered.Count, StringComparer.Ordinal);
        int nextIndex = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            int count = entry.Count;
            string name = entry.Name;
            if (!indices.TryAdd(name, nextIndex))
                throw new InvalidOperationException($"Metal binding name '{name}' is not unique within its resource kind.");
            nextIndex = checked(nextIndex + count);
        }
        return indices;
    }

    private static Dictionary<string, int> CreateMetalSamplerIndexMap(ShaderBindingSlot[] slots)
    {
        Dictionary<string, int> indices = CreateMetalIndexMap(slots);
        int maxEnd = 0;
        bool hasOverlappingSlots = false;
        var ordered = new List<(int Binding, int Count, string Name)>(slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            int binding = slot.HasPhysicalLocation ? slot.PhysicalBinding : slot.Slot;
            maxEnd = Math.Max(maxEnd, checked(binding + slot.Count));
            ordered.Add((binding, slot.Count, slot.Name));
        }
        ordered.Sort(static (left, right) =>
        {
            int byBinding = left.Binding.CompareTo(right.Binding);
            return byBinding != 0 ? byBinding : string.CompareOrdinal(left.Name, right.Name);
        });
        int previousEnd = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            (int binding, int count, _) = ordered[i];
            if (binding < previousEnd)
            {
                hasOverlappingSlots = true;
                break;
            }
            previousEnd = checked(binding + count);
        }

        // Keep established macOS sampler slots when the shader already fits Metal's limit.
        // Compact when the table is sparse/high or Slang assigned one index to distinct names.
        if (maxEnd <= 16 && !hasOverlappingSlots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                ShaderBindingSlot slot = slots[i];
                indices[slot.Name] = slot.HasPhysicalLocation ? slot.PhysicalBinding : slot.Slot;
            }
        }

        return indices;
    }

    private static string RewriteMetalAttributeByName(
        string source,
        string attribute,
        IReadOnlyDictionary<string, int> indices)
    {
        string pattern = $@"(?<name>[A-Za-z_]\w*)(?<spacing>\s*)(?<prefix>\[\[\s*{attribute}\s*\(\s*)(?<index>\d+)(?<suffix>\s*\)\s*\]\])";
        return Regex.Replace(source, pattern, match =>
        {
            string emittedName = match.Groups["name"].Value;
            if (!TryGetMetalResourceIndex(indices, emittedName, out int newIndex)
                || !int.TryParse(match.Groups["index"].Value, out int oldIndex)
                || oldIndex == newIndex)
            {
                return match.Value;
            }
            return string.Concat(
                emittedName,
                match.Groups["spacing"].Value,
                match.Groups["prefix"].Value,
                newIndex,
                match.Groups["suffix"].Value);
        }, RegexOptions.CultureInvariant);
    }

    private static bool TryGetMetalResourceIndex(
        IReadOnlyDictionary<string, int> indices,
        string emittedName,
        out int index)
    {
        if (indices.TryGetValue(emittedName, out index))
            return true;

        string normalizedName = emittedName;
        int suffixSeparator = emittedName.LastIndexOf('_');
        if (suffixSeparator > 0
            && int.TryParse(emittedName.AsSpan(suffixSeparator + 1), out _))
        {
            normalizedName = emittedName[..suffixSeparator];
            if (indices.TryGetValue(normalizedName, out index))
                return true;
        }

        if (normalizedName.StartsWith("U_", StringComparison.Ordinal)
            && indices.TryGetValue("_" + normalizedName[2..], out index))
        {
            return true;
        }

        index = 0;
        return false;
    }

    private readonly record struct MetalIndexMaps(
        Dictionary<string, int> Buffers,
        Dictionary<string, int> Textures,
        Dictionary<string, int> Samplers);

    private static ShaderBindingSlot[] AttachMetalPhysicalSlots(ShaderBindingSlot[] logicalBindings)
    {
        var result = new ShaderBindingSlot[logicalBindings.Length];
        for (int i = 0; i < logicalBindings.Length; i++)
        {
            ShaderBindingSlot logical = logicalBindings[i];
            result[i] = logical.WithPhysicalBindings(
                [new ShaderPhysicalBinding(logical.Slot, 0, logical.Stages)]);
        }
        return result;
    }

    public static ShaderBindingLayout Merge(
        ShaderBindingLayout vertex,
        ShaderBindingLayout fragment,
        bool metalPerKindSlots = false)
    {
        var bindings = new List<ShaderBindingSlot>(
            vertex.Buffers.Length + vertex.Textures.Length + vertex.Samplers.Length
            + fragment.Buffers.Length + fragment.Textures.Length + fragment.Samplers.Length);
        Add(bindings, vertex.Buffers);
        Add(bindings, vertex.Textures);
        Add(bindings, vertex.Samplers);
        Add(bindings, fragment.Buffers);
        Add(bindings, fragment.Textures);
        Add(bindings, fragment.Samplers);
        return NormalizeBindings(bindings, metalPerKindSlots);
    }

    private static ShaderBindingSlot ReflectBinding(
        VariableLayoutReflection parameter,
        ShaderStageMask stage,
        int? forcedPhysicalSlot = null)
    {
        TypeLayoutReflection declaredType = parameter.TypeLayout;
        int count = 1;
        TypeLayoutReflection type = declaredType;
        if (declaredType.IsArray)
        {
            uint reflectedCount = declaredType.TotalArrayElementCount;
            if (reflectedCount == 0 || reflectedCount > int.MaxValue)
                throw new NotSupportedException($"Slang resource '{parameter.Name}' has an unsupported unbounded array count.");
            count = (int)reflectedCount;
            type = declaredType.UnwrapArray();
        }

        ShaderBindingKind kind;
        ShaderResourceShape shape = ShaderResourceShape.Unknown;
        int byteSize = 0;
        ShaderUniformField[] fields = [];

        switch (type.Kind)
        {
            case TypeKind.ConstantBuffer:
            {
                kind = ShaderBindingKind.Buffer;
                TypeLayoutReflection elementLayout = type.ElementTypeLayout;
                byteSize = GetOccupiedSize(elementLayout);
                fields = ReflectUniformFields(parameter.Name, elementLayout);
                break;
            }
            case TypeKind.SamplerState:
                kind = ShaderBindingKind.Sampler;
                break;
            case TypeKind.Resource:
            case TypeKind.TextureBuffer:
            case TypeKind.ShaderStorageBuffer:
                shape = MapResourceShape(type.ResourceShape, parameter.Name);
                kind = MapResourceKind(type.ResourceShape, type.ResourceAccess, parameter.Name);
                break;
            default:
                throw new NotSupportedException(
                    $"Slang global parameter '{parameter.Name}' has unsupported type kind {type.Kind}; use an explicit resource or cbuffer.");
        }

        return new ShaderBindingSlot(
            kind,
            forcedPhysicalSlot ?? checked((int)parameter.BindingIndex),
            parameter.Name,
            checked((int)parameter.BindingSpace),
            count,
            stage,
            shape,
            byteSize,
            fields);
    }

    private static ShaderBindingSlot[] AttachPhysicalLocations(
        ShaderBindingSlot[] logicalBindings,
        List<ShaderBindingSlot> physicalBindings,
        bool[] consumed,
        bool metalPerKindSlots = false)
    {
        var result = new ShaderBindingSlot[logicalBindings.Length];
        for (int logicalIndex = 0; logicalIndex < logicalBindings.Length; logicalIndex++)
        {
            ShaderBindingSlot logical = logicalBindings[logicalIndex];
            int physicalIndex = FindUnconsumedByName(physicalBindings, consumed, logical.Name);
            bool reusedSharedPhysical = false;
            if (physicalIndex < 0)
            {
                physicalIndex = FindByName(physicalBindings, logical.Name);
                if (physicalIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Slang resource '{logical.Name}' is present in DXIL reflection but missing from physical reflection.");
                }

                reusedSharedPhysical = true;
            }

            ShaderBindingSlot physical = physicalBindings[physicalIndex];
            if (logical.Kind != physical.Kind || logical.Count != physical.Count)
            {
                throw new InvalidOperationException(
                    $"Slang resource '{logical.Name}' differs between DXIL and physical reflection: " +
                    $"{Describe(logical)} versus {Describe(physical)}.");
            }
            if (!reusedSharedPhysical)
                consumed[physicalIndex] = true;
            result[logicalIndex] = logical.WithPhysicalBindings(
            [
                new ShaderPhysicalBinding(
                    physical.Slot,
                    metalPerKindSlots ? 0 : physical.Space,
                    logical.Stages),
            ],
            metalPerKindSlots && logical.Kind == ShaderBindingKind.Buffer
                ? physical.ByteSize
                : null);
        }
        return result;
    }


    private static int FindUnconsumedByName(
        List<ShaderBindingSlot> bindings,
        bool[] consumed,
        string name)
    {
        for (int i = 0; i < bindings.Count; i++)
        {
            if (!consumed[i] && bindings[i].Name == name)
                return i;
        }
        return -1;
    }

    private static ShaderBindingKind MapResourceKind(
        SlangResourceShape reflectedShape,
        SlangResourceAccess access,
        string name)
    {
        SlangResourceShape baseShape = reflectedShape & SlangResourceShape.BaseShapeMask;
        bool buffer = baseShape is SlangResourceShape.StructuredBuffer or SlangResourceShape.ByteAddressBuffer;
        return access switch
        {
            SlangResourceAccess.Read => buffer
                ? ShaderBindingKind.StructuredBuffer
                : ShaderBindingKind.Texture,
            SlangResourceAccess.ReadWrite or SlangResourceAccess.RasterOrdered
                or SlangResourceAccess.Append or SlangResourceAccess.Consume
                or SlangResourceAccess.Write => buffer
                    ? ShaderBindingKind.StorageBuffer
                    : ShaderBindingKind.StorageTexture,
            _ => throw new NotSupportedException(
                $"Slang resource '{name}' has unsupported access mode {access}.")
        };
    }

    private static ShaderResourceShape MapResourceShape(
        SlangResourceShape reflectedShape,
        string name)
    {
        bool array = (reflectedShape & SlangResourceShape.TextureArrayFlag) != 0;
        bool multisample = (reflectedShape & SlangResourceShape.TextureMultisampleFlag) != 0;
        SlangResourceShape baseShape = reflectedShape & SlangResourceShape.BaseShapeMask;
        return (baseShape, array, multisample) switch
        {
            (SlangResourceShape.Texture1D, false, false) => ShaderResourceShape.Texture1D,
            (SlangResourceShape.Texture1D, true, false) => ShaderResourceShape.Texture1DArray,
            (SlangResourceShape.Texture2D, false, false) => ShaderResourceShape.Texture2D,
            (SlangResourceShape.Texture2D, true, false) => ShaderResourceShape.Texture2DArray,
            (SlangResourceShape.Texture2D, false, true) => ShaderResourceShape.Texture2DMultisample,
            (SlangResourceShape.Texture2D, true, true) => ShaderResourceShape.Texture2DMultisampleArray,
            (SlangResourceShape.Texture3D, false, false) => ShaderResourceShape.Texture3D,
            (SlangResourceShape.TextureCube, false, false) => ShaderResourceShape.TextureCube,
            (SlangResourceShape.TextureCube, true, false) => ShaderResourceShape.TextureCubeArray,
            (SlangResourceShape.TextureBuffer, false, false) => ShaderResourceShape.TextureBuffer,
            (SlangResourceShape.StructuredBuffer, false, false) => ShaderResourceShape.StructuredBuffer,
            (SlangResourceShape.ByteAddressBuffer, false, false) => ShaderResourceShape.ByteAddressBuffer,
            _ => throw new NotSupportedException(
                $"Slang resource '{name}' has unsupported shape {reflectedShape}.")
        };
    }

    private static ShaderUniformField[] ReflectUniformFields(
        string bufferName,
        TypeLayoutReflection blockLayout)
    {
        var fields = new List<ShaderUniformField>((int)blockLayout.FieldCount);
        foreach (VariableLayoutReflection field in blockLayout.Fields)
        {
            CollectUniformField(
                bufferName,
                field.Name,
                checked((int)field.GetOffset()),
                field.TypeLayout,
                fields);
        }
        return [.. fields];
    }

    private static void CollectUniformField(
        string bufferName,
        string name,
        int offset,
        TypeLayoutReflection type,
        List<ShaderUniformField> fields)
    {
        if (type.IsArray)
        {
            uint reflectedCount = type.TotalArrayElementCount;
            if (reflectedCount == 0 || reflectedCount > int.MaxValue)
                throw new NotSupportedException($"Slang uniform '{bufferName}.{name}' has an unsupported unbounded array count.");

            TypeLayoutReflection elementType = type.UnwrapArray();
            int arrayStride = checked((int)type.GetElementStride(ParameterCategory.Uniform));
            if (elementType.Kind == TypeKind.Struct)
            {
                for (int elementIndex = 0; elementIndex < reflectedCount; elementIndex++)
                {
                    foreach (VariableLayoutReflection nested in elementType.Fields)
                    {
                        CollectUniformField(
                            bufferName,
                            $"{name}[{elementIndex}].{nested.Name}",
                            checked(offset + elementIndex * arrayStride + (int)nested.GetOffset()),
                            nested.TypeLayout,
                            fields);
                    }
                }
                return;
            }

            AddValueField(
                bufferName,
                name,
                offset,
                GetOccupiedSize(type),
                elementType,
                checked((int)reflectedCount),
                arrayStride,
                fields);
            return;
        }

        if (type.Kind == TypeKind.Struct)
        {
            foreach (VariableLayoutReflection nested in type.Fields)
            {
                CollectUniformField(
                    bufferName,
                    $"{name}.{nested.Name}",
                    checked(offset + (int)nested.GetOffset()),
                    nested.TypeLayout,
                    fields);
            }
            return;
        }

        AddValueField(bufferName, name, offset, GetOccupiedSize(type), type, 1, 0, fields);
    }

    private static void AddValueField(
        string bufferName,
        string name,
        int offset,
        int size,
        TypeLayoutReflection type,
        int elementCount,
        int arrayStride,
        List<ShaderUniformField> fields)
    {
        if (type.Kind is not TypeKind.Scalar and not TypeKind.Vector and not TypeKind.Matrix)
        {
            throw new NotSupportedException(
                $"Slang uniform '{bufferName}.{name}' has unsupported type kind {type.Kind}.");
        }

        ShaderScalarType scalarType = MapScalarType(type.ScalarType, $"{bufferName}.{name}");
        byte rows = type.Kind == TypeKind.Matrix ? checked((byte)type.RowCount) : (byte)1;
        byte columns = type.Kind == TypeKind.Scalar ? (byte)1 : checked((byte)type.ColumnCount);
        int matrixStride = 0;
        if (type.Kind == TypeKind.Matrix)
        {
            int majorCount = type.MatrixLayoutMode == MatrixLayoutMode.ColumnMajor ? columns : rows;
            matrixStride = majorCount == 0 ? 0 : GetOccupiedSize(type) / majorCount;
        }

        fields.Add(new ShaderUniformField(
            name,
            offset,
            size,
            scalarType,
            rows,
            columns,
            elementCount,
            arrayStride,
            matrixStride));
    }

    private static ShaderScalarType MapScalarType(SlangScalarType type, string name) => type switch
    {
        SlangScalarType.Bool => ShaderScalarType.Bool,
        SlangScalarType.Int8 => ShaderScalarType.Int8,
        SlangScalarType.UInt8 => ShaderScalarType.UInt8,
        SlangScalarType.Int16 => ShaderScalarType.Int16,
        SlangScalarType.UInt16 => ShaderScalarType.UInt16,
        SlangScalarType.Int32 => ShaderScalarType.Int32,
        SlangScalarType.UInt32 => ShaderScalarType.UInt32,
        SlangScalarType.Int64 => ShaderScalarType.Int64,
        SlangScalarType.UInt64 => ShaderScalarType.UInt64,
        SlangScalarType.Float16 => ShaderScalarType.Float16,
        SlangScalarType.Float32 => ShaderScalarType.Float32,
        SlangScalarType.Float64 => ShaderScalarType.Float64,
        SlangScalarType.IntPtr => ShaderScalarType.IntPtr,
        SlangScalarType.UIntPtr => ShaderScalarType.UIntPtr,
        _ => throw new NotSupportedException(
            $"Slang value '{name}' has unsupported scalar type {type}.")
    };

    private static ShaderVertexInputLayout ReflectVertexInputs(
        ShaderReflection reflection,
        ShaderVertexInputLayout? logicalLayout,
        int entryPointIndex = -1)
    {
        var inputs = new List<ShaderVertexInputSlot>();
        int index = 0;
        foreach (EntryPointReflection entryPoint in reflection.EntryPoints)
        {
            if (entryPointIndex >= 0 && index++ != entryPointIndex)
                continue;
            if (entryPoint.Stage != XEngine.Slang.ShaderStage.Vertex)
                continue;
            foreach (VariableLayoutReflection parameter in entryPoint.Parameters)
                CollectVertexInputs(parameter, logicalLayout, inputs);
        }

        inputs.Sort(static (left, right) =>
        {
            int byLocation = left.Location.CompareTo(right.Location);
            if (byLocation != 0)
                return byLocation;
            int byName = string.CompareOrdinal(left.SemanticName, right.SemanticName);
            return byName != 0 ? byName : left.SemanticIndex.CompareTo(right.SemanticIndex);
        });
        for (int i = 1; i < inputs.Count; i++)
        {
            if (inputs[i - 1].Location == inputs[i].Location)
            {
                throw new InvalidOperationException(
                    $"Slang vertex location {inputs[i].Location} is assigned to both " +
                    $"'{inputs[i - 1].SemanticName}{inputs[i - 1].SemanticIndex}' and " +
                    $"'{inputs[i].SemanticName}{inputs[i].SemanticIndex}'.");
            }
        }
        return new ShaderVertexInputLayout { Inputs = [.. inputs] };
    }

    private static void CollectVertexInputs(
        VariableLayoutReflection parameter,
        ShaderVertexInputLayout? logicalLayout,
        List<ShaderVertexInputSlot> inputs)
    {
        if (parameter.TypeLayout.Kind == TypeKind.Struct)
        {
            foreach (VariableLayoutReflection field in parameter.TypeLayout.Fields)
                CollectVertexInputs(field, logicalLayout, inputs);
            return;
        }

        if (parameter.CategoryCount != 1
            || parameter.GetCategoryByIndex(0) != ParameterCategory.VaryingInput)
        {
            return;
        }

        string semanticName = parameter.SemanticName.ToUpperInvariant();
        uint semanticIndex = parameter.SemanticIndex;
        if (!ShaderReflectionUtilities.TryMapVertexSemantic(semanticName, semanticIndex, out uint semantic))
            return;

        TypeLayoutReflection type = parameter.TypeLayout;
        if (type.Kind is not TypeKind.Scalar and not TypeKind.Vector)
        {
            throw new NotSupportedException(
                $"Slang vertex input '{semanticName}{semanticIndex}' has unsupported type kind {type.Kind}.");
        }

        byte componentCount = type.Kind == TypeKind.Scalar ? (byte)1 : checked((byte)type.ColumnCount);
        uint location = logicalLayout != null && logicalLayout.TryGetLocation(semantic, out uint logicalLocation)
            ? logicalLocation
            : parameter.BindingIndex;
        inputs.Add(new ShaderVertexInputSlot(
            semantic,
            location,
            semanticName,
            semanticIndex,
            new ShaderVertexInputFormat(
                MapScalarType(type.ScalarType, $"vertex input {semanticName}{semanticIndex}"),
                componentCount)));
    }

    private static ShaderBindingLayout NormalizeBindings(
        List<ShaderBindingSlot> source,
        bool metalPerKindSlots)
    {
        var normalized = new List<ShaderBindingSlot>(source.Count);
        for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            ShaderBindingSlot candidate = source[sourceIndex];
            int sameNameIndex = FindByName(normalized, candidate.Name);
            if (sameNameIndex >= 0)
            {
                ShaderBindingSlot existing = normalized[sameNameIndex];
                if (!Equivalent(existing, candidate))
                {
                    throw new InvalidOperationException(
                        $"Slang binding '{candidate.Name}' has conflicting layouts across stages: " +
                        $"{Describe(existing)} versus {Describe(candidate)}.");
                }
                normalized[sameNameIndex] = existing.MergeStagesAndPhysicalBindings(candidate);
                continue;
            }

            for (int existingIndex = 0; existingIndex < normalized.Count; existingIndex++)
            {
                ShaderBindingSlot existing = normalized[existingIndex];
                // Metal (and MSL generally) gives each resource kind its own index space —
                // [[buffer(0)]] and [[texture(0)]] coexist — so physical slots may only collide
                // within the same register class.
                if (GetRegisterClass(existing.Kind, metalPerKindSlots)
                        == GetRegisterClass(candidate.Kind, metalPerKindSlots)
                    && PhysicalBindingsOverlap(existing.PhysicalBindings, candidate.PhysicalBindings,
                        metalPerKindSlots))
                {
                    var summary = new List<string>(source.Count);
                    for (int i = 0; i < source.Count; i++)
                        summary.Add($"'{source[i].Name}' kind={source[i].Kind} slot={source[i].Slot} [{DescribePhysical(source[i].PhysicalBindings)}]");
                    throw new InvalidOperationException(
                        "Slang physical binding " +
                        $"is assigned to both '{existing.Name}' and '{candidate.Name}': " +
                        $"existing=[{DescribePhysical(existing.PhysicalBindings)}], " +
                        $"candidate=[{DescribePhysical(candidate.PhysicalBindings)}], " +
                        $"all=[{string.Join(" | ", summary)}].");
                }
                if (GetRegisterClass(existing.Kind, metalPerKindSlots)
                        == GetRegisterClass(candidate.Kind, metalPerKindSlots)
                    && existing.Space == candidate.Space
                    && (!metalPerKindSlots || (existing.Stages & candidate.Stages) != 0)
                    && RangesOverlap(existing.Slot, existing.Count, candidate.Slot, candidate.Count))
                {
                    throw new InvalidOperationException(
                        $"Slang {GetRegisterClass(candidate.Kind, metalPerKindSlots)} range space{candidate.Space}:{candidate.Slot}[{candidate.Count}] " +
                        $"is assigned to both '{existing.Name}' and '{candidate.Name}'.");
                }
            }
            normalized.Add(candidate);
        }

        normalized.Sort(CompareBindings);
        var buffers = new List<ShaderBindingSlot>();
        var textures = new List<ShaderBindingSlot>();
        var samplers = new List<ShaderBindingSlot>();
        for (int i = 0; i < normalized.Count; i++)
        {
            ShaderBindingSlot binding = normalized[i];
            switch (binding.Kind)
            {
                case ShaderBindingKind.Texture:
                case ShaderBindingKind.StorageTexture:
                    textures.Add(binding);
                    break;
                case ShaderBindingKind.Sampler:
                    samplers.Add(binding);
                    break;
                default:
                    buffers.Add(binding);
                    break;
            }
        }

        return new ShaderBindingLayout
        {
            Buffers = [.. buffers],
            Textures = [.. textures],
            Samplers = [.. samplers],
        };
    }

    private static bool Equivalent(ShaderBindingSlot left, ShaderBindingSlot right)
    {
        if (left.Kind != right.Kind
            || left.Slot != right.Slot
            || left.Space != right.Space
            || left.Count != right.Count
            || left.ResourceShape != right.ResourceShape
            || left.ByteSize != right.ByteSize
            || left.UniformFields.Length != right.UniformFields.Length)
        {
            return false;
        }

        for (int i = 0; i < left.UniformFields.Length; i++)
        {
            if (left.UniformFields[i] != right.UniformFields[i])
                return false;
        }
        return true;
    }

    private static bool PhysicalBindingsOverlap(
        ShaderPhysicalBinding[] left,
        ShaderPhysicalBinding[] right,
        bool stageScoped)
    {
        for (int leftIndex = 0; leftIndex < left.Length; leftIndex++)
        {
            for (int rightIndex = 0; rightIndex < right.Length; rightIndex++)
            {
                if (left[leftIndex].Binding == right[rightIndex].Binding
                    && left[leftIndex].Space == right[rightIndex].Space
                    && (!stageScoped || (left[leftIndex].Stages & right[rightIndex].Stages) != 0))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string DescribePhysical(ShaderPhysicalBinding[] bindings)
    {
        var parts = new string[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
            parts[i] = $"slot={bindings[i].Binding} space={bindings[i].Space} stages={bindings[i].Stages}";
        return string.Join("; ", parts);
    }

    private static int GetOccupiedSize(TypeLayoutReflection type)
    {
        uint size = type.GetSize(ParameterCategory.Uniform);
        uint stride = type.GetStride(ParameterCategory.Uniform);
        return checked((int)Math.Max(size, stride));
    }

    private static void Add(List<ShaderBindingSlot> destination, ShaderBindingSlot[] source)
    {
        for (int i = 0; i < source.Length; i++)
            destination.Add(source[i]);
    }

    private static int FindByName(List<ShaderBindingSlot> bindings, string name)
    {
        for (int i = 0; i < bindings.Count; i++)
        {
            if (bindings[i].Name == name)
                return i;
        }
        return -1;
    }

    private static bool RangesOverlap(int leftStart, int leftCount, int rightStart, int rightCount) =>
        leftStart < rightStart + rightCount && rightStart < leftStart + leftCount;

    private static char GetRegisterClass(ShaderBindingKind kind, bool metalPerKindSlots) =>
        metalPerKindSlots && (kind is ShaderBindingKind.Buffer or ShaderBindingKind.StructuredBuffer
            or ShaderBindingKind.StorageBuffer)
            ? 'b'
            : kind switch
    {
        ShaderBindingKind.Buffer => 'b',
        ShaderBindingKind.Texture or ShaderBindingKind.StructuredBuffer => 't',
        ShaderBindingKind.Sampler => 's',
        ShaderBindingKind.StorageTexture or ShaderBindingKind.StorageBuffer => 'u',
        _ => '?',
    };

    private static int CompareBindings(ShaderBindingSlot left, ShaderBindingSlot right)
    {
        int bySpace = left.Space.CompareTo(right.Space);
        if (bySpace != 0)
            return bySpace;
        int bySlot = left.Slot.CompareTo(right.Slot);
        if (bySlot != 0)
            return bySlot;
        int byKind = left.Kind.CompareTo(right.Kind);
        return byKind != 0 ? byKind : string.CompareOrdinal(left.Name, right.Name);
    }

    private static string Describe(ShaderBindingSlot binding) =>
        $"{binding.Kind} space{binding.Space}:{binding.Slot}[{binding.Count}], " +
        $"physicalBindings={binding.PhysicalBindings.Length}, " +
        $"shape={binding.ResourceShape}, bytes={binding.ByteSize}, fields={binding.UniformFields.Length}";
}

internal readonly record struct SlangStageReflection(
    ShaderBindingLayout BindingLayout,
    ShaderVertexInputLayout VertexInputLayout);
