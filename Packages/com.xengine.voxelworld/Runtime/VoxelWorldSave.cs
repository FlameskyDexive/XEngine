using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Self-owned Echo save: a list of edited world cells. Not Kronnect format 14.</summary>
public sealed class VoxelWorldSave
{
    public const int FormatVersion = 1;

    public int Version = FormatVersion;
    public List<VoxelSaveEdit> Edits = [];

    public void Record(Int3 world, ushort type)
    {
        for (int i = 0; i < Edits.Count; i++)
        {
            if (Edits[i].X == world.X && Edits[i].Y == world.Y && Edits[i].Z == world.Z)
            {
                Edits[i] = new VoxelSaveEdit { X = world.X, Y = world.Y, Z = world.Z, Type = type };
                return;
            }
        }

        Edits.Add(new VoxelSaveEdit { X = world.X, Y = world.Y, Z = world.Z, Type = type });
    }

    public void Apply(VoxelWorldGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        for (int i = 0; i < Edits.Count; i++)
        {
            VoxelSaveEdit edit = Edits[i];
            var cell = new Int3(edit.X, edit.Y, edit.Z);
            grid.TrySetVoxel(cell, edit.Type == 0 ? Voxel.Empty : Voxel.Solid(edit.Type));
        }
    }

    public void Apply(VoxelChunkStreamer streamer)
    {
        ArgumentNullException.ThrowIfNull(streamer);
        Apply(streamer.Grid);
        streamer.SyncViews();
    }

    public string ToEcho() => Serializer.Serialize(typeof(object), this).WriteToString();

    public static VoxelWorldSave FromEcho(string text)
    {
        object? loaded = Serializer.Deserialize<object>(EchoObject.ReadFromString(text));
        return loaded as VoxelWorldSave ?? throw new InvalidOperationException("Save file is not a VoxelWorldSave.");
    }

    public static void WriteFile(string path, VoxelWorldSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, save.ToEcho());
    }

    public static VoxelWorldSave ReadFile(string path) => FromEcho(File.ReadAllText(path));
}

public struct VoxelSaveEdit
{
    public int X;
    public int Y;
    public int Z;
    public ushort Type;
}
