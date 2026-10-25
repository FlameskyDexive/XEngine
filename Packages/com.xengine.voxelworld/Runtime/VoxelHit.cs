using XEngine.Vector;

namespace XEngine.VoxelWorld;

public readonly struct VoxelHit
{
    public readonly Int3 Cell;
    public readonly Int3 Normal;
    public readonly float Distance;

    public VoxelHit(Int3 cell, Int3 normal, float distance)
    {
        Cell = cell;
        Normal = normal;
        Distance = distance;
    }

    public Int3 PlacementCell => new(Cell.X + Normal.X, Cell.Y + Normal.Y, Cell.Z + Normal.Z);
}
