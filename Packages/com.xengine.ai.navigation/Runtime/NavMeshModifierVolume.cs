using DotRecast.Recast;

using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Vector;

namespace XEngine.AI.Navigation;

/// <summary>Axis-aligned box that stamps an area onto overlapping navmesh during bake.</summary>
[AddComponentMenu("AI/NavMesh Modifier Volume")]
[Preserve]
public sealed class NavMeshModifierVolume : MonoBehaviour
{
    public Float3 Center;
    public Float3 Size = new(4f, 2f, 4f);
    public int Area = NavMeshAreas.Water;

    public RcConvexVolume ToConvexVolume()
    {
        Float3 world = Transform.TransformPoint(Center);
        Float3 half = Size * 0.5f;
        Float3 right = Transform.Right * half.X;
        Float3 forward = Transform.Forward * half.Z;
        Float3 a = world - right - forward;
        Float3 b = world + right - forward;
        Float3 c = world + right + forward;
        Float3 d = world - right + forward;
        return new RcConvexVolume
        {
            verts =
            [
                a.X, a.Y, a.Z,
                b.X, b.Y, b.Z,
                c.X, c.Y, c.Z,
                d.X, d.Y, d.Z,
            ],
            hmin = world.Y - half.Y,
            hmax = world.Y + half.Y,
            areaMod = new RcAreaModification(Area),
        };
    }

    public override void DrawGizmos()
    {
        Debug.PushMatrix(Transform.LocalToWorldMatrix);
        Debug.DrawWireCube(Center, Size * 0.5f, NavMeshGeometry.AreaColor(Area, 0.9f));
        Debug.PopMatrix();
    }
}
