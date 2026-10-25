using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Recenters large world coordinates so float precision stays usable.</summary>
public static class VoxelOriginShift
{
    public static bool NeedsShift(Float3 focus, float threshold = 2048f) =>
        Math.Abs(focus.X) >= threshold || Math.Abs(focus.Z) >= threshold;

    public static Float3 Recenter(Float3 focus, out Int3 worldShift)
    {
        int x = (int)MathF.Round(focus.X / VoxelCoords.ChunkSize) * VoxelCoords.ChunkSize;
        int z = (int)MathF.Round(focus.Z / VoxelCoords.ChunkSize) * VoxelCoords.ChunkSize;
        worldShift = new Int3(x, 0, z);
        return new Float3(focus.X - x, focus.Y, focus.Z - z);
    }

    /// <summary>Rebase legacy Echo edit lists after a recenter. Prefer <see cref="VoxelRegionStore.ShiftWorld"/> for region saves.</summary>
    public static void ShiftEdits(VoxelWorldSave save, Int3 worldShift)
    {
        ArgumentNullException.ThrowIfNull(save);
        for (int i = 0; i < save.Edits.Count; i++)
        {
            VoxelSaveEdit edit = save.Edits[i];
            save.Edits[i] = new VoxelSaveEdit
            {
                X = edit.X - worldShift.X,
                Y = edit.Y - worldShift.Y,
                Z = edit.Z - worldShift.Z,
                Type = edit.Type,
            };
        }
    }

    public static void ShiftStore(VoxelRegionStore store, Int3 worldShift)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.ShiftWorld(worldShift);
    }
}
