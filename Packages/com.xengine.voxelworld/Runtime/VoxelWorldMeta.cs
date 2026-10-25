using System;
using System.IO;

using XEngine.Echo;

namespace XEngine.VoxelWorld;

/// <summary>World directory metadata written beside region/*.vwr.</summary>
public sealed class VoxelWorldMeta
{
    public const int FormatVersion = 2;

    public int Version = FormatVersion;
    public int Seed;
    public int MinChunkY;
    public int MaxChunkY = 3;
    public string GeneratorKind = "flat";

    public string ToEcho() => Serializer.Serialize(typeof(object), this).WriteToString();

    public static VoxelWorldMeta FromEcho(string text)
    {
        object? loaded = Serializer.Deserialize<object>(EchoObject.ReadFromString(text));
        return loaded as VoxelWorldMeta ?? throw new InvalidOperationException("File is not a VoxelWorldMeta.");
    }

    public static void WriteFile(string path, VoxelWorldMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, meta.ToEcho());
    }

    public static VoxelWorldMeta ReadFile(string path) => FromEcho(File.ReadAllText(path));
}
