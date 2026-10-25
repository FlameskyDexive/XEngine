// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Runtime.Resources;

/// <summary>
/// GPU-free math behind the mesh morph-target (blend shape) system: how blend-shape frames tile
/// into the morph delta textures, the texel fill, and the per-renderer weights → active-layer
/// resolution consumed by <c>SkinnedMeshRenderer.PrepareBlendShapes</c>. Kept free of Graphics
/// calls so the layout math is unit-testable without a device.
/// </summary>
internal static class MorphLayout
{
    /// <summary>
    /// Immutable result of <see cref="ComputePlan"/>. Texel layout: each <see cref="BlendShapeFrame"/>
    /// occupies one "layer" of <see cref="VertexCount"/> texels addressed linearly as
    /// <c>idx = layer * VertexCount + vertexID</c>, tiled into a 2D texture of width
    /// <see cref="TexWidth"/> (2D address: <c>(idx % TexWidth, idx / TexWidth)</c>).
    /// </summary>
    internal readonly struct Plan
    {
        public Plan(int[] layerOffsets, int layerCount, int vertexCount, int texWidth, int texHeight,
                    bool anyNormals, bool anyTangents, bool overflows)
        {
            LayerOffsets = layerOffsets;
            LayerCount = layerCount;
            VertexCount = vertexCount;
            TexWidth = texWidth;
            TexHeight = texHeight;
            AnyNormals = anyNormals;
            AnyTangents = anyTangents;
            Overflows = overflows;
        }

        /// <summary>Per-shape starting layer index (parallel to the BlendShape array).</summary>
        public int[] LayerOffsets { get; }
        public int LayerCount { get; }
        public int VertexCount { get; }
        public int TexWidth { get; }
        public int TexHeight { get; }
        /// <summary>At least one frame carries normal deltas (a normal texture must be built).</summary>
        public bool AnyNormals { get; }
        /// <summary>At least one frame carries tangent deltas (a tangent texture must be built).</summary>
        public bool AnyTangents { get; }
        /// <summary>Tiled data exceeds the device max texture size; morphs must stay disabled.</summary>
        public bool Overflows { get; }

        public int TexelIndex(int layer, int vertexId) => layer * VertexCount + vertexId;

        public (int X, int Y) TexelCoord(int linearIndex)
            => (linearIndex % TexWidth, linearIndex / TexWidth);
    }

    /// <summary>
    /// Flattens frames into layers (per-shape offsets assigned in shape order) and derives the
    /// texture geometry: width is capped at <paramref name="maxTextureSize"/>, height rounds up,
    /// and <see cref="Plan.Overflows"/> marks data the device cannot tile.
    /// </summary>
    public static Plan ComputePlan(BlendShape[] shapes, int vertexCount, int maxTextureSize)
    {
        var offsets = new int[shapes.Length];
        int layers = 0;
        bool anyNormals = false, anyTangents = false;
        for (int s = 0; s < shapes.Length; s++)
        {
            offsets[s] = layers;
            var frames = shapes[s].Frames;
            layers += frames.Length;
            foreach (var f in frames)
            {
                if (f.DeltaNormals != null) anyNormals = true;
                if (f.DeltaTangents != null) anyTangents = true;
            }
        }

        int width = vertexCount > 0 ? Math.Min(vertexCount, maxTextureSize) : 0;
        long total = (long)layers * vertexCount;
        int height = width > 0 ? (int)((total + width - 1) / width) : 0;
        bool overflows = layers > 0 && height > maxTextureSize;

        return new Plan(offsets, layers, vertexCount, width, height, anyNormals, anyTangents, overflows);
    }

