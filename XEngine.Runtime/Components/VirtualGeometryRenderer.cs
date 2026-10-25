// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

using XEngine.Async;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.VirtualGeometry;
using XEngine.Runtime.Resources;
using XEngine.Runtime.VirtualGeometry;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Renders a mesh through its baked <see cref="VirtualGeometryData"/> sub-asset: per-view
/// cluster selection by screen-space error (no manual LODs), one packed scratch index buffer
/// update and one draw per submesh per frame. Works on every graphics backend.
/// </summary>
/// <remarks>
/// The feature is generated at import when the mesh importer's "Virtual Geometry" settings
/// are enabled; this renderer resolves the derived sub-asset
/// <c>{meshName}_virtualGeometry</c> automatically. Falls back to nothing (invisible) while
/// the feature streams in.
/// </remarks>
[AddComponentMenu("Rendering/Virtual Geometry Renderer")]
[ComponentIcon("\uf1b2")] // Cube
public class VirtualGeometryRenderer : MonoBehaviour
{
    public AssetRef<Mesh> Mesh;

    /// <summary>Materials array, one per submesh (index 0 fallback like MeshRenderer).</summary>
    public List<AssetRef<Material>> Materials = new();

    /// <summary>Which rendering layers this renderer belongs to.</summary>
    public RenderingLayerMask RenderingLayers = RenderingLayerMask.Everything;

    /// <summary>Maximum screen-space error in pixels a rendered cluster may deviate by.</summary>
    [Range(0.25f, 8f)]
    public float PixelError = 1f;

    /// <summary>Shadow passes relax the error threshold by this factor (coarse shadow proxy).</summary>
    [Range(1f, 32f)]
    public float ShadowPixelErrorScale = 4f;

    /// <summary>Prefer the GPU compute cull on backends that support compute (Vulkan/D3D12/Metal);
    /// other backends always use the CPU selector.</summary>
    public bool UseGpuCulling = true;

    // ── Runtime state (all cached across frames; steady path is allocation-free) ──
    private Guid _meshGuid;
    private AssetRef<VirtualGeometryData> _dataRef;
    private VirtualGeometryData? _data;
    private VirtualGeometryGpuResources? _resources;
    private GraphicsBuffer? _scratchIndex;
    private GraphicsVertexArray? _scratchVao;
    private uint[] _staging = Array.Empty<uint>();
    private readonly List<int> _visibleClusters = new(256);
    private VgSubMeshRange[] _ranges = Array.Empty<VgSubMeshRange>();
    private VirtualGeometryRenderable[]? _renderables;
    private PropertyState[]? _propCache;
    private bool _acquired;
    private XTask<VirtualGeometryGpuResources>? _acquireTask;

    // ── GPU culling state (per renderer: output indices double as the draw VAO's index buffer) ──
    private GraphicsBuffer? _gpuOutIndices;
    private GraphicsBuffer? _gpuCounters;
    private GraphicsBuffer? _gpuArgs;
    private GraphicsBuffer? _gpuParams;
    private GraphicsVertexArray? _gpuVao;
    private bool _gpuCullReady;

    // Selection fingerprint: reselect only when the view, error threshold, or pose changed.
    private Float3 _selCamPos;
    private Float3 _selCamFwd;
    private uint _selPixelHeight;
    private float _selProj11;
    private float _selThreshold;
    private ulong _selTransformVersion = ulong.MaxValue;
    private bool _hasSelection;

    /// <summary>Resolved feature data (null until the sub-asset loads).</summary>
    public VirtualGeometryData? Data => _data;

    /// <summary>Clusters selected by the most recent selection (debug/stats).</summary>
    public int LastVisibleClusters { get; private set; }

    /// <summary>Triangles selected by the most recent selection (debug/stats).</summary>
    public int LastVisibleTriangles { get; private set; }

    public override void OnDisable()
    {
        ReleaseGpuResources();
        base.OnDisable();
    }

    internal static Guid DeriveVirtualGeometryGuid(Guid meshGuid, string meshName)
        => DeriveSubAssetGuid(meshGuid, $"{meshName}_virtualGeometry");

