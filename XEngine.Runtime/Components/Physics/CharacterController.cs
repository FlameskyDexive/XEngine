// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// A character controller that handles collision detection and movement.
/// provides just the core functionality.
/// </summary>
[AddComponentMenu("Physics/Character Controller")]
[ComponentIcon("\uf70c")] // PersonRunning
public class CharacterController : MonoBehaviour
{
    /// <summary>
    /// The shape type used for the character controller collision detection.
    /// </summary>
    public enum ColliderShape
    {
        Capsule,
        Cylinder
    }

    public ColliderShape Shape = ColliderShape.Cylinder;
    public float Radius = 0.5f;
    public float Height = 1.8f;
    public float SkinWidth = 0.02f;

    /// <summary>
    /// Maximum angle in degrees for a surface to be considered walkable (default: 45 degrees).
    /// Unity name: <see cref="SlopeLimit"/>.
    /// </summary>
    public float MaxSlopeAngle = 55.0f;

    /// <summary>Unity name for <see cref="MaxSlopeAngle"/>.</summary>
    public float SlopeLimit
    {
        get => MaxSlopeAngle;
        set => MaxSlopeAngle = value;
    }

    /// <summary>
    /// Extra degrees allowed to *stay* grounded after already being supported.
    /// BEPU-style hysteresis so a 45° walkable slope does not flicker at 45.1°.
    /// </summary>
    public float SlopeHysteresis = 5.0f;

    /// <summary>
    /// Distance to snap down to ground when walking off slopes (default: 0.5)
    /// </summary>
    public float SnapDownDistance = 0.5f;

    /// <summary>
    /// Maximum height the character can step up onto (default: 0.3).
    /// Unity name: <see cref="StepOffset"/>.
    /// </summary>
    public float StepSize = 0.3f;

    /// <summary>Unity name for <see cref="StepSize"/>.</summary>
    public float StepOffset
    {
        get => StepSize;
        set => StepSize = value;
    }

    /// <summary>Moves smaller than this are ignored. Matches Unity's <c>minMoveDistance</c>.</summary>
    public float MinMoveDistance = 0.001f;

    /// <summary>Which sides hit during the last <see cref="Move"/>.</summary>
    public CollisionFlags collisionFlags { get; private set; }

    /// <summary>
    /// Whether the character controller is currently grounded.
    /// </summary>
    public bool IsGrounded { get; private set; }

    private ShapeCastHit lastGroundHit;
    private Float3 lastVelocity;
    private float _simpleMoveVertical;
    private readonly List<ShapeCastHit> _overlapHits = [];
    private readonly HashSet<Collider> _triggerOverlaps = [];
    private readonly HashSet<Collider> _triggerScratch = [];
    private readonly List<MonoBehaviour> _behaviourScratch = [];

    // Debug visualization for failed height attempts
    private bool failedHeightAttempt = false;
    private float failedAttemptHeight;
    private float failedAttemptRadius;

    /// <summary>
    /// Moves the character controller by the specified motion vector.
    /// This handles collision detection and sliding.
    /// Also updates the IsGrounded state.
    /// </summary>
    public void Move(Float3 motion)
    {
        Float3 position = GameObject.Transform.Position;
        lastVelocity = motion;
        collisionFlags = CollisionFlags.None;

        if (Float3.Length(motion) < MinMoveDistance)
        {
            UpdateGroundedState(position);
            UpdateTriggers(position);
            return;
        }

        // Use the grounded state from the end of the previous move for this
        // frame's step-up and snap decisions, since we haven't moved yet.
        bool wasGrounded = IsGrounded;

        // Perform movement with collision
        Float3 finalPosition = CollideAndSlide(position, motion, 0, wasGrounded);

        // Snap down to ground if moving horizontally on slopes
        if (wasGrounded && motion.Y <= 0)
        {
            finalPosition = SnapToGround(finalPosition);
        }

        GameObject.Transform.Position = finalPosition;

        // Update grounded state based on where we actually ended up this
        // frame, so callers see an up-to-date value on the next frame
        // (e.g. right after a jump leaves the ground). Prefer the below flag
        // harvested from the sweep rather than an extra ground shapecast.
        if ((collisionFlags & CollisionFlags.Below) != 0)
            IsGrounded = lastGroundHit.Hit && IsWalkable(lastGroundHit.Normal, wasGrounded);
        else
            UpdateGroundedState(finalPosition);

        UpdateTriggers(finalPosition);
    }

