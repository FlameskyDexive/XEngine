using System;
using System.IO;
using System.IO.Compression;
using XHub.Models;
using XHub.Platform;
using XHub.Services;
using Xunit;

namespace XHub.Test;

public class ReleaseManifestTests
{
    [Fact]
    public void ToEntry_ValidatesAndDerivesChannel()
    {
        var manifest = new ReleaseManifest
        {
            CanonicalVersion = "V1.0.0a1", // manifest may ship the display form; parsed either way
            DisplayVersion = "V1.0.0a1",
            Channel = "stable", // ignored — channel derives from the version (§4.2)
            Rids = { ["win-x64"] = new ReleaseRid { Url = "http://x/pkg.zip", Size = 10, Sha256 = "ab" } },
            MinHubProtocol = 1,
        };

        var entry = ReleaseFeedService.ToEntry(manifest);
        Assert.NotNull(entry);
        Assert.Equal("1.0.0-a1", entry.CanonicalVersion);
        Assert.Equal("preview", entry.Channel);
        Assert.Equal("http://x/pkg.zip", entry.DownloadUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void ToEntry_RejectsUnparsableVersions(string version)
    {
        var manifest = new ReleaseManifest
        {
            CanonicalVersion = version,
            Rids = { ["win-x64"] = new ReleaseRid { Url = "u", Size = 1, Sha256 = "s" } },
        };
        Assert.Null(ReleaseFeedService.ToEntry(manifest));
    }

    [Fact]
    public void ToEntry_RejectsMissingRid()
    {
        var manifest = new ReleaseManifest
        {
            CanonicalVersion = "1.0.0-a1",
            Rids = { ["linux-x64"] = new ReleaseRid { Url = "u", Size = 1, Sha256 = "s" } },
        };
        Assert.Null(ReleaseFeedService.ToEntry(manifest));
    }
}

[Collection("Avalonia")]
public class InstallServiceTests : IDisposable
{
    private readonly string _dir;

    public InstallServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        // AppPaths is process-global: force-reset before pinning (a prior class may have left it).
        typeof(AppPaths)
            .GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, null);
        AppPaths.Initialize(_dir);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        typeof(AppPaths)
            .GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeZip(string path, params (string Name, byte[] Content)[] entries)
    {
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach ((string name, byte[] content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content);
        }
        return path;
    }

    [Fact]
    public void ExtractSafe_ExtractsNormalEntries()
    {
        string zipPath = MakeZip(Path.Combine(_dir, "good.zip"),
            ("payload/XEngine.Editor.exe", [1, 2, 3]),
            ("payload/readme.txt", [65]));
        string target = Path.Combine(_dir, "extract-good");

        InstallService.ExtractSafe(zipPath, target);

        Assert.True(File.Exists(Path.Combine(target, "payload", "XEngine.Editor.exe")));
        Assert.True(File.Exists(Path.Combine(target, "payload", "readme.txt")));
    }

    [Fact]
    public void ExtractSafe_RejectsTraversalEntries()
    {
        string zipPath = MakeZip(Path.Combine(_dir, "evil.zip"),
            ("payload/ok.txt", [1]),
            ("../escaped.txt", [1]));
        string target = Path.Combine(_dir, "extract-evil");

        Assert.Throws<InvalidOperationException>(() => InstallService.ExtractSafe(zipPath, target));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public void Uninstall_RefusesNonHubManagedRecords()
    {
        var library = new InstallationLibrary(Path.Combine(_dir, "inst.json"));
        var service = new InstallService(library);
        var record = new InstallationRecord { Path = Path.Combine(_dir, "anywhere"), ManagedByHub = false };

        var (ok, errorKey) = service.Uninstall(record);
        Assert.False(ok);
        Assert.Equal("uninstall.error.notHubManaged", errorKey);
    }

    [Fact]
    public void Uninstall_RefusesPathsOutsideEditorsRoot()
    {
        var library = new InstallationLibrary(Path.Combine(_dir, "inst.json"));
        var service = new InstallService(library);
        string outside = Path.Combine(_dir, "not-hub-root", "1.0.0-a1-abc");
        Directory.CreateDirectory(outside);
        var record = new InstallationRecord { Path = outside, ManagedByHub = true };

        var (ok, errorKey) = service.Uninstall(record);
        Assert.False(ok);
        Assert.Equal("uninstall.error.outsideRoot", errorKey);
        Assert.True(Directory.Exists(outside)); // nothing was deleted
    }

    [Fact]
    public void Uninstall_RefusesInUseVersion_AndDeletesWhenFree()
    {
        var library = new InstallationLibrary(Path.Combine(_dir, "inst.json"));
        string inside = Path.Combine(XHub.Platform.AppPaths.EditorsRoot, "1.0.0-a1-abc");
        Directory.CreateDirectory(inside);
        var record = library.AddRecord(new InstallationRecord
        {
            Path = inside,
            ManagedByHub = true,
            VersionDisplay = "V1.0.0a1",
            CanonicalVersion = "1.0.0-a1",
        });

        bool inUse = true;
        var service = new InstallService(library, r => inUse);
        var (ok1, key1) = service.Uninstall(record);
        Assert.False(ok1);
        Assert.Equal("uninstall.error.inUse", key1);

        inUse = false;
        var (ok2, _) = service.Uninstall(record);
        Assert.True(ok2);
        Assert.False(Directory.Exists(inside));
        Assert.Empty(library.Installations);
    }
}
