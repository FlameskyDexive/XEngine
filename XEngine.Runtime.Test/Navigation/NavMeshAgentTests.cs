// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Dynamics;

using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

public sealed class NavMeshAgentTests : RuntimeTestBase
{
    public override void Dispose()
    {
        NavMesh.RemoveAllNavMeshData();
        base.Dispose();
    }

    [Fact]
    public void Agent_WalksAcrossPlane_ToDestination()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), SoloSettings());
        NavMesh.AddNavMeshData(data);

        Scene scene = CreateScene(enable: true);
        NavMeshAgent agent = CreateAgent(scene, new Float3(2f, 0f, 2f), speed: 10f);
        Float3 dest = new(18f, 0f, 18f);
        Assert.True(agent.SetDestination(dest));
        Assert.True(agent.HasPath);

        Update(scene, 240);

        Assert.True(agent.RemainingDistance < 0.45f,
            $"agent should arrive; remaining={agent.RemainingDistance} pos={agent.Transform.Position}");
        Assert.InRange(agent.Transform.Position.X, 16.5f, 19.5f);
        Assert.InRange(agent.Transform.Position.Z, 16.5f, 19.5f);
    }

    [Fact]
    public void Agent_WritesKinematicRigidbody()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), SoloSettings());
        NavMesh.AddNavMeshData(data);

        Scene scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        GameObject go = CreateGameObject("AgentBody");
        go.Transform.Position = new Float3(2f, 0.5f, 2f);
        var rb = go.AddComponent<Rigidbody3D>();
        rb.MotionType = MotionType.Kinematic;
        rb.AffectedByGravity = false;
        go.AddComponent<BoxCollider>();
        var agent = go.AddComponent<NavMeshAgent>();
        agent.Speed = 10f;
        agent.Acceleration = 40f;
        agent.StoppingDistance = 0.2f;
        scene.Add(go);

        Assert.True(agent.SetDestination(new Float3(18f, 0f, 18f)));
        Tick(scene, 240);

        Assert.Equal(MotionType.Kinematic, rb.MotionType);
        Assert.True(agent.RemainingDistance < 0.6f, $"remaining={agent.RemainingDistance} pos={agent.Transform.Position}");
        Assert.InRange(rb.Transform.Position.X, 16f, 20f);
        Assert.InRange(go.Transform.Position.X, 16f, 20f);
    }

    [Fact]
    public void OffMeshLink_BakesAndAgentTraversesGap()
    {
        Scene scene = CreateScene(enable: true);
        AddStaticPlane(scene, "IslandA", Float3.Zero, 8f);
        AddStaticPlane(scene, "IslandB", new Float3(12f, 0f, 0f), 8f);

        GameObject linkGo = CreateGameObject("Jump");
        linkGo.Transform.Position = new Float3(10f, 0f, 4f);
        var link = linkGo.AddComponent<OffMeshLink>();
        link.StartPoint = new Float3(-3.5f, 0f, 0f);
        link.EndPoint = new Float3(3.5f, 0f, 0f);
        link.Radius = 0.5f;
        link.Bidirectional = true;
        link.Activated = true;
        scene.Add(linkGo);

        NavMeshData without = NavMeshBuilder.Build(TwoIslandsGeometry(withLink: false), SoloSettings());
        NavMesh.AddNavMeshData(without);
        var blocked = new NavMeshPath();
        bool crossedOpen = NavMesh.CalculatePath(new Float3(2f, 0f, 4f), new Float3(18f, 0f, 4f), blocked);
        NavMesh.RemoveAllNavMeshData();
        Assert.True(!crossedOpen || blocked.Status != NavMeshPathStatus.PathComplete ||
                    Dist2D(blocked[blocked.CornerCount - 1], new Float3(18f, 0f, 4f)) > 2f,
            "gap without a link should not yield a complete path onto island B");

        NavMeshData data = NavMeshBuilder.Build(scene, SoloSettings());
        Assert.True(data.TileData.Length > 0);
        NavMeshTriangulation tri = NavMeshGeometry.Extract(data.RuntimeMesh);
        Assert.True(tri.OffMeshLines.Length >= 2, "baked mesh should contain the off-mesh connection");

        NavMesh.AddNavMeshData(data);
        var path = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 4f), new Float3(18f, 0f, 4f), path),
            "path across the baked off-mesh link should succeed");
        Assert.True(path.CornerCount >= 2);
        Assert.InRange(path[path.CornerCount - 1].X, 16f, 20f);

        NavMeshAgent agent = CreateAgent(scene, new Float3(2f, 0f, 4f), speed: 8f);
        Assert.True(agent.SetDestination(new Float3(18f, 0f, 4f)));

        bool sawLink = false;
        for (int i = 0; i < 360; i++)
        {
            Update(scene, 1);
            if (agent.IsOnOffMeshLink) sawLink = true;
            if (agent.RemainingDistance < 0.5f && agent.Transform.Position.X > 16f)
                break;
        }

        Assert.True(agent.Transform.Position.X > 14f,
            $"agent should cross onto island B; pos={agent.Transform.Position} remaining={agent.RemainingDistance} sawLink={sawLink}");
        Assert.True(sawLink || agent.Transform.Position.X > 16f,
            "agent should either report an off-mesh traversal or finish on island B");
    }

    [Fact]
    public void Agent_Walk_WritesPreviewScreenshot()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), SoloSettings());
        NavMesh.AddNavMeshData(data);

        Scene scene = CreateScene(enable: true);
        NavMeshAgent agent = CreateAgent(scene, new Float3(2f, 0f, 2f), speed: 10f);
        Float3 dest = new(18f, 0f, 18f);
        Assert.True(agent.SetDestination(dest));

        var trail = new List<Float3>(64) { agent.Transform.Position };
        for (int i = 0; i < 240; i++)
        {
            Update(scene, 1);
            if ((i & 3) == 0)
                trail.Add(agent.Transform.Position);
            if (agent.RemainingDistance < 0.35f)
                break;
        }

        trail.Add(agent.Transform.Position);
        Assert.True(agent.RemainingDistance < 0.5f, $"remaining={agent.RemainingDistance}");

        var planned = new NavMeshPath();
        NavMesh.CalculatePath(new Float3(2f, 0f, 2f), dest, planned);
        NavMeshTriangulation tri = NavMeshGeometry.Extract(data.RuntimeMesh);
        string png = Path.Combine(FindEvidenceDir(), "2026-08-15-m3-agent.png");
        NavMeshPreviewImage.WriteTopDownPng(png, tri, planned.Corners.ToArray(), 640, 640,
            trail.ToArray(), agent.Transform.Position, dest);
        Assert.True(File.Exists(png));
        Assert.True(new FileInfo(png).Length > 200);
    }

    private NavMeshAgent CreateAgent(Scene scene, Float3 position, float speed)
    {
        GameObject go = CreateGameObject("Agent");
        go.Transform.Position = position;
        var agent = go.AddComponent<NavMeshAgent>();
        agent.Radius = 0.4f;
        agent.Height = 2f;
        agent.Speed = speed;
        agent.Acceleration = 40f;
        agent.StoppingDistance = 0.15f;
        agent.AutoBraking = true;
        agent.AutoTraverseOffMeshLink = true;
        scene.Add(go);
        return agent;
    }

    private void AddStaticPlane(Scene scene, string name, Float3 position, float size)
    {
        GameObject go = CreateGameObject(name);
        go.IsStatic = true;
        go.Transform.Position = position;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.Mesh = new AssetRef<Mesh>(new Mesh
        {
            Vertices = CreatePlane(size),
            Indices = CreatePlaneIndices(),
        });
        scene.Add(go);
    }

    private static NavMeshInputGeometry TwoIslandsGeometry(bool withLink)
    {
        var geom = new NavMeshInputGeometry();
        geom.AddMesh(CreatePlane(8f), CreatePlaneIndices(), Float4x4.Identity);
        geom.AddMesh(CreatePlane(8f), CreatePlaneIndices(), Float4x4.CreateTranslation(new Float3(12f, 0f, 0f)));
        if (withLink)
        {
            geom.AddOffMeshConnection(
                NavMeshConvert.ToRecast(new Float3(6.5f, 0f, 4f)),
                NavMeshConvert.ToRecast(new Float3(13.5f, 0f, 4f)),
                0.5f, true, NavMeshAreas.Jump, NavMeshAreas.FlagWalk | NavMeshAreas.FlagJump);
        }

        return geom;
    }

    private static NavMeshBuildSettings SoloSettings() => new()
    {
        TileSize = 0,
        CellSize = 0.3f,
        CellHeight = 0.2f,
        AgentRadius = 0.4f,
        AgentHeight = 2f,
        AgentMaxClimb = 0.4f,
        AgentMaxSlope = 45f,
        CollectStaticOnly = true,
    };

    private static Float3[] CreatePlane(float size) =>
    [
        new(0f, 0f, 0f),
        new(size, 0f, 0f),
        new(size, 0f, size),
        new(0f, 0f, size),
    ];

    private static uint[] CreatePlaneIndices() => [0, 1, 2, 0, 2, 3];

    private static float Dist2D(Float3 a, Float3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static string FindEvidenceDir()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
            {
                string dir = Path.Combine(current.FullName, "docs", "superpowers", "evidence", "navigation");
                Directory.CreateDirectory(dir);
                return dir;
            }

            current = current.Parent;
        }

        string fallback = Path.Combine(Path.GetTempPath(), "xengine-navmesh-m3");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
