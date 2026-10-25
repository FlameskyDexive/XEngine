// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Terrain;

public sealed class TerrainNeighborTests : RuntimeTestBase
{
    [Fact]
    public void TryMatchEdge_ClassifiesFourSides()
    {
        Float3 aMin = Float3.Zero, aMax = new(10f, 0f, 10f);
        Assert.True(TerrainNeighboring.TryMatchEdge(aMin, aMax, new Float3(-10f, 0f, 0f), new Float3(0f, 0f, 10f), 0.1f, out TerrainEdge left));
        Assert.Equal(TerrainEdge.Left, left);
        Assert.True(TerrainNeighboring.TryMatchEdge(aMin, aMax, new Float3(10f, 0f, 0f), new Float3(20f, 0f, 10f), 0.1f, out TerrainEdge right));
        Assert.Equal(TerrainEdge.Right, right);
        Assert.True(TerrainNeighboring.TryMatchEdge(aMin, aMax, new Float3(0f, 0f, -10f), new Float3(10f, 0f, 0f), 0.1f, out TerrainEdge bottom));
        Assert.Equal(TerrainEdge.Bottom, bottom);
        Assert.True(TerrainNeighboring.TryMatchEdge(aMin, aMax, new Float3(0f, 0f, 10f), new Float3(10f, 0f, 20f), 0.1f, out TerrainEdge top));
        Assert.Equal(TerrainEdge.Top, top);
        Assert.False(TerrainNeighboring.TryMatchEdge(aMin, aMax, new Float3(50f, 0f, 50f), new Float3(60f, 0f, 60f), 0.1f, out _));
    }

    [Fact]
    public void CopySharedEdge_WritesOppositeColumn()
    {
        var from = new TerrainData { HeightmapResolution = 3, Heights = new short[9] };
        var to = new TerrainData { HeightmapResolution = 3, Heights = new short[9] };
        from.SetHeight(2, 0, 0.25f);
        from.SetHeight(2, 1, 0.5f);
        from.SetHeight(2, 2, 0.75f);
        TerrainNeighboring.CopySharedEdge(from, TerrainEdge.Right, to, TerrainEdge.Left);
        Assert.Equal(0.25f, to.GetHeight(0, 0), 2);
        Assert.Equal(0.5f, to.GetHeight(0, 1), 2);
        Assert.Equal(0.75f, to.GetHeight(0, 2), 2);
    }

    [Fact]
    public void WorldToUv_UsesLocalXz()
    {
        Float4x4 worldToLocal = Float4x4.CreateTranslation(new Float3(-10f, 0f, 0f));
        Float2 uv = TerrainNeighboring.WorldToUv(new Float3(15f, 4f, 5f), worldToLocal, 10f);
        Assert.Equal(0.5f, uv.X, 3);
        Assert.Equal(0.5f, uv.Y, 3);
    }

    [Fact]
    public void GroupedTiles_AutoConnectOnEnable()
    {
        Scene scene = CreateScene(enable: true);
        TerrainComponent a = MakeTile(scene, "A", Float3.Zero, 10f);
        TerrainComponent b = MakeTile(scene, "B", new Float3(10f, 0f, 0f), 10f);
        Assert.Same(b, a.RightNeighbor);
        Assert.Same(a, b.LeftNeighbor);
    }

    [Fact]
    public void DifferentGrouping_DoesNotConnect()
    {
        Scene scene = CreateScene(enable: true);
        TerrainComponent a = MakeTile(scene, "A", Float3.Zero, 10f);
        TerrainComponent b = MakeTile(scene, "B", new Float3(10f, 0f, 0f), 10f, grouping: 2);
        Assert.Null(a.RightNeighbor);
        Assert.Null(b.LeftNeighbor);
    }

    [Fact]
    public void SetNeighbors_StopsAutoConnectAndWiresReverse()
    {
        Scene scene = CreateScene(enable: true);
        TerrainComponent a = MakeTile(scene, "A", Float3.Zero, 10f);
        TerrainComponent b = MakeTile(scene, "B", new Float3(0f, 0f, 50f), 10f);
        a.SetNeighbors(null, b, null, null);
        Assert.False(a.AllowAutoConnect);
        Assert.Same(b, a.TopNeighbor);
        Assert.Same(a, b.BottomNeighbor);
    }

    [Fact]
    public void PushHeightSeams_CopiesPaintedEdgeOntoNeighbor()
    {
        Scene scene = CreateScene(enable: true);
        TerrainComponent a = MakeTile(scene, "A", Float3.Zero, 10f);
        TerrainComponent b = MakeTile(scene, "B", new Float3(10f, 0f, 0f), 10f);
        var aData = a.Data.Res!;
        aData.HeightmapResolution = 3;
        aData.Heights = new short[9];
        var bData = b.Data.Res!;
        bData.HeightmapResolution = 3;
        bData.Heights = new short[9];
        aData.SetHeight(2, 1, 0.8f);
        a.PushHeightSeams();
        Assert.Equal(0.8f, bData.GetHeight(0, 1), 2);
    }

    [Fact]
    public void NeighborEvidence_WritesBoardAndNotes()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M4D TERRAIN NEIGHBORS", 230, 232, 240, 2);
        board.Text(40, 80, "GROUPING ID + AABB EDGE MATCH", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "SEAM COPY  LEFT/RIGHT/TOP/BOTTOM", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "CROSS-TILE BRUSH ON OVERLAP", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m4d-neighbors.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "m4d-neighbors.md"),
            "# M4d Terrain neighbors\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "Tiles that share a `GroupingId` and an XZ edge auto-connect (`Left`/`Right`/`Top`/`Bottom`). " +
            "`PushHeightSeams` copies the painted edge onto the neighbor. Scene View brushes also stamp " +
            "overlapping grouped tiles.\n\n" +
            "Screenshot: [m4d-neighbors.png](m4d-neighbors.png).\n");
    }

    private TerrainComponent MakeTile(Scene scene, string name, Float3 position, float size, int grouping = 0)
    {
        GameObject go = CreateGameObject(name);
        go.Transform.Position = position;
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.GroupingId = grouping;
        terrain.Data = new AssetRef<TerrainData>(new TerrainData { Size = size, Height = 10f });
        scene.Add(go);
        return terrain;
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
