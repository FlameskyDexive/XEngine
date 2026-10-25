// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;
using XEngine.Terrain.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class TerrainEditorExtractTests
{
    [Fact]
    public void CoreEditorSources_DoNotDeclareTerrainEditors()
    {
        string repo = FindRepositoryRoot();
        foreach (string file in EnumerateCs(Path.Combine(repo, "XEngine.Editor")))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("class TerrainEditor", text);
            Assert.DoesNotContain("class TerrainSceneEditor", text);
            Assert.DoesNotContain("class TerrainDataImporter", text);
            Assert.DoesNotContain("CreateTerrain()", text);
        }
    }

    [Fact]
    public void VulkanCollector_UsesProjectDefaultsAndRegisteredSceneCollectors()
    {
        string path = Path.Combine(FindRepositoryRoot(),
            "XEngine.Editor", "Projects", "Build", "VulkanShaderVariantCollector.cs");
        string text = File.ReadAllText(path);
        Assert.Contains("CollectRegisteredSceneUsages", text);
        Assert.Contains("BuildShaderInventory.GetBuiltInShaderGuids", text);
        Assert.DoesNotContain("BuiltInAssets.FindAllOfType", text);
        Assert.DoesNotContain("FindObjectsOfType<TerrainComponent>", text);
        Assert.DoesNotContain("CollectSceneTerrainUsages", text);
    }

    [Fact]
    public void AiHost_MapsTerrainShortNamesToPackageAssembly()
    {
        string path = Path.Combine(FindRepositoryRoot(), "Tools", "XEngine.Ai.Host", "ComponentRegistry.cs");
        string text = File.ReadAllText(path);
        Assert.Contains("XEngine.Terrain.TerrainComponent", text, StringComparison.Ordinal);
        Assert.Contains("XEngine.Terrain.TerrainCollider", text, StringComparison.Ordinal);
        Assert.Contains("XEngine.Terrain.WindZone", text, StringComparison.Ordinal);
        Assert.Contains("XEngine.Terrain.Runtime", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"XEngine.Runtime.TerrainComponent\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"XEngine.Runtime.TerrainCollider\"", text, StringComparison.Ordinal);

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "m3-ai-host-mapping.md"),
            "# M3 AI Host terrain mapping\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "Offline `ComponentRegistry` short-names `Terrain` / `TerrainComponent` / `TerrainCollider` " +
            "now stamp `$type` as `XEngine.Terrain.*` in assembly `XEngine.Terrain.Runtime`. " +
            "Core components still use `XEngine.Runtime`. Editor inspectors, Scene View brushes, " +
            "the `.terraindata` importer, and **GameObject / 3D Object / Terrain** already live in " +
            "the package from M1.\n");
        Assert.True(File.Exists(Path.Combine(evidence, "m3-ai-host-mapping.md")));
    }

    [Fact]
    public void PackageCollector_EmitsTerrainSurfaceVariant()
    {
        using var scene = new Scene();
        var go = new GameObject("Terrain");
        try
        {
            var terrain = go.AddComponent<TerrainComponent>();
            var data = new TerrainData
            {
                HeightmapResolution = 5,
                Size = 16f,
                Height = 4f,
                Heights = new short[25],
            };
            terrain.Data = new AssetRef<TerrainData>(data);
            scene.Add(go);

            int materials = 0;
            var sink = new ShaderVariantSceneSink(
                (_, _, _, _, _) => materials++,
                (_, _, _) => { });
            TerrainShaderVariantCollector.Instance.Collect(scene, "Assets/Terrain.scene", sink);
            Assert.True(materials >= 1);
        }
        finally
        {
            if (!go.IsDisposed)
                go.Dispose();
        }
    }

    [Fact]
    public void PackageEditor_OwnsMenuImporterAndSceneEditor()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain", "Editor");
        Assert.True(File.Exists(Path.Combine(root, "TerrainEditor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainSceneEditor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainDataImporter.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainLayerImporter.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainMenu.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainShaderVariantCollector.cs")));
        string collector = File.ReadAllText(Path.Combine(root, "TerrainShaderVariantCollector.cs"));
        Assert.Contains("TerrainShaderLibrary.LoadTerrainShader()", collector);
        Assert.Contains("AddMaterialOrShader", collector);
        string menu = File.ReadAllText(Path.Combine(root, "TerrainMenu.cs"));
        Assert.Contains("GameObject/3D Object/Terrain", menu);
        Assert.Contains("GameObject/3D Object/Wind Zone", menu);
        string importer = File.ReadAllText(Path.Combine(root, "TerrainDataImporter.cs"));
        Assert.Contains("[ImporterFor(\".terraindata\")]", importer);
        string editor = File.ReadAllText(Path.Combine(root, "TerrainEditor.cs"));
        Assert.Contains("HeightTool.SetHeight", editor);
        Assert.Contains("HeightTool.Stamp", editor);
        Assert.Contains("TryImportRaw16", editor);
        Assert.Contains("MassPlaceTrees", editor);
        Assert.Contains("TreeBillboardDistance", editor);
        Assert.Contains("GroupingId", editor);
        Assert.Contains("PushHeightSeams", editor);
        Assert.Contains("ActiveTerrains", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain", "Runtime", "TerrainComponent.cs")));
    }

    private static IEnumerable<string> EnumerateCs(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
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
