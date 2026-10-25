// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Box2D.NET;

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Per-scene Box2D.NET world. One engine unit is one meter. Stepped from
/// <see cref="Resources.Scene.FixedUpdate"/> at <see cref="Time.FixedDeltaTime"/>.
/// Queries write into caller <see cref="Span{T}"/> buffers and reuse retained lists (0 alloc after warmup).
/// </summary>
public sealed class PhysicsWorld2D
{
    public Float2 Gravity = new(0f, -9.81f);
    public int SubstepCount = 4;
    public bool AutoSyncTransforms = true;

    private B2WorldId _worldId;
    private readonly List<Rigidbody2D> _bodies = [];
    private readonly List<RaycastHit2D> _rayHits = new(16);
    private readonly List<Collider2D> _overlapHits = new(16);
    private Collision2D[] _eventQueue = new Collision2D[32];
    private byte[] _eventKinds = new byte[32];
    private int _eventCount;
    private readonly b2CastResultFcn _castFcn;
    private readonly b2OverlapResultFcn _overlapFcn;
    private readonly b2CustomFilterFcn _customFilterFcn;
    private B2DebugDraw? _debugDraw;
    private float _rayLength;
    private uint _queryMask = uint.MaxValue;

    internal B2WorldId WorldId => _worldId;

    public PhysicsWorld2D()
    {
        _castFcn = OnCastHit;
        _overlapFcn = OnOverlapHit;
        _customFilterFcn = OnCustomFilter;
        CreateWorld();
    }

    private void CreateWorld()
    {
        B2WorldDef def = B2Types.b2DefaultWorldDef();
        def.gravity = new B2Vec2(Gravity.X, Gravity.Y);
        _worldId = B2Worlds.b2CreateWorld(ref def);
        B2Worlds.b2World_SetCustomFilterCallback(_worldId, _customFilterFcn, this);
    }

    internal bool IsValid => B2Worlds.b2World_IsValid(_worldId);

    /// <summary>Total steps executed by this world (diagnostics/telemetry; <c>IsValid</c> worlds only).</summary>
    public long StepCount { get; private set; }

    /// <summary>Number of bodies registered on this world (diagnostics/telemetry).</summary>
    public int BodyCount => _bodies.Count;

    /// <summary>b2-level world identity (diagnostics: pair against <see cref="Rigidbody2D.DumpB2State"/>).</summary>
    public string DumpWorldId => $"worldHash={_worldId.GetHashCode()} valid={B2Worlds.b2World_IsValid(_worldId)}";

    /// <summary>Diagnostics: log every shape creation (see Collider2D.RecreateShape).</summary>
    public static bool DebugShapeCreation;

    internal void RegisterBody(Rigidbody2D body)
    {
        if (!_bodies.Contains(body))
            _bodies.Add(body);
    }

    internal void UnregisterBody(Rigidbody2D body) => _bodies.Remove(body);

    /// <summary>
    /// Pushes Static/Kinematic poses, and any Dynamic Transform the user edited, into Box2D.
    /// Always runs before the solver step.
    /// </summary>
    public void SyncTransforms()
    {
        for (int i = 0; i < _bodies.Count; i++)
            _bodies[i].SyncTransformToBody(force: false);
    }

    [HotPath]
    public void Update()
    {
        if (!B2Worlds.b2World_IsValid(_worldId))
            return;

        B2Worlds.b2World_SetGravity(_worldId, new B2Vec2(Gravity.X, Gravity.Y));

        SyncTransforms();
        B2Worlds.b2World_Step(_worldId, Time.FixedDeltaTime, SubstepCount);

        for (int i = 0; i < _bodies.Count; i++)
            _bodies[i].WriteBodyToTransform();

        StepCount++;

        DrainContactEvents();
    }

    public bool Raycast(Float2 origin, Float2 direction, float maxDistance, out RaycastHit2D hit)
        => Raycast(origin, direction, maxDistance, out hit, LayerMask.Everything);

