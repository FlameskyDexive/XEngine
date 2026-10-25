// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

public enum GridLayout
{
    Rectangle = 0,
    Isometric = 1,
    Hexagon = 2
}

/// <summary>Converts cell indices to local XY for a <see cref="Tilemap"/>. Rectangle layout first.</summary>
[AddComponentMenu("2D/Grid")]
[ComponentIcon("\uf00a")]
public sealed class Grid : MonoBehaviour
{
    [SerializeField] private Float3 cellSize = new(1f, 1f, 0f);
    [SerializeField] private GridLayout layout = GridLayout.Rectangle;

    public Float3 CellSize
    {
        get => cellSize;
        set => cellSize = value;
    }

    public GridLayout Layout
    {
        get => layout;
        set => layout = value;
    }

    public Float3 CellToLocal(int x, int y)
    {
        float w = cellSize.X;
        float h = cellSize.Y;
        if (layout == GridLayout.Isometric)
            return new Float3((x - y) * w * 0.5f, (x + y) * h * 0.5f, 0f);
        if (layout == GridLayout.Hexagon)
            return new Float3((x + y * 0.5f) * w, y * h, 0f);

        return new Float3(x * w, y * h, 0f);
    }

    public void LocalToCell(Float3 local, out int x, out int y)
    {
        float w = Maths.Max(cellSize.X, 1e-6f);
        float h = Maths.Max(cellSize.Y, 1e-6f);
        if (layout == GridLayout.Isometric)
        {
            float u = local.X / (w * 0.5f);
            float v = local.Y / (h * 0.5f);
            x = FloorToInt((u + v) * 0.5f);
            y = FloorToInt((v - u) * 0.5f);
            return;
        }

        if (layout == GridLayout.Hexagon)
        {
            y = FloorToInt(local.Y / h);
            x = FloorToInt(local.X / w - y * 0.5f);
            return;
        }

        x = FloorToInt(local.X / w);
        y = FloorToInt(local.Y / h);
    }

    private static int FloorToInt(float v) => (int)Maths.Floor(v);
}
