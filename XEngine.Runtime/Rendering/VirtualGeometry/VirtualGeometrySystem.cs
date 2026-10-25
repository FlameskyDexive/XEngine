// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Async;
using XEngine.Runtime.VirtualGeometry;
using XEngine.Vector;

using static XEngine.Runtime.VertexFormat;

namespace XEngine.Runtime.Rendering.VirtualGeometry;

/// <summary>
/// GPU resources shared by every renderer using one <see cref="VirtualGeometryData"/>: a
/// single packed interleaved vertex buffer (standard <see cref="VertexFormat"/> semantics so
/// existing shaders render virtualized meshes unchanged). Index data lives in per-renderer
/// scratch buffers filled by cluster selection each frame.
/// </summary>
public sealed class VirtualGeometryGpuResources
{
    /// <summary>Interleaved vertex buffer covering every level's clusters.</summary>
    public GraphicsBuffer VertexBuffer = null!;
    public VertexFormat Layout = null!;
    public int VertexCount;

    // Keyword mirrors of Mesh.Has* for material variant selection.
    public bool HasNormals;
    public bool HasTangents;
    public bool HasUV;
    public bool HasUV2;
    public bool HasColors;

    public long SizeBytes => (long)VertexCount * Layout.Size;

    // ── GPU culling state (uploaded lazily by VirtualGeometryGpuCulling, compute backends only) ──
    public int SubMeshCount;
    public int ClusterCount;
    public GraphicsBuffer? ClusterBuffer;
    public GraphicsBuffer? IndexBufferGpu;
}

/// <summary>
/// Uploads and manages <see cref="VirtualGeometryGpuResources"/> per data asset: async
/// upload off the frame loop (blob interleave + buffer creation on the thread pool), LRU
/// retention after the last renderer releases, and a byte budget enforced per frame.
/// </summary>
public static class VirtualGeometrySystem
{
    /// <summary>GPU byte budget for retained virtual geometry vertex buffers.</summary>
    public static long BudgetBytes = 512L * 1024 * 1024;

    /// <summary>Entries unused for this many frames become evictable under budget pressure.</summary>
    public const int IdleGraceFrames = 120;

    internal sealed class Entry
    {
        public VirtualGeometryGpuResources Resources = null!;
        public int RefCount;
        public long LastUseFrame;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<int, Entry> _entries = new();
    private static readonly Dictionary<int, XTask<VirtualGeometryGpuResources>> _pending = new();
    private static long _frameCounter;

    /// <summary>
    /// Acquires resources, uploading synchronously. The blob interleave for large meshes can
    /// take tens of milliseconds — prefer <see cref="AcquireAsync"/> from frame code.
    /// </summary>
    public static VirtualGeometryGpuResources Acquire(VirtualGeometryData data)
    {
        VirtualGeometryGpuResources resources = GetOrUpload(data);
        lock (_gate)
        {
            _entries[data.InstanceID].RefCount++;
            _entries[data.InstanceID].LastUseFrame = _frameCounter;
        }
        return resources;
    }

    /// <summary>
    /// Acquires resources, building the interleaved vertex blob and creating the GPU buffer
    /// on the thread pool. Completed results are served synchronously; concurrent calls for
    /// the same asset share one task.
    /// </summary>
    public static XTask<VirtualGeometryGpuResources> AcquireAsync(VirtualGeometryData data)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(data.InstanceID, out var entry))
            {
                entry.RefCount++;
                entry.LastUseFrame = _frameCounter;
                return XTask.FromResult(entry.Resources);
            }

            if (_pending.TryGetValue(data.InstanceID, out var pending))
                return pending;

