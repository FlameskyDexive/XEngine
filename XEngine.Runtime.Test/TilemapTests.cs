// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Sparse 32×32 Tilemap chunks, Grid cell conversion, and TilemapRenderer CPU chunk meshes.
/// </summary>
public sealed class TilemapTests : RuntimeTestBase
{
    [Fact]
    public void Grid_CellToLocal_RectangleUsesCellSize()
    {
        GameObject go = CreateGameObject("Grid");
        var grid = go.AddComponent<Grid>();
        grid.CellSize = new Float3(2f, 3f, 0f);

        Float3 local = grid.CellToLocal(1, 2);

        Assert.Equal(2f, local.X);
        Assert.Equal(6f, local.Y);
        Assert.Equal(0f, local.Z);
    }

    [Fact]
    public void Grid_LocalToCell_InvertsCellToLocal()
    {
        GameObject go = CreateGameObject("Grid");
        var grid = go.AddComponent<Grid>();
        grid.CellSize = new Float3(2f, 3f, 0f);

        grid.LocalToCell(new Float3(2.1f, 6.9f, 0f), out int x, out int y);

        Assert.Equal(1, x);
        Assert.Equal(2, y);
    }

    [Fact]
    public void Grid_Hexagon_CellToLocal_UsesAxialOffset()
    {
        GameObject go = CreateGameObject("Grid");
        var grid = go.AddComponent<Grid>();
        grid.Layout = GridLayout.Hexagon;
        grid.CellSize = new Float3(2f, 2f, 0f);

        Float3 origin = grid.CellToLocal(0, 0);
        Float3 east = grid.CellToLocal(1, 0);
        Float3 north = grid.CellToLocal(0, 1);

        Assert.Equal(0f, origin.X);
        Assert.Equal(0f, origin.Y);
        Assert.Equal(2f, east.X);
        Assert.Equal(1f, north.X);
        Assert.Equal(2f, north.Y);
    }

    [Fact]
    public void Grid_Hexagon_LocalToCell_InvertsCellToLocal()
    {
        GameObject go = CreateGameObject("Grid");
        var grid = go.AddComponent<Grid>();
        grid.Layout = GridLayout.Hexagon;
        grid.CellSize = new Float3(2f, 2f, 0f);

        Float3 local = grid.CellToLocal(1, 2);
        grid.LocalToCell(local, out int x, out int y);

        Assert.Equal(1, x);
        Assert.Equal(2, y);
    }

    [Fact]
    public void Tilemap_SetGetTile_Roundtrips()
    {
        var map = new Tilemap();
        var tile = new Tile();

        map.SetTile(3, 5, tile);

        Assert.Same(tile, map.GetTile(3, 5));
        Assert.Null(map.GetTile(0, 0));
    }

    [Fact]
    public void Tilemap_SetTile_CreatesSparse32Chunks()
    {
        var map = new Tilemap();
        map.SetTile(0, 0, new Tile());
        map.SetTile(32, 0, new Tile());

        Assert.Equal(2, map.ChunkCount);
        Assert.Equal(Tilemap.ChunkSize, 32);
    }

    [Fact]
    public void Tilemap_ClearTile_RemovesEmptyChunk()
    {
        var map = new Tilemap();
        map.SetTile(40, 1, new Tile());
        Assert.Equal(1, map.ChunkCount);

        map.SetTile(40, 1, null);

        Assert.Equal(0, map.ChunkCount);
        Assert.Null(map.GetTile(40, 1));
    }

    [Fact]
    public void Tilemap_NegativeCells_UseDistinctChunks()
    {
        var map = new Tilemap();
        var negative = new Tile();
        var origin = new Tile();

        map.SetTile(-1, -1, negative);
        map.SetTile(0, 0, origin);

        Assert.Same(negative, map.GetTile(-1, -1));
        Assert.Same(origin, map.GetTile(0, 0));
        Assert.Equal(2, map.ChunkCount);
    }
}

/// <summary>CPU chunk mesh rebuilds for <see cref="TilemapRenderer"/> (no GPU upload).</summary>
public sealed class TilemapRendererTests : RuntimeTestBase
{
    [Fact]
    public void TilemapRenderer_RebuildDirtyChunks_OneQuadPerOccupiedTile()
    {
        GameObject go = CreateGameObject("Map");
        go.AddComponent<Grid>().CellSize = new Float3(1f, 1f, 0f);
        var map = go.AddComponent<Tilemap>();
        var renderer = go.AddComponent<TilemapRenderer>();
        map.SetTile(0, 0, new Tile());
        map.SetTile(1, 0, new Tile());
        map.SetTile(32, 0, new Tile());

        renderer.RebuildDirtyChunks();

        Assert.Equal(2, renderer.ChunkMeshCount);
        Assert.Equal(2, renderer.GetChunkQuadCount(0, 0));
        Assert.Equal(1, renderer.GetChunkQuadCount(1, 0));
    }

