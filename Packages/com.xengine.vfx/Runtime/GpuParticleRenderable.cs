using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VFX;

/// <summary>Transparent-queue renderable that draws GPU particles via DrawIndexedIndirect.</summary>
internal sealed class GpuParticleRenderable : IRenderable
{
    private readonly GpuParticleSystem _system;
    private Mesh _mesh;
    private readonly int _layer;
    private readonly AABB _bounds;
    private Material _material;
    private Float3 _position;

    public GpuParticleRenderable(GpuParticleSystem system, Mesh mesh, Float3 position, int layer)
    {
        _system = system;
        _mesh = mesh;
        _position = position;
        _layer = layer;
        _material = null!;
        Properties = new PropertyState();
        float pad = 8f;
        _bounds = new AABB(position - new Float3(pad), position + new Float3(pad));
    }

    public void SetMesh(Mesh mesh) => _mesh = mesh;

    public PropertyState Properties { get; }

    public void Configure(Material material, Float3 position)
    {
        _material = material;
        _position = position;
    }

    public Material GetMaterial() => _material;
    public int GetLayer() => _layer;
    public Float3 GetPosition() => _position;
    public GraphicsBuffer? GetIndirectDrawArgs() => _system.DrawArgs;

    public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData)
    {
        properties = Properties;
        mesh = _mesh;
        model = Float4x4.Identity;
        instanceData = null;
    }

    public void GetCullingData(out bool isRenderable, out AABB bounds)
    {
        isRenderable = _material != null && _mesh != null;
        bounds = _bounds;
    }
}
