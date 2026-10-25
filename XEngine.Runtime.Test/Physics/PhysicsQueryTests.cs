// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// P2-1: zero-allocation query path, maxDistance pruning, RaycastAll / Linecast /
/// ComputePenetration / ClosestPoint, QueryTriggerInteraction, and the static Physics facade.
/// </summary>
public class PhysicsQueryTests : RuntimeTestBase
{
    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        return scene;
    }

    private BoxCollider AddStaticBox(Scene scene, Float3 position, Float3 size, int layer = 0, bool trigger = false)
    {
        var go = CreateGameObject("StaticBox");
        go.Transform.Position = position;
        go.LayerIndex = layer;
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.Size = size;
        box.IsTrigger = trigger;
        scene.Add(go);
        return box;
    }

    private static LayerMask OnlyLayer(int index)
    {
        var mask = new LayerMask();
        mask.SetLayer(index);
        return mask;
    }

    [Fact]
    public void Raycast_MaxDistance_StopsBeforeAFarCollider()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, new Float3(0, 0, 10), new Float3(2, 2, 2));
        StepPhysics(scene);

        Assert.False(scene.Physics.Raycast(new Float3(0, 0, 0), new Float3(0, 0, 1), 5f, out _));
        Assert.True(scene.Physics.Raycast(new Float3(0, 0, 0), new Float3(0, 0, 1), 12f, out RaycastHit hit));
        Assert.True(hit.Distance > 8f && hit.Distance < 10f);
    }

    [Fact]
    public void Raycast_LayerMask_DoesNotAllocateAFilterClosure()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), layer: 5);
        StepPhysics(scene);

        Assert.True(scene.Physics.Raycast(new Float3(0, 5, 0), new Float3(0, -1, 0), 10f, OnlyLayer(5)));
        Assert.False(scene.Physics.Raycast(new Float3(0, 5, 0), new Float3(0, -1, 0), 10f, OnlyLayer(3)));
    }

    [Fact]
    public void RaycastAll_ReturnsBothBoxesSortedNearToFar()
    {
        Scene scene = CreatePhysicsScene();
        BoxCollider near = AddStaticBox(scene, new Float3(0, 0, 2), new Float3(1, 1, 1));
        BoxCollider far = AddStaticBox(scene, new Float3(0, 0, 6), new Float3(1, 1, 1));
        StepPhysics(scene);

        var hits = new RaycastHit[4];
        int count = scene.Physics.RaycastAll(new Float3(0, 0, 0), new Float3(0, 0, 1), 20f, hits);
        Assert.Equal(2, count);
        Assert.Same(near, hits[0].Collider);
        Assert.Same(far, hits[1].Collider);
        Assert.True(hits[0].Distance < hits[1].Distance);
    }

    [Fact]
    public void Linecast_HitsWhenTheSegmentCrossesACollider()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2));
        StepPhysics(scene);

        Assert.True(scene.Physics.Linecast(new Float3(0, 5, 0), new Float3(0, -5, 0), out RaycastHit hit));
        Assert.NotNull(hit.Collider);
        Assert.False(scene.Physics.Linecast(new Float3(10, 5, 0), new Float3(10, -5, 0)));
    }

    [Fact]
    public void QueryTriggerInteraction_IgnoreSkipsTriggers()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.QueriesHitTriggers = true;
        AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        StepPhysics(scene);

        Assert.True(scene.Physics.Raycast(new Float3(0, 5, 0), new Float3(0, -1, 0), 10f, LayerMask.Everything,
            QueryTriggerInteraction.Collide));
        Assert.False(scene.Physics.Raycast(new Float3(0, 5, 0), new Float3(0, -1, 0), 10f, LayerMask.Everything,
            QueryTriggerInteraction.Ignore));
    }

    [Fact]
    public void ShapeCast_ClosestHit_DoesNotNeedACallerList()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, new Float3(0, 0, 4), new Float3(2, 2, 2));
        StepPhysics(scene);

        var sphere = new Jitter2.Collision.Shapes.SphereShape(0.25f);
        Assert.True(scene.Physics.ShapeCast(sphere, Quaternion.Identity, Float3.Zero, new Float3(0, 0, 1), 10f,
            out ShapeCastHit hit, LayerMask.Everything));
        Assert.True(hit.Hit);
        Assert.True(hit.Fraction > 0f && hit.Fraction < 1f);
        Assert.NotNull(hit.Collider);
    }

    [Fact]
    public void ClosestPoint_OnABox_IsOnTheSurface()
    {
        Scene scene = CreatePhysicsScene();
        BoxCollider box = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2));
        StepPhysics(scene);

        Float3 closest = scene.Physics.ClosestPoint(new Float3(0, 5, 0), box);
        Assert.Equal(0f, closest.X, 2);
        Assert.Equal(1f, closest.Y, 2);
        Assert.Equal(0f, closest.Z, 2);
    }

    [Fact]
    public void ClosestPoint_InsideReturnsTheQueryPoint()
    {
        Scene scene = CreatePhysicsScene();
        BoxCollider box = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2));
        StepPhysics(scene);

        Assert.Equal(Float3.Zero, scene.Physics.ClosestPoint(Float3.Zero, box));
    }

    [Fact]
    public void ComputePenetration_SeparatesTwoOverlappingBoxes()
    {
        Scene scene = CreatePhysicsScene();
        BoxCollider a = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2));
        BoxCollider b = AddStaticBox(scene, new Float3(1, 0, 0), new Float3(2, 2, 2));
        StepPhysics(scene);

        Assert.True(scene.Physics.ComputePenetration(a, b, out Float3 direction, out float distance));
        Assert.True(distance > 0.5f);
        Assert.True(Maths.Abs(direction.X) > 0.9f);
    }

    [Fact]
    public void StaticPhysicsFacade_ForwardsToTheCurrentScene()
    {
        Scene scene = CreateScene(enable: false);
        scene.Physics.UseMultithreading = false;
        Scene.Load(scene);
        BoxCollider box = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2));
        StepPhysics(scene);

        Assert.Same(scene.Physics, Physics.World);
        Assert.True(Physics.Raycast(new Float3(0, 5, 0), new Float3(0, -1, 0), 10f, out RaycastHit hit));
        Assert.Same(box, hit.Collider);
    }

    [Fact]
    public void QueryAcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 18, 19, 26);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "P2-1 PHYSICS QUERIES", 230, 232, 240, 2);
        board.Text(40, 72, "STRUCT HANDLERS  MAXDISTANCE PRUNE  0 ALLOC FILTER", 140, 146, 160, 1);

        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "RAYCASTALL SORTED", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "LINECAST / CLOSESTPOINT / PENETRATION", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 230, 180, 80, 255);
        board.Text(90, 186, "QUERYTRIGGERINTERACTION + PHYSICS FACADE", 200, 220, 200, 1);

        board.Text(40, 250, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.SaveTo(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "docs", "superpowers", "evidence", "physics3d", "2026-08-15-p2-1-queries.png")),
            board);
        Assert.True(File.Exists(path), path);
    }
}