            XTask<VirtualGeometryGpuResources> task = UploadAsync(data);
            _pending[data.InstanceID] = task;
            return task;
        }
    }

    private static async XTask<VirtualGeometryGpuResources> UploadAsync(VirtualGeometryData data)
    {
        await XTask.SwitchToThreadPool();
        VirtualGeometryGpuResources resources = BuildResources(data);
        lock (_gate)
        {
            _pending.Remove(data.InstanceID);
            if (!_entries.TryGetValue(data.InstanceID, out var entry))
            {
                entry = new Entry { Resources = resources, RefCount = 0 };
                _entries[data.InstanceID] = entry;
            }
            entry.RefCount++;
            entry.LastUseFrame = _frameCounter;
            return resources;
        }
    }

    private static VirtualGeometryGpuResources GetOrUpload(VirtualGeometryData data)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(data.InstanceID, out var entry))
                return entry.Resources;
        }

        var resources = BuildResources(data);
        lock (_gate)
        {
            if (!_entries.TryGetValue(data.InstanceID, out var entry))
            {
                entry = new Entry { Resources = resources };
                _entries[data.InstanceID] = entry;
            }
            return entry.Resources;
        }
    }

    public static void Release(VirtualGeometryData data)
    {
        int key = data.InstanceID;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;

            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                entry.RefCount = 0;
                entry.LastUseFrame = _frameCounter;
                // Keep the GPU resources resident for a grace period (cheap re-enable);
                // the per-frame budget tick evicts idle entries under pressure.
            }
        }
    }

    /// <summary>Per-frame maintenance: updates usage frames and evicts idle entries over budget.</summary>
    public static void FrameTick()
    {
        List<Entry>? evict = null;
        long residentBytes;
        lock (_gate)
        {
            _frameCounter++;
            residentBytes = 0;
            foreach (var kv in _entries)
                residentBytes += kv.Value.Resources.SizeBytes;

            if (residentBytes > BudgetBytes)
                evict = SelectEvictionsLocked(_frameCounter);
        }

        RenderStats.SetVirtualGeometryResidentBytes(residentBytes);
        if (evict == null) return;

        for (int i = 0; i < evict.Count; i++)
        {
            Graphics.DeferDispose(evict[i].Resources.VertexBuffer);
            if (evict[i].Resources.ClusterBuffer != null) Graphics.DeferDispose(evict[i].Resources.ClusterBuffer!);
            if (evict[i].Resources.IndexBufferGpu != null) Graphics.DeferDispose(evict[i].Resources.IndexBufferGpu!);
        }
    }

    /// <summary>Byte budget policy, isolated for testing: LRU among idle entries (no live
    /// refcount) past the grace window until the budget is met.</summary>
    internal static List<(int Key, Entry Value)> SelectEvictions(Dictionary<int, Entry> entries, long budgetBytes, long currentFrame, int graceFrames)
    {
        var result = new List<(int, Entry)>();
        long total = 0;
        foreach (var kv in entries) total += kv.Value.Resources.SizeBytes;
        if (total <= budgetBytes) return result;

        // Candidates: unreferenced and idle past the grace window, least recently used first.
        var candidates = new List<(long LastUse, int Key, Entry Value)>();
        foreach (var kv in entries)
        {
            var e = kv.Value;
            if (e.RefCount > 0) continue;
            if (currentFrame - e.LastUseFrame < graceFrames) continue;
            candidates.Add((e.LastUseFrame, kv.Key, e));
        }
        candidates.Sort(static (a, b) => a.LastUse.CompareTo(b.LastUse));

        for (int i = 0; i < candidates.Count && total > budgetBytes; i++)
        {
            total -= candidates[i].Value.Resources.SizeBytes;
            result.Add((candidates[i].Key, candidates[i].Value));
        }
        return result;
    }

    private static List<Entry>? SelectEvictionsLocked(long currentFrame)
    {
        var evictions = SelectEvictions(_entries, BudgetBytes, currentFrame, IdleGraceFrames);
        if (evictions.Count == 0) return null;

        for (int i = 0; i < evictions.Count; i++)
            _entries.Remove(evictions[i].Key);
        return evictions.ConvertAll(e => e.Value);
    }

    /// <summary>Bytes currently resident in virtual geometry vertex buffers.</summary>
    public static long ResidentBytes()
    {
        lock (_gate)
        {
            long total = 0;
            foreach (var kv in _entries)
                total += kv.Value.Resources.SizeBytes;
            return total;
        }
    }

    private static VirtualGeometryGpuResources BuildResources(VirtualGeometryData data)
    {
        var resources = new VirtualGeometryGpuResources
        {
            HasNormals = data.HasNormals,
            HasTangents = data.HasTangents,
            HasUV = data.HasUV,
            HasUV2 = data.HasUV2,
            HasColors = data.HasColors,
            VertexCount = data.Positions.Length,
            SubMeshCount = Math.Max(1, data.SubMeshCount),
        };

        // Element order matches Mesh.GetVertexLayout so existing vertex shaders bind unchanged.
        List<Element> elements = [new Element(VertexSemantic.Position, VertexType.Float, 3)];
        if (resources.HasUV) elements.Add(new Element(VertexSemantic.TexCoord0, VertexType.Float, 2));
        if (resources.HasUV2) elements.Add(new Element(VertexSemantic.TexCoord1, VertexType.Float, 2));
        if (resources.HasNormals) elements.Add(new Element(VertexSemantic.Normal, VertexType.Float, 3, 0, true));
        if (resources.HasColors) elements.Add(new Element(VertexSemantic.Color, VertexType.Float, 4));
        if (resources.HasTangents) elements.Add(new Element(VertexSemantic.Tangent, VertexType.Float, 4, 0, true));
        resources.Layout = new VertexFormat([.. elements]);

        byte[] blob = BuildVertexBlob(data, resources);
        resources.VertexBuffer = Graphics.CreateBuffer(BufferType.VertexBuffer, blob, dynamic: false);
        return resources;
    }

    private static byte[] BuildVertexBlob(VirtualGeometryData data, VirtualGeometryGpuResources resources)
    {
        int stride = resources.Layout.Size;
        byte[] blob = new byte[stride * data.Positions.Length];
        int count = data.Positions.Length;

        for (int i = 0; i < count; i++)
        {
            int o = i * stride;
            WriteFloat3(blob, ref o, data.Positions[i]);
            if (resources.HasUV) WriteFloat2(blob, ref o, data.UV![i]);
            if (resources.HasUV2) WriteFloat2(blob, ref o, data.UV2![i]);
            if (resources.HasNormals) WriteFloat3(blob, ref o, data.Normals![i]);
            if (resources.HasColors)
            {
                var c = data.Colors![i];
                WriteFloat(blob, ref o, c.R);
                WriteFloat(blob, ref o, c.G);
                WriteFloat(blob, ref o, c.B);
                WriteFloat(blob, ref o, c.A);
            }
            if (resources.HasTangents)
            {
                var t = data.Tangents![i];
                WriteFloat(blob, ref o, t.X);
                WriteFloat(blob, ref o, t.Y);
                WriteFloat(blob, ref o, t.Z);
                WriteFloat(blob, ref o, t.W);
            }
        }
        return blob;
    }

    private static void WriteFloat(byte[] blob, ref int offset, float value)
    {
        BitConverter.TryWriteBytes(blob.AsSpan(offset), value);
        offset += 4;
    }

    private static void WriteFloat2(byte[] blob, ref int offset, in Float2 value)
    {
        WriteFloat(blob, ref offset, value.X);
        WriteFloat(blob, ref offset, value.Y);
    }

    private static void WriteFloat3(byte[] blob, ref int offset, in Float3 value)
    {
        WriteFloat(blob, ref offset, value.X);
        WriteFloat(blob, ref offset, value.Y);
        WriteFloat(blob, ref offset, value.Z);
    }
}
