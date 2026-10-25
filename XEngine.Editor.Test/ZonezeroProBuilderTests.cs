using XEngine.Editor.Projects.Packages;
using XEngine.ProBuilder;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// zonezero M5 — com.xengine.probuilder: parametric shape meshes (grid counts, bounds,
/// normals) and the ProBuilderMesh component's renderer/collider wiring.
/// </summary>
public sealed class ZonezeroProBuilderTests : EditorTestHarness
{
    [Fact]
    public void PlaneShape_GridCountsBoundsAndUpNormals()
    {
        Mesh mesh = ProBuilderMesh.BuildShape(ProBuilderShape.Plane, new Float3(1000f, 0f, 1000f), segments: 3);

        // (segments+1)² vertex grid, 2 triangles per cell.
        Assert.Equal(16, mesh.Vertices.Length);
        Assert.Equal(3 * 3 * 6, mesh.Indices.Length);

        // Bounds match the full size on XZ, flat on Y.
        Assert.Equal(-500f, mesh.bounds.Min.X, 2);
        Assert.Equal(500f, mesh.bounds.Max.X, 2);
        Assert.Equal(0f, mesh.bounds.Min.Y, 3);
        Assert.Equal(0f, mesh.bounds.Max.Y, 3);

        // Ground grid: every normal points up.
        foreach (Float3 normal in mesh.Normals)
            Assert.True(normal.Y > 0.99f, $"plane normal must be +Y, got {normal}");
    }

    [Fact]
    public void CubeShape_BoundsAndFaceCount()
    {
        Mesh mesh = ProBuilderMesh.BuildShape(ProBuilderShape.Cube, new Float3(52.78f, 5.74f, 17.54f));
        Assert.Equal(24, mesh.Vertices.Length);            // 4 per face, sharp edges
        Assert.Equal(36, mesh.Indices.Length);             // 12 triangles
        Assert.Equal(-52.78f * 0.5f, mesh.bounds.Min.X, 2);
        Assert.Equal(5.74f * 0.5f, mesh.bounds.Max.Y, 2);
        Assert.Equal(17.54f * 0.5f, mesh.bounds.Max.Z, 2);
    }

    [Fact]
    public void StairsShape_StepRiseMatchesHeight()
    {
        // 0.9 height / 0.3 step → 3 steps like the ZZZ arena's three step cubes.
        Mesh mesh = ProBuilderMesh.BuildShape(ProBuilderShape.Stairs, new Float3(5.04f, 0.9f, 7.86f), stepHeight: 0.3f);

        Assert.Equal(0.9f, mesh.bounds.Max.Y, 3);          // top step at full height
        Assert.Equal(0f, mesh.bounds.Min.Y, 3);
        Assert.Equal(5.04f, mesh.bounds.Size.X, 2);
        Assert.Equal(7.86f, mesh.bounds.Size.Z, 2);

        // Steps ascend toward +Z: the vertex with max Z must sit at the top height (back face),
        // the vertex at min Z sits at the floor.
        float maxZ = mesh.bounds.Max.Z, minZ = mesh.bounds.Min.Z;
        float topAtFront = 0f, topAtBack = 0f;
        foreach (Float3 v in mesh.Vertices)
        {
            if (System.MathF.Abs(v.Z - minZ) < 0.001f) topAtFront = System.MathF.Max(topAtFront, v.Y);
            if (System.MathF.Abs(v.Z - maxZ) < 0.001f) topAtBack = System.MathF.Max(topAtBack, v.Y);
        }
        Assert.Equal(0.3f, topAtFront, 3);                 // first step only at the entry edge
        Assert.Equal(0.9f, topAtBack, 3);                  // full stack at the back edge
    }

    [Fact]
    public void ProBuilderMeshComponent_WiresRendererAndCollider()
    {
        var go = new GameObject("PbCube");
        try
        {
            var pb = go.AddComponent<ProBuilderMesh>();
            pb.Shape = ProBuilderShape.Cube;
            pb.Size = new Float3(2f, 2f, 2f);

            MeshRenderer? renderer = go.GetComponent<MeshRenderer>();
            Assert.NotNull(renderer);
            Assert.NotNull(renderer!.Mesh.Res);
            Assert.Equal(24, renderer.Mesh.Res!.Vertices.Length);

            // Static collider wiring: no Rigidbody → world-static MeshCollider.
            Assert.NotNull(go.GetComponent<MeshCollider>());
        }
        finally
        {
            go.Dispose();
        }
    }

    [Fact]
    public void ProBuilderPackage_LayoutAndDependencies()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.probuilder");
        Assert.True(Directory.Exists(root), "com.xengine.probuilder package missing");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.probuilder", package.Name);
        Assert.False(package.IsInstalledByDefault);
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "ProBuilderMesh.cs")));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent!;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