    public bool Raycast(Float2 origin, Float2 direction, float maxDistance, out RaycastHit2D hit, LayerMask layerMask)
    {
        hit = default;
        if (!PrepareQuery() || maxDistance <= 0f || !TryMakeTranslation(direction, maxDistance, out B2Vec2 translation))
            return false;

        if (layerMask.Mask == uint.MaxValue)
        {
            B2QueryFilter filter = B2Types.b2DefaultQueryFilter();
            B2RayResult result = B2Worlds.b2World_CastRayClosest(_worldId, new B2Vec2(origin.X, origin.Y), translation, ref filter);
            if (!result.hit)
                return false;

            hit = MakeHit(result.shapeId, result.point, result.normal, result.fraction, maxDistance);
            return true;
        }

        _rayHits.Clear();
        _rayLength = maxDistance;
        _queryMask = layerMask.Mask;
        B2QueryFilter masked = B2Types.b2DefaultQueryFilter();
        B2Worlds.b2World_CastRay(_worldId, new B2Vec2(origin.X, origin.Y), translation, ref masked, _castFcn, this);
        _queryMask = uint.MaxValue;

        int best = -1;
        float bestFraction = 2f;
        for (int i = 0; i < _rayHits.Count; i++)
        {
            float fraction = _rayHits[i].Fraction;
            if (fraction < bestFraction)
            {
                bestFraction = fraction;
                best = i;
            }
        }

        if (best < 0)
            return false;

        hit = _rayHits[best];
        return true;
    }

    public int Raycast(Float2 origin, Float2 direction, float maxDistance, Span<RaycastHit2D> results)
        => Raycast(origin, direction, maxDistance, results, LayerMask.Everything);

    public int Raycast(Float2 origin, Float2 direction, float maxDistance, Span<RaycastHit2D> results, LayerMask layerMask)
    {
        _rayHits.Clear();
        if (!PrepareQuery() || maxDistance <= 0f || results.Length == 0
            || !TryMakeTranslation(direction, maxDistance, out B2Vec2 translation))
            return 0;

        _rayLength = maxDistance;
        _queryMask = layerMask.Mask;
        B2QueryFilter filter = B2Types.b2DefaultQueryFilter();
        B2Worlds.b2World_CastRay(_worldId, new B2Vec2(origin.X, origin.Y), translation, ref filter, _castFcn, this);
        _queryMask = uint.MaxValue;

        int count = Maths.Min(_rayHits.Count, results.Length);
        for (int i = 0; i < count; i++)
            results[i] = _rayHits[i];
        return count;
    }

    public int OverlapCircle(Float2 point, float radius, Span<Collider2D> results)
        => OverlapCircle(point, radius, results, LayerMask.Everything);

    public int OverlapCircle(Float2 point, float radius, Span<Collider2D> results, LayerMask layerMask)
    {
        _overlapHits.Clear();
        if (!PrepareQuery() || radius <= 0f || results.Length == 0)
            return 0;

        _queryMask = layerMask.Mask;
        B2ShapeProxy proxy = B2Distances.b2MakeProxy(new B2Vec2(point.X, point.Y), 1, radius);
        B2QueryFilter filter = B2Types.b2DefaultQueryFilter();
        B2Worlds.b2World_OverlapShape(_worldId, ref proxy, ref filter, _overlapFcn, this);
        _queryMask = uint.MaxValue;
        return CopyOverlaps(results);
    }

    public int OverlapBox(Float2 point, Float2 size, float angleDegrees, Span<Collider2D> results)
        => OverlapBox(point, size, angleDegrees, results, LayerMask.Everything);

    public int OverlapBox(Float2 point, Float2 size, float angleDegrees, Span<Collider2D> results, LayerMask layerMask)
    {
        _overlapHits.Clear();
        if (!PrepareQuery() || results.Length == 0)
            return 0;

        _queryMask = layerMask.Mask;
        float hx = 0.5f * Maths.Abs(size.X);
        float hy = 0.5f * Maths.Abs(size.Y);
        B2QueryFilter filter = B2Types.b2DefaultQueryFilter();

        if (angleDegrees == 0f)
        {
            B2AABB aabb = new()
            {
                lowerBound = new B2Vec2(point.X - hx, point.Y - hy),
                upperBound = new B2Vec2(point.X + hx, point.Y + hy)
            };
            B2Worlds.b2World_OverlapAABB(_worldId, ref aabb, ref filter, _overlapFcn, this);
        }
        else
        {
            float rad = Maths.Deg2Rad * angleDegrees;
            float c = Maths.Cos(rad);
            float s = Maths.Sin(rad);
            Span<B2Vec2> corners = stackalloc B2Vec2[4];
            corners[0] = Rotate(point, -hx, -hy, c, s);
            corners[1] = Rotate(point, hx, -hy, c, s);
            corners[2] = Rotate(point, hx, hy, c, s);
            corners[3] = Rotate(point, -hx, hy, c, s);
            B2ShapeProxy proxy = B2Distances.b2MakeProxy(corners, 4, 0f);
            B2Worlds.b2World_OverlapShape(_worldId, ref proxy, ref filter, _overlapFcn, this);
        }

        _queryMask = uint.MaxValue;
        return CopyOverlaps(results);
    }

