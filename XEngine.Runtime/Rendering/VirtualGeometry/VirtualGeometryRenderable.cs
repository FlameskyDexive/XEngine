// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering.VirtualGeometry;

/// <summary>
/// One submesh of a <see cref="XEngine.Runtime.VirtualGeometryRenderer"/> for one frame.
/// Carries no geometry itself — the pipeline's virtual-geometry draw branch pulls the packed
/// scratch index range, vertex array, and per-view selection from the owning renderer.
/// </summary>
public sealed class VirtualGeometryRenderable : IRenderable
{
    private readonly VirtualGeometryRenderer _owner;
    private readonly int _subMesh;
    private Material _material = null!;
    private Float4x4 _world;
    private Float4x4? _worldToObject;
    private int _layerIndex;
    private bool _staticForShadows;
    private ulong _shadowCasterVersion;
    private uint _renderingLayerMask;
    private AABB _bounds;

    public VirtualGeometryRenderable(VirtualGeometryRenderer owner, int subMesh)
    {
        _owner = owner;
        _subMesh = subMesh;
    }

    internal void Update(Material material, in Float4x4 world, int layerIndex, bool staticForShadows, ulong shadowCasterVersion, uint renderingLayerMask)
    {
        _material = material;
        _world = world;
        _worldToObject = null;
        _layerIndex = layerIndex;
        _staticForShadows = staticForShadows;
        _shadowCasterVersion = shadowCasterVersion;
        _renderingLayerMask = renderingLayerMask;
        _bounds = _owner.Data!.Bounds.TransformBy(world);
    }

    public Material GetMaterial() => _material;
    public int GetLayer() => _layerIndex;
    public uint GetRenderingLayerMask() => _renderingLayerMask;
    public int GetSubMeshIndex() => _subMesh;

    public Float3 GetPosition()
        => new(_world[0, 3], _world[1, 3], _world[2, 3]);

    public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData)
    {
        // Mesh stays null: the generic batching path skips this renderable; the dedicated
        // virtual-geometry branch in RenderPipeline.DrawRenderables encodes the draw.
        mesh = null!;
        properties = _owner.GetProperties(_subMesh);
        model = _world;
        instanceData = null;
    }

    public Float4x4 GetWorldToObjectMatrix(in Float4x4 model) => _worldToObject ??= _world.Invert();

    public void GetCullingData(out bool isRenderable, out AABB bounds)
    {
        isRenderable = true;
        bounds = _bounds;
    }

    public bool IsStaticForShadows => _staticForShadows;
    public ulong ShadowCasterVersion => _shadowCasterVersion;

    internal int SubMesh => _subMesh;
    internal VirtualGeometryRenderer Owner => _owner;
}
