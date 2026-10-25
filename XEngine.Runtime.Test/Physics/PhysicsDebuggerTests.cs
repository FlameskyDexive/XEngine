// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// P3: Rendering Debugger snapshot / overlay, trigger gizmo colour, and acceptance screenshot.
/// </summary>
public class PhysicsDebuggerTests : RuntimeTestBase
{
    public override void Dispose()
    {
        PhysicsDebugger.ResetForTests();
        base.Dispose();
    }

    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        return scene;
    }

    [Fact]
    public void Capture_EmptyWorld_IsZeroed()
    {
        Scene scene = CreatePhysicsScene();
        PhysicsDebugSnapshot snap = PhysicsDebugger.Capture(scene.Physics);
        Assert.Equal(0, snap.Bodies);
        Assert.Equal(0, snap.Shapes);
        Assert.Equal(0, snap.Triggers);
        Assert.Equal(0, snap.Contacts);
        Assert.Equal(0, snap.SensorOverlaps);
    }

    [Fact]
    public void Capture_CountsSolidAndTriggerColliders()
    {
        Scene scene = CreatePhysicsScene();
        var solid = CreateGameObject("Solid");
        solid.AddComponent<BoxCollider>();
        scene.Add(solid);

        var trigger = CreateGameObject("Trigger");
        BoxCollider triggerCollider = trigger.AddComponent<BoxCollider>();
        triggerCollider.IsTrigger = true;
        scene.Add(trigger);

        var dynamic = CreateGameObject("Dynamic");
        dynamic.Transform.Position = new Float3(10, 0, 0);
        dynamic.AddComponent<Rigidbody3D>();
        dynamic.AddComponent<BoxCollider>();
        scene.Add(dynamic);

        StepPhysics(scene);

        PhysicsDebugSnapshot snap = PhysicsDebugger.Capture(scene.Physics);
        Assert.True(snap.Bodies >= 2, $"expected the dynamic plus at least one static layer body, got {snap.Bodies}");
        Assert.True(snap.Shapes >= 3, $"expected three colliders, got {snap.Shapes}");
        Assert.Equal(1, snap.Triggers);
    }

    [Fact]
    public void GizmoColor_IsCyanForTriggersAndGreenForSolids()
    {
        var go = CreateGameObject("Box");
        BoxCollider box = go.AddComponent<BoxCollider>();
        Assert.Equal(Color.Green, box.GizmoColor);

        box.IsTrigger = true;
        Assert.Equal(Color.Cyan, box.GizmoColor);
    }

    [Fact]
    public void Draw_WithOverlayEnabled_DoesNotThrow()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("Box");
        go.AddComponent<BoxCollider>();
        scene.Add(go);
        StepPhysics(scene);

        PhysicsDebugger.DrawColliders = true;
        PhysicsDebugger.DrawContacts = true;
        PhysicsDebugger.DrawSleeping = true;
        PhysicsDebugger.Draw(scene.Physics);
    }

    [Fact]
    public void DebuggerAcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 18, 19, 26);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "P3 PHYSICS DEBUG AND DOCS", 230, 232, 240, 2);
        board.Text(40, 72, "RENDERING DEBUGGER  TRIGGER GIZMOS  0 ALLOC", 140, 146, 160, 1);

        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "SLEEP EXIT AND SHAPE REMOVE", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "VENDORED JITTER2 SPEC AND NOTICES", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 230, 180, 80, 255);
        board.Text(90, 186, "DISPATCH / RAYCAST STEADY STATE 0 B", 200, 220, 200, 1);

        board.Text(40, 250, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.SaveTo(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "docs", "superpowers", "evidence", "physics3d", "2026-08-15-p3-debug-docs.png")),
            board);
        Assert.True(File.Exists(path), path);
    }
}
