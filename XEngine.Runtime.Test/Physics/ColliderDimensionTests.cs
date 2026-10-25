// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests that a collider's authored dimensions are the dimensions simulated: that a value the kernel
/// would reject is clamped rather than thrown, that each shape is built from the right numbers in the
/// right order, and that a capsule's height means what a Unity user expects it to.
/// </summary>
public class ColliderDimensionTests : RuntimeTestBase
{
    private const float MinExtent = 0.01f;

    private T AddCollider<T>() where T : Collider, new()
    {
        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Collider");
        T collider = go.AddComponent<T>();
        scene.Add(go);
        return collider;
    }

    private static JBoundingBox BoundsOf(RigidBodyShape shape)
    {
        shape.CalculateBoundingBox(JQuaternion.Identity, JVector.Zero, out JBoundingBox box);
        return box;
    }

    // ---- Dimensions below the minimum are clamped, not thrown ------------------------------------

    [Fact]
    public void AZeroBoxSize_IsClamped()
    {
        // The kernel throws on a zero extent, so a size typed as 0 in the inspector has to be caught
        // before it gets there.
        BoxCollider collider = AddCollider<BoxCollider>();

        collider.Size = Float3.Zero;

        Assert.Equal(MinExtent, collider.Size.X, 5);
        Assert.Equal(MinExtent, collider.Size.Y, 5);
        Assert.Equal(MinExtent, collider.Size.Z, 5);
    }

    [Fact]
    public void ANegativeBoxSize_IsClamped()
    {
        BoxCollider collider = AddCollider<BoxCollider>();

        collider.Size = new Float3(-5, 2, -0.001f);

        Assert.Equal(MinExtent, collider.Size.X, 5);
        Assert.Equal(2f, collider.Size.Y, 5);
        Assert.Equal(MinExtent, collider.Size.Z, 5);
    }

