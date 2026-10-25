// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

public class MeshRenderable : IRenderable
{
    private Mesh _mesh;
    private Material _material;
    private Float4x4 _transform;
    private int _layerIndex;
    private PropertyState _properties;
    private int _subMeshIndex;
    private Float4x4? _worldToObject;
    private bool _staticForShadows;
    private ulong _shadowCasterVersion;
    private uint _renderingLayerMask;
    private GameObject? _owner;

    public MeshRenderable(Mesh mesh, Material material, Float4x4 matrix, int layerIndex, PropertyState? propertyBlock = null,
        int subMeshIndex = -1, bool staticForShadows = false, ulong shadowCasterVersion = 0,
        uint renderingLayerMask = 0xFFu)
    {
        _mesh = mesh;
        _material = material;
        _transform = matrix;
        _layerIndex = layerIndex;
        _properties = propertyBlock ?? new();
        _subMeshIndex = subMeshIndex;
        _staticForShadows = staticForShadows;
        _shadowCasterVersion = shadowCasterVersion;
        _renderingLayerMask = renderingLayerMask;
    }

    /// <summary>Re-arms a cached instance for this frame's collect. Renderables are consumed
    /// during the camera's render (the command buffer snapshots properties at encode time), so
    /// a renderer can reuse one instance per submesh across frames - same lifetime contract as
    /// the per-renderer PropertyState cache.</summary>
    internal void Reset(Mesh mesh, Material material, in Float4x4 matrix, int layerIndex, PropertyState properties,
        int subMeshIndex, bool staticForShadows, ulong shadowCasterVersion, uint renderingLayerMask)
    {
        _mesh = mesh;
        _material = material;
        _transform = matrix;
        _layerIndex = layerIndex;
        _properties = properties;
        _subMeshIndex = subMeshIndex;
        _worldToObject = null;
        _staticForShadows = staticForShadows;
        _shadowCasterVersion = shadowCasterVersion;
        _renderingLayerMask = renderingLayerMask;
    }

    public Material GetMaterial() => _material;
    public int GetLayer() => _layerIndex;
    public uint GetRenderingLayerMask() => _renderingLayerMask;
    public GameObject? GetOwner() => _owner;

    /// <summary>★ M7 — Records the scene object this proxy draws for (selection outline, editor tooling).</summary>
    internal void SetOwner(GameObject? owner) => _owner = owner;

    internal void SetTransform(in Float4x4 transform)
    {
        _transform = transform;
        _worldToObject = null;
    }

    public Float3 GetPosition()
    {
        // Extract position from the transform matrix (4th column)
        return new Float3(_transform[0, 3], _transform[1, 3], _transform[2, 3]);
    }

    public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData)
    {
        mesh = _mesh;
        properties = _properties;
        model = _transform;
        instanceData = null;
    }

    // The transform is fixed for this renderable's lifetime (one frame), so invert once and
    // reuse across every render pass instead of re-inverting per pass.
    public Float4x4 GetWorldToObjectMatrix(in Float4x4 model) => _worldToObject ??= _transform.Invert();

    /// <summary>
    /// Get the submesh index for this renderable. -1 means draw the entire mesh (legacy/no submeshes).
    /// </summary>
    public int GetSubMeshIndex() => _subMeshIndex;
    public bool IsStaticForShadows => _staticForShadows;
    public ulong ShadowCasterVersion => _shadowCasterVersion;

    public void GetCullingData(out bool isRenderable, out AABB bounds)
    {
        isRenderable = true;
        bounds = _mesh.bounds.TransformBy(_transform);
    }
}
