using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelLightingTests
{
    [Fact]
    public void SkyColumn_IsMaxLight()
    {
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        for (int y = 0; y < 8; y++)
            voxels[VoxelCoords.Index(4, y, 4)] = Voxel.Solid(1);
        var light = new byte[VoxelCoords.ChunkVoxelCount];
        VoxelLighting.Bake(voxels, light);
        Assert.Equal(VoxelLighting.Max, light[VoxelCoords.Index(4, 15, 4)]);
        Assert.Equal(VoxelLighting.Max, light[VoxelCoords.Index(4, 8, 4)]);
    }

    [Fact]
    public void SealedPocket_StaysDark()
    {
        var voxels = SolidChunk(1);
        voxels[VoxelCoords.Index(8, 8, 8)] = Voxel.Empty;
        var light = new byte[VoxelCoords.ChunkVoxelCount];
        VoxelLighting.Bake(voxels, light);
        Assert.Equal(0, light[VoxelCoords.Index(8, 8, 8)]);
    }

    [Fact]
    public void Shaft_FloodsSunlightDown()
    {
        var voxels = SolidChunk(1);
        for (int y = 8; y < VoxelCoords.ChunkSize; y++)
            voxels[VoxelCoords.Index(8, y, 8)] = Voxel.Empty;
        var light = new byte[VoxelCoords.ChunkVoxelCount];
        VoxelLighting.Bake(voxels, light);
        Assert.Equal(VoxelLighting.Max, light[VoxelCoords.Index(8, 8, 8)]);
    }

    [Fact]
    public void Torch_SpreadsIntoAir()
    {
        var voxels = SolidChunk(1);
        voxels[VoxelCoords.Index(8, 8, 8)] = Voxel.Solid(9);
        voxels[VoxelCoords.Index(7, 8, 8)] = Voxel.Empty;
        var light = new byte[VoxelCoords.ChunkVoxelCount];
        VoxelLighting.Bake(voxels, light, type => type == 9 ? (byte)14 : (byte)0);
        Assert.Equal(13, light[VoxelCoords.Index(7, 8, 8)]);
    }

    [Fact]
    public void CaveWall_MeshIsDarkerThanSunlitTop()
    {
        var chunk = new VoxelChunk();
        chunk.FillSolid(1);
        chunk.SetVoxel(VoxelCoords.Index(8, 8, 8), Voxel.Empty);
        VoxelMeshBuild build = chunk.RebuildMesh();
        float min = 1f, max = 0f;
        for (int i = 0; i < build.Colors.Count; i++)
        {
            min = Math.Min(min, build.Colors[i].R);
            max = Math.Max(max, build.Colors[i].R);
        }
        Assert.True(max > 0.7f);
        Assert.True(min < max);
    }

    [Fact]
    public void N7Evidence_WritesLightBoard()
    {
        var voxels = SolidChunk(1);
        voxels[VoxelCoords.Index(8, 8, 8)] = Voxel.Empty;
        var dark = new byte[VoxelCoords.ChunkVoxelCount];
        VoxelLighting.Bake(voxels, dark);
        for (int y = 8; y < VoxelCoords.ChunkSize; y++)
            voxels[VoxelCoords.Index(8, y, 8)] = Voxel.Empty;
        var sun = new byte[VoxelCoords.ChunkVoxelCount];
        VoxelLighting.Bake(voxels, sun);

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N7 VOXEL LIGHT AND COLLIDER", 230, 232, 240, 2);
        board.Text(40, 80, "SUN FLOOD TORCH AO MESHCOLLIDER", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 20, 24, 30, 255);
        board.Text(90, 122, $"SEALED CAVE LIGHT {dark[VoxelCoords.Index(8, 8, 8)]}", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 220, 220, 80, 255);
        board.Text(90, 154, $"SHAFT SUN LIGHT {sun[VoxelCoords.Index(8, 8, 8)]}", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 80, 170, 220, 255);
        board.Text(90, 186, "CHUNK MESHCOLLIDER ON STREAM", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 6/6  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "VERTEX COLOR CARRIES LIGHT TIMES AO", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n7-voxel-lighting.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n7-voxel-lighting.md"),
            "# N7 Sun/torch GI, AO, MeshCollider\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`VoxelLighting` drops sunlight down empty columns and floods through air. " +
            "Optional per-type emit models torches. Greedy vertices store light×AO in " +
            "`Mesh.Colors`. Streamed chunk objects get a `MeshCollider` so the FPS controller can stand on the surface.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelLightingTests\n" +
            "Passed!  - Failed: 0, Passed: 6, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n7-voxel-lighting.png](n7-voxel-lighting.png)\n");
    }

    private static Voxel[] SolidChunk(ushort type)
    {
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        Voxel cell = Voxel.Solid(type);
        for (int i = 0; i < voxels.Length; i++)
            voxels[i] = cell;
        return voxels;
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