    [Fact]
    public void TheClampedSizeIsWhatGetsSimulated()
    {
        // Clamping the field rather than only the shape argument is the point: the number shown in the
        // inspector, the gizmo drawn from it and the shape built from it are then the same size.
        BoxCollider collider = AddCollider<BoxCollider>();
        collider.Size = new Float3(0, 0, 0);

        JBoundingBox box = BoundsOf(collider.CreateShapes()[0]);

        Assert.Equal(collider.Size.X, box.Max.X - box.Min.X, 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void AnUnusableSphereRadius_IsClamped(float radius)
    {
        SphereCollider collider = AddCollider<SphereCollider>();

        collider.Radius = radius;

        Assert.Equal(MinExtent, collider.Radius, 5);
        Assert.Equal(MinExtent, ((SphereShape)collider.CreateShapes()[0]).Radius, 5);
    }

    [Fact]
    public void AnUnusableCylinderSize_IsClamped()
    {
        CylinderCollider collider = AddCollider<CylinderCollider>();

        collider.Radius = -1f;
        collider.Height = 0f;

        Assert.Equal(MinExtent, collider.Radius, 5);
        Assert.Equal(MinExtent, collider.Height, 5);
    }

    [Fact]
    public void AnUnusableConeSize_IsClamped()
    {
        ConeCollider collider = AddCollider<ConeCollider>();

        collider.Radius = 0f;
        collider.Height = -3f;

        Assert.Equal(MinExtent, collider.Radius, 5);
        Assert.Equal(MinExtent, collider.Height, 5);
    }

    [Fact]
    public void AnUnusableCapsuleSize_IsClamped()
    {
        CapsuleCollider collider = AddCollider<CapsuleCollider>();

        collider.Radius = 0f;
        collider.Height = 0f;

        Assert.Equal(MinExtent, collider.Radius, 5);
        Assert.Equal(MinExtent, collider.Height, 5);
    }

    [Fact]
    public void EveryColliderSurvivesBeingSetToZero()
    {
        // The claim being made is that no dimension can reach the kernel in a state it rejects, so the
        // test is that building the shape does not throw for any of them.
        AddCollider<BoxCollider>().Size = Float3.Zero;
        AddCollider<SphereCollider>().Radius = 0f;

        CapsuleCollider capsule = AddCollider<CapsuleCollider>();
        capsule.Radius = 0f;
        capsule.Height = 0f;

        CylinderCollider cylinder = AddCollider<CylinderCollider>();
        cylinder.Radius = 0f;
        cylinder.Height = 0f;

        ConeCollider cone = AddCollider<ConeCollider>();
        cone.Radius = 0f;
        cone.Height = 0f;

        Assert.NotNull(capsule.CreateShapes()[0]);
        Assert.NotNull(cylinder.CreateShapes()[0]);
        Assert.NotNull(cone.CreateShapes()[0]);
    }

    // ---- Each shape is built from the right numbers ------------------------------------------------

    [Fact]
    public void ABoxIsBuiltWithItsAxesInOrder()
    {
        BoxCollider collider = AddCollider<BoxCollider>();
        collider.Size = new Float3(1, 2, 3);

        JBoundingBox box = BoundsOf(collider.CreateShapes()[0]);

        Assert.Equal(1f, box.Max.X - box.Min.X, 4);
        Assert.Equal(2f, box.Max.Y - box.Min.Y, 4);
        Assert.Equal(3f, box.Max.Z - box.Min.Z, 4);
    }

    [Fact]
    public void ACylinderIsNotBuiltInsideOut()
    {
        // CylinderShape's constructor takes height first. Passing radius first built the two swapped, so
        // a tall thin cylinder simulated as a short wide one while the gizmo drew what was asked for.
        CylinderCollider collider = AddCollider<CylinderCollider>();
        collider.Radius = 0.25f;
        collider.Height = 4f;

        var shape = (CylinderShape)collider.CreateShapes()[0];

        Assert.Equal(4f, shape.Height, 4);
        Assert.Equal(0.25f, shape.Radius, 4);
    }

    [Fact]
    public void ACylindersSimulatedExtentMatchesItsAuthoredExtent()
    {
        CylinderCollider collider = AddCollider<CylinderCollider>();
        collider.Radius = 0.25f;
        collider.Height = 4f;

        JBoundingBox box = BoundsOf(collider.CreateShapes()[0]);

        Assert.Equal(4f, box.Max.Y - box.Min.Y, 3);
        Assert.Equal(0.5f, box.Max.X - box.Min.X, 3);
    }

    [Fact]
    public void AConeIsBuiltWithRadiusAndHeightTheRightWayRound()
    {
        ConeCollider collider = AddCollider<ConeCollider>();
        collider.Radius = 0.25f;
        collider.Height = 3f;

        var shape = (ConeShape)collider.CreateShapes()[0];

        Assert.Equal(0.25f, shape.Radius, 4);
        Assert.Equal(3f, shape.Height, 4);
    }

    // ---- A capsule's height is Unity's total height ------------------------------------------------

    [Fact]
    public void ACapsuleHeightIsTheTotalIncludingCaps()
    {
        // Unity's meaning. The kernel's CapsuleShape takes the cylindrical section instead, so the
        // difference has to be subtracted here or a capsule comes out two radii too long.
        CapsuleCollider collider = AddCollider<CapsuleCollider>();
        collider.Radius = 0.5f;
        collider.Height = 2f;

        var shape = (CapsuleShape)collider.CreateShapes()[0];

        Assert.Equal(1f, shape.Length, 4);
        Assert.Equal(0.5f, shape.Radius, 4);
    }

    [Fact]
    public void ACapsulesSimulatedExtentMatchesItsAuthoredHeight()
    {
        // The observable form of the claim, and the one a user would notice: a capsule authored 2 units
        // tall occupies 2 units, not 3.
        CapsuleCollider collider = AddCollider<CapsuleCollider>();
        collider.Radius = 0.5f;
        collider.Height = 2f;

        JBoundingBox box = BoundsOf(collider.CreateShapes()[0]);

        Assert.Equal(2f, box.Max.Y - box.Min.Y, 3);
        Assert.Equal(1f, box.Max.X - box.Min.X, 3);
    }

    [Fact]
    public void ACapsuleShorterThanItsCaps_BecomesASphere()
    {
        // Unity degenerates rather than refusing the value, and a zero-length capsule is exactly a
        // sphere, so nothing special is needed beyond not letting the length go negative.
        CapsuleCollider collider = AddCollider<CapsuleCollider>();
        collider.Radius = 1f;
        collider.Height = 0.5f;

        var shape = (CapsuleShape)collider.CreateShapes()[0];

        Assert.Equal(0f, shape.Length, 5);
        Assert.Equal(1f, shape.Radius, 4);
    }

    [Fact]
    public void ACapsuleExactlyTwiceItsRadius_IsASphere()
    {
        CapsuleCollider collider = AddCollider<CapsuleCollider>();
        collider.Radius = 0.75f;
        collider.Height = 1.5f;

        JBoundingBox box = BoundsOf(collider.CreateShapes()[0]);

        Assert.Equal(1.5f, box.Max.Y - box.Min.Y, 3);
        Assert.Equal(1.5f, box.Max.X - box.Min.X, 3);
    }

    [Fact]
    public void RaisingACapsulesRadius_ShortensItsCylinder()
    {
        // The two dimensions interact, so the derived length has to be recomputed on either setter
        // rather than cached when height is assigned.
        CapsuleCollider collider = AddCollider<CapsuleCollider>();
        collider.Height = 4f;
        collider.Radius = 0.5f;
        Assert.Equal(3f, ((CapsuleShape)collider.CreateShapes()[0]).Length, 4);

        collider.Radius = 1.5f;

        Assert.Equal(1f, ((CapsuleShape)collider.CreateShapes()[0]).Length, 4);
    }

    // ---- Dimensions reach the simulation ----------------------------------------------------------

    [Fact]
    public void AnAuthoredColliderSizeReachesTheBody()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;

        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        CylinderCollider collider = go.AddComponent<CylinderCollider>();
        scene.Add(go);

        collider.Radius = 0.25f;
        collider.Height = 4f;

        var shape = (CylinderShape)Assert.Single(rb._body.Shapes);
        Assert.Equal(4f, shape.Height, 3);
        Assert.Equal(0.25f, shape.Radius, 3);
    }
}

/// <summary>
/// Tests for the sleep-related setters on <see cref="Rigidbody3D"/>: that a value the kernel would
/// reject is refused with a useful message, and that the linear and angular thresholds reach the slots
/// they name.
/// </summary>
public class RigidbodySleepSettingTests : RuntimeTestBase
{
    private Rigidbody3D AddBody()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        scene.Physics.Gravity = Float3.Zero;

        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        go.AddComponent<BoxCollider>();
        scene.Add(go);
        return rb;
    }

