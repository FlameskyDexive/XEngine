// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// One GPU-instanced sprite batch: a shared unit quad plus per-instance transforms.
/// Full-rect Simple sprites use this instead of a per-renderer <see cref="MeshRenderable"/>.
/// </summary>
public sealed class SpriteBatchRenderable : IRenderable
{
    private readonly InstanceData[] _instances;
    private readonly Float3 _sortPosition;
    private readonly Material? _material;
    private readonly PropertyState _properties;
    private readonly int _layer;
    private readonly uint _renderingLayerMask;
    private readonly AABB _bounds;

    public SpriteBatchRenderable(
        int materialId,
        int atlasId,
        InstanceData[] instances,
        Float3 sortPosition,
        Material? material,
        int layer,
        uint renderingLayerMask,
        PropertyState? properties = null)
    {
        MaterialId = materialId;
        AtlasId = atlasId;
        _instances = instances;
        _sortPosition = sortPosition;
        _material = material;
        _properties = properties ?? new PropertyState();
        _layer = layer;
        _renderingLayerMask = renderingLayerMask;
        _bounds = ComputeBounds(instances);
    }

    public int MaterialId { get; }
    public int AtlasId { get; }
    public int InstanceCount => _instances.Length;

    public Material GetMaterial() => _material!;
    public int GetLayer() => _layer;
    public uint GetRenderingLayerMask() => _renderingLayerMask;
    public Float3 GetPosition() => _sortPosition;

    public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData)
    {
        properties = _properties;
        mesh = SpriteRendererRegistry.SharedQuad;
        model = Float4x4.Identity;
        instanceData = _instances;
    }

    public void GetCullingData(out bool isRenderable, out AABB bounds)
    {
        isRenderable = _instances.Length > 0;
        bounds = _bounds;
    }

    private static AABB ComputeBounds(InstanceData[] instances)
    {
        if (instances.Length == 0)
            return new AABB(Float3.Zero, Float3.Zero);

        Float3 min = new(float.MaxValue);
        Float3 max = new(float.MinValue);
        for (int i = 0; i < instances.Length; i++)
        {
            Float3 p = instances[i].GetMatrix().c3.XYZ;
            min = new Float3(Maths.Min(min.X, p.X), Maths.Min(min.Y, p.Y), Maths.Min(min.Z, p.Z));
            max = new Float3(Maths.Max(max.X, p.X), Maths.Max(max.Y, p.Y), Maths.Max(max.Z, p.Z));
        }

        return new AABB(min, max);
    }
}
