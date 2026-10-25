// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>
/// Runtime / bake-time jump or teleport between two points on (or near) the navmesh.
/// Baked into Recast as an off-mesh connection when present during <see cref="NavMeshBuilder.Collect"/>.
/// </summary>
[AddComponentMenu("AI/Off Mesh Link")]
[Preserve]
public sealed class OffMeshLink : MonoBehaviour
{
    public Float3 StartPoint = new(-1f, 0f, 0f);
    public Float3 EndPoint = new(1f, 0f, 0f);
    public Transform? StartTransform;
    public Transform? EndTransform;
    public float Radius = 0.5f;
    public bool Bidirectional = true;
    public bool Activated = true;
    public int Area = NavMeshAreas.Jump;
    public float CostOverride = -1f;

    public Float3 StartWorld => StartTransform != null
        ? StartTransform.Position
        : Transform.TransformPoint(StartPoint);

    public Float3 EndWorld => EndTransform != null
        ? EndTransform.Position
        : Transform.TransformPoint(EndPoint);

    public override void OnEnable() => NavMeshWorld.Instance.RegisterLink(this);

    public override void OnDisable() => NavMeshWorld.Instance.UnregisterLink(this);

    public override void DrawGizmos()
    {
        if (!Activated) return;
        Color color = NavMeshGeometry.AreaColor(Area, 1f);
        Float3 start = StartWorld;
        Float3 end = EndWorld;
        Debug.DrawLine(start, end, color);
        Debug.DrawSphere(start, MathF.Max(0.08f, Radius * 0.35f), color, 8);
        Debug.DrawSphere(end, MathF.Max(0.08f, Radius * 0.35f), color, 8);
        if (Bidirectional)
            Debug.DrawArrow(end, start - end, color);
        else
            Debug.DrawArrow(start, end - start, color);
    }
}

/// <summary>Active off-mesh traversal the agent is on or about to take.</summary>
public struct OffMeshLinkData
{
    public bool Valid;
    public bool Activated;
    public Float3 StartPos;
    public Float3 EndPos;
    public OffMeshLink? OffMeshLink;
}