    [Fact]
    public void TilemapRenderer_Rebuild_OnlyReplacesEditedChunkMesh()
    {
        GameObject go = CreateGameObject("Map");
        go.AddComponent<Grid>();
        var map = go.AddComponent<Tilemap>();
        var renderer = go.AddComponent<TilemapRenderer>();
        map.SetTile(0, 0, new Tile());
        renderer.RebuildDirtyChunks();
        Mesh first = renderer.GetChunkMesh(0, 0)!;

        map.SetTile(32, 0, new Tile());
        renderer.RebuildDirtyChunks();

        Assert.Same(first, renderer.GetChunkMesh(0, 0));
        Assert.Equal(2, renderer.ChunkMeshCount);
    }
}

/// <summary>Tile Palette stores a grid of tiles and stamps the selection onto a <see cref="Tilemap"/>.</summary>
public sealed class TilePaletteTests : RuntimeTestBase
{
    [Fact]
    public void TilePalette_SetGetTile_Roundtrips()
    {
        var palette = new TilePalette();
        var grass = new Tile();
        palette.SetSize(2, 1);
        palette.SetTile(0, 0, grass);
        palette.SetTile(1, 0, new Tile());

        Assert.Same(grass, palette.GetTile(0, 0));
        Assert.NotNull(palette.GetTile(1, 0));
        Assert.Null(palette.GetTile(2, 0));
    }

    [Fact]
    public void TilePalette_Paint_StampsSelectedTile()
    {
        var palette = new TilePalette();
        var grass = new Tile();
        var dirt = new Tile();
        palette.SetSize(2, 1);
        palette.SetTile(0, 0, grass);
        palette.SetTile(1, 0, dirt);
        palette.Select(1, 0);

        var map = new Tilemap();
        palette.Paint(map, 4, 2);

        Assert.Same(dirt, map.GetTile(4, 2));
        Assert.Null(map.GetTile(0, 0));
    }

    [Fact]
    public void TilePalette_Stamp_CopiesRectangleOntoTilemap()
    {
        var palette = new TilePalette();
        var a = new Tile();
        var b = new Tile();
        palette.SetSize(2, 1);
        palette.SetTile(0, 0, a);
        palette.SetTile(1, 0, b);

        var map = new Tilemap();
        palette.Stamp(map, destX: 10, destY: 3, srcX: 0, srcY: 0, srcWidth: 2, srcHeight: 1);

        Assert.Same(a, map.GetTile(10, 3));
        Assert.Same(b, map.GetTile(11, 3));
    }
}

/// <summary>Per-chunk BoxCollider2D children merged by <see cref="CompositeCollider2D"/>.</summary>
public sealed class TilemapCollider2DTests : RuntimeTestBase
{
    [Fact]
    public void TilemapCollider2D_Rebuild_OneBoxPerOccupiedTile()
    {
        GameObject go = CreateGameObject("Map");
        go.AddComponent<Grid>().CellSize = new Float3(1f, 1f, 0f);
        var map = go.AddComponent<Tilemap>();
        go.AddComponent<Rigidbody2D>().BodyType = RigidbodyType2D.Static;
        go.AddComponent<CompositeCollider2D>();
        var collider = go.AddComponent<TilemapCollider2D>();
        map.SetTile(0, 0, new Tile());
        map.SetTile(1, 0, new Tile());
        map.SetTile(32, 0, new Tile());

        collider.RebuildDirtyChunks();

        Assert.Equal(2, collider.ChunkColliderCount);
        Assert.Equal(2, collider.GetChunkBoxCount(0, 0));
        Assert.Equal(1, collider.GetChunkBoxCount(1, 0));
    }

    [Fact]
    public void TilemapCollider2D_Rebuild_OnlyReplacesEditedChunkHost()
    {
        GameObject go = CreateGameObject("Map");
        go.AddComponent<Grid>();
        var map = go.AddComponent<Tilemap>();
        go.AddComponent<Rigidbody2D>().BodyType = RigidbodyType2D.Static;
        go.AddComponent<CompositeCollider2D>();
        var collider = go.AddComponent<TilemapCollider2D>();
        map.SetTile(0, 0, new Tile());
        collider.RebuildDirtyChunks();
        GameObject first = collider.GetChunkHost(0, 0)!;

        map.SetTile(32, 0, new Tile());
        collider.RebuildDirtyChunks();

        Assert.Same(first, collider.GetChunkHost(0, 0));
        Assert.Equal(2, collider.ChunkColliderCount);
    }