    /// <summary>
    /// Draws live Box2D shapes into <see cref="Debug"/> gizmos (editor / debug views).
    /// </summary>
    public void DrawDebug()
    {
        if (!B2Worlds.b2World_IsValid(_worldId))
            return;
        if (AutoSyncTransforms)
            SyncTransforms();

        B2Worlds.b2World_Draw(_worldId, EnsureDebugDraw());
    }

    public void Clear()
    {
        _bodies.Clear();
        _rayHits.Clear();
        _overlapHits.Clear();
        _eventCount = 0;
        if (B2Worlds.b2World_IsValid(_worldId))
            B2Worlds.b2DestroyWorld(_worldId);
        _worldId = default;
        _debugDraw = null;
    }

    private B2DebugDraw EnsureDebugDraw()
    {
        if (_debugDraw != null)
            return _debugDraw;

        B2DebugDraw draw = B2Types.b2DefaultDebugDraw();
        draw.drawShapes = true;
        draw.drawJoints = false;
        draw.drawingBounds = new B2AABB
        {
            lowerBound = new B2Vec2(-1_000_000f, -1_000_000f),
            upperBound = new B2Vec2(1_000_000f, 1_000_000f)
        };
        draw.DrawPolygonFcn = DrawPolygon;
        draw.DrawSolidPolygonFcn = DrawSolidPolygon;
        draw.DrawCircleFcn = DrawCircleOutline;
        draw.DrawSolidCircleFcn = DrawSolidCircle;
        draw.DrawSolidCapsuleFcn = DrawSolidCapsule;
        draw.drawLineFcn = DrawSegment;
        _debugDraw = draw;
        return draw;
    }

    private static void DrawPolygon(ReadOnlySpan<B2Vec2> vertices, int count, B2HexColor color, object context)
    {
        Color c = ToColor(color);
        int n = Maths.Min(count, vertices.Length);
        for (int i = 0; i < n; i++)
        {
            B2Vec2 a = vertices[i];
            B2Vec2 b = vertices[(i + 1) % n];
            Debug.DrawLine(new Float3(a.X, a.Y, 0f), new Float3(b.X, b.Y, 0f), c);
        }
    }

    private static void DrawSolidPolygon(in B2Transform xf, ReadOnlySpan<B2Vec2> vertices, int count, float radius, B2HexColor color, object context)
    {
        Color c = ToColor(color);
        int n = Maths.Min(count, vertices.Length);
        if (n < 2)
            return;

        Float3 prev = TransformPoint(in xf, vertices[n - 1]);
        for (int i = 0; i < n; i++)
        {
            Float3 cur = TransformPoint(in xf, vertices[i]);
            Debug.DrawLine(prev, cur, c);
            prev = cur;
        }
    }

    private static void DrawCircleOutline(in B2Vec2 center, float radius, B2HexColor color, object context)
        => Debug.DrawWireCircle(new Float3(center.X, center.Y, 0f), Float3.UnitZ, radius, ToColor(color));

    private static void DrawSolidCircle(in B2Transform xf, float radius, B2HexColor color, object context)
        => Debug.DrawWireCircle(new Float3(xf.p.X, xf.p.Y, 0f), Float3.UnitZ, radius, ToColor(color));

    private static void DrawSolidCapsule(in B2Vec2 p1, in B2Vec2 p2, float radius, B2HexColor color, object context)
        => Debug.DrawWireCapsule(new Float3(p1.X, p1.Y, 0f), new Float3(p2.X, p2.Y, 0f), radius, ToColor(color));

    private static void DrawSegment(in B2Vec2 p1, in B2Vec2 p2, B2HexColor color, object context)
        => Debug.DrawLine(new Float3(p1.X, p1.Y, 0f), new Float3(p2.X, p2.Y, 0f), ToColor(color));

