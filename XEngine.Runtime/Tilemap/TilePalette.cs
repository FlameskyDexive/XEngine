// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>
/// Grid of tiles used as a paint source. Select a cell and <see cref="Paint"/> it onto a
/// <see cref="Tilemap"/>, or <see cref="Stamp"/> a rectangle.
/// </summary>
[CreateAssetMenu("2D/Tile Palette", Extension = ".tilepalette", Order = 61)]
public sealed class TilePalette : ScriptableObject
{
    [SerializeField] private int width;
    [SerializeField] private int height;
    [SerializeField] private TileBase?[] cells = [];
    [SerializeField] private int selectedX;
    [SerializeField] private int selectedY;
    [SerializeField] private GridLayout layout = GridLayout.Rectangle;

    public int Width => width;
    public int Height => height;

    public GridLayout Layout
    {
        get => layout;
        set => layout = value;
    }

    public int SelectedX => selectedX;
    public int SelectedY => selectedY;
    public TileBase? SelectedTile => GetTile(selectedX, selectedY);

    public void SetSize(int newWidth, int newHeight)
    {
        newWidth = Math.Max(newWidth, 0);
        newHeight = Math.Max(newHeight, 0);
        var next = new TileBase?[newWidth * newHeight];
        int copyW = Math.Min(width, newWidth);
        int copyH = Math.Min(height, newHeight);
        for (int y = 0; y < copyH; y++)
        {
            for (int x = 0; x < copyW; x++)
                next[y * newWidth + x] = GetTile(x, y);
        }

        width = newWidth;
        height = newHeight;
        cells = next;
        ClampSelection();
    }

    public TileBase? GetTile(int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            return null;
        return cells[y * width + x];
    }

    public void SetTile(int x, int y, TileBase? tile)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            return;
        cells[y * width + x] = tile;
    }

    public void Select(int x, int y)
    {
        selectedX = x;
        selectedY = y;
        ClampSelection();
    }

    public void Paint(Tilemap map, int cellX, int cellY)
    {
        if (map == null)
            return;
        map.SetTile(cellX, cellY, SelectedTile);
    }

    public void Stamp(Tilemap map, int destX, int destY, int srcX, int srcY, int srcWidth, int srcHeight)
    {
        if (map == null)
            return;

        for (int y = 0; y < srcHeight; y++)
        {
            for (int x = 0; x < srcWidth; x++)
                map.SetTile(destX + x, destY + y, GetTile(srcX + x, srcY + y));
        }
    }

    private void ClampSelection()
    {
        if (width <= 0 || height <= 0)
        {
            selectedX = 0;
            selectedY = 0;
            return;
        }

        if (selectedX < 0)
            selectedX = 0;
        else if (selectedX >= width)
            selectedX = width - 1;

        if (selectedY < 0)
            selectedY = 0;
        else if (selectedY >= height)
            selectedY = height - 1;
    }
}
