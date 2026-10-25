// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Box2D.NET;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>Base for 2D colliders attached to a <see cref="Rigidbody2D"/> (or a static body of their own).</summary>
public abstract class Collider2D : MonoBehaviour
{
    [SerializeField] private Float2 offset;

    /// <summary>Center offset of the shape in the body's local space (meters). Recreates the shape.</summary>
    public Float2 Offset
    {
        get => offset;
        set
        {
            offset = value;
            OnValidate();
            GetComponentInParent<CompositeCollider2D>()?.OnValidate();
        }
    }

    [SerializeField] private bool isTrigger;
    [SerializeField] private bool usedByComposite;
    [SerializeField] private PhysicsMaterial2D? sharedMaterial;

    private Rigidbody2D? _attachedBody;
    private B2ShapeId _shapeId;

    internal Rigidbody2D? AttachedBody => _attachedBody;
    internal B2ShapeId ShapeId => _shapeId;

    public PhysicsMaterial2D? SharedMaterial
    {
        get => sharedMaterial;
        set
        {
            sharedMaterial = value;
            OnValidate();
        }
    }

    public bool UsedByComposite
    {
        get => usedByComposite;
        set
        {
            if (usedByComposite == value)
                return;
            usedByComposite = value;
            if (usedByComposite)
            {
                DestroyShape();
                _attachedBody = null;
            }

            OnValidate();
            GetComponentInParent<CompositeCollider2D>()?.OnValidate();
        }
    }

    public bool IsTrigger
    {
        get => isTrigger;
        set
        {
            if (isTrigger == value)
                return;
            isTrigger = value;
            OnValidate();
        }
    }

    public event Action<Collision2D>? CollisionEnter;
    public event Action<Collision2D>? CollisionExit;
    public event Action<Collision2D>? TriggerEnter;
    public event Action<Collision2D>? TriggerExit;

    public override void OnEnable()
    {
        if (usedByComposite)
            return;

        Rigidbody2D? rb = GetComponentInParent<Rigidbody2D>();
        if (rb != null)
            rb.AttachCollider(this);
        else
            AttachAsStatic();
    }

    public override void OnDisable()
    {
        DestroyShape();
        _attachedBody = null;
    }

    public override void OnValidate()
    {
        if (usedByComposite)
            return;
        if (_attachedBody != null && _attachedBody.HasBody)
            RecreateShape(_attachedBody.BodyId);
    }

    internal bool TryAttachTo(Rigidbody2D body)
    {
        if (usedByComposite)
            return false;
        if (_attachedBody != null && _attachedBody != body)
            return false;

        _attachedBody = body;
        if (body.HasBody)
            RecreateShape(body.BodyId);
        return true;
    }

    internal void Detach()
    {
        DestroyShape();
        _attachedBody = null;
    }

    internal void RecreateShape(B2BodyId bodyId)
    {
        if (usedByComposite)
        {
            DestroyShape();
            return;
        }

        DestroyShape();
        if (!B2Worlds.b2Body_IsValid(bodyId))
            return;

        B2ShapeDef def = B2Types.b2DefaultShapeDef();
        def.density = ComputeDensity();
        def.updateBodyMass = true;
        def.userData = B2UserData.Ref(this);
        def.isSensor = isTrigger;
        def.enableContactEvents = !isTrigger;
        def.enableSensorEvents = true;
        def.enableCustomFiltering = true;
        if (sharedMaterial != null)
        {
            def.material.friction = sharedMaterial.Friction;
            def.material.restitution = sharedMaterial.Restitution;
        }
        _shapeId = CreateShape(bodyId, ref def);
        if (B2Worlds.b2Shape_IsValid(_shapeId))
        {
            B2Shapes.b2Shape_EnableSensorEvents(_shapeId, true);
            B2Shapes.b2Shape_EnableContactEvents(_shapeId, !isTrigger);
        }
        if (PhysicsWorld2D.DebugShapeCreation)
            Debug.Log($"[Phys2D] RecreateShape go={GameObject.Name} type={GetType().Name} body={_attachedBody?.GameObject.Name ?? "?"} sensor={isTrigger} ownerScene={GameObject.Scene?.Name}");
    }

    protected abstract B2ShapeId CreateShape(B2BodyId bodyId, ref B2ShapeDef def);

    protected abstract float ComputeDensity();

    private void AttachAsStatic()
    {
        // Static orphan colliders are created with a dedicated static Rigidbody2D in a later slice.
        // Core P3 bodies always carry their own colliders.
    }

    private void DestroyShape() => DestroyCreatedShapes();

    protected virtual void DestroyCreatedShapes()
    {
        if (B2Worlds.b2Shape_IsValid(_shapeId))
            B2Shapes.b2DestroyShape(_shapeId, updateBodyMass: true);
        _shapeId = default;
    }

    protected void ClearShapeId() => _shapeId = default;

    internal void RaiseCollisionEnter(in Collision2D collision) => CollisionEnter?.Invoke(collision);
    internal void RaiseCollisionExit(in Collision2D collision) => CollisionExit?.Invoke(collision);
    internal void RaiseTriggerEnter(in Collision2D collision) => TriggerEnter?.Invoke(collision);
    internal void RaiseTriggerExit(in Collision2D collision) => TriggerExit?.Invoke(collision);

    protected Color GizmoColor => IsTrigger ? Color.Cyan : Color.Green;

    protected bool TryPushColliderGizmoMatrix()
    {
        if (Transform == null)
            return false;
        Debug.PushMatrix(Float4x4.CreateTRS(Transform.Position, Transform.Rotation, Transform.LossyScale));
        return true;
    }
}
