using System;
using System.IO;
using XHub.Models;
using XHub.Services;
using Xunit;

namespace XHub.Test;

[Collection("Avalonia")]
public class InstallationLibraryTests : IDisposable
{
    private readonly string _dir;

    public InstallationLibraryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Fakes an install payload: an editor exe (version resource unreadable → version
    /// comes from the record), optionally inside the source-repo layout.</summary>
    private string MakeInstall(string name, string? version = null, bool repoLayout = false)
    {
        string root = Path.Combine(_dir, name);
        string exeDir = repoLayout ? Path.Combine(root, "Build", "Editor", "Release") : root;
        Directory.CreateDirectory(exeDir);
        File.WriteAllBytes(Path.Combine(exeDir, "XEngine.Editor.exe"), [1, 2, 3]);
        return root;
    }

    private InstallationLibrary Create() => new(Path.Combine(_dir, $"inst-{Guid.NewGuid():N}.json"));

    private InstallationRecord Record(string root, string display, string canonical, string channel = "preview", string source = "manual") =>
        new()
        {
            Path = root,
            VersionDisplay = display,
            CanonicalVersion = canonical,
            Channel = channel,
            Source = source,
        };

    [Fact]
    public void Add_FindsExeAndRegisters()
    {
        string root = MakeInstall("Flat");
        var library = Create();

        var record = library.Add(root);
        Assert.NotNull(record);
        Assert.False(record.IsBroken);
        Assert.Equal(root, record.Path, ignoreCase: true);
        Assert.Single(library.Installations);
        Assert.Equal(record.Id, library.DefaultInstallationId); // first install becomes default
    }

    [Fact]
    public void Add_FindsSourceRepoLayout()
    {
        string root = MakeInstall("Repo", repoLayout: true);
        var library = Create();

        var record = library.Add(root);
        Assert.NotNull(record);
        Assert.Contains(Path.Combine("Build", "Editor", "Release"), record.EditorExePath);
    }

    [Fact]
    public void Add_RejectsRootWithoutEditor()
    {
        string root = Path.Combine(_dir, "Empty");
        Directory.CreateDirectory(root);
        Assert.Null(Create().Add(root));
    }

    [Fact]
    public void Add_SamePath_Deduplicates()
    {
        string root = MakeInstall("Dup");
        var library = Create();
        var first = library.Add(root);
        var second = library.Add(root.ToUpperInvariant());

        Assert.Same(first, second);
        Assert.Single(library.Installations);
    }

    [Fact]
    public void SameVersion_CanExistAtTwoPaths()
    {
        // §3.3: 同一版本可有多个安装路径 — dedupe is by path, never by version.
        string a = MakeInstall("InstA");
        string b = MakeInstall("InstB");
        var library = Create();
        library.AddRecord(Record(a, "V1.0.0a1", "1.0.0-a1"));
        library.AddRecord(Record(b, "V1.0.0a1", "1.0.0-a1"));

        Assert.Equal(2, library.Installations.Count);
        Assert.All(library.Installations, r => Assert.Equal("1.0.0-a1", r.CanonicalVersion));
    }

    [Fact]
    public void ProbeAll_MarksBrokenWhenExeVanishes_AndRecoversWhenItReturns()
    {
        string root = MakeInstall("Vanishing");
        var library = Create();
        var record = library.Add(root)!;
        Assert.False(record.IsBroken);

        File.Delete(Path.Combine(root, "XEngine.Editor.exe"));
        library.ProbeAll();
        Assert.True(record.IsBroken);

        File.WriteAllBytes(Path.Combine(root, "XEngine.Editor.exe"), [1, 2, 3]);
        library.ProbeAll();
        Assert.False(record.IsBroken);
    }

    [Fact]
    public void Default_FallsBackWhenPinnedDefaultRemoved()
    {
        string a = MakeInstall("DefA");
        string b = MakeInstall("DefB");
        var library = Create();
        var first = library.Add(a)!;
        var second = library.Add(b)!;

        library.SetDefault(second.Id);
        Assert.Equal(second.Id, library.DefaultInstallationId);

        library.Remove(second.Id);
        Assert.Equal(first.Id, library.DefaultInstallationId);
    }

    [Fact]
    public void MatchForProject_PrefersPinnedInstallation()
    {
        string a = MakeInstall("PrefA");
        string b = MakeInstall("PrefB");
        var library = Create();
        var first = library.AddRecord(Record(a, "V1.0.0a1", "1.0.0-a1"));
        var second = library.AddRecord(Record(b, "1.0.0", "1.0.0", channel: "stable"));

        var match = library.MatchForProject(second.Id, "V1.0.0a1");
        Assert.Equal(InstallationMatchKind.Preferred, match.Kind);
        Assert.Same(second, match.Installation); // the pin wins over the version-exact candidate
    }

    [Fact]
    public void MatchForProject_ExactVersion_WhenNoPin()
    {
        string a = MakeInstall("ExactA");
        string b = MakeInstall("ExactB");
        var library = Create();
        library.AddRecord(Record(a, "V1.0.0a1", "1.0.0-a1"));
        library.AddRecord(Record(b, "1.0.0", "1.0.0", channel: "stable"));

        // Project stamped by the display form resolves to the canonical-equal install.
        var match = library.MatchForProject(null, "V1.0.0a1");
        Assert.Equal(InstallationMatchKind.VersionExact, match.Kind);
        Assert.Equal("1.0.0-a1", match.Installation!.CanonicalVersion);
    }

    [Fact]
    public void MatchForProject_MissingVersion_Hints()
    {
        string a = MakeInstall("MissA");
        var library = Create();
        library.AddRecord(Record(a, "1.0.0", "1.0.0", channel: "stable"));

        var match = library.MatchForProject(null, "V9.9.9a9");
        Assert.Equal(InstallationMatchKind.None, match.Kind);
        Assert.Equal("projects.match.missing", match.HintKey);
    }

    [Fact]
    public void MatchForProject_UnknownStamp_NeverSilentlyFallsBack()
    {
        string a = MakeInstall("UnkA");
        var library = Create();
        library.AddRecord(Record(a, "1.0.0", "1.0.0", channel: "stable"));

        var match = library.MatchForProject(null, null);
        Assert.Equal(InstallationMatchKind.None, match.Kind);
        Assert.Equal("projects.match.unknownVersion", match.HintKey);
    }

    [Fact]
    public void Rescan_RegistersEnvDiscoveredRoots()
    {
        string root = MakeInstall("Discovered");
        var library = Create();
        Environment.SetEnvironmentVariable("XENGINE_EDITOR_ROOT", root);
        try
        {
            Assert.Equal(1, library.Rescan());
            Assert.Single(library.Installations);
            Assert.Equal(0, library.Rescan()); // second scan is a no-op
        }
        finally
        {
            Environment.SetEnvironmentVariable("XENGINE_EDITOR_ROOT", null);
        }
    }
}
