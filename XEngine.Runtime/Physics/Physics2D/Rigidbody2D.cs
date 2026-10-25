// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Box2D.NET;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics 2D/Rigidbody 2D")]
[ComponentIcon("\uf1b2")]
public sealed class Rigidbody2D : MonoBehaviour
{
    [SerializeField] private RigidbodyType2D bodyType = RigidbodyType2D.Dynamic;
    [SerializeField] private float mass = 1f;
    [SerializeField] private float gravityScale = 1f;
    [SerializeField] private float linearDamping;
    [SerializeField] private float angularDamping;
    [SerializeField] private bool fixedRotation;

    private B2BodyId _bodyId;
    private uint _lastSyncedTransformVersion;

    internal B2BodyId BodyId => _bodyId;
    internal bool HasBody => B2Worlds.b2Body_IsValid(_bodyId);

    /// <summary>
    /// Locks world rotation. Box2D 3 dropped native fixed-rotation, so the lock is
    /// enforced on every body-to-transform sync: angular velocity is zeroed and the
    /// written angle forced to zero (characters, vehicles, kettles).
    /// </summary>
    public bool FixedRotation
    {
        get => fixedRotation;
        set
        {
            fixedRotation = value;
            if (HasBody)
                B2Bodies.b2Body_SetAwake(_bodyId, true);
        }
    }

    public RigidbodyType2D BodyType
    {
        get => bodyType;
        set
        {
            bodyType = value;
            if (HasBody)
                B2Bodies.b2Body_SetType(_bodyId, ToBox2D(value));
        }
    }

    public float Mass
    {
        get => mass;
        set
        {
            if (value <= 0f)
                throw new ArgumentException("Mass can not be zero or negative.", nameof(value));
            mass = value;
            RebuildShapes();
        }
    }

    public float GravityScale
    {
        get => gravityScale;
        set
        {
            gravityScale = value;
            if (HasBody)
                B2Bodies.b2Body_SetGravityScale(_bodyId, value);
        }
    }

    public bool IsAwake => HasBody && B2Bodies.b2Body_IsAwake(_bodyId);

    /// <summary>
    /// Solver-level state straight from Box2D (body type, gravity scale, world gravity, velocity)
    /// for physics debugging — unlike the C# properties above, which may cache desired values.
    /// </summary>
    public string DumpB2State()
    {
        if (!HasBody)
            return "no-body";
        B2WorldId bodyWorld = B2Bodies.b2Body_GetWorld(_bodyId);
        B2Vec2 g = B2Worlds.b2World_GetGravity(bodyWorld);
        B2Vec2 v = B2Bodies.b2Body_GetLinearVelocity(_bodyId);
        return $"b2type={B2Bodies.b2Body_GetType(_bodyId)} b2gravScale={B2Bodies.b2Body_GetGravityScale(_bodyId)} "
             + $"mass={B2Bodies.b2Body_GetMass(_bodyId)} shapes={B2Bodies.b2Body_GetShapeCount(_bodyId)} "
             + $"sleepEnabled={B2Bodies.b2Body_IsSleepEnabled(_bodyId)} b2awake={B2Bodies.b2Body_IsAwake(_bodyId)} "
             + $"worldGravity=({g.X},{g.Y}) b2vel=({v.X},{v.Y}) "
             + $"bodyWorldHash={bodyWorld.GetHashCode()}";
    }

    /// <summary>
    /// World-space linear velocity in units per second. Reading on a body that has not
    /// entered a physics world yet returns zero; writing creates the body (like Teleport).
    /// </summary>
    public Float2 LinearVelocity
    {
        get
        {
            if (!HasBody)
                return default;

            B2Vec2 v = B2Bodies.b2Body_GetLinearVelocity(_bodyId);
            return new Float2(v.X, v.Y);
        }
        set
        {
            EnsureBody();
            if (!HasBody)
                return;

            B2Bodies.b2Body_SetLinearVelocity(_bodyId, new B2Vec2(value.X, value.Y));
            B2Bodies.b2Body_SetAwake(_bodyId, true);
        }
    }

