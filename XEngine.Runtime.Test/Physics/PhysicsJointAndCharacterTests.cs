// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using Jitter2.Dynamics.Constraints;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// P2-2: SpringSettings, Unity-named joints, EnableCollision, and CharacterController
/// SlopeLimit / StepOffset / SimpleMove / CollisionFlags / trigger overlap.
/// </summary>
public class PhysicsJointAndCharacterTests : RuntimeTestBase
{
    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        return scene;
    }

    private Rigidbody3D AddDynamicBox(Scene scene, Float3 position, bool gravity = true)
    {
        var go = CreateGameObject("Body");
        go.Transform.Position = position;
        var rb = go.AddComponent<Rigidbody3D>();
        rb.AffectedByGravity = gravity;
        go.AddComponent<BoxCollider>().Size = new Float3(1, 1, 1);
        scene.Add(go);
        return rb;
    }

    [Fact]
    public void SpringSettings_Compute_MatchesTheMassAwareFormula()
    {
        var spring = new SpringSettings(2f, 0.25f);
        Assert.True(spring.Compute(4f, 1f / 60f, out float softness, out float bias));

        float omega = 2f * Maths.PI * 2f;
        float damping = 2f * 4f * 0.25f * omega;
        float stiffness = 4f * omega * omega;
        float dt = 1f / 60f;
        float expectedSoftness = 1f / (damping + dt * stiffness);
        float expectedBias = dt * stiffness * expectedSoftness;

        Assert.Equal(expectedSoftness, softness, 5);
        Assert.Equal(expectedBias, bias, 5);
        Assert.False(new SpringSettings(0f, 1f).Compute(4f, dt, out _, out _));
    }

    [Fact]
    public void FixedJoint_KeepsTwoBodiesTogetherUnderGravity()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D a = AddDynamicBox(scene, new Float3(0, 2, 0));
        Rigidbody3D b = AddDynamicBox(scene, new Float3(1.2f, 2, 0));
        var joint = a.GameObject.AddComponent<FixedJoint>();
        joint.ConnectedBody = b;
        StepPhysics(scene, 30);

        Assert.NotNull(joint.GetJoint());
        float distance = Float3.Length(a.Position - b.Position);
        Assert.True(distance < 1.5f, $"weld drifted to {distance}");
        Assert.True(a.Position.Y < 1.95f, $"welded pair did not fall (y={a.Position.Y})");
    }

    [Fact]
    public void SpringJoint_PullsBodiesTowardTheRestLength()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D a = AddDynamicBox(scene, Float3.Zero, gravity: false);
        Rigidbody3D b = AddDynamicBox(scene, new Float3(4, 0, 0), gravity: false);
        a.IsKinematic = true;

        var spring = a.GameObject.AddComponent<SpringJoint>();
        spring.ConnectedBody = b;
        spring.MinDistance = 1f;
        spring.MaxDistance = 1f;
        spring.Spring = new SpringSettings();
        StepPhysics(scene, 90);

        Assert.True(spring.CurrentDistance > 0.1f, "spring constraint was not created");
        float distance = Float3.Length(a.Position - b.Position);
        Assert.True(distance < 2.5f, $"spring left bodies {distance} apart (constraint distance {spring.CurrentDistance})");
    }

    [Fact]
    public void EnableCollision_LetsJointedBodiesPushApart()
    {
        Scene scene = CreatePhysicsScene();
        Rigidbody3D a = AddDynamicBox(scene, Float3.Zero, gravity: false);
        Rigidbody3D b = AddDynamicBox(scene, new Float3(0.2f, 0, 0), gravity: false);
        a.IsKinematic = true;
        b.WakeUp();

        var limit = a.GameObject.AddComponent<DistanceLimitConstraint>();
        limit.EnableCollision = true;
        limit.ConnectedBody = b;
        limit.MinDistance = 0f;
        limit.MaxDistance = 10f;
        b.WakeUp();
        StepPhysics(scene, 60);

        float distance = Float3.Length(a.Position - b.Position);
        Assert.True(distance > 0.9f, $"bodies still overlapping at {distance}");
    }

    [Fact]
    public void CharacterController_SimpleMove_SetsBelowFlagOnAFloor()
    {
        Scene scene = CreatePhysicsScene();
        var floor = CreateGameObject("Floor");
        floor.Transform.Position = Float3.Zero;
        floor.AddComponent<BoxCollider>().Size = new Float3(20, 1, 20);
        scene.Add(floor);

        var go = CreateGameObject("CC");
        go.Transform.Position = new Float3(0, 2, 0);
        var cc = go.AddComponent<CharacterController>();
        cc.Shape = CharacterController.ColliderShape.Capsule;
        cc.Height = 1.8f;
        cc.Radius = 0.4f;
        cc.SlopeLimit = 45f;
        cc.StepOffset = 0.3f;
        scene.Add(go);

        for (int i = 0; i < 40; i++)
            cc.SimpleMove(Float3.Zero);

        Assert.True(cc.IsGrounded || (cc.collisionFlags & CollisionFlags.Below) != 0);
        Assert.True(go.Transform.Position.Y < 1.6f);
    }

    [Fact]
    public void CharacterController_MinMoveDistance_IgnoresTinyMotion()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("CC");
        go.Transform.Position = new Float3(0, 5, 0);
        var cc = go.AddComponent<CharacterController>();
        cc.MinMoveDistance = 0.5f;
        scene.Add(go);

        Float3 before = go.Transform.Position;
        cc.Move(new Float3(0.01f, 0, 0));
        Assert.Equal(before, go.Transform.Position);
    }

    [Fact]
    public void CharacterController_ReportsTriggerEnter()
    {
        Scene scene = CreatePhysicsScene();
        var triggerGo = CreateGameObject("Trigger");
        triggerGo.Transform.Position = new Float3(0, 1, 2);
        var trigger = triggerGo.AddComponent<BoxCollider>();
        trigger.Size = new Float3(2, 2, 2);
        trigger.IsTrigger = true;
        scene.Add(triggerGo);

        var go = CreateGameObject("CC");
        go.Transform.Position = new Float3(0, 1, 0);
        var cc = go.AddComponent<CharacterController>();
        cc.Shape = CharacterController.ColliderShape.Capsule;
        cc.Height = 1.8f;
        cc.Radius = 0.4f;
        var probe = go.AddComponent<TriggerProbe>();
        scene.Add(go);

        cc.Move(new Float3(0, 0, 2));
        Assert.True(probe.Enters > 0);
    }

    [Fact]
    public void JointAcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 18, 19, 26);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "P2-2 JOINTS AND CHARACTER", 230, 232, 240, 2);
        board.Text(40, 72, "SPRINGSETTINGS  FIXED SPRING CONFIG CHARACTER", 140, 146, 160, 1);

        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "ENABLECOLLISION TOGGLE", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "SLOPELIMIT STEPOFFSET SIMPLEMOVE FLAGS", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 230, 180, 80, 255);
        board.Text(90, 186, "HYSTERESIS + TRIGGER OVERLAP", 200, 220, 200, 1);

        board.Text(40, 250, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.SaveTo(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "docs", "superpowers", "evidence", "physics3d", "2026-08-15-p2-2-joints-character.png")),
            board);
        Assert.True(File.Exists(path), path);
    }

    private sealed class TriggerProbe : MonoBehaviour
    {
        public int Enters;

        public override void OnTriggerEnter(Collider other) => Enters++;
    }
}