    private static Float3 TransformPoint(in B2Transform xf, B2Vec2 v)
        => new(xf.q.c * v.X - xf.q.s * v.Y + xf.p.X, xf.q.s * v.X + xf.q.c * v.Y + xf.p.Y, 0f);

    private static Color ToColor(B2HexColor hex)
    {
        uint v = (uint)hex;
        return new Color(((v >> 16) & 0xFFu) / 255f, ((v >> 8) & 0xFFu) / 255f, (v & 0xFFu) / 255f, 1f);
    }

    private bool PrepareQuery()
    {
        if (!B2Worlds.b2World_IsValid(_worldId))
            return false;
        if (AutoSyncTransforms)
            SyncTransforms();
        return true;
    }

    private static bool TryMakeTranslation(Float2 direction, float maxDistance, out B2Vec2 translation)
    {
        float lenSq = direction.X * direction.X + direction.Y * direction.Y;
        if (lenSq < 1e-12f)
        {
            translation = default;
            return false;
        }

        float inv = 1f / Maths.Sqrt(lenSq);
        translation = new B2Vec2(direction.X * inv * maxDistance, direction.Y * inv * maxDistance);
        return true;
    }

    private float OnCastHit(B2ShapeId shapeId, B2Vec2 point, B2Vec2 normal, float fraction, object context)
    {
        Collider2D? collider = ColliderFromShape(shapeId);
        if (!MatchesQueryMask(collider))
            return 1f;

        _rayHits.Add(MakeHit(shapeId, point, normal, fraction, _rayLength));
        return 1f;
    }

    private bool OnOverlapHit(B2ShapeId shapeId, object context)
    {
        Collider2D? collider = ColliderFromShape(shapeId);
        if (collider != null && MatchesQueryMask(collider))
            _overlapHits.Add(collider);
        return true;
    }

    private bool MatchesQueryMask(Collider2D? collider)
    {
        if (_queryMask == uint.MaxValue)
            return true;

        GameObject? go = collider!.GameObject;
        if (go == null)
            return false;

        int layer = go.LayerIndex;
        if ((uint)layer >= 32u)
            return false;

        return (_queryMask & (1u << layer)) != 0;
    }

    private int CopyOverlaps(Span<Collider2D> results)
    {
        int count = Maths.Min(_overlapHits.Count, results.Length);
        for (int i = 0; i < count; i++)
            results[i] = _overlapHits[i];
        return count;
    }

    private static RaycastHit2D MakeHit(B2ShapeId shapeId, B2Vec2 point, B2Vec2 normal, float fraction, float rayLength)
    {
        Collider2D? collider = ColliderFromShape(shapeId);
        Rigidbody2D? body = collider!.AttachedBody;
        if (body == null && B2Worlds.b2Shape_IsValid(shapeId))
            body = B2Bodies.b2Body_GetUserData(B2Shapes.b2Shape_GetBody(shapeId)).oValue as Rigidbody2D;

        return new RaycastHit2D
        {
            Hit = true,
            Point = new Float2(point.X, point.Y),
            Normal = new Float2(normal.X, normal.Y),
            Fraction = fraction,
            Distance = fraction * rayLength,
            Collider = collider,
            Rigidbody = body
        };
    }

    private static Collider2D? ColliderFromShape(B2ShapeId shapeId)
    {
        if (!B2Worlds.b2Shape_IsValid(shapeId))
            return null;
        return B2Shapes.b2Shape_GetUserData(shapeId).oValue as Collider2D;
    }

    /// <summary>
    /// Live CollisionMatrix check. Reads GameObject.LayerIndex so matrix edits after shape
    /// creation still apply (same contract as 3D LayerFilter).
    /// </summary>
    private static bool OnCustomFilter(B2ShapeId shapeA, B2ShapeId shapeB, object context)
    {
        Collider2D? a = ColliderFromShape(shapeA);
        Collider2D? b = ColliderFromShape(shapeB);
        if (a == null || b == null)
            return true;

        GameObject? ga = a.GameObject;
        GameObject? gb = b.GameObject;
        if (ga == null || gb == null)
            return true;

        return CollisionMatrix.GetLayerCollision(ga.LayerIndex, gb.LayerIndex);
    }

    private static B2Vec2 Rotate(Float2 center, float lx, float ly, float cos, float sin)
        => new(center.X + lx * cos - ly * sin, center.Y + lx * sin + ly * cos);

