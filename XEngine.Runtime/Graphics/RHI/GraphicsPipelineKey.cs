// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Exact backend pipeline-cache key. Keeps the full raster state instead of relying on
/// a hash-only lookup, so collisions cannot select an incompatible PSO.
/// <para>
/// When the caller supplies the vertex declaration(s), identity is the interned
/// <see cref="VertexFormat.LayoutId"/>s rather than the vertex-array handle — every mesh with
/// an identical declaration then shares one PSO per shader/raster/topology instead of
/// compiling its own. <see cref="VertexArrayHandle"/> stays on the struct purely as the
/// carrier the backend uses to build vertex-input state on a cache miss (PSOs bind vertex
/// buffers at draw time, so any same-layout VAO produces an interchangeable pipeline).
/// Without formats the handle remains part of identity (legacy per-VAO behavior).
/// </para>
/// </summary>
internal readonly struct GraphicsPipelineKey : IEquatable<GraphicsPipelineKey>
{
    public int ShaderVariantId { get; }
    public uint VertexArrayHandle { get; }
    public int VertexLayoutId { get; }
    public int InstanceLayoutId { get; }
    public bool KeyByLayout { get; }
    public Topology Topology { get; }
    public RasterizerState RasterState { get; }
    public bool Index32Bit { get; }

    /// <summary>
    /// Multisample count of the render targets this PSO rasterizes into. A pipeline compiled for
    /// 1× cannot be bound while a 4× target is set, so this must be part of identity — otherwise
    /// the first PSO cached for a shader/raster combination would be reused for both and the
    /// driver would reject (or silently mis-rasterize) the mismatched draw.
    /// </summary>
    public int SampleCount { get; }

    public GraphicsPipelineKey(
        ShaderVariant shaderVariant,
        uint vertexArrayHandle,
        Topology topology,
        in RasterizerState rasterState,
        bool index32Bit,
        VertexFormat? vertexFormat = null,
        VertexFormat? instanceFormat = null,
        int sampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(shaderVariant);

        ShaderVariantId = shaderVariant.Id;
        VertexArrayHandle = vertexArrayHandle;
        VertexLayoutId = vertexFormat?.LayoutId ?? 0;
        InstanceLayoutId = instanceFormat?.LayoutId ?? 0;
        KeyByLayout = vertexFormat != null;
        Topology = topology;
        RasterState = rasterState;
        Index32Bit = index32Bit;
        SampleCount = sampleCount < 1 ? 1 : sampleCount;
    }

    public bool Equals(GraphicsPipelineKey other)
    {
        if (KeyByLayout != other.KeyByLayout)
            return false;
        if (KeyByLayout)
        {
            if (VertexLayoutId != other.VertexLayoutId || InstanceLayoutId != other.InstanceLayoutId)
                return false;
        }
        else if (VertexArrayHandle != other.VertexArrayHandle)
        {
            return false;
        }

        RasterizerState rasterState = RasterState;
        RasterizerState otherRasterState = other.RasterState;
        return ShaderVariantId == other.ShaderVariantId &&
            Topology == other.Topology &&
            Index32Bit == other.Index32Bit &&
            SampleCount == other.SampleCount &&
            RasterEquals(in rasterState, in otherRasterState);
    }

    public override bool Equals(object? obj) => obj is GraphicsPipelineKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(ShaderVariantId);
        if (KeyByLayout)
        {
            hash.Add(VertexLayoutId);
            hash.Add(InstanceLayoutId);
        }
        else
        {
            hash.Add(VertexArrayHandle);
        }
        hash.Add(Topology);
        hash.Add(Index32Bit);
        hash.Add(SampleCount);
        RasterizerState rasterState = RasterState;
        AddRasterHash(ref hash, in rasterState);
        return hash.ToHashCode();
    }

    public static bool operator ==(GraphicsPipelineKey left, GraphicsPipelineKey right) => left.Equals(right);
    public static bool operator !=(GraphicsPipelineKey left, GraphicsPipelineKey right) => !left.Equals(right);

    private static bool RasterEquals(in RasterizerState left, in RasterizerState right) =>
        left.DepthTest == right.DepthTest &&
        left.DepthWrite == right.DepthWrite &&
        left.Depth == right.Depth &&
        left.DoBlend == right.DoBlend &&
        left.BlendSrc == right.BlendSrc &&
        left.BlendDst == right.BlendDst &&
        left.Blend == right.Blend &&
        left.ColorMask == right.ColorMask &&
        left.CullFace == right.CullFace &&
        left.Winding == right.Winding &&
        left.FillMode == right.FillMode &&
        left.StencilEnabled == right.StencilEnabled &&
        left.StencilFunc == right.StencilFunc &&
        left.StencilRef == right.StencilRef &&
        left.StencilReadMask == right.StencilReadMask &&
        left.StencilWriteMask == right.StencilWriteMask &&
        left.StencilPassOp == right.StencilPassOp &&
        left.StencilFailOp == right.StencilFailOp &&
        left.StencilZFailOp == right.StencilZFailOp;

    private static void AddRasterHash(ref HashCode hash, in RasterizerState state)
    {
        hash.Add(state.DepthTest);
        hash.Add(state.DepthWrite);
        hash.Add(state.Depth);
        hash.Add(state.DoBlend);
        hash.Add(state.BlendSrc);
        hash.Add(state.BlendDst);
        hash.Add(state.Blend);
        hash.Add(state.ColorMask);
        hash.Add(state.CullFace);
        hash.Add(state.Winding);
        hash.Add(state.FillMode);
        hash.Add(state.StencilEnabled);
        hash.Add(state.StencilFunc);
        hash.Add(state.StencilRef);
        hash.Add(state.StencilReadMask);
        hash.Add(state.StencilWriteMask);
        hash.Add(state.StencilPassOp);
        hash.Add(state.StencilFailOp);
        hash.Add(state.StencilZFailOp);
    }
}
