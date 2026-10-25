// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// A renderable for skinned meshes that uses pre-computed world-space bounds
/// (derived from bone positions) instead of the static mesh bind-pose bounds.
/// This ensures correct frustum culling for animated/deformed meshes.
/// </summary>
public class SkinnedMeshRenderable : IRenderable
{
    private Mesh _mesh;
    private Material _material;
    private Float4x4 _transform;
    private int _layerIndex;
    private PropertyState _properties;
    private int _subMeshIndex;
    private AABB _worldBounds;
    private Float4x4? _worldToObject;
    private uint _renderingLayerMask;
    internal Float4x4[]? SkinMatrices { get; set; }

    /// <summary>★ N7 — Active morph layers (layerIndex, weight) resolved by the owning SMR this
    /// frame; consumed by <see cref="SkinnedInstanceBatch"/> to build the per-instance morph
    /// weight palette row. The list is the SMR's reused per-frame buffer — valid only during the
    /// same frame's batch/draw. Null when the mesh has no active morphs.</summary>
    internal List<Float4>? MorphActiveLayers { get; set; }

    public SkinnedMeshRenderable(Mesh mesh, Material material, Float4x4 matrix, int layerIndex, AABB worldBounds, PropertyState? propertyBlock = null, int subMeshIndex = -1, uint renderingLayerMask = 0xFFu)
    {
        _mesh = mesh;
        _material = material;
        _transform = matrix;
        _layerIndex = layerIndex;
        _worldBounds = worldBounds;
        _properties = propertyBlock ?? new();
        _subMeshIndex = subMeshIndex;
        _renderingLayerMask = renderingLayerMask;
    }

    /// <summary>Re-arms a cached instance for this frame's collect (see
    /// <see cref="MeshRenderable.Reset"/> for the lifetime contract).</summary>
    internal void Reset(Mesh mesh, Material material, in Float4x4 matrix, int layerIndex, in AABB worldBounds,
        PropertyState properties, int subMeshIndex, uint renderingLayerMask)
    {
        _mesh = mesh;
        _material = material;
        _transform = matrix;
        _layerIndex = layerIndex;
        _worldBounds = worldBounds;
        _properties = properties;
        _subMeshIndex = subMeshIndex;
        _worldToObject = null;
        _renderingLayerMask = renderingLayerMask;
    }

    public Material GetMaterial() => _material;
    public int GetLayer() => _layerIndex;
    public uint GetRenderingLayerMask() => _renderingLayerMask;
    public GameObject? GetOwner() => _owner;

    /// <summary>★ M7 — Records the scene object this proxy draws for (selection outline, editor tooling).</summary>
    internal void SetOwner(GameObject? owner) => _owner = owner;

    private GameObject? _owner;

    public Float3 GetPosition()
    {
        return new Float3(_transform[0, 3], _transform[1, 3], _transform[2, 3]);
    }

    public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData)
    {
        mesh = _mesh;
        properties = _properties;
        model = _transform;
        instanceData = null;
    }

    public int GetSubMeshIndex() => _subMeshIndex;

    // The transform is fixed for this renderable's lifetime (one frame); invert once and reuse
    // across every render pass instead of re-inverting per pass.
    public Float4x4 GetWorldToObjectMatrix(in Float4x4 model) => _worldToObject ??= _transform.Invert();

    public void GetCullingData(out bool isRenderable, out AABB bounds)
    {
        isRenderable = true;
        bounds = _worldBounds; // Already in world space, no transform needed
    }
}