    /// <summary>
    /// Moves with <paramref name="speed"/> * dt and applies gravity.
    /// Returns true when the controller hit the ground. Matches Unity's <c>SimpleMove</c>.
    /// </summary>
    public bool SimpleMove(Float3 speed)
    {
        float dt = Time.DeltaTime;
        Float3 gravity = GameObject.Scene?.Physics?.Gravity ?? new Float3(0f, -9.81f, 0f);
        if (IsGrounded && speed.Y <= 0f)
            _simpleMoveVertical = 0f;
        else
            _simpleMoveVertical += gravity.Y * dt;

        Move((speed + new Float3(0f, _simpleMoveVertical, 0f)) * dt);
        if (IsGrounded)
            _simpleMoveVertical = 0f;
        return IsGrounded || (collisionFlags & CollisionFlags.Below) != 0;
    }

    /// <summary>
    /// Updates the grounded state by performing a ground check.
    /// </summary>
    private void UpdateGroundedState(Float3 position)
    {
        float groundCheckDistance = 0.1f;
        IsGrounded = PerformGroundCheck(position, groundCheckDistance, out lastGroundHit);
    }

    /// <summary>
    /// Performs a ground check using shape casting.
    /// Only considers the character grounded if the surface angle is walkable.
    /// </summary>
    private bool PerformGroundCheck(Float3 position, float distance, out ShapeCastHit hitInfo)
    {
        bool hit = PerformShapeCast(position, new Float3(0, -1, 0), distance, out hitInfo);

        if (!hit)
            return false;

        return IsWalkable(hitInfo.Normal, currentlySupported: IsGrounded);
    }

    private bool IsWalkable(Float3 normal, bool currentlySupported)
    {
        float slopeAngle = GetSlopeAngle(normal);
        float limit = currentlySupported ? MaxSlopeAngle + SlopeHysteresis : MaxSlopeAngle;
        return slopeAngle <= limit;
    }

    /// <summary>
    /// Attempts to set the height of the collider.
    /// Returns true if successful, false if the new size would collide with something.
    /// </summary>
    public bool TrySetHeight(float newHeight)
    {
        float minHeight = Shape == ColliderShape.Capsule ? Radius * 2 : 0.1f;
        if (newHeight <= minHeight)
        {
            failedHeightAttempt = false;
            return false;
        }

        Float3 position = GameObject.Transform.Position;
        bool wouldCollide = CheckShapeOverlap(position, newHeight, Radius);

        if (!wouldCollide)
        {
            Height = newHeight;
            failedHeightAttempt = false;
            return true;
        }

        // Store failed attempt for debug visualization
        failedHeightAttempt = true;
        failedAttemptHeight = newHeight;
        failedAttemptRadius = Radius;

        return false;
    }

