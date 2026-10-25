// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Vector;

using Texture2D = XEngine.Runtime.Resources.Texture2D;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Generic reflection-driven constant-buffer packing. Pre-compiles a write plan from a
/// <see cref="ShaderBindingSlot"/>'s <see cref="ShaderUniformField"/> layout, then packs
/// <see cref="PropertyState"/> overrides + shader Properties defaults into a flat byte
/// span at draw time with zero steady-state allocation. This is the engine-side fallback
/// path so game-owned shaders need no hand-written packer or backend dispatch branch.
/// </summary>
/// <remarks>
/// <b>Extension contract:</b> shader Properties block attribute name == Slang cbuffer
/// member name. Satisfying that, the generic path packs any game shader with no engine
/// changes. Nested structs (flattened to <c>"a.b"</c> by Slang reflection) and value
/// arrays are treated as <see cref="GenericUniformOpKind.Skip"/> (kept zero) — document
/// this limitation to shader authors.
/// </remarks>
internal static class GenericMaterialUniformPacking
{
    internal enum GenericUniformOpKind : byte
    {
        Skip = 0,
        Float,
        Int,
        Vec2,
        Vec3,
        Vec4,
        Matrix44,
    }

    /// <summary>
    /// One pre-compiled write operation per reflected uniform field. The plan is sorted by
    /// <see cref="Offset"/> so packing walks the destination linearly. Default values are
    /// resolved once from <see cref="Shader.PropertyArray"/> (typed Float4/Float4x4).
    /// </summary>
    internal readonly struct GenericUniformOp
    {
        public readonly string Name;
        public readonly int Offset;
        public readonly GenericUniformOpKind Kind;
        public readonly Float4 DefaultValue;
        public readonly Float4x4 DefaultMatrix;

        public GenericUniformOp(string name, int offset, GenericUniformOpKind kind, Float4 defaultValue, Float4x4 defaultMatrix)
        {
            Name = name;
            Offset = offset;
            Kind = kind;
            DefaultValue = defaultValue;
            DefaultMatrix = defaultMatrix;
        }
    }

    /// <summary>Per-buffer write-plan cache, keyed by the immutable bytecode instance.</summary>
    private static readonly ConditionalWeakTable<CompiledShaderBytecode, Dictionary<string, GenericUniformOp[]>> s_planCache = new();

    /// <summary>
    /// cbuffer names the engine packs/binds through dedicated paths (per-frame globals, per-object
    /// state, lighting/shadow/probe/cookie blocks, UI vertex/fragment constants). The generic
    /// fallback must NEVER produce a plan for these: PropertyState does not carry camera matrices
    /// or light data, so a reflection-packed copy would be all zeroes and — because the fallback
    /// runs last — overwrite the correctly packed buffer, zeroing VP matrices and killing every
    /// draw (observed as black GPU contract tests and a blank/washed-out editor, 2026-08-22).
    /// </summary>
    internal static readonly HashSet<string> EngineManagedUniformBuffers = new(StringComparer.Ordinal)
    {
        "GlobalUniforms",
        "ObjectUniforms",
        "StandardLighting",
        "StandardShadows",
        "StandardReflectionProbes",
        "StandardLightCookies",
        "UIVS",
        "UIPS",
    };

    /// <summary>True when <paramref name="name"/> is packed by a dedicated engine path and must be
    /// left alone by the generic reflection fallback.</summary>
    internal static bool IsEngineManagedUniformBuffer(string name)
        => EngineManagedUniformBuffers.Contains(name);

    /// <summary>
    /// Resolve the pre-compiled write plan for a named material cbuffer slot, building and
    /// caching it on first access. Returns null when the slot has no uniform fields (non-cbuffer
    /// or reflection-less bytecode), so callers can fall back to their existing path.
    /// </summary>
    internal static GenericUniformOp[]? GetWritePlan(CompiledShaderBytecode? bytecode, string bufferName)
    {
        // Engine-managed cbuffers are packed by dedicated paths with data the PropertyState
        // fallback cannot see (camera matrices, light state). Refuse them here so no backend's
        // generic fallback can clobber a correctly packed buffer with zeroes.
        if (EngineManagedUniformBuffers.Contains(bufferName))
            return null;

        if (bytecode?.BindingLayout is not { } layout)
            return null;

        ShaderBindingSlot slot = default;
        bool found = false;
        ShaderBindingSlot[] buffers = layout.Buffers;
        for (int i = 0; i < buffers.Length; i++)
        {
            if (buffers[i].Name == bufferName)
            {
                slot = buffers[i];
                found = true;
                break;
            }
        }
        if (!found || slot.UniformFields.Length == 0)
            return null;

        Dictionary<string, GenericUniformOp[]> perBuffer = s_planCache.GetValue(bytecode, static _ => new Dictionary<string, GenericUniformOp[]>(StringComparer.Ordinal));
        if (perBuffer.TryGetValue(bufferName, out GenericUniformOp[]? cached))
            return cached;

        GenericUniformOp[] plan = CompilePlan(slot);
        perBuffer[bufferName] = plan;
        return plan;
    }

