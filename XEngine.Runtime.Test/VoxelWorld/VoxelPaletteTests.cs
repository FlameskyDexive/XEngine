using System.Text;

using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelPaletteTests
{
    [Fact]
    public void Fill_SingleValued_GetAllSame()
    {
        var p = VoxelPalette.Fill(3);
        Assert.Equal(0, p.BitsPerEntry);
        Assert.Equal(4096, p.NonAirCount);
        Assert.Equal((ushort)3, p.Get(0));
        Assert.Equal((ushort)3, p.Get(4095));
        Assert.Empty(p.Data);
    }

    [Fact]
    public void Set_IntroducesSecondType_GrowsTo4Bit()
    {
        var p = VoxelPalette.Fill(3);
        p.Set(VoxelCoords.Index(8, 15, 8), 0);
        Assert.Equal(4, p.BitsPerEntry);
        Assert.Equal(4095, p.NonAirCount);
        Assert.Equal((ushort)0, p.Get(VoxelCoords.Index(8, 15, 8)));
        Assert.Equal((ushort)3, p.Get(0));
    }

    [Fact]
    public void CompactExpand_RoundTrip_PreservesTypes()
    {
        var dense = new Voxel[4096];
        dense.AsSpan().Fill(Voxel.Solid(3));
        dense[VoxelCoords.Index(1, 2, 3)] = Voxel.Empty;
        dense[VoxelCoords.Index(4, 5, 6)] = Voxel.Solid(4);
        var p = VoxelPalette.CompactFrom(dense);
        var back = new Voxel[4096];
        p.ExpandTo(back);
        for (int i = 0; i < 4096; i++)
            Assert.Equal(dense[i].TypeIndex, back[i].TypeIndex);
    }

    [Fact]
    public void WriteRead_PaddedLongs_Stable()
    {
        var p = VoxelPalette.Fill(2);
        p.Set(7, 4);
        using var ms = new MemoryStream();
        p.WriteTo(ms);
        ms.Position = 0;
        VoxelPalette q = VoxelPalette.ReadFrom(ms);
        Assert.Equal(p.BitsPerEntry, q.BitsPerEntry);
        Assert.Equal(p.Get(7), q.Get(7));
        Assert.Equal(p.Get(0), q.Get(0));
    }

    [Fact]
    public void PackedSize_SingleValued_SmallerThanDense()
    {
        var p = VoxelPalette.Fill(3);
        using var ms = new MemoryStream();
        p.WriteTo(ms);
        Assert.True(ms.Length < 32);
        Assert.True(ms.Length < 4096 * 2);
    }
}
