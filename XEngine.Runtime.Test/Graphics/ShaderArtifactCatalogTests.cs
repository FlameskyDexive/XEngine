// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ShaderArtifactCatalogTests
{
    [Fact]
    public void StableV2EntriesRoundTripDeterministically()
    {
        Guid assetGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        ShaderArtifactKey firstKey = ShaderArtifactKey.Parse(new string('1', ShaderArtifactKey.ByteLength * 2));
        ShaderArtifactKey secondKey = ShaderArtifactKey.Parse(new string('2', ShaderArtifactKey.ByteLength * 2));
        var first = new ShaderArtifactCatalogEntry(
            assetGuid,
            "Default/Standard",
            "Forward",
            GraphicsBackend.Vulkan,
            "spirv_1_5",
            "HAS_TANGENTS;",
            firstKey);
        var second = new ShaderArtifactCatalogEntry(
            assetGuid,
            "Default/Standard",
            "Shadow",
            GraphicsBackend.Vulkan,
            "spirv_1_5",
            "",
            secondKey);

        byte[] firstBytes = Write(new ShaderArtifactCatalog([second, first]));
        byte[] secondBytes = Write(new ShaderArtifactCatalog([first, second]));
        Assert.Equal(firstBytes, secondBytes);

        using var stream = new MemoryStream(firstBytes, writable: false);
        ShaderArtifactCatalog roundTrip = ShaderArtifactCatalog.Read(stream);
        Assert.True(roundTrip.HasStableEntries);
        Assert.Equal(2, roundTrip.StableEntries.Count);
        Assert.Equal(firstKey, roundTrip.StableEntries[0].ArtifactKey);
        Assert.Equal("HAS_TANGENTS;", roundTrip.StableEntries[0].CanonicalKeywordSet);
        Assert.Equal([firstKey, secondKey], roundTrip.Entries[assetGuid]);
    }

    [Fact]
    public void RoundTripIsDeterministicAndCanonical()
    {
        Guid firstGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid secondGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        ShaderArtifactKey firstKey = ShaderArtifactKey.Parse(new string('1', ShaderArtifactKey.ByteLength * 2));
        ShaderArtifactKey secondKey = ShaderArtifactKey.Parse(new string('2', ShaderArtifactKey.ByteLength * 2));

        var firstEntries = new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>>
        {
            [secondGuid] = [secondKey, firstKey, secondKey],
            [firstGuid] = [secondKey],
        };
        var secondEntries = new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>>
        {
            [firstGuid] = [secondKey],
            [secondGuid] = [firstKey, secondKey],
        };

        byte[] firstBytes = Write(new ShaderArtifactCatalog(firstEntries));
        byte[] secondBytes = Write(new ShaderArtifactCatalog(secondEntries));
        Assert.Equal(firstBytes, secondBytes);

        using var stream = new MemoryStream(firstBytes, writable: false);
        ShaderArtifactCatalog roundTrip = ShaderArtifactCatalog.Read(stream);
        Assert.Equal([firstKey, secondKey], roundTrip.Entries[secondGuid]);
        Assert.Equal([secondKey], roundTrip.Entries[firstGuid]);
    }

    [Fact]
    public void ReadRejectsCorruptPayload()
    {
        Guid assetGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        ShaderArtifactKey key = ShaderArtifactKey.Parse(new string('A', ShaderArtifactKey.ByteLength * 2));
        byte[] bytes = Write(new ShaderArtifactCatalog(
            new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>> { [assetGuid] = [key] }));
        bytes[^1] ^= 0x7F;

        using var stream = new MemoryStream(bytes, writable: false);
        Assert.Throws<InvalidDataException>(() => ShaderArtifactCatalog.Read(stream));
    }

    private static byte[] Write(ShaderArtifactCatalog catalog)
    {
        using var stream = new MemoryStream();
        catalog.Write(stream);
        return stream.ToArray();
    }
}