    /// <summary>
    /// Instantly sets the XY pose and wakes the body. Does not interpolate.
    /// </summary>
    public void Teleport(Float2 position)
    {
        if (Transform != null)
            Transform.Position = new Float3(position.X, position.Y, Transform.Position.Z);

        EnsureBody();
        if (!HasBody)
            return;

        SyncTransformToBody(force: true);
        B2Bodies.b2Body_SetAwake(_bodyId, true);
    }

    public override void OnEnable()
    {
        EnsureBody();
        ClaimChildColliders();
    }

    public override void OnDisable()
    {
        PhysicsWorld2D? world = GameObject?.Scene?.Physics2D;
        world?.UnregisterBody(this);

        if (HasBody)
            B2Bodies.b2DestroyBody(_bodyId);
        _bodyId = default;
    }

    internal void AttachCollider(Collider2D collider)
    {
        EnsureBody();
        collider.TryAttachTo(this);
    }

    internal void EnsureBody()
    {
        if (HasBody)
            return;

        PhysicsWorld2D? world = GameObject?.Scene?.Physics2D;
        if (world == null || !world.IsValid)
            return;

        B2BodyDef def = B2Types.b2DefaultBodyDef();
        def.type = ToBox2D(bodyType);
        def.gravityScale = gravityScale;
        def.linearDamping = linearDamping;
        def.angularDamping = angularDamping;
        def.isAwake = true;
        if (Transform != null)
        {
            Float3 p = Transform.Position;
            def.position = new B2Vec2(p.X, p.Y);
            def.rotation = B2MathFunction.b2MakeRot(Maths.Deg2Rad * Transform.EulerAngles.Z);
        }

        def.userData = B2UserData.Ref(this);
        _bodyId = B2Bodies.b2CreateBody(world.WorldId, ref def);
        world.RegisterBody(this);
        _lastSyncedTransformVersion = Transform != null ? Transform.Version : 0;
        RebuildShapes();
    }

    internal void SyncTransformToBody(bool force)
    {
        if (!HasBody || Transform == null)
            return;

        bool userEdited = Transform.Version != _lastSyncedTransformVersion;
        // Static and kinematic always push before the step; dynamic only when the Transform changed.
        if (!force && bodyType == RigidbodyType2D.Dynamic && !userEdited)
            return;

        Float3 p = Transform.Position;
        B2Rot rot = B2MathFunction.b2MakeRot(Maths.Deg2Rad * Transform.EulerAngles.Z);
        B2Bodies.b2Body_SetTransform(_bodyId, new B2Vec2(p.X, p.Y), rot);
        _lastSyncedTransformVersion = Transform.Version;
    }

    internal void WriteBodyToTransform()
    {
        if (!HasBody || Transform == null)
            return;
        if (bodyType != RigidbodyType2D.Dynamic)
            return;

        if (fixedRotation)
            B2Bodies.b2Body_SetAngularVelocity(_bodyId, 0f);

        B2Vec2 p = B2Bodies.b2Body_GetPosition(_bodyId);
        B2Rot rot = B2Bodies.b2Body_GetRotation(_bodyId);
        float angleDeg = fixedRotation ? 0f : Maths.Rad2Deg * B2MathFunction.b2Rot_GetAngle(ref rot);

        Transform.Position = new Float3(p.X, p.Y, Transform.Position.Z);
        Transform.EulerAngles = new Float3(0f, 0f, angleDeg);
        _lastSyncedTransformVersion = Transform.Version;
    }

    private void ClaimChildColliders()
    {
        foreach (Collider2D collider in GetComponentsInChildren<Collider2D>())
            collider.TryAttachTo(this);
    }

    private void RebuildShapes()
    {
        if (!HasBody)
            return;

        foreach (Collider2D collider in GetComponents<Collider2D>())
        {
            if (collider.UsedByComposite)
                continue;
            collider.RecreateShape(_bodyId);
        }
    }

    private static B2BodyType ToBox2D(RigidbodyType2D type) => type switch
    {
        RigidbodyType2D.Kinematic => B2BodyType.b2_kinematicBody,
        RigidbodyType2D.Static => B2BodyType.b2_staticBody,
        _ => B2BodyType.b2_dynamicBody
    };
}
