// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using DotRecast.Detour.Crowd;

using XEngine.Runtime.Navigation;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

public sealed class NavMeshCrowdTests : RuntimeTestBase
{
    public override void Dispose()
    {
        NavMesh.RemoveAllNavMeshData();
        base.Dispose();
    }

    [Fact]
    public void Crowd_TwoAgentsSwap_AndAvoidEachOther()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), SoloSettings());
        NavMesh.AddNavMeshData(data);

        NavMeshCrowdParams p = NavMeshCrowdParams.Default;
        p.Radius = 0.4f;
        p.MaxSpeed = 6f;
        p.Acceleration = 20f;
        p.Priority = 2f;

        DtCrowdAgent a = NavMesh.Crowd.AddAgent(new Float3(2f, 0f, 2f), p);
        DtCrowdAgent b = NavMesh.Crowd.AddAgent(new Float3(18f, 0f, 18f), p);
        Assert.True(NavMesh.Crowd.SetDestination(a, new Float3(18f, 0f, 18f)));
        Assert.True(NavMesh.Crowd.SetDestination(b, new Float3(2f, 0f, 2f)));
        Assert.Equal(2, NavMesh.Crowd.ActiveCount);

        for (int i = 0; i < 180; i++)
            NavMesh.Crowd.Update(1f / 60f);

        Float3 pa = NavMesh.Crowd.GetPosition(a);
        Float3 pb = NavMesh.Crowd.GetPosition(b);
        Assert.True(pa.X > 6f, $"agent A should walk toward B's start; pos={pa}");
        Assert.True(pb.X < 14f, $"agent B should walk toward A's start; pos={pb}");
        float dist = Float3.Distance(new Float3(pa.X, 0f, pa.Z), new Float3(pb.X, 0f, pb.Z));
        Assert.True(dist > 0.7f, $"crowd agents should not occupy the same point; dist={dist} a={pa} b={pb}");
    }

    [Fact]
    public void Crowd_TwelveAgents_WritePreview()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), SoloSettings());
        NavMesh.AddNavMeshData(data);

        NavMeshCrowdParams p = NavMeshCrowdParams.Default;
        p.Radius = 0.35f;
        p.MaxSpeed = 5f;
        p.Acceleration = 16f;

        var agents = new DtCrowdAgent[12];
        var trail = new List<Float3>(12);
        Float3 center = new(10f, 0f, 10f);
        for (int i = 0; i < agents.Length; i++)
        {
            float ang = i * MathF.PI * 2f / agents.Length;
            Float3 spawn = new(10f + MathF.Cos(ang) * 8f, 0f, 10f + MathF.Sin(ang) * 8f);
            agents[i] = NavMesh.Crowd.AddAgent(spawn, p);
            Assert.True(NavMesh.Crowd.SetDestination(agents[i], center));
        }

        for (int i = 0; i < 120; i++)
            NavMesh.Crowd.Update(1f / 60f);

        for (int i = 0; i < agents.Length; i++)
            trail.Add(NavMesh.Crowd.GetPosition(agents[i]));

        float minPair = float.MaxValue;
        for (int i = 0; i < trail.Count; i++)
        {
            for (int j = i + 1; j < trail.Count; j++)
            {
                float d = Dist2D(trail[i], trail[j]);
                if (d < minPair) minPair = d;
            }
        }

        Assert.True(minPair > 0.45f, $"crowd should keep agents apart; minPair={minPair}");

        NavMeshTriangulation tri = NavMeshGeometry.Extract(data.RuntimeMesh);
        string png = Path.Combine(FindEvidenceDir(), "2026-08-15-m5-crowd.png");
        NavMeshPreviewImage.WriteTopDownPng(png, tri, [center, center], 640, 640,
            trail: trail.ToArray(), destination: center);
        Assert.True(File.Exists(png));
        Assert.True(new FileInfo(png).Length > 200);
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

        string fallback = Path.Combine(Path.GetTempPath(), "xengine-navmesh-m5");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
