// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using XEngine.PackageRegistry;

namespace XEngine.PackageRegistry.Tests;

public class NpmTarballTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xengine-npm-tarball-tests-" + Guid.NewGuid().ToString("N"));

    public NpmTarballTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void PackThenExtract_RoundTripsFiles_AndRewritesWireNames()
    {
        string source = TestPackageDir.Create(_root, "com.xengine.demo", "0.3.7", dependency: "com.xengine.xgui");
        string tgz = Path.Combine(_root, "demo.tgz");

        (string fileName, string wireName, string version) = NpmTarball.Pack(source, tgz);

        Assert.Equal("inxep-demo-0.3.7.tgz", fileName);
        Assert.Equal("@inxep/demo", wireName);
        Assert.Equal("0.3.7", version);
        Assert.True(File.Exists(tgz));

        string extracted = Path.Combine(_root, "extracted");
        NpmTarball.Extract(tgz, extracted);

        Assert.True(File.Exists(Path.Combine(extracted, "package.json")));
        Assert.True(File.Exists(Path.Combine(extracted, "Runtime", "Marker.cs")));
        Assert.Equal(File.ReadAllText(Path.Combine(source, "Runtime", "Marker.cs")),
            File.ReadAllText(Path.Combine(extracted, "Runtime", "Marker.cs")));

        // The tarball's manifest carries wire names; the on-disk source keeps engine names.
        JsonObject packed = JsonNode.Parse(File.ReadAllText(Path.Combine(extracted, "package.json")))!.AsObject();
        Assert.Equal("@inxep/demo", packed["name"]!.GetValue<string>());
        Assert.Equal("0.3.7", packed["version"]!.GetValue<string>());
        Assert.Equal("0.1.0", packed["dependencies"]!["@inxep/xgui"]!.GetValue<string>());

        JsonObject onDisk = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "package.json")))!.AsObject();
        Assert.Equal("com.xengine.demo", onDisk["name"]!.GetValue<string>());
        Assert.Contains("com.xengine.xgui", onDisk["dependencies"]!.AsObject());
    }

    [Fact]
    public void Pack_IsDeterministic_ForIdenticalSources()
    {
        string source = TestPackageDir.Create(_root, "com.xengine.demo", "1.0.0");
        string first = Path.Combine(_root, "a.tgz");
        string second = Path.Combine(_root, "b.tgz");

        // Settle mtimes so entry headers compare equal across both packs.
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        NpmTarball.Pack(source, first);
        NpmTarball.Pack(source, second);

        // gzip payload bytes should be identical for identical inputs (deterministic order + mtimes).
        Assert.Equal(File.ReadAllBytes(first).Length, File.ReadAllBytes(second).Length);
        Assert.True(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)));
    }

    [Fact]
    public void Pack_ThrowsWithoutPackageJson()
    {
        string dir = Path.Combine(_root, "no-manifest");
        Directory.CreateDirectory(dir);
        Assert.Throws<FileNotFoundException>(() => NpmTarball.Pack(dir, Path.Combine(_root, "x.tgz")));
    }

    [Fact]
    public void Extract_RejectsEntriesOutsidePackageRoot()
    {
        string tgz = Path.Combine(_root, "evil.tgz");
        using (FileStream output = File.Create(tgz))
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/good.txt")
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("ok")),
            });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "outside.txt")
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("evil")),
            });
        }

        Assert.Throws<InvalidDataException>(() => NpmTarball.Extract(tgz, Path.Combine(_root, "evil-out")));
    }

    [Fact]
    public void Extract_RejectsPathTraversal()
    {
        string tgz = Path.Combine(_root, "traversal.tgz");
        using (FileStream output = File.Create(tgz))
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/../../escape.txt")
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("evil")),
            });
        }

        Assert.Throws<InvalidDataException>(() => NpmTarball.Extract(tgz, Path.Combine(_root, "traversal-out")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }
}

public class NpmTarballExclusionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xengine-npm-tarball-excl-" + Guid.NewGuid().ToString("N"));

    public NpmTarballExclusionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Pack_SkipsObjAndBin_AtAnyDepth()
    {
        string source = TestPackageDir.Create(_root, "com.xengine.demo", "0.3.7");
        // Regenerate-on-every-restore NuGet artifacts (project-local by design) + nested bin.
        string objDir = Path.Combine(source, "Tests~", "obj");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "X.Engine.Tests.csproj.nuget.dgspec.json"), "{}");
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}");
        string binDir = Path.Combine(source, "Runtime", "bin", "Debug");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "stale.dll"), "junk");

        string tgz = Path.Combine(_root, "demo.tgz");
        NpmTarball.Pack(source, tgz);

        var names = new System.Collections.Generic.List<string>();
        using var gzip = new GZipStream(File.OpenRead(tgz), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
            names.Add(entry.Name);

        Assert.DoesNotContain(names, n => n.Contains("/obj/", StringComparison.Ordinal) || n.EndsWith("/obj", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("/bin/", StringComparison.Ordinal) || n.EndsWith("/bin", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("Runtime/Marker.cs", StringComparison.Ordinal));
    }
}