    // Mirrors the editor's AssetEntry.DeriveSubAssetGuid so players resolve the feature
    // sub-asset from the mesh reference without editor assemblies.
    internal static Guid DeriveSubAssetGuid(Guid parentGuid, string subAssetName)
    {
        byte[] parentBytes = parentGuid.ToByteArray();
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(subAssetName);
        byte[] combined = new byte[parentBytes.Length + nameBytes.Length];
        parentBytes.CopyTo(combined, 0);
        nameBytes.CopyTo(combined, parentBytes.Length);
        byte[] hash = SHA256.HashData(combined);
        return new Guid(hash.AsSpan(0, 16));
    }

    internal bool ResolveData()
    {
        var mesh = Mesh.Res;
        if (mesh == null) return false;

        Guid meshGuid = Mesh.AssetID;
        if (meshGuid != _meshGuid)
        {
            ReleaseGpuResources();
            _meshGuid = meshGuid;
            _dataRef = new AssetRef<VirtualGeometryData>(DeriveVirtualGeometryGuid(meshGuid, mesh.Name ?? "Mesh"));
            _data = null;
        }

        if (_data == null)
        {
            _data = _dataRef.Res;
            if (_data == null) return false;
        }
        return true;
    }

    internal bool EnsureGpuResources()
    {
        if (_data == null || !_acquired)
        {
            if (_data == null) return false;

            // Upload (blob interleave + buffer creation) runs on the thread pool; the
            // renderer pops in once the task completes.
            XTask<VirtualGeometryGpuResources> task = _acquireTask ??= VirtualGeometrySystem.AcquireAsync(_data);
            if (!task.IsCompleted)
                return false;

            _resources = task.GetAwaiter().GetResult();
            _acquireTask = null;
            _acquired = true;
            _hasSelection = false;

            if (_staging.Length < _data.Indices.Length)
                _staging = new uint[_data.Indices.Length];

            if (_ranges.Length < Math.Max(1, _data.SubMeshCount))
                _ranges = new VgSubMeshRange[Math.Max(1, _data.SubMeshCount)];

            if (_scratchIndex == null)
            {
                _scratchIndex = Graphics.CreateBuffer(BufferType.ElementsBuffer, new byte[_data.Indices.Length * sizeof(uint)], dynamic: true);
                _scratchVao = Graphics.CreateVertexArray(_resources.Layout, _resources.VertexBuffer, _scratchIndex);
            }

            if (UseGpuCulling && VirtualGeometryGpuCulling.IsSupported)
            {
                VirtualGeometryGpuCulling.EnsureDataBuffers(_data, _resources);
                if (_gpuOutIndices == null)
                {
                    VirtualGeometryGpuCulling.CreateRendererBuffers(_data, Math.Max(1, _data.SubMeshCount),
                        out _gpuOutIndices, out _gpuCounters, out _gpuArgs);
                    _gpuParams = Graphics.CreateBuffer(BufferType.StructuredBuffer,
                        new byte[System.Runtime.InteropServices.Marshal.SizeOf<VirtualGeometryGpuCulling.VgParamsGpu>()], dynamic: true);
                    _gpuVao = Graphics.CreateVertexArray(_resources.Layout, _resources.VertexBuffer, _gpuOutIndices);
                    _gpuCullReady = true;
                }
            }
        }
        return _resources != null;
    }

    internal void ReleaseGpuResources()
    {
        if (_acquired && _data != null)
            VirtualGeometrySystem.Release(_data);
        _acquireTask = null; // pending upload result is simply not adopted
        _acquired = false;
        _resources = null;
        _data = null;
        _hasSelection = false;
        if (_scratchIndex != null)
        {
            Graphics.DeferDispose(_scratchIndex);
            Graphics.DeferDispose(_scratchVao!);
            _scratchIndex = null;
            _scratchVao = null;
        }
        if (_gpuOutIndices != null)
        {
            Graphics.DeferDispose(_gpuOutIndices);
            Graphics.DeferDispose(_gpuCounters!);
            Graphics.DeferDispose(_gpuArgs!);
            Graphics.DeferDispose(_gpuParams!);
            Graphics.DeferDispose(_gpuVao!);
            _gpuOutIndices = null;
            _gpuCullReady = false;
        }
    }

    /// <summary>True when this renderer draws through the compute cull this frame.</summary>
    internal bool GpuCullingActive => _gpuCullReady && UseGpuCulling && VirtualGeometryGpuCulling.IsSupported;

