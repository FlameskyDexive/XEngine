// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Crowd;

using Jitter2.Dynamics;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>
/// Kinematic navmesh follower. Uses <see cref="DtPathCorridor"/> for local steering and
/// writes the pose to <see cref="Transform"/> (and a sibling kinematic <see cref="Rigidbody3D"/>).
/// </summary>
[AddComponentMenu("AI/NavMesh Agent")]
[Preserve]
public sealed class NavMeshAgent : MonoBehaviour
{
    public float Radius = 0.5f;
    public float Height = 2f;
    public float Speed = 3.5f;
    public float Acceleration = 8f;
    public float AngularSpeed = 120f;
    public float StoppingDistance = 0.1f;
    public bool AutoBraking = true;
    public bool AutoTraverseOffMeshLink = true;
    public bool UpdatePosition = true;
    public bool UpdateRotation = true;

    private readonly DtPathCorridor _corridor = CreateCorridor();
    private readonly DtStraightPath[] _corners = new DtStraightPath[8];
    private readonly long[] _offMeshRefs = new long[2];
    private readonly Float3[] _debugCorners = new Float3[8];
    private readonly NavMeshPath _path = new();

    private Rigidbody3D? _rigidbody;
    private Float3 _destination;
    private Float3 _velocity;
    private float _currentSpeed;
    private int _cornerCount;
    private bool _hasPath;
    private bool _arrived;
    private bool _traversing;
    private Float3 _offMeshStart;
    private Float3 _offMeshEnd;
    private float _offMeshT;
    private float _offMeshDuration;
    private OffMeshLink? _runtimeLink;

    public bool HasPath => _hasPath && !_arrived;
    public bool PathPending => false;
    public bool IsOnNavMesh => NavMeshWorld.Instance.Query != null && SampleOnMesh(Transform.Position);
    public bool IsOnOffMeshLink => _traversing;
    public bool IsStopped { get; set; }
    public Float3 Destination => _destination;
    public Float3 Velocity => _velocity;
    public Float3 DesiredVelocity
    {
        get
        {
            if (_cornerCount <= 0) return Float3.Zero;
            Float3 to = NavMeshConvert.ToEngine(_corners[0].pos) - Transform.Position;
            to.Y = 0f;
            float len = Float3.Length(to);
            return len > 1e-5f ? to * (Speed / len) : Float3.Zero;
        }
    }

    public float RemainingDistance
    {
        get
        {
            if (_traversing)
                return Float3.Distance(Transform.Position, _offMeshEnd);
            if (!_hasPath || _arrived || _cornerCount <= 0)
                return 0f;
            Float3 pos = Transform.Position;
            float sum = Dist2D(pos, NavMeshConvert.ToEngine(_corners[0].pos));
            for (int i = 1; i < _cornerCount; i++)
                sum += Dist2D(NavMeshConvert.ToEngine(_corners[i - 1].pos), NavMeshConvert.ToEngine(_corners[i].pos));
            return sum;
        }
    }

    public NavMeshPath Path => _path;
    public OffMeshLinkData CurrentOffMeshLinkData
    {
        get
        {
            if (!_traversing) return default;
            return new OffMeshLinkData
            {
                Valid = true,
                Activated = true,
                StartPos = _offMeshStart,
                EndPos = _offMeshEnd,
                OffMeshLink = _runtimeLink,
            };
        }
    }

    public override void OnEnable()
    {
        _rigidbody = GetComponent<Rigidbody3D>();
        if (_rigidbody != null)
        {
            _rigidbody.MotionType = MotionType.Kinematic;
            _rigidbody.AffectedByGravity = false;
        }

        NavMeshWorld.Instance.RegisterAgent(this);
    }

    public override void OnDisable()
    {
        NavMeshWorld.Instance.UnregisterAgent(this);
        ResetPath();
    }

    public override void Update() => Tick(Time.DeltaTime);

    public bool SetDestination(Float3 target)
    {
        _destination = target;
        _arrived = false;
        _traversing = false;
        _runtimeLink = null;
        _currentSpeed = 0f;
        return Replan(Transform.Position, target);
    }

    public void ResetPath()
    {
        _hasPath = false;
        _arrived = false;
        _traversing = false;
        _cornerCount = 0;
        _currentSpeed = 0f;
        _velocity = Float3.Zero;
        _path.Clear();
        _runtimeLink = null;
    }