    /// <summary>
    /// Attempts to set the radius of the collider.
    /// Returns true if successful, false if the new size would collide with something.
    /// </summary>
    public bool TrySetRadius(float newRadius)
    {
        if (newRadius <= 0)
            return false;

        if (Shape == ColliderShape.Capsule && newRadius * 2 >= Height)
            return false;

        Float3 position = GameObject.Transform.Position;
        bool wouldCollide = CheckShapeOverlap(position, Height, newRadius);

        if (!wouldCollide)
        {
            Radius = newRadius;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a shape with the given dimensions would overlap with anything.
    /// </summary>
    private bool CheckShapeOverlap(Float3 position, float height, float radius)
    {
        float effectiveRadius = radius - SkinWidth;

        if (Shape == ColliderShape.Capsule)
        {
            Float3 bottom = position + new Float3(0, radius, 0);
            Float3 top = position + new Float3(0, height - radius, 0);
            return GameObject.Scene.Physics.CheckCapsule(bottom, top, effectiveRadius);
        }
        else // Cylinder
        {
            Float3 center = position + new Float3(0, height * 0.5f, 0);
            return GameObject.Scene.Physics.CheckCylinder(center, effectiveRadius, height, Quaternion.Identity);
        }
    }

    private Float3 GetShapeCenter(Float3 position)
    {
        if (Shape == ColliderShape.Capsule)
            return position + new Float3(0, Height * 0.5f, 0);
        else // Cylinder
            return position + new Float3(0, Height * 0.5f, 0);
    }

    private Float3 GetCapsuleBottom(Float3 position)
    {
        return position + new Float3(0, Radius, 0);
    }

    private Float3 GetCapsuleTop(Float3 position)
    {
        return position + new Float3(0, Height - Radius, 0);
    }

    private float GetEffectiveRadius()
    {
        return Radius - SkinWidth;
    }

    /// <summary>
    /// Performs the body sweep through <see cref="PhysicsWorld.CapsuleCast"/> regardless of the visual
    /// <see cref="Shape"/>: a vertical cylinder swept laterally grazes its supporting floor along the
    /// whole bottom rim and Fabricates a start-of-cast Sides hit (fraction 0 vs Ground) that freezes
    /// movement; the rounded caps of a capsule keep standing contact out of lateral sweeps. The cap
    /// spheres use the same collapsed height so both shapes cover nearly identical volume.
    /// </summary>
    private bool PerformShapeCast(Float3 position, Float3 direction, float distance, out ShapeCastHit hitInfo)
    {
        position = position + new Float3(0, SkinWidth, 0);
        distance -= SkinWidth;
        if (distance <= 0f)
        {
            hitInfo = default;
            return false;
        }

        Float3 top = position + new Float3(0, Height - Radius, 0);
        return GameObject.Scene.Physics.CapsuleCast(
            GetCapsuleBottom(position),
            top,
            GetEffectiveRadius(),
            direction,
            distance,
            out hitInfo,
            LayerMask.Everything,
            QueryTriggerInteraction.Ignore
        );
    }

    private Float3 CollideAndSlide(Float3 position, Float3 velocity, int depth, bool grounded)
    {
        const int MaxDepth = 5;
        if (depth >= MaxDepth)
            return position;

        float moveDistance = Float3.Length(velocity);
        if (moveDistance < 0.0001)
            return position;

        Float3 moveDirection = Float3.Normalize(velocity);

        bool hit = PerformShapeCast(
            position,
            moveDirection,
            moveDistance + SkinWidth,
            out ShapeCastHit hitInfo
        );

        if (hit)
        {
            RecordHit(hitInfo, moveDirection, moveDistance);

            // Start-of-move penetration (Fraction ≈ 0, e.g. spawned against a wall): zero progress
            // plus slide recursion would freeze the controller forever — nudge out along the contact
            // normal like Unity's gradual depenetration so later sweeps regain freedom.
            if (hitInfo.Fraction <= 0f)
                position += hitInfo.Normal * (SkinWidth * 2f);

            // Move to safe distance from hit point. A cast starting inside the surface returns
            // Fraction ≈ 0; letting safeDistance go NEGATIVE here shoves the body backwards every
            // frame it grazes geometry — clamp so penetration resolves only through the slide step.
            float safeDistance = moveDistance * hitInfo.Fraction - SkinWidth;
            if (safeDistance < 0f)
                safeDistance = 0f;
            position += moveDirection * safeDistance;

            // Calculate remaining movement after hitting surface
            float remainingDistance = moveDistance - safeDistance;
            Float3 remainingMove = moveDirection * remainingDistance;

            // Check if this is a step we can climb
            // Only attempt step-up if we're grounded and moving mostly horizontally
            float horizontalSpeed = Maths.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
            if (grounded && horizontalSpeed > 0.0001 && StepSize > 0)
            {
                if (TryStepUp(position, moveDirection, remainingDistance, out Float3 steppedPosition))
                {
                    return steppedPosition;
                }
            }

            // Project remaining movement onto the hit surface (slide)
            Float3 slideMove = ProjectOntoSurface(remainingMove, hitInfo.Normal);

            // Recurse with remaining slide movement
            return CollideAndSlide(position, slideMove, depth + 1, grounded);
        }
        else
        {
            position += velocity;
        }

        return position;
    }

    /// <summary>
    /// Attempts to step up onto an obstacle.
    /// Returns true if step-up was successful, with the new position.
    /// </summary>
    private bool TryStepUp(Float3 position, Float3 moveDirection, float moveDistance, out Float3 newPosition)
    {
        newPosition = position;

        // Step 1: Extract horizontal direction
        Float3 forwardDirection = new(moveDirection.X, 0, moveDirection.Z);
        if (Float3.LengthSquared(forwardDirection) < 0.0001)
            return false; // No horizontal movement

        forwardDirection = Float3.Normalize(forwardDirection);

        // Step 2: Move up by StepSize
        Float3 upPosition = position + new Float3(0, StepSize, 0);

        // Step 3: Check if there's clearance at the elevated position
        bool hasOverheadClearance = !PerformShapeCast(
            position,
            new Float3(0, 1, 0),
            StepSize + SkinWidth,
            out _
        );

        if (!hasOverheadClearance)
            return false;

        // Step 4: Try to move forward at the elevated position
        bool hitAtElevated = PerformShapeCast(
            upPosition,
            forwardDirection,
            moveDistance + SkinWidth,
            out ShapeCastHit elevatedHit
        );

        // If we still hit something at the elevated position, we can't step up
        if (hitAtElevated && elevatedHit.Fraction < 0.5)
            return false;

        // Step 5: Move forward at elevated height
        Float3 forwardPosition = upPosition + forwardDirection * moveDistance;

        // Step 6: Cast down to find the actual step surface
        // Search from StepSize height down to slightly below original position for reliability sake
        float maxStepDownDistance = StepSize + SkinWidth + 0.1f;
        bool hasGroundBelow = PerformShapeCast(
            forwardPosition,
            new Float3(0, -1, 0),
            maxStepDownDistance,
            out ShapeCastHit downHit
        );

        if (hasGroundBelow)
        {
            // Verify the surface is walkable dont want to perform steps on steep surfaces or walls
            if (!IsWalkable(downHit.Normal, currentlySupported: true))
                return false; // Surface is too steep

            // Calculate the actual step height
            float actualStepHeight = StepSize - (downHit.Fraction * maxStepDownDistance - SkinWidth);

            // Only accept if we're actually stepping up (not down)
            if (actualStepHeight < 0.0)
                return false;

            // Step down onto the surface
            float stepDownDistance = downHit.Fraction * maxStepDownDistance - SkinWidth;
            newPosition = forwardPosition - new Float3(0, stepDownDistance, 0);
            return true;
        }

        // No ground found - don't allow step up as character would fall
        return false;
    }

    private Float3 ProjectOntoSurface(Float3 movement, Float3 surfaceNormal)
    {
        // Project remaining movement onto the hit surface
        return Float3.ProjectOntoPlane(movement, surfaceNormal);
    }

    /// <summary>
    /// Calculates the angle of a surface in degrees from horizontal.
    /// </summary>
    private float GetSlopeAngle(Float3 normal)
    {
        // Angle between surface normal and up vector
        return Maths.Acos(normal.Y) * (180.0f / Maths.PI);
    }

    /// <summary>
    /// Snaps the character down to the ground when walking on slopes.
    /// This prevents the character from "floating" when transitioning between slopes.
    /// </summary>
    private Float3 SnapToGround(Float3 position)
    {
        // Only snap if we have horizontal velocity
        float horizontalSpeed = Maths.Sqrt(lastVelocity.X * lastVelocity.X + lastVelocity.Z * lastVelocity.Z);
        if (horizontalSpeed < 0.0001f)
            return position;

        // Check if there's ground below us within snap distance
        bool hit = PerformShapeCast(
            position,
            new Float3(0, -1, 0),
            SnapDownDistance,
            out ShapeCastHit hitInfo
        );

        if (hit)
        {
            // Check if the surface is walkable
            if (IsWalkable(hitInfo.Normal, currentlySupported: true))
            {
                // Snap down to the surface
                float snapDistance = hitInfo.Fraction * SnapDownDistance - SkinWidth;
                if (snapDistance > 0)
                {
                    position.Y -= snapDistance;
                }
            }
        }

        return position;
    }

    private void RecordHit(in ShapeCastHit hitInfo, Float3 moveDirection, float moveDistance)
    {
        if (hitInfo.Normal.Y > 0.5f)
        {
            collisionFlags |= CollisionFlags.Below;
            lastGroundHit = hitInfo;
        }
        else if (hitInfo.Normal.Y < -0.5f)
        {
            collisionFlags |= CollisionFlags.Above;
        }
        else
        {
            collisionFlags |= CollisionFlags.Sides;
        }

        var hit = new ControllerColliderHit
        {
            Controller = this,
            Collider = hitInfo.Collider,
            Rigidbody = hitInfo.Rigidbody,
            Transform = hitInfo.Transform,
            GameObject = hitInfo.Collider?.GameObject ?? hitInfo.Rigidbody?.GameObject,
            Point = hitInfo.Point,
            Normal = hitInfo.Normal,
            MoveDirection = moveDirection,
            MoveLength = moveDistance,
        };

        GameObject.GetComponents(_behaviourScratch);
        for (int i = 0; i < _behaviourScratch.Count; i++)
            _behaviourScratch[i].OnControllerColliderHit(hit);
    }

    private void UpdateTriggers(Float3 position)
    {
        PhysicsWorld physics = GameObject.Scene?.Physics;
        if (physics == null) return;

        _overlapHits.Clear();
        if (Shape == ColliderShape.Capsule)
        {
            physics.OverlapCapsule(GetCapsuleBottom(position), GetCapsuleTop(position), GetEffectiveRadius(),
                _overlapHits, LayerMask.Everything, QueryTriggerInteraction.Collide);
        }
        else
        {
            physics.OverlapCylinder(GetShapeCenter(position), GetEffectiveRadius(), Height, Quaternion.Identity,
                _overlapHits, LayerMask.Everything, QueryTriggerInteraction.Collide);
        }

        _triggerScratch.Clear();
        for (int i = 0; i < _overlapHits.Count; i++)
        {
            Collider collider = _overlapHits[i].Collider;
            if (collider != null && collider.IsTrigger)
                _triggerScratch.Add(collider);
        }

        GameObject.GetComponents(_behaviourScratch);

        foreach (Collider previous in _triggerOverlaps)
        {
            if (!_triggerScratch.Contains(previous))
            {
                for (int i = 0; i < _behaviourScratch.Count; i++)
                    _behaviourScratch[i].InternalOnTrigger(PhysicsEventPhase.Exit, previous);
            }
        }

        foreach (Collider current in _triggerScratch)
        {
            bool entered = !_triggerOverlaps.Contains(current);
            PhysicsEventPhase phase = entered ? PhysicsEventPhase.Enter : PhysicsEventPhase.Stay;
            for (int i = 0; i < _behaviourScratch.Count; i++)
                _behaviourScratch[i].InternalOnTrigger(phase, current);
        }

        _triggerOverlaps.Clear();
        foreach (Collider current in _triggerScratch)
            _triggerOverlaps.Add(current);
    }

    public override void DrawGizmos()
    {
        if (GameObject.Scene.Physics == null) return;

        Float3 position = GameObject.Transform.Position;

        if (Shape == ColliderShape.Capsule)
        {
            Debug.DrawWireCapsule(GetCapsuleBottom(position), GetCapsuleTop(position), Radius, Color.Cyan, 16);
        }
        else // Cylinder
        {
            Debug.DrawWireCylinder(GetShapeCenter(position), Quaternion.Identity, Radius, Height, Color.Cyan, 16);
        }

        // Draw ground hit if grounded
        if (lastGroundHit.Hit)
        {
            lastGroundHit.DrawGizmos();
        }

        // Draw failed height attempt in red
        if (failedHeightAttempt)
        {
            if (Shape == ColliderShape.Capsule)
            {
                Float3 bottom = position + new Float3(0, failedAttemptRadius, 0);
                Float3 top = position + new Float3(0, failedAttemptHeight - failedAttemptRadius, 0);
                Debug.DrawWireCapsule(bottom, top, failedAttemptRadius, Color.Red, 16);
            }
            else // Cylinder
            {
                Float3 center = position + new Float3(0, failedAttemptHeight * 0.5f, 0);
                Debug.DrawWireCylinder(center, Quaternion.Identity, failedAttemptRadius, failedAttemptHeight, Color.Red, 16);
            }
        }
    }
}
