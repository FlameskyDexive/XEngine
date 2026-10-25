using System.IO.Compression;
using System.Formats.Tar;
using System.Text.Json;

using XEngine.PackageRegistry;

using Xunit;

namespace XEngine.PackageRegistry.Tests;

public sealed class VoxelWorldPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XEngineVoxelWorldPack", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Pack_RewritesWireName_AndExcludesDungeonMason()
    {
        string src = SourcePackageDir();
        string outFile = Path.Combine(_root, "inxep-voxelworld-0.1.0.tgz");
        Directory.CreateDirectory(_root);

        var (fileName, npmName, version) = NpmTarball.Pack(src, outFile);
        Assert.Equal("inxep-voxelworld-0.1.0.tgz", fileName);
        Assert.Equal("@inxep/voxelworld", npmName);
        Assert.Equal("0.1.0", version);
        Assert.True(new FileInfo(outFile).Length > 1000);

        string extractDir = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extractDir);
        NpmTarball.Extract(outFile, extractDir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(extractDir, "package.json")));
        Assert.Equal("@inxep/voxelworld", doc.RootElement.GetProperty("name").GetString());
        Assert.Contains("xengine", doc.RootElement.GetProperty("keywords").EnumerateArray().Select(e => e.GetString()));
        Assert.True(File.Exists(Path.Combine(extractDir, "THIRD_PARTY_ASSETS.md")));

        using var gzip = new GZipStream(File.OpenRead(outFile), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
            Assert.DoesNotContain("DungeonMason", entry.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionMarker_AlignedWithPackageJson()
    {
        string src = SourcePackageDir();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(src, "package.json")));
        string version = doc.RootElement.GetProperty("version").GetString()!;
        string marker = File.ReadAllText(Path.Combine(src, "Runtime", "VoxelWorldRuntime.cs"));
        Assert.Contains($"\"{version}\"", marker, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string SourcePackageDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XEngine.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Packages", "com.xengine.voxelworld");
    }
}