    private const byte KindCollisionEnter = 0;
    private const byte KindCollisionExit = 1;
    private const byte KindTriggerEnter = 2;
    private const byte KindTriggerExit = 3;

    private void DrainContactEvents()
    {
        _eventCount = 0;

        B2ContactEvents contacts = B2Worlds.b2World_GetContactEvents(_worldId);
        B2ContactBeginTouchEvent[] begins = contacts.beginEvents;
        int beginCount = contacts.beginCount;
        if (begins != null)
        {
            for (int i = 0; i < beginCount; i++)
                EnqueuePair(begins[i].shapeIdA, begins[i].shapeIdB, KindCollisionEnter);
        }

        B2ContactEndTouchEvent[] ends = contacts.endEvents;
        int endCount = contacts.endCount;
        if (ends != null)
        {
            for (int i = 0; i < endCount; i++)
                EnqueuePair(ends[i].shapeIdA, ends[i].shapeIdB, KindCollisionExit);
        }

        B2SensorEvents sensors = B2Worlds.b2World_GetSensorEvents(_worldId);
        B2SensorBeginTouchEvent[] sensorBegins = sensors.beginEvents;
        int sensorBeginCount = sensors.beginCount;
        if (sensorBegins != null)
        {
            for (int i = 0; i < sensorBeginCount; i++)
                EnqueuePair(sensorBegins[i].sensorShapeId, sensorBegins[i].visitorShapeId, KindTriggerEnter);
        }

        B2SensorEndTouchEvent[] sensorEnds = sensors.endEvents;
        int sensorEndCount = sensors.endCount;
        if (sensorEnds != null)
        {
            for (int i = 0; i < sensorEndCount; i++)
                EnqueuePair(sensorEnds[i].sensorShapeId, sensorEnds[i].visitorShapeId, KindTriggerExit);
        }

        for (int i = 0; i < _eventCount; i++)
        {
            Collision2D ev = _eventQueue[i];
            byte kind = _eventKinds[i];
            switch (kind)
            {
                case KindCollisionEnter:
                    if (ev.Collider is { IsDisposed: false }) ev.Collider.RaiseCollisionEnter(ev);
                    Swap(ref ev);
                    if (ev.Collider is { IsDisposed: false }) ev.Collider.RaiseCollisionEnter(ev);
                    break;
                case KindCollisionExit:
                    if (ev.Collider is { IsDisposed: false }) ev.Collider.RaiseCollisionExit(ev);
                    Swap(ref ev);
                    if (ev.Collider is { IsDisposed: false }) ev.Collider.RaiseCollisionExit(ev);
                    break;
                case KindTriggerEnter:
                    if (ev.Collider is { IsDisposed: false }) ev.Collider.RaiseTriggerEnter(ev);
                    Swap(ref ev);
                    if (ev.Collider is { IsDisposed: false }) ev.Collider.RaiseTriggerEnter(ev);
                    break;
                case KindTriggerExit:
                    if (ev.Collider is { IsDisposed: false }) ev.Collider.RaiseTriggerExit(ev);
                    Swap(ref ev);
                    if (ev.Collider is { IsDisposed: false }) ev.Collider.RaiseTriggerExit(ev);
                    break;
            }
        }
    }

    private void EnqueuePair(B2ShapeId shapeA, B2ShapeId shapeB, byte kind)
    {
        Collider2D? a = ColliderFromShape(shapeA);
        Collider2D? b = ColliderFromShape(shapeB);
        if (a == null && b == null)
            return;

        if (_eventCount >= _eventQueue.Length)
            GrowEventQueue();

        _eventQueue[_eventCount] = new Collision2D
        {
            Collider = a,
            OtherCollider = b,
            Rigidbody = a is not null ? a.AttachedBody : null,
            OtherRigidbody = b is not null ? b.AttachedBody : null
        };
        _eventKinds[_eventCount] = kind;
        _eventCount++;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void GrowEventQueue()
    {
        int next = _eventQueue.Length * 2;
        Array.Resize(ref _eventQueue, next);
        Array.Resize(ref _eventKinds, next);
    }

    private static void Swap(ref Collision2D ev)
    {
        (ev.Collider, ev.OtherCollider) = (ev.OtherCollider, ev.Collider);
        (ev.Rigidbody, ev.OtherRigidbody) = (ev.OtherRigidbody, ev.Rigidbody);
    }
}