    [Fact]
    public void TilemapCollider2D_Composite_SupportsDynamicRest()
    {
        Scene scene = CreateScene(enable: true);
        GameObject mapGo = CreateGameObject("Map");
        mapGo.AddComponent<Grid>();
        var map = mapGo.AddComponent<Tilemap>();
        mapGo.AddComponent<Rigidbody2D>().BodyType = RigidbodyType2D.Static;
        mapGo.AddComponent<CompositeCollider2D>();
        var collider = mapGo.AddComponent<TilemapCollider2D>();
        map.SetTile(0, 0, new Tile());
        map.SetTile(1, 0, new Tile());
        scene.Add(mapGo);
        collider.RebuildDirtyChunks();

        GameObject box = CreateGameObject("Dynamic");
        box.Transform.Position = new Float3(0.5f, 3f, 0f);
        box.AddComponent<Rigidbody2D>().BodyType = RigidbodyType2D.Dynamic;
        box.AddComponent<BoxCollider2D>();
        scene.Add(box);

        Tick(scene, 180);

        Assert.InRange(box.Transform.Position.Y, 1.3f, 1.7f);
    }
}

/// <summary>RuleTile, AnimatedTile, WeightedRandomTile, and GameObjectTile (no GPU, no sprites).</summary>
public sealed class TilemapExtrasTests : RuntimeTestBase
{
    [Fact]
    public void RuleTile_Resolve_MatchesThisNeighbor()
    {
        var isolated = new Tile();
        var connected = new Tile();
        var rule = new RuleTile { DefaultTile = isolated };
        rule.Rules.Add(new RuleTile.Rule { East = RuleTile.Neighbor.This, Output = connected });

        var map = new Tilemap();
        map.SetTile(0, 0, rule);
        map.SetTile(1, 0, rule);

        Assert.Same(connected, rule.Resolve(map, 0, 0));
        Assert.Same(isolated, rule.Resolve(map, 1, 0));
    }

    [Fact]
    public void AnimatedTile_GetFrame_WrapsByTime()
    {
        var a = new Tile();
        var b = new Tile();
        var anim = new AnimatedTile { FramesPerSecond = 1f };
        anim.Frames = [a, b];

        Assert.Same(a, anim.GetFrame(0f));
        Assert.Same(b, anim.GetFrame(1.5f));
        Assert.Same(a, anim.GetFrame(2f));
    }

    [Fact]
    public void WeightedRandomTile_Pick_IsDeterministicForCell()
    {
        var rare = new Tile();
        var common = new Tile();
        var weighted = new WeightedRandomTile();
        weighted.Add(rare, 1);
        weighted.Add(common, 99);

        TileBase? first = weighted.Pick(seed: 7, x: 3, y: 4);
        TileBase? second = weighted.Pick(seed: 7, x: 3, y: 4);

        Assert.Same(first, second);
        Assert.True(first == rare || first == common);
    }

    [Fact]
    public void GameObjectTile_InstantiateAt_CreatesNamedChild()
    {
        GameObject go = CreateGameObject("Map");
        var map = go.AddComponent<Tilemap>();
        var tile = new GameObjectTile { PrefabName = "Tree" };

        GameObject instance = tile.InstantiateAt(map, 2, 3)!;

        Assert.Equal("Tree", instance.Name);
        Assert.Same(go, instance.Parent);
    }
}

/// <summary>Rectangle and hex GridBrush fills in cell-index space (layout only affects world pose).</summary>
public sealed class GridBrushTests : RuntimeTestBase
{
    [Fact]
    public void GridBrush_FillRect_PaintsInclusiveBox()
    {
        var tile = new Tile();
        var brush = new GridBrush { Tile = tile };
        var map = new Tilemap();

        brush.FillRect(map, 1, 2, 2, 3);

        Assert.Same(tile, map.GetTile(1, 2));
        Assert.Same(tile, map.GetTile(2, 3));
        Assert.Null(map.GetTile(0, 2));
        Assert.Null(map.GetTile(1, 1));
        Assert.Equal(4, CountOccupied(map, 0, 0, 4, 4));
    }

    [Fact]
    public void GridBrush_FillHex_Radius1_PaintsSevenCells()
    {
        var tile = new Tile();
        var brush = new GridBrush { Tile = tile };
        var map = new Tilemap();

        brush.FillHex(map, 5, 5, radius: 1);

        Assert.Same(tile, map.GetTile(5, 5));
        Assert.Same(tile, map.GetTile(6, 5));
        Assert.Same(tile, map.GetTile(6, 4));
        Assert.Same(tile, map.GetTile(5, 4));
        Assert.Same(tile, map.GetTile(4, 5));
        Assert.Same(tile, map.GetTile(4, 6));
        Assert.Same(tile, map.GetTile(5, 6));
        Assert.Null(map.GetTile(6, 6));
        Assert.Equal(7, CountOccupied(map, 3, 3, 8, 8));
    }

    private static int CountOccupied(Tilemap map, int x0, int y0, int x1, int y1)
    {
        int count = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (map.GetTile(x, y) != null)
                    count++;
            }
        }

        return count;
    }
}
