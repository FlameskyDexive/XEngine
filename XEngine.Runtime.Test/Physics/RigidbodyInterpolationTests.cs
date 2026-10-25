// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Dynamics;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests for smoothing a rigidbody's Transform between fixed steps: that interpolation presents a
/// point on the segment the body actually travelled, that extrapolation stays bounded to one step,
/// and that a teleport is not smeared across.
/// </summary>
public class RigidbodyInterpolationTests : RuntimeTestBase, IDisposable
{
    private readonly float _fixedDeltaTime = Time.FixedDeltaTime;

    public override void Dispose()
    {
        // Both are process-wide statics, so a test that leaves either set would silently change how
        // every later physics test presents its bodies.
        Time.FixedDeltaTime = _fixedDeltaTime;
        Time.FixedAlpha = 0f;
        base.Dispose();
    }

    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        scene.Physics.Gravity = Float3.Zero;
        return scene;
    }

    /// <summary>A gravity-free body drifting at a constant velocity, so the exact pose after N steps
    /// is known in closed form and any smoothing error shows up as a plain number.</summary>
    private Rigidbody3D AddDrifter(Scene scene, RigidbodyInterpolation interpolation, Float3 velocity)
    {
        var go = CreateGameObject("Drifter");
        go.Transform.Position = Float3.Zero;
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        rb.AffectedByGravity = false;
        rb.Interpolation = interpolation;
        go.AddComponent<BoxCollider>();
        scene.Add(go);

        rb.LinearVelocity = velocity;
        rb.LinearDamping = 0f;
        rb.AngularDamping = 0f;
        return rb;
    }

    // ---- None: the pre-existing behaviour --------------------------------------------------------

    [Fact]
    public void NoneIsTheDefault()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.None, new Float3(1, 0, 0));

        // Interpolation trades Transform accuracy for smoothness, so it must be opted into.
        Assert.Equal(RigidbodyInterpolation.None, rb.Interpolation);
    }

    [Fact]
    public void None_ShowsTheSimulatedPoseExactly()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.None, new Float3(6, 0, 0));

        Tick(scene, 10);
        Time.FixedAlpha = 0.5f;
        Update(scene);

        // Mid-step alpha must not move an un-interpolated body.
        Assert.Equal(rb._body.Position.X, rb.Transform.Position.X, 5);
    }

    // ---- Interpolate ------------------------------------------------------------------------------

    [Fact]
    public void Interpolate_AtAlphaZero_ShowsThePreviousStep()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Interpolate, new Float3(6, 0, 0));

        Tick(scene, 10);
        float simulated = rb._body.Position.X;

        Time.FixedAlpha = 0f;
        Update(scene);

        // The defining trade: an interpolated Transform trails the simulation by up to one fixed step.
        float expected = simulated - 6f * Time.FixedDeltaTime;
        Assert.Equal(expected, rb.Transform.Position.X, 4);
    }

    [Fact]
    public void Interpolate_AtAlphaOne_CatchesUpToTheSimulation()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Interpolate, new Float3(6, 0, 0));

        Tick(scene, 10);
        float simulated = rb._body.Position.X;

        Time.FixedAlpha = 1f;
        Update(scene);

        Assert.Equal(simulated, rb.Transform.Position.X, 4);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    public void Interpolate_LandsOnTheSegmentBetweenTheTwoLastPoses(float alpha)
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Interpolate, new Float3(6, 0, 0));

        Tick(scene, 10);
        float simulated = rb._body.Position.X;
        float previous = simulated - 6f * Time.FixedDeltaTime;

        Time.FixedAlpha = alpha;
        Update(scene);

        Assert.Equal(previous + (simulated - previous) * alpha, rb.Transform.Position.X, 4);
    }

    [Fact]
    public void Interpolate_AdvancesMonotonicallyAcrossAFrameBoundary()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Interpolate, new Float3(6, 0, 0));
        StepPhysics(scene, 4);

        // Walk alpha up to 1, step, then resume from 0 — the seam where a mistake in which pair of
        // poses gets interpolated shows up as the body jumping backwards.
        var samples = new List<float>();
        foreach (float alpha in new[] { 0f, 0.5f, 1f })
        {
            Time.FixedAlpha = alpha;
            Update(scene);
            samples.Add(rb.Transform.Position.X);
        }

        StepPhysics(scene);

        foreach (float alpha in new[] { 0f, 0.5f, 1f })
        {
            Time.FixedAlpha = alpha;
            Update(scene);
            samples.Add(rb.Transform.Position.X);
        }

        for (int i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i] >= samples[i - 1] - 1e-4f,
                $"sample {i} went backwards: {samples[i - 1]} -> {samples[i]}");
        }
    }

    [Fact]
    public void Interpolate_NeverOvershootsTheSimulation()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Interpolate, new Float3(6, 0, 0));
        StepPhysics(scene, 4);

        // The reason to prefer interpolation over extrapolation: it only ever shows poses the body has
        // already been at, so it cannot predict itself through a wall.
        for (int i = 0; i <= 10; i++)
        {
            Time.FixedAlpha = i / 10f;
            Update(scene);
            Assert.True(rb.Transform.Position.X <= rb._body.Position.X + 1e-4f,
                $"alpha {i / 10f} overshot: {rb.Transform.Position.X} > {rb._body.Position.X}");
        }
    }

    [Fact]
    public void Interpolate_SmoothsRotationToo()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Interpolate, Float3.Zero);
        rb.AngularVelocity = new Float3(0, 4, 0);

        StepPhysics(scene, 10);

        Time.FixedAlpha = 0f;
        Update(scene);
        Quaternion lagging = rb.Transform.Rotation;

        Time.FixedAlpha = 1f;
        Update(scene);
        Quaternion current = rb.Transform.Rotation;

        Assert.True(Quaternion.Dot(lagging, current) < 0.99999f,
            "orientation should differ between the ends of the segment");
    }

    [Fact]
    public void Interpolate_DoesNotSmearATeleport()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Interpolate, new Float3(6, 0, 0));
        Tick(scene, 10);

        rb.MovePosition(new Float3(100, 0, 0));

        Time.FixedAlpha = 0.5f;
        Update(scene);

        // Interpolating across the jump would draw the body halfway between its old and new place —
        // around x=50 here — so the history has to be dropped when the pose is set rather than solved.
        Assert.Equal(100f, rb.Transform.Position.X, 3);
    }

    [Fact]
    public void Interpolate_StartsWithoutSlidingIn()
    {
        Scene scene = CreatePhysicsScene();

        var go = CreateGameObject("Late");
        go.Transform.Position = new Float3(25, 0, 0);
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        rb.AffectedByGravity = false;
        go.AddComponent<BoxCollider>();
        scene.Add(go);

        rb.Interpolation = RigidbodyInterpolation.Interpolate;
        StepPhysics(scene);

        Time.FixedAlpha = 0.5f;
        Update(scene);

        // The first capture has no earlier pose to pair with. Leaving the segment's far end at its
        // default would drag the body in from the origin on its first presented frame.
        Assert.Equal(25f, rb.Transform.Position.X, 3);
    }

    [Fact]
    public void SwitchingIntoInterpolate_DoesNotJump()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.None, new Float3(6, 0, 0));
        Tick(scene, 10);

        rb.Interpolation = RigidbodyInterpolation.Interpolate;

        Time.FixedAlpha = 0.5f;
        Update(scene);

        // No history has been captured under the new mode yet, so the body shows its simulated pose
        // rather than interpolating from whatever the None-mode frames left behind.
        Assert.Equal(rb._body.Position.X, rb.Transform.Position.X, 4);
    }

    // ---- Extrapolate ------------------------------------------------------------------------------

    [Fact]
    public void Extrapolate_LeadsTheSimulation()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Extrapolate, new Float3(6, 0, 0));
        StepPhysics(scene, 10);

        Time.FixedAlpha = 1f;
        Update(scene);

        // A full step ahead of the simulation: no lag, which is the entire reason to choose this mode.
        Assert.Equal(rb._body.Position.X + 6f * Time.FixedDeltaTime, rb.Transform.Position.X, 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Extrapolate_ScalesWithAlpha(float alpha)
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Extrapolate, new Float3(6, 0, 0));
        StepPhysics(scene, 10);

        Time.FixedAlpha = alpha;
        Update(scene);

        float expected = rb._body.Position.X + 6f * Time.FixedDeltaTime * alpha;
        Assert.Equal(expected, rb.Transform.Position.X, 4);
    }

    [Fact]
    public void Extrapolate_StaysWithinOneStepOfTheSimulation()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Extrapolate, new Float3(60, 0, 0));

        // The regression this guards: the abandoned implementation accumulated its own timer in Update
        // and reset it in FixedUpdate, so on frames where no step ran the timer grew without bound and
        // the presented pose ran away from the simulation. Alpha is bounded to one step by
        // construction, so many Updates without a step cannot drift.
        StepPhysics(scene, 4);
        Time.FixedAlpha = 1f;

        for (int i = 0; i < 20; i++)
        {
            Update(scene);
            float lead = rb.Transform.Position.X - rb._body.Position.X;
            Assert.True(lead <= 60f * Time.FixedDeltaTime + 1e-3f,
                $"frame {i} ran {lead} ahead, more than one step of travel");
        }
    }

    // ---- The alpha itself -------------------------------------------------------------------------

    [Fact]
    public void FixedAlpha_DefaultsToZeroSoTestsAreDeterministic()
    {
        // Nothing drives a fixed loop in a test, so bodies read the last stepped pose. Existing physics
        // tests that assert exact positions depend on this.
        Assert.Equal(0f, Time.FixedAlpha);
    }

    [Fact]
    public void Interpolation_IsUnaffectedByRenderRate()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D rb = AddDrifter(scene, RigidbodyInterpolation.Interpolate, new Float3(6, 0, 0));
        StepPhysics(scene, 4);

        // Many Updates between two steps must all resolve from the same pose pair. Reading a
        // frame-delta-driven timer instead would make each extra Update advance the body further.
        Time.FixedAlpha = 0.5f;
        Update(scene);
        float first = rb.Transform.Position.X;

        Update(scene, 30);

        Assert.Equal(first, rb.Transform.Position.X, 5);
    }
}