    public bool Warp(Float3 position)
    {
        DtNavMeshQuery? query = NavMeshWorld.Instance.Query;
        if (query == null)
        {
            ApplyPose(position, Transform.Rotation);
            ResetPath();
            return false;
        }

        RcVec3f extents = NavMeshWorld.Instance.QueryExtents;
        DtStatus status = query.FindNearestPoly(NavMeshConvert.ToRecast(position), extents, NavMeshWorld.Instance.Filter,
            out long polyRef, out RcVec3f nearest, out _);
        if (status.Failed() || polyRef == 0)
        {
            ApplyPose(position, Transform.Rotation);
            ResetPath();
            return false;
        }

        _corridor.Reset(polyRef, nearest);
        ApplyPose(NavMeshConvert.ToEngine(nearest), Transform.Rotation);
        ResetPath();
        return true;
    }

    public void CompleteOffMeshLink()
    {
        if (!_traversing) return;
        FinishOffMesh();
    }

    public void Tick(float deltaTime)
    {
        if (IsStopped) return;
        float dt = MathF.Max(0f, deltaTime);

        if (_traversing)
        {
            AdvanceOffMesh(dt);
            return;
        }

        DtNavMeshQuery? query = NavMeshWorld.Instance.Query;
        IDtQueryFilter filter = NavMeshWorld.Instance.Filter;
        if (query == null) return;

        if (_hasPath && !_corridor.IsValid(16, query, filter))
            Replan(Transform.Position, _destination);

        if (!_hasPath)
        {
            TryEnterRuntimeLink();
            return;
        }

        _cornerCount = _corridor.FindCorners(_corners, _corners.Length, query, filter);
        RefreshPathBuffer();
        if (_cornerCount <= 0)
        {
            TryEnterRuntimeLink();
            return;
        }

        if (RemainingDistance <= StoppingDistance)
        {
            Arrive();
            return;
        }

        DtStraightPath last = _corners[_cornerCount - 1];
        bool offMesh = (last.flags & DtStraightPathFlags.DT_STRAIGHTPATH_OFFMESH_CONNECTION) != 0;
        RcVec3f pos = _corridor.GetPos();
        if (offMesh && Dist2D(NavMeshConvert.ToEngine(pos), NavMeshConvert.ToEngine(last.pos)) <= MathF.Max(Radius, 0.2f))
        {
            if (AutoTraverseOffMeshLink)
                BeginBakedOffMesh(query, last.refs);
            else
                _traversing = true;
            return;
        }

        Steer(dt, query, filter);
    }

    public override void DrawGizmos()
    {
        Color color = new(0.2f, 0.95f, 0.45f, 1f);
        Debug.DrawSphere(Transform.Position + new Float3(0f, Height * 0.5f, 0f), Radius, color, 10);
        if (_path.CornerCount > 1)
            NavMeshVisualizer.DrawPath(_path.Corners, new Color(1f, 0.85f, 0.2f, 1f));
        if (_traversing)
            Debug.DrawLine(_offMeshStart, _offMeshEnd, new Color(1f, 0.3f, 0.9f, 1f));
    }

    private bool Replan(Float3 source, Float3 target)
    {
        DtNavMeshQuery? query = NavMeshWorld.Instance.Query;
        if (query == null)
        {
            _hasPath = false;
            return false;
        }

        IDtQueryFilter filter = NavMeshWorld.Instance.Filter;
        RcVec3f extents = NavMeshWorld.Instance.QueryExtents;
        DtStatus startStatus = query.FindNearestPoly(NavMeshConvert.ToRecast(source), extents, filter,
            out long startRef, out RcVec3f startPt, out _);
        DtStatus endStatus = query.FindNearestPoly(NavMeshConvert.ToRecast(target), extents, filter,
            out long endRef, out RcVec3f endPt, out _);
        if (startStatus.Failed() || endStatus.Failed() || startRef == 0 || endRef == 0)
        {
            _hasPath = false;
            return TryPlanViaRuntimeLink(source, target, query, filter);
        }

        long[] corridor = NavMeshWorld.Instance.Corridor;
        DtStatus pathStatus = query.FindPath(startRef, endRef, startPt, endPt, filter, corridor, out int pathCount, corridor.Length);
        if (pathStatus.Failed() || pathCount <= 0)
        {
            _hasPath = false;
            return TryPlanViaRuntimeLink(source, target, query, filter);
        }

        _corridor.Reset(startRef, startPt);
        _corridor.SetCorridor(endPt, corridor.AsSpan(0, pathCount), pathCount);
        _hasPath = true;
        _arrived = false;
        ApplyPose(NavMeshConvert.ToEngine(startPt), Transform.Rotation);
        return true;
    }

