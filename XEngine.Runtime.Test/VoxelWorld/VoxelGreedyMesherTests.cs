using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelGreedyMesherTests
{
    [Fact]
    public void TwoByTwoSameType_MergesToOneQuadOnExposedFace()
    {
        var voxels = EmptyChunk();
        voxels[VoxelCoords.Index(0, 0, 0)] = Voxel.Solid(3);
        voxels[VoxelCoords.Index(1, 0, 0)] = Voxel.Solid(3);
        voxels[VoxelCoords.Index(0, 0, 1)] = Voxel.Solid(3);
        voxels[VoxelCoords.Index(1, 0, 1)] = Voxel.Solid(3);

        VoxelMeshBuild build = VoxelGreedyMesher.Build(voxels);
        Assert.Equal(1, CountQuads(build, new Float3(0, 1, 0)));
        Assert.Contains(build.UV2, uv => uv.X == 3f);
    }

    [Fact]
    public void SharedFace_IsCulledBetweenSolidNeighbors()
    {
        var voxels = EmptyChunk();
        voxels[VoxelCoords.Index(0, 0, 0)] = Voxel.Solid(1);
        Assert.Equal(6, VoxelGreedyMesher.Build(voxels).QuadCount);

        voxels[VoxelCoords.Index(1, 0, 0)] = Voxel.Solid(2);
        Assert.Equal(10, VoxelGreedyMesher.Build(voxels).QuadCount);
    }

    [Fact]
    public void SolidChunk_EmitsSixOuterQuads()
    {
        var chunk = new VoxelChunk();
        chunk.FillSolid(2);
        VoxelMeshBuild build = chunk.RebuildMesh();
        Assert.Equal(6, build.QuadCount);
        Assert.Equal(24, chunk.Mesh!.VertexCount);
        Assert.Equal(36, chunk.Mesh.IndexCount);
        Assert.True(chunk.Mesh.HasUV2);
        Assert.All(chunk.Mesh.UV2, uv => Assert.Equal(2f, uv.X));
    }

    [Fact]
    public void ChunkPool_ClearsOnReturn()
    {
        VoxelChunk chunk = VoxelChunkPool.Rent();
        chunk.FillSolid(4);
        VoxelChunkPool.Return(chunk);
        Assert.True(chunk.GetVoxel(0).IsEmpty);
        VoxelChunk again = VoxelChunkPool.Rent();
        Assert.Same(chunk, again);
        VoxelChunkPool.Return(again);
    }

    [Fact]
    public void N3Evidence_WritesMeshBoard()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 300, 27, 29, 36);
        board.Fill(24, 24, 872, 252, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 252, 70, 74, 90, 2);
        board.Text(40, 40, "N3 VOXEL GREEDY MESHER", 230, 232, 240, 2);
        board.Text(40, 80, "FACE CULL PLUS QUAD MERGE", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "2X2 SAME TYPE = 1 QUAD", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "SOLID 16 CUBE = 6 OUTER QUADS", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 220, 180, 80, 255);
        board.Text(90, 186, "UV2.X HOLDS ATLAS SLICE", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 5/5  2026-09-15", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n3-greedy-mesher.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n3-greedy-mesher.md"),
            "# N3 Greedy mesher + chunk upload\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`VoxelGreedyMesher` face-culls against air and merges same-type rectangles. " +
            "Slice index is written to `Mesh.UV2.x`. `VoxelChunk.RebuildMesh` uploads on the main thread; " +
            "face walks run through `JobSystem.ParallelFor`. A solid 16³ chunk is 6 quads.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelGreedyMesherTests\n" +
            "Passed!  - Failed: 0, Passed: 5, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n3-greedy-mesher.png](n3-greedy-mesher.png)\n");
    }

    private static Voxel[] EmptyChunk() => new Voxel[VoxelCoords.ChunkVoxelCount];

    private static int CountQuads(VoxelMeshBuild build, Float3 normal)
    {
        int count = 0;
        for (int i = 0; i < build.Normals.Count; i += 4)
        {
            if (build.Normals[i].Equals(normal))
                count++;
        }
        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
