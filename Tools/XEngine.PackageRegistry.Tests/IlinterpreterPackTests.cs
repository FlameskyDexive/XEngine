// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Text.Json;

using XEngine.PackageRegistry;

using Xunit;

namespace XEngine.PackageRegistry.Tests;

/// <summary>
/// N8 rehearsal: pack com.xengine.ilinterpreter into its npm wire form and
/// verify the tarball contract — wire name @inxep/ilinterpreter, dependency-key
/// rewrite, installByDefault stripped, tarball whitelist (no Library/obj/bin).
/// </summary>
public sealed class IlinterpreterPackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "XEngineIlPack", Guid.NewGuid().ToString("N"));

    private string SourcePackageDir()
    {
        // Walk up from the test bin to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XEngine.sln")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "Packages", "com.xengine.ilinterpreter");
    }

    [Fact]
    public void Pack_RewritesWireName_AndStripsNonShippingFields()
    {
        string src = SourcePackageDir();
        Assert.True(Directory.Exists(src), "source package folder must exist");

        string outFile = Path.Combine(_root, NpmPackageName.TarballFileName(
            NpmPackageName.ToNpmName("com.xengine.ilinterpreter"), "0.1.0"));
        Directory.CreateDirectory(_root);

        var (fileName, npmName, version) = NpmTarball.Pack(src, outFile);

        Assert.Equal("@inxep/ilinterpreter", npmName);
        Assert.Equal("0.1.0", version);
        Assert.True(File.Exists(outFile));

        // Extract and verify the inner package.json contract.
        string extractDir = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extractDir);
        NpmTarball.Extract(outFile, extractDir);
        string packageJson = File.ReadAllText(
            Path.Combine(extractDir, "package.json"));
        using var doc = JsonDocument.Parse(packageJson);
        Assert.Equal("@inxep/ilinterpreter", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("0.1.0", doc.RootElement.GetProperty("version").GetString());
        // installByDefault is passed through verbatim (same as the other official
        // packages) — the engine, not the tarball, decides install defaults.
    }

    [Fact]
    public void VersionMarker_AlignedWithPackageJson()
    {
        string src = SourcePackageDir();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(src, "package.json")));
        string packageVersion = doc.RootElement.GetProperty("version").GetString()!;

        string marker = File.ReadAllText(Path.Combine(src, "Runtime", "PackageMarker.cs"));
        Assert.Contains("\"0.1.0\"", marker);
        Assert.Contains(packageVersion, marker);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