    private bool TryPlanViaRuntimeLink(Float3 source, Float3 target, DtNavMeshQuery query, IDtQueryFilter filter)
    {
        if (!NavMeshWorld.Instance.TryGetAutoOffMeshLink(source, target, 1024f, out OffMeshLinkData data) || data.OffMeshLink == null)
            return false;

        RcVec3f extents = NavMeshWorld.Instance.QueryExtents;
        DtStatus startStatus = query.FindNearestPoly(NavMeshConvert.ToRecast(source), extents, filter,
            out long startRef, out RcVec3f startPt, out _);
        DtStatus linkStatus = query.FindNearestPoly(NavMeshConvert.ToRecast(data.StartPos), extents, filter,
            out long linkRef, out RcVec3f linkPt, out _);
        if (startStatus.Failed() || linkStatus.Failed() || startRef == 0 || linkRef == 0)
            return false;

        long[] corridor = NavMeshWorld.Instance.Corridor;
        DtStatus pathStatus = query.FindPath(startRef, linkRef, startPt, linkPt, filter, corridor, out int pathCount, corridor.Length);
        if (pathStatus.Failed() || pathCount <= 0) return false;

        _corridor.Reset(startRef, startPt);
        _corridor.SetCorridor(linkPt, corridor.AsSpan(0, pathCount), pathCount);
        _runtimeLink = data.OffMeshLink;
        _offMeshStart = data.StartPos;
        _offMeshEnd = data.EndPos;
        _hasPath = true;
        _arrived = false;
        return true;
    }

    private void TryEnterRuntimeLink()
    {
        if (!AutoTraverseOffMeshLink) return;
        if (_runtimeLink != null && Dist2D(Transform.Position, _offMeshStart) <= MathF.Max(Radius, 0.25f))
        {
            BeginRuntimeOffMesh();
            return;
        }

        if (NavMeshWorld.Instance.TryGetAutoOffMeshLink(Transform.Position, _destination, MathF.Max(Radius * 2f, 0.5f), out OffMeshLinkData data))
        {
            _runtimeLink = data.OffMeshLink;
            _offMeshStart = data.StartPos;
            _offMeshEnd = data.EndPos;
            BeginRuntimeOffMesh();
        }
    }

    private void Steer(float dt, DtNavMeshQuery query, IDtQueryFilter filter)
    {
        Float3 current = NavMeshConvert.ToEngine(_corridor.GetPos());
        Float3 corner = NavMeshConvert.ToEngine(_corners[0].pos);
        Float3 delta = corner - current;
        delta.Y = 0f;
        float dist = Float3.Length(delta);
        if (dist < 0.01f)
        {
            if (_cornerCount <= 1)
            {
                Arrive();
                return;
            }
        }

        float remaining = RemainingDistance;
        float desired = Speed;
        if (AutoBraking && Acceleration > 1e-4f)
        {
            float brake = Speed * Speed / (2f * Acceleration);
            if (remaining < brake)
                desired = MathF.Sqrt(MathF.Max(0f, 2f * Acceleration * remaining));
        }

        float maxDelta = Acceleration * dt;
        if (_currentSpeed < desired)
            _currentSpeed = MathF.Min(desired, _currentSpeed + maxDelta);
        else
            _currentSpeed = MathF.Max(desired, _currentSpeed - maxDelta);

        float step = _currentSpeed * dt;
        Float3 next = current;
        if (dist > 1e-5f)
            next = current + delta * (MathF.Min(step, dist) / dist);

        _corridor.MovePosition(NavMeshConvert.ToRecast(next), query, filter);
        Float3 snapped = NavMeshConvert.ToEngine(_corridor.GetPos());
        _velocity = dt > 1e-8f ? (snapped - current) / dt : Float3.Zero;
        ApplyMovedPose(snapped, delta);
    }

