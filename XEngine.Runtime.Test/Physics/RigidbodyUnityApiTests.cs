// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Jitter2.Dynamics;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests for the Unity-named surface on <see cref="Rigidbody3D"/>: that each alias is genuinely the
/// same storage as the engine name rather than a second copy of it, that the four
/// <see cref="ForceMode"/> interpretations produce the accelerations their names claim, and that a
/// rotation is normalised before it reaches the kernel.
/// </summary>
public class RigidbodyUnityApiTests : RuntimeTestBase
{
    private const float Dt = 1f / 60f;

    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        scene.Physics.Gravity = Float3.Zero;
        return scene;
    }

    // Bodies are spaced out along X because two unit boxes at the origin interpenetrate, and the
    // contact impulse separating them would swamp whatever the test is actually measuring.
    private int _bodyCount;

    private Rigidbody3D AddBody(Scene scene, float mass = 1f)
    {
        var go = CreateGameObject("Body");
        go.Transform.Position = new Float3(_bodyCount++ * 10f, 0, 0);
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        go.AddComponent<BoxCollider>();
        scene.Add(go);
        rb.Mass = mass;
        rb.LinearDamping = 0f;
        rb.AngularDamping = 0f;
        return rb;
    }

    /// <summary>
    /// Steps far enough for a queued force to reach velocity.
    /// </summary>
    /// <remarks>
    /// Two steps, because the kernel pipelines: the accumulator becomes a velocity delta at the end of
    /// one step and is integrated during the next. One step shows nothing, and any number beyond two
    /// shows the same value, since the accumulator was cleared.
    /// </remarks>
    private void StepForForce(Scene scene) => StepPhysics(scene, 2);

    // ---- Aliases share storage with the engine names ---------------------------------------------

    [Fact]
    public void Velocity_IsLinearVelocity()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Velocity = new Float3(1, 2, 3);

        Assert.Equal(new Float3(1, 2, 3), rb.LinearVelocity);

        rb.LinearVelocity = new Float3(4, 5, 6);

        Assert.Equal(new Float3(4, 5, 6), rb.Velocity);
    }

    [Fact]
    public void Drag_IsLinearDamping()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Drag = 0.25f;

        Assert.Equal(0.25f, rb.LinearDamping);
        Assert.Equal(0.25f, rb._body.Damping.linear, 5);
    }

    [Fact]
    public void AngularDrag_IsAngularDamping()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.AngularDrag = 0.4f;

        Assert.Equal(0.4f, rb.AngularDamping);
        Assert.Equal(0.4f, rb._body.Damping.angular, 5);
    }

    [Fact]
    public void Drag_AndAngularDrag_DoNotOverwriteEachOther()
    {
        // Jitter stores both in one tuple, so setting either has to read the other back rather than
        // assume a default.
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Drag = 0.3f;
        rb.AngularDrag = 0.7f;

        Assert.Equal(0.3f, rb.Drag, 5);
        Assert.Equal(0.7f, rb.AngularDrag, 5);
        Assert.Equal(0.3f, rb._body.Damping.linear, 5);
        Assert.Equal(0.7f, rb._body.Damping.angular, 5);
    }

    [Fact]
    public void UseGravity_IsAffectedByGravity()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.UseGravity = false;

        Assert.False(rb.AffectedByGravity);
        Assert.False(rb._body.AffectedByGravity);

        rb.AffectedByGravity = true;

        Assert.True(rb.UseGravity);
    }

    [Fact]
    public void UseGravity_ActuallyStopsTheFall()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = new Float3(0, -9.81f, 0);
        Rigidbody3D rb = AddBody(scene);

        rb.UseGravity = false;
        StepPhysics(scene, 10);

        Assert.Equal(0f, rb.LinearVelocity.Y, 4);
    }

    [Fact]
    public void IsKinematic_SelectsTheKinematicMotionType()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.IsKinematic = true;

        Assert.Equal(MotionType.Kinematic, rb.MotionType);
        Assert.Equal(MotionType.Kinematic, rb._body.Data.MotionType);
        Assert.True(rb.IsKinematic);
    }

    [Fact]
    public void ClearingIsKinematic_ReturnsToDynamic()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.IsKinematic = true;

        rb.IsKinematic = false;

        Assert.Equal(MotionType.Dynamic, rb.MotionType);
        Assert.False(rb.IsKinematic);
    }

    [Fact]
    public void IsKinematic_ReadsFalseForAStaticBody()
    {
        // The boolean cannot express three states, so the documented answer for Static is false rather
        // than an approximation of "not simulated".
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.MotionType = MotionType.Static;

        Assert.False(rb.IsKinematic);
    }

    // ---- Sleep and wake ---------------------------------------------------------------------------

    [Fact]
    public void Sleep_TakesEffectWithinTwoSteps()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Sleep();

        // Deferred by design, and by two steps rather than one: the request is a sleep-timer value that
        // the first step reads when deciding not to re-mark the island active, and the second step acts
        // on. Callers should not depend on the count, only on it not being immediate.
        StepPhysics(scene);
        Assert.False(rb.IsSleeping());

        StepPhysics(scene);
        Assert.True(rb.IsSleeping());
        Assert.False(rb.IsActive);
    }

    [Fact]
    public void WakeUp_UndoesSleep()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.Sleep();
        StepPhysics(scene, 2);
        Assert.True(rb.IsSleeping());

        rb.WakeUp();
        StepPhysics(scene);

        Assert.False(rb.IsSleeping());
        Assert.True(rb.IsActive);
    }

    [Fact]
    public void MotionCancelsAPendingSleep()
    {
        // The request is only a timer value, so a body that is still moving resets it. Worth pinning
        // because it means Sleep() on a moving body is a no-op rather than a hard stop.
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Sleep();
        rb.Velocity = new Float3(5, 0, 0);
        StepPhysics(scene, 4);

        Assert.False(rb.IsSleeping());
    }

    [Fact]
    public void IsSleeping_IsTheInverseOfIsActive()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        StepPhysics(scene);

        Assert.Equal(rb.IsActive, !rb.IsSleeping());
    }

    // ---- Position and Rotation read the body, not the Transform ----------------------------------

    [Fact]
    public void Position_ReadsTheSimulatedPose()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.Velocity = new Float3(10, 0, 0);

        StepPhysics(scene, 6);

        Assert.True(rb.Position.X > 0.5f, $"expected the body to have moved, it is at {rb.Position.X}");
        Assert.Equal(rb._body.Position.X, rb.Position.X, 5);
    }

    [Fact]
    public void Position_DivergesFromTheTransformWhileInterpolating()
    {
        // The point of exposing the body pose separately: with interpolation on, the Transform is a
        // presentation value and only Position is the simulation's answer.
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.Interpolation = RigidbodyInterpolation.Interpolate;
        rb.Velocity = new Float3(10, 0, 0);

        Tick(scene, 6);

        Assert.True(rb.Position.X - rb.Transform.Position.X > 0.05f,
            $"body at {rb.Position.X}, transform at {rb.Transform.Position.X}");
    }

    [Fact]
    public void SettingPosition_Teleports()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Position = new Float3(5, 6, 7);

        Assert.Equal(5f, rb._body.Position.X, 5);
        Assert.Equal(6f, rb._body.Position.Y, 5);
        Assert.Equal(7f, rb._body.Position.Z, 5);
    }

    [Fact]
    public void SettingPosition_DoesNotSmearAnInterpolatedBody()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.Interpolation = RigidbodyInterpolation.Interpolate;
        Tick(scene, 3);

        rb.Position = new Float3(100, 0, 0);
        Tick(scene);

        // Halfway between 0 and 100 is what a teleport that kept its history would draw.
        Assert.True(rb.Transform.Position.X > 99f,
            $"expected the transform at the new pose, it is at {rb.Transform.Position.X}");
    }

    [Fact]
    public void Rotation_RoundTrips()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        Quaternion target = Quaternion.FromEuler(new Float3(0, 90, 0));
        rb.Rotation = target;

        Quaternion read = rb.Rotation;

        // Sign is free in a quaternion, so compare the rotation they describe rather than components.
        Float3 rotated = read * new Float3(1, 0, 0);
        Float3 expected = target * new Float3(1, 0, 0);
        Assert.Equal(expected.X, rotated.X, 4);
        Assert.Equal(expected.Y, rotated.Y, 4);
        Assert.Equal(expected.Z, rotated.Z, 4);
    }

    // ---- Rotations are normalised before the kernel sees them -------------------------------------

    [Fact]
    public void ADenormalisedRotation_IsNormalisedRatherThanRejected()
    {
        // The kernel asserts unit length, and only in Debug builds. Passing a drifted quaternion
        // straight through would throw in the editor and skew inertia in a shipped player.
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Rotation = new Quaternion(0, 0, 0, 2f);

        Assert.Equal(1f, LengthSquared(rb._body.Orientation), 4);
    }

    [Fact]
    public void ADenormalisedTransformRotation_ReachesTheBodyAsAUnitQuaternion()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Transform.Rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.4f);
        rb.OnValidate();

        Assert.Equal(1f, LengthSquared(rb._body.Orientation), 4);
    }

    [Fact]
    public void ADegenerateRotation_BecomesIdentity()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.Rotation = new Quaternion(0, 0, 0, 0);

        Assert.Equal(1f, LengthSquared(rb._body.Orientation), 4);
    }

    private static float LengthSquared(Jitter2.LinearMath.JQuaternion q)
        => q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;

    // ---- ForceMode: the four interpretations ------------------------------------------------------

    [Fact]
    public void Force_AcceleratesByForceOverMassTimesTime()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene, mass: 2f);

        rb.AddForce(new Float3(10, 0, 0), ForceMode.Force);
        StepForForce(scene);

        Assert.Equal(10f / 2f * Dt, rb.Velocity.X, 4);
    }

    [Fact]
    public void Acceleration_IgnoresMass()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D light = AddBody(scene, mass: 1f);
        Rigidbody3D heavy = AddBody(scene, mass: 50f);

        light.AddForce(new Float3(10, 0, 0), ForceMode.Acceleration);
        heavy.AddForce(new Float3(10, 0, 0), ForceMode.Acceleration);
        StepForForce(scene);

        Assert.Equal(10f * Dt, light.Velocity.X, 4);
        Assert.Equal(10f * Dt, heavy.Velocity.X, 4);
    }

    [Fact]
    public void Impulse_DividesByMassAndLandsImmediately()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene, mass: 4f);

        rb.AddForce(new Float3(20, 0, 0), ForceMode.Impulse);

        // No step: the instant modes are what "instant" says.
        Assert.Equal(20f / 4f, rb.Velocity.X, 4);
    }

    [Fact]
    public void VelocityChange_IgnoresMassAndLandsImmediately()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene, mass: 4f);

        rb.AddForce(new Float3(20, 0, 0), ForceMode.VelocityChange);

        Assert.Equal(20f, rb.Velocity.X, 4);
    }

    [Fact]
    public void AForce_IsNotVisibleUntilTheStep()
    {
        // The other half of the split: a continuous mode queues into the accumulator, so reading
        // velocity straight after the call must show nothing.
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.AddForce(new Float3(100, 0, 0), ForceMode.Force);

        Assert.Equal(0f, rb.Velocity.X, 5);
    }

    [Fact]
    public void AddForce_WithoutAModeIsStillAForce()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D withMode = AddBody(scene, mass: 3f);
        Rigidbody3D withoutMode = AddBody(scene, mass: 3f);

        withMode.AddForce(new Float3(15, 0, 0), ForceMode.Force);
        withoutMode.AddForce(new Float3(15, 0, 0));
        StepForForce(scene);

        Assert.Equal(15f / 3f * Dt, withMode.Velocity.X, 4);
        Assert.Equal(withoutMode.Velocity.X, withMode.Velocity.X, 5);
    }

    [Fact]
    public void AnImpulse_WakesASleepingBody()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.Sleep();
        StepPhysics(scene, 2);
        Assert.True(rb.IsSleeping());

        rb.AddForce(new Float3(0, 0, 10), ForceMode.Impulse);
        StepPhysics(scene);

        Assert.False(rb.IsSleeping());
    }

    [Fact]
    public void ForcesAreIgnoredOnAKinematicBody()
    {
        // Unity's rule: a kinematic body is moved, not pushed. Without the guard, dividing by a zero
        // inverse mass would also be a silent infinity.
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.IsKinematic = true;

        rb.AddForce(new Float3(1000, 0, 0), ForceMode.Impulse);
        rb.AddForce(new Float3(1000, 0, 0), ForceMode.VelocityChange);
        StepForForce(scene);

        Assert.Equal(0f, rb.Velocity.X, 5);
    }

    [Fact]
    public void AddRelativeForce_IsRotatedIntoWorldSpace()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.Rotation = Quaternion.FromEuler(new Float3(0, 90, 0));

        // Local +X after a 90 degree yaw is world -Z.
        rb.AddRelativeForce(new Float3(10, 0, 0), ForceMode.VelocityChange);

        Assert.Equal(0f, rb.Velocity.X, 3);
        Assert.Equal(-10f, rb.Velocity.Z, 3);
    }

    // ---- ForceMode: the angular counterpart -------------------------------------------------------

    [Fact]
    public void AngularImpulse_DividesByInertia()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        float inertia = rb.InertiaTensor.Y;
        rb.AddTorque(new Float3(0, 1, 0), ForceMode.Impulse);

        Assert.Equal(1f / inertia, rb.AngularVelocity.Y, 3);
    }

    [Fact]
    public void AngularVelocityChange_IgnoresInertia()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.AddTorque(new Float3(0, 5, 0), ForceMode.VelocityChange);

        Assert.Equal(5f, rb.AngularVelocity.Y, 4);
    }

    [Fact]
    public void AngularAcceleration_IgnoresInertia()
    {
        // The mass-independent angular modes need the tensor rather than its inverse, so this is the
        // test that the inversion is actually done and in the right direction.
        Scene scene = CreatePhysicsScene();
        Rigidbody3D small = AddBody(scene);
        Rigidbody3D large = AddBody(scene, mass: 30f);

        small.AddTorque(new Float3(0, 4, 0), ForceMode.Acceleration);
        large.AddTorque(new Float3(0, 4, 0), ForceMode.Acceleration);
        StepForForce(scene);

        Assert.Equal(4f * Dt, small.AngularVelocity.Y, 3);
        Assert.Equal(4f * Dt, large.AngularVelocity.Y, 3);
    }

    [Fact]
    public void AngularForce_IsATorque()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        float inertia = rb.InertiaTensor.Y;
        rb.AddTorque(new Float3(0, 2, 0), ForceMode.Force);
        StepForForce(scene);

        Assert.Equal(2f / inertia * Dt, rb.AngularVelocity.Y, 3);
    }

    [Fact]
    public void AddTorque_WithoutAModeIsStillATorque()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D withMode = AddBody(scene);
        Rigidbody3D withoutMode = AddBody(scene);

        withMode.AddTorque(new Float3(0, 2, 0), ForceMode.Force);
        withoutMode.AddTorque(new Float3(0, 2, 0));
        StepForForce(scene);

        Assert.True(withMode.AngularVelocity.Y > 0.01f, "the torque should have landed");
        Assert.Equal(withoutMode.AngularVelocity.Y, withMode.AngularVelocity.Y, 5);
    }

    [Fact]
    public void AngularAcceleration_GivesUpOnAFullyLockedBody()
    {
        // A rotation-locked body has a singular inertia tensor, which is the documented reason the
        // Acceleration branch can decline. It must decline quietly rather than throw or emit NaN.
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.Constraints = RigidbodyConstraints.FreezeRotation;

        rb.AddTorque(new Float3(0, 10, 0), ForceMode.Acceleration);
        StepForForce(scene);

        Assert.Equal(0f, rb.AngularVelocity.Y, 5);
        Assert.False(float.IsNaN(rb.AngularVelocity.Y));
    }

    [Fact]
    public void AddRelativeTorque_IsRotatedIntoWorldSpace()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);
        rb.Rotation = Quaternion.FromEuler(new Float3(0, 90, 0));

        rb.AddRelativeTorque(new Float3(4, 0, 0), ForceMode.VelocityChange);

        Assert.Equal(0f, rb.AngularVelocity.X, 3);
        Assert.Equal(-4f, rb.AngularVelocity.Z, 3);
    }

    // ---- ForceMode values -------------------------------------------------------------------------

    [Fact]
    public void ForceModes_MatchUnitysValues()
    {
        // Pinned so a serialized scene or a copied Unity script keeps its meaning. Acceleration is 5,
        // not 3, in Unity.
        Assert.Equal(0, (int)ForceMode.Force);
        Assert.Equal(1, (int)ForceMode.Impulse);
        Assert.Equal(2, (int)ForceMode.VelocityChange);
        Assert.Equal(5, (int)ForceMode.Acceleration);
    }

    [Fact]
    public void AnUnknownForceMode_IsIgnored()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddBody(scene);

        rb.AddForce(new Float3(10, 0, 0), (ForceMode)99);
        StepForForce(scene);

        Assert.Equal(0f, rb.Velocity.X, 5);
    }
}
