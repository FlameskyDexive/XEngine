// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

public enum NavMeshObstacleShape
{
    Box = 0,
    Capsule = 1,
}

public enum NavMeshObstacleCarveMode
{
    Carve = 0,
    CarveAndRaise = 1,
    Ignore = 2,
}

/// <summary>
/// Runtime obstacle. When carving is enabled on the loaded navmesh's tile cache, this punches a hole
/// (or raises the area) without re-voxelizing the scene.
/// </summary>
[AddComponentMenu("AI/NavMesh Obstacle")]
[Preserve]
public sealed class NavMeshObstacle : MonoBehaviour
{
    public NavMeshObstacleShape Shape = NavMeshObstacleShape.Box;
    public NavMeshObstacleCarveMode Carve = NavMeshObstacleCarveMode.Carve;
    public Float3 Size = new(1f, 2f, 1f);
    public float Radius = 0.5f;
    public float Height = 2f;

    private long _carveRef;
    private Float3 _lastPosition;
    private Quaternion _lastRotation;
    private bool _carved;

    public AABB WorldBounds
    {
        get
        {
            Float3 center = Transform.Position;
            if (Shape == NavMeshObstacleShape.Capsule)
            {
                float r = MathF.Max(0.05f, Radius);
                float h = MathF.Max(r * 2f, Height);
                return new AABB(center - new Float3(r, 0f, r), center + new Float3(r, h, r));
            }

            Float3 half = Size * 0.5f;
            half.X = MathF.Abs(half.X);
            half.Y = MathF.Abs(half.Y);
            half.Z = MathF.Abs(half.Z);
            return new AABB(center - half, center + half);
        }
    }

    public override void OnEnable() => ApplyCarve();

    public override void OnDisable() => RemoveCarve();

    public override void Update()
    {
        if (!_carved) return;
        if (Transform.Position.Equals(_lastPosition) && Transform.Rotation.Equals(_lastRotation))
            return;
        ApplyCarve();
    }

    public override void DrawGizmos()
    {
        Color color = Carve == NavMeshObstacleCarveMode.Ignore
            ? new Color(0.5f, 0.5f, 0.55f, 0.8f)
            : new Color(1f, 0.35f, 0.2f, 0.95f);
        if (Shape == NavMeshObstacleShape.Capsule)
            Debug.DrawSphere(Transform.Position + new Float3(0f, Height * 0.5f, 0f), MathF.Max(0.05f, Radius), color, 10);
        else
            Debug.DrawWireCube(Transform.Position, Size * 0.5f, color);
    }

    public void ApplyCarve()
    {
        RemoveCarve();
        if (Carve == NavMeshObstacleCarveMode.Ignore) return;
        _carveRef = NavMeshWorld.Instance.AddObstacle(this);
        _carved = _carveRef != 0;
        _lastPosition = Transform.Position;
        _lastRotation = Transform.Rotation;
    }

    public void RemoveCarve()
    {
        if (_carveRef != 0)
            NavMeshWorld.Instance.RemoveObstacle(_carveRef);
        _carveRef = 0;
        _carved = false;
    }
}
