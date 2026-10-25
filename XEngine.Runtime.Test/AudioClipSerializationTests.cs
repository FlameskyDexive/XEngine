using System.Runtime.InteropServices;
using XEngine.Echo;
using XEngine.Runtime.Audio;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class AudioClipSerializationTests
{
    [Fact]
    public void CachedClip_RoundTripsEncodedDataThroughEngineObject()
    {
        byte[] encoded = [82, 73, 70, 70, 4, 0, 0, 0, 87, 65, 86, 69];
        EchoObject cache;
        using (var source = new AudioClip(encoded))
        {
            source.ClipName = "Portable tone";
            cache = Serializer.Serialize(typeof(EngineObject), source);
        }

        using var restored = Assert.IsType<AudioClip>(Serializer.Deserialize<EngineObject>(cache));
        Assert.Equal("Portable tone", restored.ClipName);
        Assert.Equal(string.Empty, restored.FilePath);
        Assert.False(restored.StreamFromDisk);
        Assert.Equal((ulong)encoded.Length, restored.DataSize);
        var roundTrip = new byte[encoded.Length];
        Marshal.Copy(restored.Handle, roundTrip, 0, roundTrip.Length);
        Assert.Equal(encoded, roundTrip);
    }

    [Fact]
    public void CachedClip_SharedDataRemainsRegisteredUntilBothClipsAreDisposed()
    {
        byte[] encoded = [82, 73, 70, 70, 5, 0, 0, 0, 87, 65, 86, 69, 1];
        using var source = new AudioClip(encoded);
        var hash = source.Hash;
        var cache = Serializer.Serialize(typeof(EngineObject), source);
        using var restored = Assert.IsType<AudioClip>(Serializer.Deserialize<EngineObject>(cache));
        Assert.Equal(source.Handle, restored.Handle);

        restored.Dispose();
        Assert.True(AudioContext.GetAudioClipHandle(hash, out var retained));
        Assert.Equal(source.Handle, retained);

        source.Dispose();
        Assert.False(AudioContext.GetAudioClipHandle(hash, out _));
    }
}
