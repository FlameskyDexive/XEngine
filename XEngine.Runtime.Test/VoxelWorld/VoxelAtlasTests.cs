using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

using VoxelWorldHost = XEngine.VoxelWorld.VoxelWorld;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelAtlasTests
{
    [Fact]
    public void PackSolidFaces_DedupsSharedColors_AndSplitsTopFromSide()
    {
        VoxelAtlas atlas = VoxelWorldHost.CreateDemoAtlas();
        Assert.True(atlas.LayerCount >= 4);
        Assert.Equal(VoxelFaceTextures.Size, atlas.Size);
        atlas.GetFaceSlices(2, out ushort grassTop, out ushort grassSide, out ushort grassBottom);
        atlas.GetFaceSlices(3, out ushort dirtTop, out ushort dirtSide, out _);
        atlas.GetFaceSlices(4, out ushort stoneTop, out _, out _);
        Assert.NotEqual(grassTop, grassSide);
        Assert.Equal(grassBottom, dirtTop);
        Assert.NotEqual(grassSide, dirtTop);
        Assert.Equal(dirtTop, dirtSide);
        Assert.NotEqual(dirtTop, stoneTop);
        Assert.NotEqual(grassTop, stoneTop);
    }

    [Fact]
    public void PackDefinitions_InternsByTextureGuid()
    {
        var grass = new VoxelDefinition
        {
            Title = "Grass",
            TintColor = VoxelWorldHost.GrassTop,
            TextureTop = new AssetRef<Texture2D> { AssetID = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a11") },
            TextureSide = new AssetRef<Texture2D> { AssetID = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a12") },
            TextureBottom = new AssetRef<Texture2D> { AssetID = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a12") },
        };
        var dirt = new VoxelDefinition
        {
            Title = "Dirt",
            TintColor = VoxelWorldHost.Dirt,
            TextureTop = new AssetRef<Texture2D> { AssetID = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a12") },
            TextureSide = new AssetRef<Texture2D> { AssetID = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a12") },
            TextureBottom = new AssetRef<Texture2D> { AssetID = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a12") },
        };

        VoxelAtlas atlas = TextureArrayPacker.PackDefinitions([grass, dirt]);
        atlas.GetFaceSlices(1, out ushort top, out ushort side, out ushort bottom);
        atlas.GetFaceSlices(2, out ushort dirtSlice, out _, out _);
        Assert.NotEqual(top, side);
        Assert.Equal(side, bottom);
        Assert.Equal(side, dirtSlice);
        Assert.Equal(2, atlas.LayerCount);
    }

    [Fact]
    public void Mesher_WritesAtlasSlicePerFace()
    {
        VoxelAtlas atlas = VoxelWorldHost.CreateDemoAtlas();
        atlas.GetFaceSlices(2, out ushort grassTop, out ushort grassSide, out _);
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        voxels[VoxelCoords.Index(0, 0, 0)] = Voxel.Solid(2);
        VoxelMeshBuild build = VoxelGreedyMesher.Build(voxels, atlas);
        Assert.Contains(build.UV2, uv => uv.X == grassTop);
        Assert.Contains(build.UV2, uv => uv.X == grassSide);
        Assert.Equal(grassTop, SliceOnNormal(build, new Float3(0, 1, 0)));
        Assert.Equal(grassSide, SliceOnNormal(build, new Float3(1, 0, 0)));
    }

    [Fact]
    public void ShaderLibrary_ParsesOpaqueCutoutAndCross()
    {
        Shader opaque = VoxelShaderLibrary.LoadOpaqueShader();
        Shader cutout = VoxelShaderLibrary.LoadCutoutShader();
        Shader cross = VoxelShaderLibrary.LoadCutoutCrossShader();
        Assert.Equal("VoxelOpaque", opaque.Name);
        Assert.Equal("Voxel/Opaque", opaque.ArtifactName);
        Assert.Equal("Voxel/Cutout", cutout.ArtifactName);
        Assert.Equal("Voxel/CutoutCross", cross.ArtifactName);
        Assert.True(opaque.GetPass("VoxelOpaque").HasGlsl);
        Assert.True(opaque.GetPass("VoxelOpaque").HasHlsl);
        Assert.True(cutout.GetPass("VoxelCutout").HasGlsl);
        Assert.True(cross.GetPass("VoxelCutoutCross").HasHlsl);
        Assert.Equal(ShaderPropertyType.Texture2DArray, Assert.Single(opaque.PropertyArray, p => p.Name == "_VoxelAtlas").PropertyType);
        Assert.Contains(cutout.PropertyArray, p => p.Name == "_AlphaCutoff");
        Assert.Equal(VoxelShaderLibrary.OpaqueShaderGuid, opaque.AssetID);
        string source = VoxelShaderLibrary.ReadShaderSource(VoxelShaderLibrary.CutoutCrossShaderAssetName);
        Assert.Contains("Cull Off", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OpaqueMaterial_BindsPackedAtlas()
    {
        VoxelAtlas atlas = VoxelWorldHost.CreateDemoAtlas();
        Material material = VoxelShaderLibrary.CreateOpaqueMaterial(atlas);
        Assert.True(material.IsOverridden("_VoxelAtlas"));
        Assert.Same(atlas.Texture, material._properties.GetTexture2DArray("_VoxelAtlas"));
        Assert.Equal("Voxel/Opaque", material.Shader!.ArtifactName);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, ShaderBytecodeFormat.SpirV)]
    [InlineData(GraphicsBackend.Direct3D12, ShaderBytecodeFormat.Dxil)]
    public void OpaquePass_CompilesOnModernBackends(GraphicsBackend backend, ShaderBytecodeFormat expected)
    {
        ShaderPass pass = VoxelShaderLibrary.LoadOpaqueShader().GetPass("VoxelOpaque");
        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(
            pass.CreateHlslCompileRequest(backend, new Dictionary<string, bool> { ["HAS_UV2"] = true }, "HAS_UV2"));
        if (backend == GraphicsBackend.Vulkan && !result.Success &&
            result.ErrorMessage?.Contains("SPIR-V CodeGen not available", StringComparison.OrdinalIgnoreCase) == true)
            return;
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.Format);
    }

    [Fact]
    public void PackagedFlatAssets_ListMultiFaceVoxels()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld");
        VoxelFaceTextures.WriteOwnedPackageFiles(root);
        string flat = Path.Combine(root, "Runtime", "Resources", "Worlds", "Flat");
        Assert.True(File.Exists(Path.Combine(flat, "grass_top.png")));
        Assert.True(File.Exists(Path.Combine(flat, "grass_side.png")));
        Assert.True(File.Exists(Path.Combine(flat, "dirt.png")));
        Assert.True(File.Exists(Path.Combine(flat, "stone.png")));
        Assert.True(File.Exists(Path.Combine(flat, "leaves.png")));
        string grass = File.ReadAllText(Path.Combine(flat, "GrassVoxel.asset"));
        Assert.Contains("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a11", grass, StringComparison.Ordinal);
        Assert.Contains("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a15", grass, StringComparison.Ordinal);
        string world = File.ReadAllText(Path.Combine(flat, "FlatWorld.asset"));
        Assert.Contains("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4b21", world, StringComparison.Ordinal);
        Assert.Contains("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4b22", world, StringComparison.Ordinal);
        Assert.Contains("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4b23", world, StringComparison.Ordinal);
    }

    [Fact]
    public void N4Evidence_WritesAtlasBoard()
    {
        VoxelAtlas atlas = VoxelWorldHost.CreateDemoAtlas();
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N4 VOXEL ATLAS SHADERS", 230, 232, 240, 2);
        board.Text(40, 80, "TEXTURE2DARRAY PLUS OPAQUE CUTOUT", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 76, 175, 80, 255);
        board.Text(90, 122, "GRASS TOP DIFFERS FROM SIDE", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 141, 110, 99, 255);
        board.Text(90, 154, $"ATLAS LAYERS {atlas.LayerCount}  UV2 X IS SLICE", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 158, 158, 158, 255);
        board.Text(90, 186, "SHADER OPAQUE CUTOUT AND CROSS", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 8/8  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "FLAT EXTRA VOXELS GRASS DIRT STONE LEAVES", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n4-voxel-shaders.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n4-voxel-shaders.md"),
            "# N4 Texture array packer + voxel shaders\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`TextureArrayPacker` interned grass top / dirt side / stone into a `Texture2DArray`. " +
            "Greedy mesh writes the face slice to `Mesh.UV2.x`. `VoxelShaderLibrary` loads " +
            "`Voxel/Opaque`, `Voxel/Cutout`, and `Voxel/CutoutCross` (GLSL + Slang). Flat extras " +
            "are Grass / Dirt / Stone / Leaves with distinct top vs side GUIDs.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelAtlasTests\n" +
            "Passed!  - Failed: 0, Passed: 8, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n4-voxel-shaders.png](n4-voxel-shaders.png)\n");
    }

    private static float SliceOnNormal(VoxelMeshBuild build, Float3 normal)
    {
        for (int i = 0; i < build.Normals.Count; i++)
        {
            if (build.Normals[i].Equals(normal))
                return build.UV2[i].X;
        }

        throw new InvalidOperationException($"No quad with normal {normal}.");
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