    internal GraphicsBuffer? GpuOutIndices => _gpuOutIndices;
    internal GraphicsBuffer? GpuCounters => _gpuCounters;
    internal GraphicsBuffer? GpuArgs => _gpuArgs;
    internal GraphicsBuffer? GpuParams => _gpuParams;
    internal GraphicsVertexArray? GpuVao => _gpuVao;

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        if (!ResolveData() || !EnsureGpuResources()) return;
        if (Materials.Count == 0) return;

        VirtualGeometryData data = _data!;
        int subCount = Math.Max(1, data.SubMeshCount);

        if (_renderables == null || _renderables.Length != subCount || _propCache == null || _propCache.Length != subCount)
        {
            _renderables = new VirtualGeometryRenderable[subCount];
            _propCache = new PropertyState[subCount];
            for (int i = 0; i < subCount; i++)
            {
                _renderables[i] = new VirtualGeometryRenderable(this, i);
                _propCache[i] = new PropertyState();
            }
        }

        for (int s = 0; s < subCount; s++)
        {
            Material? mat = s < Materials.Count ? Materials[s].Res : (Materials.Count > 0 ? Materials[^1].Res : null);
            if (mat == null) continue;

            PropertyState props = _propCache[s];
            props.Clear();
            props.SetInt("_ObjectID", InstanceID);

            _renderables[s].Update(mat, Transform.LocalToWorldMatrix, GameObject.LayerIndex,
                GameObject.IsStatic, Transform.Version, RenderingLayers.Mask);
            renderables.Add(_renderables[s]);
        }
    }

    /// <summary>
    /// Run (or reuse) cluster selection for the given view and threshold; called lazily by
    /// the pipeline's virtual-geometry draw branch at encode time so every pass (color,
    /// gbuffer, shadow cascades) selects against its actual view.
    /// </summary>
    internal void EnsureSelection(in ViewerData viewer, float threshold)
    {
        if (_data == null) return;

        ulong transformVersion = Transform.Version;
        if (_hasSelection
            && _selTransformVersion == transformVersion
            && _selThreshold == threshold
            && _selPixelHeight == viewer.PixelHeight
            && _selProj11 == viewer.ProjectionMatrix[1, 1]
            && Float3.LengthSquared(_selCamPos - viewer.Position) < 1e-8f
            && Float3.LengthSquared(_selCamFwd - viewer.Forward) < 1e-8f)
            return;

        Float4x4 world = Transform.LocalToWorldMatrix;
        float maxScale = Math.Max(
            Math.Max(Float3.Length(new Float3(world[0, 0], world[1, 0], world[2, 0])),
                     Float3.Length(new Float3(world[0, 1], world[1, 1], world[2, 1]))),
            Float3.Length(new Float3(world[0, 2], world[1, 2], world[2, 2])));

        int used = VirtualGeometrySelector.Select(_data, world, maxScale, viewer, threshold,
            _staging, _visibleClusters, _ranges, Math.Max(1, _data.SubMeshCount));

        int visibleTris = used / 3;
        LastVisibleClusters = _visibleClusters.Count;
        LastVisibleTriangles = visibleTris;
        RenderStats.RecordVirtualGeometry(LastVisibleClusters, visibleTris);

        _selCamPos = viewer.Position;
        _selCamFwd = viewer.Forward;
        _selPixelHeight = viewer.PixelHeight;
        _selProj11 = viewer.ProjectionMatrix[1, 1];
        _selThreshold = threshold;
        _selTransformVersion = transformVersion;
        _hasSelection = true;
        _selectionUsedIndices = used;
    }

    internal int _selectionUsedIndices;

    internal GraphicsVertexArray? ScratchVao => _scratchVao;
    internal GraphicsBuffer? ScratchIndex => _scratchIndex;
    internal VirtualGeometryGpuResources? Resources => _resources;
    internal uint[] Staging => _staging;
    internal VgSubMeshRange[] Ranges => _ranges;
    internal PropertyState GetProperties(int subMesh) => _propCache![subMesh];
    internal bool IndexFormat32 => true; // scratch indices are always uint32

    /// <summary>Threshold for this pass: shadow passes relax by <see cref="ShadowPixelErrorScale"/>.</summary>
    internal float ThresholdFor(string? tagValue)
        => PixelError * (tagValue == "ShadowCaster" ? ShadowPixelErrorScale : 1f);
}