    [Fact]
    public void ANegativeDeactivationTime_IsRefused()
    {
        Rigidbody3D rb = AddBody();

        Assert.Throws<ArgumentOutOfRangeException>(() => rb.DeactivationTime = -1f);
    }

    [Fact]
    public void ARefusedDeactivationTime_LeavesTheOldValue()
    {
        Rigidbody3D rb = AddBody();
        rb.DeactivationTime = 2f;

        Assert.Throws<ArgumentOutOfRangeException>(() => rb.DeactivationTime = -1f);

        Assert.Equal(2f, rb.DeactivationTime, 5);
        Assert.Equal(2f, rb._body.DeactivationTime.TotalSeconds, 4);
    }

    [Fact]
    public void ZeroDeactivationTime_IsAllowed()
    {
        // Zero is meaningful — sleep as soon as the body is slow enough — so the check is non-negative
        // rather than positive.
        Rigidbody3D rb = AddBody();

        rb.DeactivationTime = 0f;

        Assert.Equal(0f, rb._body.DeactivationTime.TotalSeconds, 5);
    }

    [Theory]
    [InlineData(-0.001f)]
    [InlineData(-100f)]
    public void ANegativeSleepThreshold_IsRefused(float value)
    {
        Rigidbody3D rb = AddBody();

        Assert.Throws<ArgumentOutOfRangeException>(() => rb.LinearSleepThreshold = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => rb.AngularSleepThreshold = value);
    }

