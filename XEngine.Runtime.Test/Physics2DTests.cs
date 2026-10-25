// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Runtime.Utils;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Wiring tests for Physics2D (Box2D.NET): per-scene worlds, fixed-step integration,
/// Rigidbody2D / BoxCollider2D transform sync, teleport, and 1 unit = 1 meter.
/// </summary>
public sealed class Physics2DTests : RuntimeTestBase
{
    [Fact]
    public void Scene_OwnsIndependentPhysics2DWorld()
    {
        Scene scene = CreateScene(enable: true);
        Assert.NotNull(scene.Physics2D);
        Assert.NotSame(scene.Physics, scene.Physics2D);
    }

    [Fact]
    public void Rigidbody2D_Mass_RejectsZeroAndNegative()
    {
        var rb = new Rigidbody2D();
        Assert.Throws<ArgumentException>(() => rb.Mass = 0f);
        Assert.Throws<ArgumentException>(() => rb.Mass = -5f);
    }

    [Fact]
    public void DynamicBox_FallsUnderGravity_AfterFixedSteps()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = AddBox(scene, new Float3(0f, 4f, 0f), RigidbodyType2D.Dynamic);

        float startY = go.Transform.Position.Y;
        Tick(scene, 60);

        Assert.True(go.Transform.Position.Y < startY - 1f,
            $"dynamic body should fall ~4.9 m in 1 s; y={go.Transform.Position.Y}");
        Assert.Equal(0f, go.Transform.Position.Z);
        Assert.Equal(0f, go.Transform.Position.X, 2);
    }

    [Fact]
    public void StaticBox_DoesNotFall()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = AddBox(scene, new Float3(0f, 4f, 0f), RigidbodyType2D.Static);

        Tick(scene, 60);

        Assert.Equal(4f, go.Transform.Position.Y, 3);
    }

    [Fact]
    public void KinematicBox_DoesNotFall_AndSyncsTransformBeforeStep()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = AddBox(scene, new Float3(0f, 10f, 0f), RigidbodyType2D.Kinematic);

        Tick(scene, 10);
        Assert.Equal(10f, go.Transform.Position.Y, 3);

        go.Transform.Position = new Float3(2f, 3f, 0f);
        Tick(scene, 1);

        Assert.Equal(2f, go.Transform.Position.X, 3);
        Assert.Equal(3f, go.Transform.Position.Y, 3);
    }

    [Fact]
    public void Teleport_SetsPoseAndWakesBody()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = AddBox(scene, new Float3(0f, 4f, 0f), RigidbodyType2D.Dynamic);
        var rb = go.GetComponent<Rigidbody2D>();

        rb.Teleport(new Float2(1.5f, 8f));

        Assert.True(rb.IsAwake);
        Assert.Equal(1.5f, go.Transform.Position.X, 3);
        Assert.Equal(8f, go.Transform.Position.Y, 3);
        Assert.Equal(0f, go.Transform.Position.Z);
    }

    [Fact]
    public void TwoScenes_HaveIsolatedWorlds()
    {
        Scene a = CreateScene(enable: true);
        Scene b = CreateScene(enable: true);
        GameObject falling = AddBox(a, new Float3(0f, 4f, 0f), RigidbodyType2D.Dynamic);
        GameObject parked = AddBox(b, new Float3(0f, 4f, 0f), RigidbodyType2D.Dynamic);

        Tick(a, 60);

        Assert.True(falling.Transform.Position.Y < 3f);
        Assert.Equal(4f, parked.Transform.Position.Y, 3);
    }

    [Fact]
    public void CircleCollider2D_DynamicLandsOnStaticFloor()
    {
        Scene scene = CreateScene(enable: true);
        AddFloor(scene);
        GameObject go = CreateGameObject("Circle");
        go.Transform.Position = new Float3(0f, 3f, 0f);
        go.AddComponent<Rigidbody2D>();
        go.AddComponent<CircleCollider2D>().Radius = 0.5f;
        scene.Add(go);

        Tick(scene, 120);

        Assert.InRange(go.Transform.Position.Y, 0.9f, 1.2f);
    }

    [Fact]
    public void CapsuleCollider2D_DynamicLandsOnStaticFloor()
    {
        Scene scene = CreateScene(enable: true);
        AddFloor(scene);
        GameObject go = CreateGameObject("Capsule");
        go.Transform.Position = new Float3(0f, 4f, 0f);
        go.AddComponent<Rigidbody2D>();
        var capsule = go.AddComponent<CapsuleCollider2D>();
        capsule.Radius = 0.25f;
        capsule.Height = 1.5f;
        scene.Add(go);

        Tick(scene, 120);

        // Vertical capsule: half-height 0.75, floor top at 0.5 => rest Y ≈ 1.25
        Assert.InRange(go.Transform.Position.Y, 1.1f, 1.5f);
    }

    [Fact]
    public void PolygonCollider2D_ConvexPointsLandOnStaticFloor()
    {
        Scene scene = CreateScene(enable: true);
        AddFloor(scene);
        GameObject go = CreateGameObject("Polygon");
        go.Transform.Position = new Float3(0f, 3f, 0f);
        go.AddComponent<Rigidbody2D>();
        go.AddComponent<PolygonCollider2D>().Points =
        [
            new Float2(-0.5f, -0.5f),
            new Float2(0.5f, -0.5f),
            new Float2(0f, 0.5f)
        ];
        scene.Add(go);

        Tick(scene, 120);

        Assert.InRange(go.Transform.Position.Y, 0.9f, 1.4f);
    }

    [Fact]
    public void Raycast_HitsStaticBoxAndFillsHit()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = AddFloor(scene);

        Assert.True(scene.Physics2D.Raycast(new Float2(0f, 5f), new Float2(0f, -1f), 10f, out RaycastHit2D hit));
        Assert.True(hit.Hit);
        Assert.InRange(hit.Point.Y, 0.4f, 0.6f);
        Assert.True(hit.Normal.Y > 0.9f);
        Assert.Same(floor.GetComponent<Rigidbody2D>(), hit.Rigidbody);
        Assert.Same(floor.GetComponent<BoxCollider2D>(), hit.Collider);
    }

    [Fact]
    public void Raycast_MissesWhenAimingAway()
    {
        Scene scene = CreateScene(enable: true);
        AddFloor(scene);

        Assert.False(scene.Physics2D.Raycast(new Float2(0f, 5f), new Float2(0f, 1f), 10f, out RaycastHit2D hit));
        Assert.False(hit.Hit);
    }

    [Fact]
    public void Raycast_NonAllocWritesIntoCallerSpan()
    {
        Scene scene = CreateScene(enable: true);
        AddFloor(scene);
        AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Static);

        var hits = new RaycastHit2D[4];
        int count = scene.Physics2D.Raycast(new Float2(0f, 6f), new Float2(0f, -1f), 20f, hits);

        Assert.Equal(2, count);
        Assert.True(hits[0].Hit);
        Assert.True(hits[1].Hit);
        Assert.False(hits[2].Hit);
    }

    [Fact]
    public void OverlapCircle_NonAllocFindsColliderAtOrigin()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = AddFloor(scene);
        var results = new Collider2D[4];
        int count = scene.Physics2D.OverlapCircle(new Float2(0f, 0f), 1f, results);

        Assert.Equal(1, count);
        Assert.Same(floor.GetComponent<BoxCollider2D>(), results[0]);
        Assert.Null(results[1]);
    }

    [Fact]
    public void OverlapCircle_EmptyWhenFarAway()
    {
        Scene scene = CreateScene(enable: true);
        AddFloor(scene);
        var results = new Collider2D[4];
        Assert.Equal(0, scene.Physics2D.OverlapCircle(new Float2(0f, 20f), 0.5f, results));
    }

    [Fact]
    public void Raycast_LayerMask_RespectsGameObjectLayer()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = AddFloor(scene);
        floor.LayerIndex = 5;

        Assert.True(scene.Physics2D.Raycast(new Float2(0f, 5f), new Float2(0f, -1f), 10f, out _, OnlyLayer(5)));
        Assert.False(scene.Physics2D.Raycast(new Float2(0f, 5f), new Float2(0f, -1f), 10f, out _, OnlyLayer(3)));
    }

    [Fact]
    public void OverlapCircle_LayerMask_RespectsGameObjectLayer()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = AddFloor(scene);
        floor.LayerIndex = 5;
        var results = new Collider2D[4];

        Assert.Equal(1, scene.Physics2D.OverlapCircle(new Float2(0f, 0f), 1f, results, OnlyLayer(5)));
        Assert.Equal(0, scene.Physics2D.OverlapCircle(new Float2(0f, 0f), 1f, results, OnlyLayer(3)));
    }

    [Fact]
    public void OverlapBox_NonAllocFindsOverlappingBox()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = AddFloor(scene);
        var results = new Collider2D[4];
        int count = scene.Physics2D.OverlapBox(new Float2(0f, 0.4f), new Float2(2f, 2f), 0f, results);

        Assert.Equal(1, count);
        Assert.Same(floor.GetComponent<BoxCollider2D>(), results[0]);
    }

    [Fact]
    public void CollisionEnter_FiresOnBothCollidersWhenDynamicHitsFloor()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = AddFloor(scene);
        GameObject box = AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Dynamic);
        int enters = 0;
        box.GetComponent<BoxCollider2D>().CollisionEnter += _ => enters++;
        floor.GetComponent<BoxCollider2D>().CollisionEnter += _ => enters++;

        Tick(scene, 120);

        Assert.True(enters >= 2, $"expected CollisionEnter on both colliders, got {enters}");
    }

    [Fact]
    public void Trigger_DoesNotBlockAndFiresTriggerEnter()
    {
        Scene scene = CreateScene(enable: true);
        GameObject triggerGo = CreateGameObject("Trigger");
        triggerGo.Transform.Position = Float3.Zero;
        var triggerBody = triggerGo.AddComponent<Rigidbody2D>();
        triggerBody.BodyType = RigidbodyType2D.Static;
        var trigger = triggerGo.AddComponent<BoxCollider2D>();
        trigger.Size = new Float2(40f, 1f);
        trigger.IsTrigger = true;
        scene.Add(triggerGo);

        GameObject box = AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Dynamic);
        int enters = 0;
        trigger.TriggerEnter += _ => enters++;

        Tick(scene, 120);

        Assert.True(box.Transform.Position.Y < 0f, $"sensor must not block; y={box.Transform.Position.Y}");
        Assert.True(enters >= 1, $"expected TriggerEnter, got {enters}");
    }

    [Fact]
    public void PhysicsMaterial2D_ZeroRestitutionStaysOnFloor()
    {
        Scene scene = CreateScene(enable: true);
        var mat = new PhysicsMaterial2D { Friction = 0.6f, Restitution = 0f };
        GameObject floor = AddFloor(scene);
        floor.GetComponent<BoxCollider2D>().SharedMaterial = mat;
        GameObject box = AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Dynamic);
        box.GetComponent<BoxCollider2D>().SharedMaterial = mat;

        Tick(scene, 180);

        Assert.InRange(box.Transform.Position.Y, 0.9f, 1.3f);
    }

    [Fact]
    public void PhysicsMaterial2D_HighRestitutionBouncesOffFloor()
    {
        Scene scene = CreateScene(enable: true);
        var mat = new PhysicsMaterial2D { Friction = 0f, Restitution = 1f };
        GameObject floor = AddFloor(scene);
        floor.GetComponent<BoxCollider2D>().SharedMaterial = mat;
        GameObject box = AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Dynamic);
        box.GetComponent<BoxCollider2D>().SharedMaterial = mat;

        float minY = box.Transform.Position.Y;
        for (int i = 0; i < 90; i++)
        {
            Tick(scene, 1);
            minY = Maths.Min(minY, box.Transform.Position.Y);
        }

        Assert.True(minY < 1.3f, $"should reach the floor; minY={minY}");
        Assert.True(box.Transform.Position.Y > minY + 0.4f,
            $"bouncy material should rebound; minY={minY} y={box.Transform.Position.Y}");
    }

    [Fact]
    public void UsedByComposite_WithoutComposite_DoesNotCreateShape()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = CreateGameObject("Floor");
        floor.Transform.Position = Float3.Zero;
        var rb = floor.AddComponent<Rigidbody2D>();
        rb.BodyType = RigidbodyType2D.Static;
        var col = floor.AddComponent<BoxCollider2D>();
        col.Size = new Float2(40f, 1f);
        col.UsedByComposite = true;
        scene.Add(floor);

        GameObject box = AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Dynamic);
        Tick(scene, 180);

        Assert.True(box.Transform.Position.Y < 0f,
            $"UsedByComposite without a composite must not collide; y={box.Transform.Position.Y}");
    }

    [Fact]
    public void CompositeCollider2D_ChildBoxes_SupportDynamic()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = CreateGameObject("CompositeFloor");
        floor.Transform.Position = Float3.Zero;
        var rb = floor.AddComponent<Rigidbody2D>();
        rb.BodyType = RigidbodyType2D.Static;
        floor.AddComponent<CompositeCollider2D>();

        GameObject left = CreateGameObject("Left");
        var leftCol = left.AddComponent<BoxCollider2D>();
        leftCol.Size = new Float2(2f, 1f);
        leftCol.UsedByComposite = true;
        left.SetParent(floor, worldPositionStays: false);
        left.Transform.LocalPosition = new Float3(-0.5f, 0f, 0f);

        GameObject right = CreateGameObject("Right");
        var rightCol = right.AddComponent<BoxCollider2D>();
        rightCol.Size = new Float2(2f, 1f);
        rightCol.UsedByComposite = true;
        right.SetParent(floor, worldPositionStays: false);
        right.Transform.LocalPosition = new Float3(0.5f, 0f, 0f);

        scene.Add(floor);

        GameObject box = AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Dynamic);
        Tick(scene, 180);

        Assert.InRange(box.Transform.Position.Y, 0.9f, 1.3f);
    }

    [Fact]
    public void BoxCollider2D_DrawGizmos_WritesDebugLines()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = AddBox(scene, Float3.Zero, RigidbodyType2D.Static);
        Debug.ClearGizmos();

        go.GetComponent<BoxCollider2D>().DrawGizmos();

        Assert.True(Debug.HasGizmos);
    }

    [Fact]
    public void PhysicsWorld2D_DrawDebug_WritesShapeOutlines()
    {
        Scene scene = CreateScene(enable: true);
        AddFloor(scene);
        Debug.ClearGizmos();

        scene.Physics2D.DrawDebug();

        Assert.True(Debug.HasGizmos);
    }

    [Fact]
    public void CollisionMatrix_DisabledLayers_DynamicPassesThroughStatic()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = AddFloor(scene);
        floor.LayerIndex = 1;
        GameObject box = AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Dynamic);
        box.LayerIndex = 2;
        CollisionMatrix.SetLayerCollision(1, 2, false);

        Tick(scene, 180);

        Assert.True(box.Transform.Position.Y < 0f,
            $"non-colliding layers should pass through; y={box.Transform.Position.Y}");
    }

    [Fact]
    public void CollisionMatrix_EnabledLayers_DynamicRestsOnStatic()
    {
        Scene scene = CreateScene(enable: true);
        GameObject floor = AddFloor(scene);
        floor.LayerIndex = 1;
        GameObject box = AddBox(scene, new Float3(0f, 3f, 0f), RigidbodyType2D.Dynamic);
        box.LayerIndex = 2;

        Tick(scene, 180);

        Assert.True(box.Transform.Position.Y > 0.5f,
            $"colliding layers should rest on the floor; y={box.Transform.Position.Y}");
    }

    public override void Dispose()
    {
        CollisionMatrix.Reset(); // engine default: every layer collides with every other.
        base.Dispose();
    }

    private static LayerMask OnlyLayer(int index)
    {
        var mask = new LayerMask();
        mask.SetLayer(index);
        return mask;
    }

    private GameObject AddFloor(Scene scene)
    {
        GameObject go = CreateGameObject("Floor");
        go.Transform.Position = Float3.Zero;
        var rb = go.AddComponent<Rigidbody2D>();
        rb.BodyType = RigidbodyType2D.Static;
        go.AddComponent<BoxCollider2D>().Size = new Float2(40f, 1f);
        scene.Add(go);
        return go;
    }

    private GameObject AddBox(Scene scene, Float3 position, RigidbodyType2D type)
    {
        GameObject go = CreateGameObject($"Box2D_{type}");
        go.Transform.Position = position;
        var rb = go.AddComponent<Rigidbody2D>();
        rb.BodyType = type;
        go.AddComponent<BoxCollider2D>();
        scene.Add(go);
        return go;
    }
}