    /// <summary>
    /// Fills the RGBA32F texel arrays: position deltas always, normal/tangent arrays only when at
    /// least one frame carries them (null otherwise). Texels past a frame's delta-array end and the
    /// tail of the last texture row stay zero. W is reserved (always 0).
    /// </summary>
    public static (Float4[] Position, Float4[]? Normals, Float4[]? Tangents) BuildArrays(BlendShape[] shapes, Plan plan)
    {
        int texels = plan.TexWidth * plan.TexHeight;
        var pos = new Float4[texels];
        var nrm = plan.AnyNormals ? new Float4[texels] : null;
        var tan = plan.AnyTangents ? new Float4[texels] : null;
        int vtx = plan.VertexCount;

        for (int s = 0; s < shapes.Length; s++)
        {
            var frames = shapes[s].Frames;
            for (int fi = 0; fi < frames.Length; fi++)
            {
                var f = frames[fi];
                int baseIdx = plan.TexelIndex(plan.LayerOffsets[s] + fi, 0);

                var dv = f.DeltaVertices;
                int count = Math.Min(vtx, dv.Length);
                for (int v = 0; v < count; v++)
                    pos[baseIdx + v] = new Float4(dv[v].X, dv[v].Y, dv[v].Z, 0f);

                if (nrm != null && f.DeltaNormals != null)
                {
                    var dn = f.DeltaNormals;
                    int cn = Math.Min(vtx, dn.Length);
                    for (int v = 0; v < cn; v++)
                        nrm[baseIdx + v] = new Float4(dn[v].X, dn[v].Y, dn[v].Z, 0f);
                }
                if (tan != null && f.DeltaTangents != null)
                {
                    var dt = f.DeltaTangents;
                    int ct = Math.Min(vtx, dt.Length);
                    for (int v = 0; v < ct; v++)
                        tan[baseIdx + v] = new Float4(dt[v].X, dt[v].Y, dt[v].Z, 0f);
                }
            }
        }

        return (pos, nrm, tan);
    }

    /// <summary>
    /// Resolves per-shape weights (0-100, overshooting allowed) into active
    /// <c>(layerIndex, normalizedWeight)</c> texel pairs for the per-renderer weight texture. The
    /// shader adds <c>delta[layerIndex] * weight</c> per entry, so the contract is:
    /// single-frame shapes scale by <c>1/frameWeight</c>; below the first frame scales down;
    /// at/above the last frame is clamped to full; between two frames the neighbouring frames
    /// interpolate. Entries with |weight| &lt; 1e-4 are skipped; <paramref name="results"/> is
    /// cleared first (it is the caller's reused per-frame buffer).
    /// </summary>
    public static void ResolveLayers(BlendShape[] shapes, float[]? weights, int[] layerOffsets, List<Float4> results)
    {
        results.Clear();
        if (weights == null || shapes.Length == 0)
            return;

        for (int s = 0; s < shapes.Length; s++)
        {
            float w = s < weights.Length ? weights[s] : 0f;
            if (MathF.Abs(w) < 1e-4f) continue;

            var frames = shapes[s].Frames;
            int frameCount = frames.Length;
            if (frameCount <= 0) continue;
            int shapeOffset = s < layerOffsets.Length ? layerOffsets[s] : 0;

            if (frameCount == 1)
            {
                float fw = frames[0].Weight;
                AddLayer(results, shapeOffset, fw != 0f ? w / fw : 0f);
                continue;
            }

            float first = frames[0].Weight;
            float last = frames[frameCount - 1].Weight;
            if (w <= first)
            {
                AddLayer(results, shapeOffset, first != 0f ? w / first : 0f);
            }
            else if (w >= last)
            {
                AddLayer(results, shapeOffset + frameCount - 1, 1f);
            }
            else
            {
                int i = 0;
                while (i < frameCount - 1 && frames[i + 1].Weight <= w) i++;
                float a = frames[i].Weight;
                float b = frames[i + 1].Weight;
                float t = b > a ? (w - a) / (b - a) : 0f;
                AddLayer(results, shapeOffset + i, 1f - t);
                AddLayer(results, shapeOffset + i + 1, t);
            }
        }
    }

    private static void AddLayer(List<Float4> results, int layer, float weight)
    {
        if (MathF.Abs(weight) < 1e-5f) return;
        results.Add(new Float4(layer, weight, 0f, 0f));
    }
}
