using System;
using System.IO;
using XEngine.Echo;
using XEngine.Editor.Projects.Settings;

namespace XEngine.Editor.Build.Profiles;

/// <summary>Marks packaged meshes for compact GPU uploads without changing source assets or caches.</summary>
internal static class BuildMeshVertexCompression
{
    internal static bool Enabled => BuildProfileRegistry.GetActive(EditorRegistries.GetSettings<BuildSettings>())
        ?.Profile.AssetImportOverrides is { Enabled: true, CompressMeshVertexData: true };

    internal static byte[] Transform(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream);
        var echo = EchoObject.ReadFromBinary(reader);
        if (!echo.TryGet("MeshData", out _)) return payload;
        echo["CompressVertexData"] = new EchoObject(true);
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true)) echo.WriteToBinary(writer);
        return output.ToArray();
    }
}
