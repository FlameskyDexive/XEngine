using System.Text;

using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelRegionFileTests
{
    [Fact]
    public void WriteThenRead_RoundTripPayload()
    {
        string path = Path.Combine(Path.GetTempPath(), "vw-r.0.0.vwr");
        File.Delete(path);
        using (var region = VoxelRegionFile.Open(path))
            region.WriteColumn(3, 5, "hello-column"u8);
        using (var region = VoxelRegionFile.Open(path))
        {
            Assert.True(region.TryReadColumn(3, 5, out byte[] payload));
            Assert.Equal("hello-column", Encoding.UTF8.GetString(payload));
            Assert.False(region.TryReadColumn(0, 0, out _));
        }
        Assert.Equal(0, new FileInfo(path).Length % 4096);
    }

    [Fact]
    public void MissingFile_ReadsAsEmpty()
    {
        using var region = VoxelRegionFile.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vwr"));
        Assert.False(region.TryReadColumn(0, 0, out _));
    }

    [Fact]
    public void DeleteColumn_ClearsSlot()
    {
        string path = Path.Combine(Path.GetTempPath(), "vw-r-del.vwr");
        File.Delete(path);
        using (var region = VoxelRegionFile.Open(path))
        {
            region.WriteColumn(1, 2, "gone"u8);
            region.DeleteColumn(1, 2);
        }
        using (var region = VoxelRegionFile.Open(path))
            Assert.False(region.TryReadColumn(1, 2, out _));
    }
}
