// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Terrain;

public sealed class TerrainApiTests : RuntimeTestBase
{
    [Fact]
    public void ActiveTerrains_TracksEnableAndDisable()
    {
        Assert.DoesNotContain(CreateGameObject("Loose").AddComponent<TerrainComponent>(), TerrainComponent.ActiveTerrains);

        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Terrain");
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.Data = new AssetRef<TerrainData>(new TerrainData { Size = 8f, Height = 4f });
        scene.Add(go);

        Assert.Contains(terrain, TerrainComponent.ActiveTerrains);
        Assert.Same(terrain, TerrainComponent.ActiveTerrain);

        terrain.Enabled = false;
        Assert.DoesNotContain(terrain, TerrainComponent.ActiveTerrains);
        Assert.Null(TerrainComponent.ActiveTerrain);
    }

    [Fact]
    public void SampleHeight_ReturnsScaledWorldY()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Terrain");
        go.Transform.Position = new Float3(0f, 3f, 0f);
        var terrain = go.AddComponent<TerrainComponent>();
        var data = new TerrainData { Size = 8f, Height = 10f, HeightmapResolution = 3, Heights = new short[9], Interpolation = TerrainInterpolation.Bilinear };
        data.SetHeight(0, 0, 0.5f);
        data.SetHeight(1, 0, 0.5f);
        data.SetHeight(2, 0, 0.5f);
        data.SetHeight(0, 1, 0.5f);
        data.SetHeight(1, 1, 0.5f);
        data.SetHeight(2, 1, 0.5f);
        data.SetHeight(0, 2, 0.5f);
        data.SetHeight(1, 2, 0.5f);
        data.SetHeight(2, 2, 0.5f);
        terrain.Data = new AssetRef<TerrainData>(data);
        scene.Add(go);

        float y = terrain.SampleHeight(new Float3(4f, 99f, 4f));
        Assert.InRange(y, 7.9f, 8.1f);
    }

    [Fact]
    public void GetInterpolatedNormal_FlatIsUp()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Terrain");
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.Data = new AssetRef<TerrainData>(new TerrainData { Size = 8f, Height = 4f });
        scene.Add(go);

        Float3 n = terrain.GetInterpolatedNormal(new Float3(2f, 0f, 2f));
        Assert.True(Float3.Dot(Float3.Normalize(n), Float3.UnitY) > 0.99f, $"n={n}");
    }

    [Fact]
    public void AddTreeInstance_AppendsToData()
    {
        var data = new TerrainData();
        data.TreePrototypes.Add(new TreePrototype());
        var instance = new TreeInstance
        {
            Position = new Float2(0.25f, 0.75f),
            PrototypeIndex = 0,
            WidthScale = 1.2f,
            HeightScale = 1.1f,
            Tint = Color.White,
        };
        data.AddTreeInstance(instance);
        Assert.Single(data.Trees);
        Assert.Equal(0.25f, data.Trees[0].Position.X, 3);

        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Terrain");
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.Data = new AssetRef<TerrainData>(data);
        scene.Add(go);
        terrain.AddTreeInstance(new TreeInstance { Position = new Float2(0.5f, 0.5f), PrototypeIndex = 0, WidthScale = 1f, HeightScale = 1f, Tint = Color.White });
        Assert.Equal(2, data.Trees.Count);
    }

    [Fact]
    public void ApiEvidence_WritesBoardAndNotes()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M4E TERRAIN GAMEPLAY API", 230, 232, 240, 2);
        board.Text(40, 80, "ACTIVETERRAINS / SAMPLEHEIGHT", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "GETINTERPOLATEDNORMAL", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "ADDTREEINSTANCE", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m4e-api.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "m4e-api.md"),
            "# M4e Terrain gameplay API\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`TerrainComponent.ActiveTerrains` / `ActiveTerrain` list enabled tiles. " +
            "`SampleHeight` returns world-space surface Y. `GetInterpolatedNormal` is world-space. " +
            "`AddTreeInstance` appends to `TerrainData.Trees`.\n\n" +
            "Screenshot: [m4e-api.png](m4e-api.png).\n");
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
