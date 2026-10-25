using System.Globalization;
using System.Runtime.CompilerServices;

using XEngine.Editor.Build;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;

namespace XEngine.Terrain.Editor;

/// <summary>
/// Scene shader-variant scan for terrain, grass, and trees. Registered when the Terrain Editor
/// assembly loads so core collectors stay terrain-agnostic.
/// </summary>
public sealed class TerrainShaderVariantCollector : IShaderVariantSceneCollector
{
    public static readonly TerrainShaderVariantCollector Instance = new();

    [ModuleInitializer]
    internal static void Register() => ShaderVariantSceneCollectors.Register(Instance);

    public void Collect(Scene scene, string scenePath, ShaderVariantSceneSink sink)
    {
        foreach (TerrainComponent? terrain in scene.FindObjectsOfType<TerrainComponent>())
        {
            if (terrain == null)
                continue;

            AssetRef<TerrainData> terrainDataRef = terrain.Data;
            terrainDataRef.EnsureLoaded();
            TerrainData? terrainData = terrainDataRef.Res;
            if (terrainData == null)
                continue;

            string source = "scene-terrain:" + scenePath + ":" + terrain.Identifier.ToString("N");
            Material? terrainMaterial = Resolve(terrain.Material);
            sink.AddMaterialOrShader(
                terrainMaterial,
                TerrainShaderLibrary.LoadTerrainShader(),
                TerrainShaderLibrary.TerrainShaderGuid,
                source + ":surface",
                VulkanShaderVariantCollector.CreateTerrainVariantKeywords(
                    terrainMaterial?._localKeywords,
                    terrainData.LayerCount,
                    terrainData.Interpolation == TerrainInterpolation.Bicubic));

            CollectDetails(sink, terrain, terrainData, source);
            CollectTrees(sink, terrainData, source);
        }
    }

    private static void CollectDetails(
        ShaderVariantSceneSink sink,
        TerrainComponent terrain,
        TerrainData terrainData,
        string source)
    {
        Material? terrainGrassMaterial = Resolve(terrain.GrassMaterial);
        for (int prototypeIndex = 0; prototypeIndex < terrainData.DetailPrototypes.Count; prototypeIndex++)
        {
            if (prototypeIndex >= terrainData.DetailLayers.Count)
                continue;

            DetailPrototype prototype = terrainData.DetailPrototypes[prototypeIndex];
            string prototypeSource = source + ":detail-" + prototypeIndex.ToString(CultureInfo.InvariantCulture);
            if (prototype.RenderMode != DetailRenderMode.Mesh)
            {
                Material? grassMaterial = Resolve(prototype.GrassMaterial) ?? terrainGrassMaterial;
                sink.AddMaterialOrShader(
                    grassMaterial,
                    TerrainShaderLibrary.LoadGrassShader(),
                    TerrainShaderLibrary.GrassShaderGuid,
                    prototypeSource + ":grass",
                    VulkanShaderVariantCollector.CreateGrassVariantKeywords(grassMaterial?._localKeywords));
                continue;
            }

            Mesh? mesh = ResolveMesh(prototype.Mesh);
            if (mesh == null || mesh.SubMeshCount <= 0)
                continue;

            for (int subMeshIndex = 0; subMeshIndex < mesh.SubMeshCount; subMeshIndex++)
            {
                Material? detailMaterial = subMeshIndex < prototype.Materials.Count
                    ? Resolve(prototype.Materials[subMeshIndex])
                    : null;
                sink.AddInstancedMesh(
                    detailMaterial,
                    mesh,
                    prototypeSource + ":mesh-submesh-" + subMeshIndex.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static void CollectTrees(ShaderVariantSceneSink sink, TerrainData terrainData, string source)
    {
        for (int prototypeIndex = 0; prototypeIndex < terrainData.TreePrototypes.Count; prototypeIndex++)
        {
            if (!HasTreeInstance(terrainData, prototypeIndex))
                continue;

            TreePrototype prototype = terrainData.TreePrototypes[prototypeIndex];
            Mesh? mesh = ResolveMesh(prototype.Mesh);
            if (mesh == null || mesh.SubMeshCount <= 0)
                continue;

            string prototypeSource = source + ":tree-" + prototypeIndex.ToString(CultureInfo.InvariantCulture);
            for (int subMeshIndex = 0; subMeshIndex < mesh.SubMeshCount; subMeshIndex++)
            {
                Material? treeMaterial = subMeshIndex < prototype.Materials.Count
                    ? Resolve(prototype.Materials[subMeshIndex])
                    : null;
                sink.AddInstancedMesh(
                    treeMaterial,
                    mesh,
                    prototypeSource + ":submesh-" + subMeshIndex.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static bool HasTreeInstance(TerrainData terrainData, int prototypeIndex)
    {
        for (int treeIndex = 0; treeIndex < terrainData.Trees.Count; treeIndex++)
        {
            if (terrainData.Trees[treeIndex].PrototypeIndex == prototypeIndex)
                return true;
        }
        return false;
    }

    private static Material? Resolve(AssetRef<Material> materialRef)
    {
        materialRef.EnsureLoaded();
        return materialRef.Res;
    }

    private static Mesh? ResolveMesh(AssetRef<Mesh> meshRef)
    {
        meshRef.EnsureLoaded();
        return meshRef.Res;
    }
}
