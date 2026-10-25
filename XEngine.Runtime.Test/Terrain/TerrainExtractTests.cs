// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Terrain;

public sealed class TerrainExtractTests : RuntimeTestBase
{
    [Fact]
    public void CoreRuntimeSources_DoNotDeclareTerrainTypes()
    {
        string repo = FindRepositoryRoot();
        foreach (string file in EnumerateCs(Path.Combine(repo, "XEngine.Runtime")))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("namespace XEngine.Runtime.Terrain", text);
            Assert.DoesNotContain("class TerrainComponent", text);
            Assert.DoesNotContain("class TerrainCollider", text);
            Assert.DoesNotContain("sealed class TerrainData", text);
        }
    }

    [Fact]
    public void PackageRuntime_OwnsTerrainSources()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain", "Runtime");
        Assert.True(File.Exists(Path.Combine(root, "TerrainComponent.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainData.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainLayer.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainCollider.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainTypeRedirects.cs")));
        Assert.True(File.Exists(Path.Combine(root, "TerrainNavMeshGeometryCollector.cs")));
        string component = File.ReadAllText(Path.Combine(root, "TerrainComponent.cs"));
        Assert.Contains("namespace XEngine.Terrain", component);
        Assert.Contains("[FormerlySerializedAs(\"XEngine.Runtime.Terrain.TerrainComponent\")]", component);
    }

    [Fact]
    public void LegacyTypeNames_ResolveToPackageTypes()
    {
        Assert.Same(typeof(TerrainComponent),
            RuntimeUtils.FindType("XEngine.Runtime.Terrain.TerrainComponent"));
        Assert.Same(typeof(TerrainComponent),
            RuntimeUtils.FindType(
                "XEngine.Runtime.Terrain.TerrainComponent, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
        Assert.Same(typeof(TerrainCollider),
            RuntimeUtils.FindType("XEngine.Runtime.TerrainCollider, XEngine.Runtime"));
        Assert.Same(typeof(TerrainData),
            RuntimeUtils.FindType("XEngine.Runtime.TerrainData"));
        Assert.Same(typeof(TerrainLayer),
            RuntimeTypeRegistry.Resolve("XEngine.Runtime.TerrainLayer, XEngine.Runtime"));
    }

    [Fact]
    public void TerrainData_GetInterpolatedHeightBilinear_Midpoint()
    {
        var data = new TerrainData
        {
            HeightmapResolution = 2,
            Size = 10f,
            Height = 100f,
            Interpolation = TerrainInterpolation.Bilinear,
            Heights = new short[4],
        };
        data.SetHeight(0, 0, 0f);
        data.SetHeight(1, 0, 0f);
        data.SetHeight(0, 1, 1f);
        data.SetHeight(1, 1, 1f);
        Assert.Equal(50f, data.GetInterpolatedHeight(0.5f, 0.5f), 3);
        Assert.Equal(0f, data.GetHeight(0, 0), 3);
        Assert.Equal(1f, data.GetHeight(0, 1), 3);
    }

    [Fact]
    public void TerrainData_LegacyTypeDeserializesHeights()
    {
        var data = CreateRamp(res: 3, size: 16f, height: 8f);
        EchoObject echo = Serializer.Serialize(data);
        RewriteLegacyTypes(echo);
        Assert.Contains("XEngine.Runtime.TerrainData", echo["$type"].StringValue, StringComparison.Ordinal);

        var loaded = Assert.IsType<TerrainData>(Serializer.Deserialize(echo, typeof(object)));
        Assert.Equal(3, loaded.HeightmapResolution);
        Assert.Equal(16f, loaded.Size);
        Assert.Equal(8f, loaded.Height);
        Assert.Equal(data.GetHeight(0, 2), loaded.GetHeight(0, 2), 3);
        Assert.Equal(data.GetHeight(2, 2), loaded.GetHeight(2, 2), 3);
    }

    [Fact]
    public void GameObject_LegacyComponentTypes_RestoreTerrainAndCollider()
    {
        GameObject go = CreateGameObject("LegacyTerrain");
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.MaxLODLevel = 6;
        terrain.GrassDistance = 80f;
        terrain.TreeDistance = 250f;
        terrain.Data = new AssetRef<TerrainData>(CreateRamp(res: 3, size: 12f, height: 4f));
        go.AddComponent<TerrainCollider>();

        EchoObject echo = Serializer.Serialize(go);
        RewriteLegacyTypes(echo);
        AssertTypePresent(echo, "XEngine.Runtime.Terrain.TerrainComponent");
        AssertTypePresent(echo, "XEngine.Runtime.TerrainCollider");

        GameObject clone = Serializer.Deserialize<GameObject>(echo);
        var restored = clone.GetComponent<TerrainComponent>();
        Assert.NotNull(restored);
        Assert.IsType<TerrainComponent>(restored);
        Assert.Null(clone.GetComponent<MissingMonobehaviour>());
        Assert.Equal(6, restored.MaxLODLevel);
        Assert.Equal(80f, restored.GrassDistance);
        Assert.Equal(250f, restored.TreeDistance);
        Assert.NotNull(clone.GetComponent<TerrainCollider>());
        Assert.NotNull(restored.Data.Res);
        Assert.Equal(12f, restored.Data.Res!.Size);
    }

    [Fact]
    public void ExtractEvidence_WritesBoardAndNotes()
    {
        Assert.Same(typeof(TerrainComponent),
            RuntimeUtils.FindType("XEngine.Runtime.Terrain.TerrainComponent, XEngine.Runtime"));

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M1 TERRAIN RUNTIME EXTRACT", 230, 232, 240, 2);
        board.Text(40, 80, "TYPES LIVE IN COM.XENGINE.TERRAIN", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "NAMESPACE XENGINE.TERRAIN", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "LEGACY $TYPE REDIRECTS REGISTERED", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string boardPath = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m1-runtime-extract.png"), board);
        Assert.True(File.Exists(boardPath));

        File.WriteAllText(Path.Combine(evidence, "m1-runtime-extract.md"),
            "# M1 Terrain runtime extract\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`TerrainComponent`, `TerrainData`, `TerrainCollider`, quadtree/chunk, grass/tree " +
            "renderers, and the NavMesh collector now live in `Packages/com.xengine.terrain` " +
            "under namespace `XEngine.Terrain`. Editor inspector, Scene View brushes, importer, " +
            "and **GameObject / 3D Object / Terrain** moved with them so `XEngine.Editor` compiles " +
            "without terrain types.\n\n" +
            "Old Echo `$type` strings (`XEngine.Runtime.Terrain.TerrainComponent`, " +
            "`XEngine.Runtime.TerrainData`, `XEngine.Runtime.TerrainCollider`) resolve through " +
            "`RuntimeTypeRegistry.RegisterAlias` plus `[FormerlySerializedAs]`.\n\n" +
            "Screenshot: [m1-runtime-extract.png](m1-runtime-extract.png).\n");
    }

    private static TerrainData CreateRamp(int res, float size, float height)
    {
        var data = new TerrainData
        {
            HeightmapResolution = res,
            Size = size,
            Height = height,
            Heights = new short[res * res],
        };
        for (int z = 0; z < res; z++)
        {
            for (int x = 0; x < res; x++)
                data.SetHeight(x, z, z / (float)(res - 1));
        }

        return data;
    }

    private static void RewriteLegacyTypes(EchoObject node)
    {
        if (node.TagType == EchoType.Compound)
        {
            if (node.Tags.TryGetValue("$type", out EchoObject? typeTag)
                && typeTag.TagType == EchoType.String
                && TryMapLegacy(typeTag.StringValue, out string legacy))
            {
                node["$type"] = new EchoObject(legacy);
            }

            foreach (KeyValuePair<string, EchoObject> kv in node.Tags.ToArray())
                RewriteLegacyTypes(kv.Value);
        }
        else if (node.TagType == EchoType.List)
        {
            foreach (EchoObject child in node.List)
                RewriteLegacyTypes(child);
        }
    }

    private static bool TryMapLegacy(string raw, out string legacy)
    {
        string typeOnly = raw;
        int comma = raw.IndexOf(',');
        if (comma >= 0)
            typeOnly = raw[..comma].Trim();

        legacy = typeOnly switch
        {
            "XEngine.Terrain.TerrainComponent" =>
                "XEngine.Runtime.Terrain.TerrainComponent, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            "XEngine.Terrain.TerrainCollider" =>
                "XEngine.Runtime.TerrainCollider, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            "XEngine.Terrain.TerrainData" =>
                "XEngine.Runtime.TerrainData, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            "XEngine.Terrain.TerrainLayer" =>
                "XEngine.Runtime.TerrainLayer, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            "XEngine.Terrain.DetailPrototype" =>
                "XEngine.Runtime.DetailPrototype, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            "XEngine.Terrain.TreePrototype" =>
                "XEngine.Runtime.TreePrototype, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            "XEngine.Terrain.TreeInstance" =>
                "XEngine.Runtime.TreeInstance, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            _ => "",
        };
        return legacy.Length > 0;
    }

    private static void AssertTypePresent(EchoObject root, string typeName)
    {
        Assert.True(ContainsType(root, typeName), $"Serialized graph is missing $type {typeName}");
    }

    private static bool ContainsType(EchoObject node, string typeName)
    {
        if (node.TagType == EchoType.Compound)
        {
            if (node.Tags.TryGetValue("$type", out EchoObject? typeTag)
                && typeTag.TagType == EchoType.String
                && typeTag.StringValue.StartsWith(typeName, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (KeyValuePair<string, EchoObject> kv in node.Tags)
            {
                if (ContainsType(kv.Value, typeName))
                    return true;
            }
        }
        else if (node.TagType == EchoType.List)
        {
            foreach (EchoObject child in node.List)
            {
                if (ContainsType(child, typeName))
                    return true;
            }
        }

        return false;
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