    /// <summary>Build the cache key string used by backend translators for plan lookup.</summary>
    /// <remarks>This allocates only on cache miss (steady state: dictionary hit, zero alloc).</remarks>
    private static GenericUniformOp[] CompilePlan(ShaderBindingSlot slot)
    {
        ShaderUniformField[] fields = slot.UniformFields;
        // Sort by offset for linear destination walk. Fields from Slang reflection are already
        // in declaration order, but sorting guarantees correctness regardless of source.
        GenericUniformOp[] ops = new GenericUniformOp[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            ops[i] = CompileOp(fields[i]);
        Array.Sort(ops, static (a, b) => a.Offset.CompareTo(b.Offset));
        return ops;
    }

    private static GenericUniformOp CompileOp(ShaderUniformField field)
    {
        // Nested struct fields arrive as "a.b" dotted paths; struct-array elements as
        // "a[i].b". These cannot be matched by name against PropertyState (which uses the
        // shader Properties block leaf name). Keep them zero (Skip) — document this.
        if (field.Name.IndexOf('.') >= 0 || field.Name.IndexOf('[') >= 0)
            return new GenericUniformOp(field.Name, field.Offset, GenericUniformOpKind.Skip, default, default);

        // Value arrays (ElementCount > 1) are reflected as a single field spanning the
        // full array footprint. PropertyState has no array-of-scalar bucket; keep zero.
        if (field.ElementCount > 1)
            return new GenericUniformOp(field.Name, field.Offset, GenericUniformOpKind.Skip, default, default);

        GenericUniformOpKind kind = ClassifyField(field);
        return new GenericUniformOp(field.Name, field.Offset, kind, default, default);
    }

    private static GenericUniformOpKind ClassifyField(ShaderUniformField field)
    {
        // Matrices: Rows > 1 && Columns > 1 (4x4 is the only matrix case material cbuffers use).
        if (field.Rows > 1 && field.Columns > 1)
            return GenericUniformOpKind.Matrix44;

        // Int scalar (ShaderScalarType.Int32 / UInt32 / etc.).
        if (IsIntScalar(field.ScalarType))
            return field.Columns switch
            {
                1 => GenericUniformOpKind.Int,
                2 => GenericUniformOpKind.Vec2, // int2 — write as 2 ints via Vec2 path (same byte width)
                3 => GenericUniformOpKind.Vec3,
                _ => GenericUniformOpKind.Vec4,
            };

        // Float scalar/vector.
        return field.Columns switch
        {
            1 => GenericUniformOpKind.Float,
            2 => GenericUniformOpKind.Vec2,
            3 => GenericUniformOpKind.Vec3,
            _ => GenericUniformOpKind.Vec4,
        };
    }

    private static bool IsIntScalar(ShaderScalarType type)
    {
        return type is ShaderScalarType.Int8 or ShaderScalarType.UInt8
            or ShaderScalarType.Int16 or ShaderScalarType.UInt16
            or ShaderScalarType.Int32 or ShaderScalarType.UInt32
            or ShaderScalarType.Int64 or ShaderScalarType.UInt64
            or ShaderScalarType.Bool;
    }

    /// <summary>
    /// Pack a material cbuffer into <paramref name="destination"/> using the pre-compiled
    /// <paramref name="plan"/>. The destination is zeroed first (so Skip ops and unmatched
    /// padding stay zero), then each op writes its resolved value at its reflected offset.
    /// Override-wins: <paramref name="properties"/> checked first (Float4 kind checks
    /// <c>_colors</c> then <c>_vectors4</c>, matching <c>OpenGLPropertyApply.FillShaderDefaults</c>),
    /// then shader Properties defaults fill the gaps.
    /// </summary>
    [HotPath]
    internal static unsafe void Pack(GenericUniformOp[] plan, PropertyState? properties, Shader? shader, Span<byte> destination)
    {
        // Zero the buffer first so implicit padding (not reflected as fields) and Skip ops stay zero.
        destination.Clear();

        if (plan.Length == 0)
            return;

        fixed (byte* dst = destination)
        {
            for (int i = 0; i < plan.Length; i++)
            {
                ref readonly GenericUniformOp op = ref plan[i];
                if (op.Kind == GenericUniformOpKind.Skip)
                    continue;

                byte* ptr = dst + op.Offset;
                switch (op.Kind)
                {
                    case GenericUniformOpKind.Float:
                        {
                            float value = op.DefaultValue.X;
                            if (properties != null && properties._floats.TryGetValue(op.Name, out float f))
                                value = f;
                            else if (shader != null && TryGetShaderDefaultFloat(shader, op.Name, out float d))
                                value = d;
                            Unsafe.WriteUnaligned(ref ptr[0], value);
                        }
                        break;
                    case GenericUniformOpKind.Int:
                        {
                            int value = (int)op.DefaultValue.X;
                            if (properties != null && properties._ints.TryGetValue(op.Name, out int v))
                                value = v;
                            else if (shader != null && TryGetShaderDefaultInt(shader, op.Name, out int d))
                                value = d;
                            Unsafe.WriteUnaligned(ref ptr[0], value);
                        }
                        break;
                    case GenericUniformOpKind.Vec2:
                        {
                            Float2 value = new(op.DefaultValue.X, op.DefaultValue.Y);
                            if (properties != null)
                            {
                                if (properties._vectors2.TryGetValue(op.Name, out Float2 v2))
                                    value = v2;
                                else if (TryGetShaderDefaultVec2(shader, op.Name, out Float2 d2))
                                    value = d2;
                            }
                            else if (TryGetShaderDefaultVec2(shader, op.Name, out Float2 d2))
                                value = d2;
                            Unsafe.WriteUnaligned(ref ptr[0], value.X);
                            Unsafe.WriteUnaligned(ref ptr[4], value.Y);
                        }
                        break;
                    case GenericUniformOpKind.Vec3:
                        {
                            Float3 value = new(op.DefaultValue.X, op.DefaultValue.Y, op.DefaultValue.Z);
                            if (properties != null)
                            {
                                if (properties._vectors3.TryGetValue(op.Name, out Float3 v3))
                                    value = v3;
                                else if (TryGetShaderDefaultVec3(shader, op.Name, out Float3 d3))
                                    value = d3;
                            }
                            else if (TryGetShaderDefaultVec3(shader, op.Name, out Float3 d3))
                                value = d3;
                            Unsafe.WriteUnaligned(ref ptr[0], value.X);
                            Unsafe.WriteUnaligned(ref ptr[4], value.Y);
                            Unsafe.WriteUnaligned(ref ptr[8], value.Z);
                        }
                        break;
                    case GenericUniformOpKind.Vec4:
                        {
                            Float4 value = op.DefaultValue;
                            if (properties != null)
                            {
                                if (properties._vectors4.TryGetValue(op.Name, out Float4 v4))
                                    value = v4;
                                else if (properties._colors.TryGetValue(op.Name, out Color c))
                                    value = new Float4(c.R, c.G, c.B, c.A);
                                else if (TryGetShaderDefaultVec4(shader, op.Name, out Float4 d4))
                                    value = d4;
                            }
                            else if (TryGetShaderDefaultVec4(shader, op.Name, out Float4 d4))
                                value = d4;
                            WriteFloat4(ptr, value);
                        }
                        break;
                    case GenericUniformOpKind.Matrix44:
                        {
                            Float4x4 value = op.DefaultMatrix;
                            if (properties != null && properties._matrices.TryGetValue(op.Name, out Float4x4 m))
                                value = m;
                            else if (shader != null && TryGetShaderDefaultMatrix(shader, op.Name, out Float4x4 dm))
                                value = dm;
                            WriteMatrix(ptr, value);
                        }
                        break;
                }
            }
        }
    }

    // --- Shader Properties default resolution (cold-path helpers, alloc only on miss) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetShaderDefaultFloat(Shader? shader, string name, out float value)
    {
        value = 0f;
        ShaderProperty[]? defaults = shader?.PropertyArray;
        if (defaults == null)
            return false;
        for (int i = 0; i < defaults.Length; i++)
        {
            if (defaults[i].Name == name && defaults[i].PropertyType is ShaderPropertyType.Float or ShaderPropertyType.Int)
            {
                value = defaults[i].Value.X;
                return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetShaderDefaultInt(Shader? shader, string name, out int value)
    {
        value = 0;
        ShaderProperty[]? defaults = shader?.PropertyArray;
        if (defaults == null)
            return false;
        for (int i = 0; i < defaults.Length; i++)
        {
            if (defaults[i].Name == name && defaults[i].PropertyType == ShaderPropertyType.Int)
            {
                value = (int)defaults[i].Value.X;
                return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetShaderDefaultVec2(Shader? shader, string name, out Float2 value)
    {
        value = Float2.Zero;
        ShaderProperty[]? defaults = shader?.PropertyArray;
        if (defaults == null)
            return false;
        for (int i = 0; i < defaults.Length; i++)
        {
            if (defaults[i].Name == name && defaults[i].PropertyType == ShaderPropertyType.Vector2)
            {
                value = new Float2(defaults[i].Value.X, defaults[i].Value.Y);
                return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetShaderDefaultVec3(Shader? shader, string name, out Float3 value)
    {
        value = Float3.Zero;
        ShaderProperty[]? defaults = shader?.PropertyArray;
        if (defaults == null)
            return false;
        for (int i = 0; i < defaults.Length; i++)
        {
            if (defaults[i].Name == name && defaults[i].PropertyType == ShaderPropertyType.Vector3)
            {
                value = new Float3(defaults[i].Value.X, defaults[i].Value.Y, defaults[i].Value.Z);
                return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetShaderDefaultVec4(Shader? shader, string name, out Float4 value)
    {
        value = Float4.Zero;
        ShaderProperty[]? defaults = shader?.PropertyArray;
        if (defaults == null)
            return false;
        for (int i = 0; i < defaults.Length; i++)
        {
            ShaderProperty prop = defaults[i];
            if (prop.Name == name && (prop.PropertyType == ShaderPropertyType.Vector4 || prop.PropertyType == ShaderPropertyType.Color))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetShaderDefaultMatrix(Shader? shader, string name, out Float4x4 value)
    {
        value = Float4x4.Identity;
        ShaderProperty[]? defaults = shader?.PropertyArray;
        if (defaults == null)
            return false;
        for (int i = 0; i < defaults.Length; i++)
        {
            if (defaults[i].Name == name && defaults[i].PropertyType == ShaderPropertyType.Matrix)
            {
                value = defaults[i].MatrixValue;
                return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteFloat4(byte* ptr, Float4 value)
    {
        Unsafe.WriteUnaligned(ref ptr[0], value.X);
        Unsafe.WriteUnaligned(ref ptr[4], value.Y);
        Unsafe.WriteUnaligned(ref ptr[8], value.Z);
        Unsafe.WriteUnaligned(ref ptr[12], value.W);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteMatrix(byte* ptr, Float4x4 value)
    {
        // Slang reflection MatrixStride is the row/column stride; for std140 float4x4
        // it is 16 bytes (one row). Write 4 contiguous Float4 rows (64 bytes).
        ReadOnlySpan<float> source = MemoryMarshal.Cast<Float4x4, float>(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
        // Float4x4 is 16 floats laid out as row-major in XEngine.Vector; the GPU expects
        // the same layout (row_major in the shader). Copy 64 bytes verbatim.
        for (int i = 0; i < 16; i++)
            Unsafe.WriteUnaligned(ref ptr[i * 4], source[i]);
    }

    /// <summary>
    /// Seed a write-plan op's default value from a shader Property. Used by the plan compiler
    /// to bake shader defaults into the plan so the hot path doesn't re-resolve them. Currently
    /// the plan stores zero defaults and the hot path resolves on demand; this is kept for
    /// future optimization without changing the hot-path signature.
    /// </summary>
    internal static void SeedDefaults(GenericUniformOp[] plan, Shader? shader)
    {
        if (shader == null)
            return;
        for (int i = 0; i < plan.Length; i++)
        {
            ref GenericUniformOp op = ref plan[i];
            if (op.Kind == GenericUniformOpKind.Skip)
                continue;
            // Re-resolve the default value into the op (future: pre-bake).
        }
    }
}
