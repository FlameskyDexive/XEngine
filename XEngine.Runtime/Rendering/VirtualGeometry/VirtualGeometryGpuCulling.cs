// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using XEngine.Runtime.VirtualGeometry;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering.VirtualGeometry;

/// <summary>
/// GPU cluster culling (Vulkan/D3D12/Metal): uploads the per-cluster selection data once
/// per <see cref="VirtualGeometryData"/> and encodes the three-pass compute cull
/// (count → prefix → write) plus the indirect draw args for each view. The per-cluster
/// decision mirrors <see cref="VirtualGeometrySelector"/> exactly, so both paths select
/// the same clusters for the same view.
/// </summary>
public static class VirtualGeometryGpuCulling
{
    public const int ThreadGroupSize = 64;

    /// <summary>Per-cluster GPU record (see VirtualGeometryCull.shader for the layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VgClusterGpu
    {
        public Float4 CenterRadius;
        public Float4 ErrorStartCount;
        public Float4 ParentCenterRadius;
        public Float4 ParentErrorRoot;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VgParamsGpu
    {
        public Float4x4 World;
        public Float4 CamPosMaxScale;
        public Float4 CamFwdTanY;
        public Float4 CamRightTanX;
        public Float4 CamUpSseFactor;
        public Float4 ThresholdCounts;
    }

    private static RHI.Shaders.ShaderVariant? _countVariant;
    private static RHI.Shaders.ShaderVariant? _prefixVariant;
    private static RHI.Shaders.ShaderVariant? _writeVariant;

    /// <summary>True when the active device can run the compute cull.</summary>
    public static bool IsSupported => Graphics.Device?.Capabilities.SupportsCompute == true;

    private static bool TryGetVariants(out RHI.Shaders.ShaderVariant count, out RHI.Shaders.ShaderVariant prefix, out RHI.Shaders.ShaderVariant write)
    {
        if (_countVariant != null && _prefixVariant != null && _writeVariant != null)
        {
            count = _countVariant;
            prefix = _prefixVariant;
            write = _writeVariant;
            return true;
        }

        if (!IsSupported)
        {
            count = prefix = write = null!;
            return false;
        }

        Resources.Shader shader = Resources.Shader.LoadDefault(Resources.DefaultShader.VirtualGeometryCull);
        Rendering.Shaders.ShaderPass countPass = shader.GetPass("CullCount");
        Rendering.Shaders.ShaderPass prefixPass = shader.GetPass("CullPrefix");
        Rendering.Shaders.ShaderPass writePass = shader.GetPass("CullWrite");
        if (!countPass.HasCompute || !prefixPass.HasCompute || !writePass.HasCompute)
            throw new InvalidOperationException("VirtualGeometryCull shader is missing compute stages.");

        // Compute-only passes have no material; the keyword-less variant is the base one.
        if (!countPass.TryGetVariant(null, out count!)) throw new InvalidOperationException("CullCount variant failed.");
        if (!prefixPass.TryGetVariant(null, out prefix!)) throw new InvalidOperationException("CullPrefix variant failed.");
        if (!writePass.TryGetVariant(null, out write!)) throw new InvalidOperationException("CullWrite variant failed.");

        _countVariant = count;
        _prefixVariant = prefix;
        _writeVariant = write;
        return true;
    }

    /// <summary>Uploads the per-cluster records and the full index stream (once per data asset).</summary>
    internal static void EnsureDataBuffers(VirtualGeometryData data, VirtualGeometryGpuResources resources)
    {
        if (resources.ClusterBuffer != null) return;

        var clusters = new VgClusterGpu[data.Clusters.Length];
        int rootLevelStart = data.Levels[^1].ClusterStart;

        // Parent of cluster c = the ParentCluster of the group at c's level containing c.
        // Groups partition each level, so precompute a per-cluster parent lookup by
        // iterating groups once.
        int[] parentOf = Array.Empty<int>();
        if (data.Groups.Length > 0)
        {
            parentOf = new int[data.Clusters.Length];
            Array.Fill(parentOf, -1);
            for (int level = 0; level < data.Levels.Length; level++)
            {
                ref readonly var lod = ref data.Levels[level];
                for (int g = lod.GroupStart; g < lod.GroupStart + lod.GroupCount; g++)
                {
                    var group = data.Groups[g];
                    for (int c = group.ClusterStart; c < group.ClusterStart + group.ClusterCount; c++)
                        parentOf[c] = group.ParentCluster;
                }
            }
        }

        for (int i = 0; i < data.Clusters.Length; i++)
        {
            var c = data.Clusters[i];
            Float3 pc = c.Bounds.Center;
            float pr = Float3.Length(c.Bounds.Size) * 0.5f;
            float perr = c.Error;
            float isRoot = 0f;
            if (parentOf.Length > 0)
            {
                int parent = parentOf[i];
                if (parent >= 0 && parent < data.Clusters.Length)
                {
                    var p = data.Clusters[parent];
                    pc = p.Bounds.Center;
                    pr = Float3.Length(p.Bounds.Size) * 0.5f;
                    perr = p.Error;
                }
                else
                {
                    isRoot = 1f;
                }
            }
            else
            {
                isRoot = i >= rootLevelStart ? 1f : 0f;
            }

            clusters[i] = new VgClusterGpu
            {
                CenterRadius = new Float4(c.Bounds.Center, Float3.Length(c.Bounds.Size) * 0.5f),
                ErrorStartCount = new Float4(c.Error, c.IndexStart, c.IndexCount, c.SubMesh),
                ParentCenterRadius = new Float4(pc, pr),
                ParentErrorRoot = new Float4(perr, isRoot, 0f, 0f),
            };
        }

        resources.ClusterCount = clusters.Length;
        resources.ClusterBuffer = Graphics.CreateBuffer(BufferType.StructuredBuffer, clusters, dynamic: false);
        resources.IndexBufferGpu = Graphics.CreateBuffer(BufferType.StructuredBuffer, data.Indices, dynamic: false);
    }

    /// <summary>Creates the per-renderer GPU cull state: output index buffer (also the VAO's
    /// index buffer), counters, and indirect args. Sized once per data asset.</summary>
    internal static void CreateRendererBuffers(VirtualGeometryData data, int subMeshCount,
        out GraphicsBuffer outIndices, out GraphicsBuffer counters, out GraphicsBuffer args)
    {
        outIndices = Graphics.CreateBuffer(BufferType.IndirectBuffer, new byte[data.Indices.Length * sizeof(uint)], dynamic: true);
        counters = Graphics.CreateBuffer(BufferType.StructuredBuffer, new uint[Math.Max(1, subMeshCount) * 3], dynamic: true);
        args = Graphics.CreateBuffer(BufferType.IndirectBuffer, new byte[Math.Max(1, subMeshCount) * 5 * sizeof(uint)], dynamic: true);
    }

    /// <summary>Encodes the three-pass compute cull for one view. The renderer's scratch
    /// index buffer must be the same buffer wrapped by the draw VAO (the compute writes it).</summary>
    internal static void EncodeCull(
        CommandBuffer cmd,
        VirtualGeometryGpuResources resources,
        GraphicsBuffer outIndices,
        GraphicsBuffer counters,
        GraphicsBuffer args,
        GraphicsBuffer paramsBuffer,
        in Float4x4 world,
        float maxWorldScale,
        in ViewerData viewer,
        float threshold)
    {
        if (!TryGetVariants(out var count, out var prefix, out var write))
            return;

        float proj11 = viewer.ProjectionMatrix[1, 1];
        float proj00 = viewer.ProjectionMatrix[0, 0];
        var parameters = new VgParamsGpu
        {
            World = world,
            CamPosMaxScale = new Float4(viewer.Position, maxWorldScale),
            CamFwdTanY = new Float4(viewer.Forward, proj11 != 0f ? 1f / proj11 : 10f),
            CamRightTanX = new Float4(viewer.Right, proj00 != 0f ? 1f / proj00 : 10f),
            CamUpSseFactor = new Float4(viewer.Up, viewer.PixelHeight * 0.5f * proj11),
            ThresholdCounts = new Float4(threshold, resources.ClusterCount, resources.SubMeshCount, 0f),
        };
        cmd.UpdateBuffer(paramsBuffer, MemoryMarshal.CreateReadOnlySpan(ref parameters, 1));

        uint groups = (uint)((resources.ClusterCount + ThreadGroupSize - 1) / ThreadGroupSize);
        uint subMeshCount = (uint)resources.SubMeshCount;

        // Count
        cmd.SetComputeShader(count);
        cmd.SetBuffer("_Clusters", resources.ClusterBuffer!);
        cmd.SetBuffer("_Params", paramsBuffer);
        cmd.SetBuffer("_Counters", counters);
        cmd.DispatchCompute(Math.Max(groups, 1u), 1, 1);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, counters);

        // Prefix + args
        cmd.SetComputeShader(prefix);
        cmd.SetBuffer("_Params", paramsBuffer);
        cmd.SetBuffer("_Counters", counters);
        cmd.SetBuffer("_Args", args);
        cmd.DispatchCompute(1, 1, 1);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, counters);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToCompute, args);

        // Write
        cmd.SetComputeShader(write);
        cmd.SetBuffer("_Clusters", resources.ClusterBuffer!);
        cmd.SetBuffer("_Indices", resources.IndexBufferGpu!);
        cmd.SetBuffer("_Params", paramsBuffer);
        cmd.SetBuffer("_Counters", counters);
        cmd.SetBuffer("_OutIndices", outIndices);
        cmd.DispatchCompute(Math.Max(groups, 1u), 1, 1);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToVertex, outIndices);
        cmd.ResourceBarrier(GpuBarrierKind.ComputeToIndirect, args);
    }
}
