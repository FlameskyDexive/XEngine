using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Vector;

namespace XEngine.AI.Navigation;

/// <summary>Off-mesh connection (jump / teleport) baked into the navmesh.</summary>
[AddComponentMenu("AI/NavMesh Link")]
[Preserve]
public sealed class NavMeshLink : MonoBehaviour
{
    public Float3 StartPoint = new(-1f, 0f, 0f);
    public Float3 EndPoint = new(1f, 0f, 0f);
    public float Width = 1f;
    public bool Bidirectional = true;
    public int Area = NavMeshAreas.Jump;
    public bool Activated = true;

    public Float3 StartWorld => Transform.TransformPoint(StartPoint);
    public Float3 EndWorld => Transform.TransformPoint(EndPoint);

    public override void DrawGizmos()
    {
        if (!Activated) return;
        Color color = NavMeshGeometry.AreaColor(Area, 1f);
        Debug.DrawLine(StartWorld, EndWorld, color);
        Debug.DrawSphere(StartWorld, Width * 0.25f, color, 8);
        Debug.DrawSphere(EndWorld, Width * 0.25f, color, 8);
        if (Bidirectional)
            Debug.DrawArrow(EndWorld, StartWorld - EndWorld, color);
        else
            Debug.DrawArrow(StartWorld, EndWorld - StartWorld, color);
    }
}