    private void BeginBakedOffMesh(DtNavMeshQuery query, long offMeshRef)
    {
        RcVec3f start = default;
        RcVec3f end = default;
        if (!_corridor.MoveOverOffmeshConnection(offMeshRef, _offMeshRefs, ref start, ref end, query))
            return;
        _offMeshStart = NavMeshConvert.ToEngine(start);
        _offMeshEnd = NavMeshConvert.ToEngine(end);
        _runtimeLink = null;
        StartOffMeshLerp();
    }

    private void BeginRuntimeOffMesh()
    {
        StartOffMeshLerp();
    }

    private void StartOffMeshLerp()
    {
        _traversing = true;
        _offMeshT = 0f;
        float dist = Float3.Distance(_offMeshStart, _offMeshEnd);
        _offMeshDuration = Speed > 1e-4f ? dist / Speed : 0f;
        _currentSpeed = Speed;
        ApplyPose(_offMeshStart, LookAt(_offMeshEnd - _offMeshStart));
    }

    private void AdvanceOffMesh(float dt)
    {
        if (_offMeshDuration <= 1e-5f)
        {
            FinishOffMesh();
            return;
        }

        _offMeshT += dt / _offMeshDuration;
        float t = Math.Clamp(_offMeshT, 0f, 1f);
        Float3 pos = _offMeshStart + (_offMeshEnd - _offMeshStart) * t;
        _velocity = (_offMeshEnd - _offMeshStart) * (1f / _offMeshDuration);
        ApplyPose(pos, LookAt(_offMeshEnd - _offMeshStart));
        if (t >= 1f)
            FinishOffMesh();
    }

    private void FinishOffMesh()
    {
        _traversing = false;
        Warp(_offMeshEnd);
        Float3 dest = _destination;
        _runtimeLink = null;
        Replan(_offMeshEnd, dest);
    }

    private void Arrive()
    {
        _arrived = true;
        _hasPath = true;
        _currentSpeed = 0f;
        _velocity = Float3.Zero;
        _cornerCount = 0;
        ApplyPose(NavMeshConvert.ToEngine(_corridor.GetPos()), Transform.Rotation);
    }

    private void ApplyMovedPose(Float3 position, Float3 lookDelta)
    {
        Quaternion rotation = Transform.Rotation;
        if (UpdateRotation)
        {
            Float3 dir = lookDelta;
            dir.Y = 0f;
            if (Float3.LengthSquared(dir) > 1e-6f)
            {
                Quaternion want = LookAt(dir);
                float t = AngularSpeed <= 0f ? 1f : Math.Clamp(AngularSpeed * Time.DeltaTime / 180f, 0f, 1f);
                rotation = Quaternion.Slerp(Transform.Rotation, want, t);
            }
        }

        ApplyPose(position, rotation);
    }

    private void ApplyPose(Float3 position, Quaternion rotation)
    {
        if (UpdatePosition)
            Transform.Position = position;
        if (UpdateRotation)
            Transform.Rotation = rotation;

        if (_rigidbody == null)
            _rigidbody = GetComponent<Rigidbody3D>();
        if (_rigidbody == null) return;

        if (_rigidbody.MotionType != MotionType.Kinematic)
            _rigidbody.MotionType = MotionType.Kinematic;
        if (UpdatePosition)
            _rigidbody.MovePosition(position);
        if (UpdateRotation)
            _rigidbody.MoveRotation(rotation);
    }

    private void RefreshPathBuffer()
    {
        int n = Math.Min(_cornerCount, _debugCorners.Length);
        for (int i = 0; i < n; i++)
            _debugCorners[i] = NavMeshConvert.ToEngine(_corners[i].pos);
        _path.SetCorners(_debugCorners.AsSpan(0, n),
            _arrived ? NavMeshPathStatus.PathComplete : NavMeshPathStatus.PathPartial);
    }

    private static bool SampleOnMesh(Float3 position) => NavMesh.SamplePosition(position, out _, 1.5f);

    private static Quaternion LookAt(Float3 direction)
    {
        Float3 dir = direction;
        dir.Y = 0f;
        if (Float3.LengthSquared(dir) < 1e-8f)
            return Quaternion.Identity;
        return Quaternion.LookRotation(Float3.Normalize(dir), Float3.UnitY);
    }

    private static float Dist2D(Float3 a, Float3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static DtPathCorridor CreateCorridor()
    {
        var corridor = new DtPathCorridor();
        corridor.Init(256);
        return corridor;
    }
}
