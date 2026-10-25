using System.Runtime.InteropServices;
using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class AudioImporterTests : EditorTestHarness
{
    [Fact]
    public void ImportedAudio_CacheCarriesEncodedDataWithoutTheSourceFile()
    {
        // The importer stores encoded bytes; decoding belongs to the native playback backend.
        byte[] encoded = [82, 73, 70, 70, 4, 0, 0, 0, 87, 65, 86, 69];
        string path = AssetAbsolutePath("中文 tone.wav");
        File.WriteAllBytes(path, encoded);
        Guid guid = Assets.ImportFile("中文 tone.wav");
        var imported = Assert.IsType<AudioClip>(Assets.Get(guid));
        EchoObject cache = Serializer.Serialize(typeof(EngineObject), imported);

        Assert.Equal(string.Empty, imported.FilePath);
        Assert.Equal((ulong)encoded.Length, imported.DataSize);
        File.Delete(path);
        using var restored = Assert.IsType<AudioClip>(Serializer.Deserialize<EngineObject>(cache));
        var roundTrip = new byte[encoded.Length];
        Marshal.Copy(restored.Handle, roundTrip, 0, roundTrip.Length);
        Assert.Equal(encoded, roundTrip);
        Assert.False(restored.StreamFromDisk);
        Assert.True(new AudioImporter().Version > 1, "Invalidate caches that only carried an absolute source path.");
    }
}