    [Fact]
    public void TheLinearThresholdReachesTheLinearSlot()
    {
        // The kernel spells this tuple (angular, linear) while Damping is (linear, angular), so a
        // positional assignment that reads correctly puts each value in the other's slot. Defaults are
        // equal, which is why that went unnoticed.
        Rigidbody3D rb = AddBody();

        rb.LinearSleepThreshold = 5f;

        Assert.Equal(5f, rb._body.DeactivationThreshold.linear, 4);
        Assert.Equal(0.1f, rb._body.DeactivationThreshold.angular, 4);
    }

    [Fact]
    public void TheAngularThresholdReachesTheAngularSlot()
    {
        Rigidbody3D rb = AddBody();

        rb.AngularSleepThreshold = 7f;

        Assert.Equal(7f, rb._body.DeactivationThreshold.angular, 4);
        Assert.Equal(0.1f, rb._body.DeactivationThreshold.linear, 4);
    }

    [Fact]
    public void TheTwoThresholdsDoNotOverwriteEachOther()
    {
        Rigidbody3D rb = AddBody();

        rb.LinearSleepThreshold = 3f;
        rb.AngularSleepThreshold = 8f;

        Assert.Equal(3f, rb._body.DeactivationThreshold.linear, 4);
        Assert.Equal(8f, rb._body.DeactivationThreshold.angular, 4);
    }

    [Fact]
    public void TheThresholdsSurviveOnValidateInTheRightSlots()
    {
        // The property setters and UpdateProperties are two separate writes of the same pair, and only
        // one of them was wrong. This is the other path.
        Rigidbody3D rb = AddBody();
        rb.LinearSleepThreshold = 3f;
        rb.AngularSleepThreshold = 8f;

        rb.OnValidate();

        Assert.Equal(3f, rb._body.DeactivationThreshold.linear, 4);
        Assert.Equal(8f, rb._body.DeactivationThreshold.angular, 4);
    }

    [Fact]
    public void ARaisedLinearThreshold_LetsASlidingBodySleep()
    {
        // The behavioural form of the same claim: raising the *linear* threshold has to affect a body
        // that is translating. Under the swap it would have affected a spinning one instead.
        Rigidbody3D rb = AddBody();
        rb.LinearDamping = 0f;
        rb.DeactivationTime = 0.05f;
        rb.LinearSleepThreshold = 100f;

        rb.Velocity = new Float3(1f, 0, 0);
        StepPhysics(rb.GameObject.Scene, 20);

        Assert.True(rb.IsSleeping(), "a body below its linear threshold should be allowed to sleep");
    }

    [Fact]
    public void ARaisedAngularThreshold_DoesNotLetASlidingBodySleep()
    {
        // The negative half, which is what actually distinguishes the two slots.
        Rigidbody3D rb = AddBody();
        rb.LinearDamping = 0f;
        rb.DeactivationTime = 0.05f;
        rb.AngularSleepThreshold = 100f;

        rb.Velocity = new Float3(1f, 0, 0);
        StepPhysics(rb.GameObject.Scene, 20);

        Assert.False(rb.IsSleeping(), "translation is not governed by the angular threshold");
    }

    [Fact]
    public void ARaisedAngularThreshold_LetsASpinningBodySleep()
    {
        Rigidbody3D rb = AddBody();
        rb.AngularDamping = 0f;
        rb.DeactivationTime = 0.05f;
        rb.AngularSleepThreshold = 100f;

        rb.AngularVelocity = new Float3(0, 1f, 0);
        StepPhysics(rb.GameObject.Scene, 20);

        Assert.True(rb.IsSleeping(), "a body below its angular threshold should be allowed to sleep");
    }
}
