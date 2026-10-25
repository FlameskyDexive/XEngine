using System;
using System.IO;
using XHub.Services;
using Xunit;

namespace XHub.Test;

public class ProjectPathTests : IDisposable
{
    private readonly string _dir;

    public ProjectPathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeProject(string name, string? xengineJson = null, string? version = null)
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        if (xengineJson != null)
            File.WriteAllText(Path.Combine(root, $"{name}.xengine"), xengineJson);
        if (version != null)
        {
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"),
                $"m_EditorVersion: {version}{Environment.NewLine}");
        }
        return root;
    }

    [Fact]
    public void Normalize_Folder_StripsQuotesAndTrailingSeparators()
    {
        var project = MakeProject("Quotes");
        string raw = $"\"{project}{Path.DirectorySeparatorChar}\"";

        var location = ProjectPath.Normalize(raw);

        Assert.True(location.DirectoryExists);
        Assert.True(location.HasAssets);
        Assert.Equal(project.TrimEnd(Path.DirectorySeparatorChar), location.RootPath, ignoreCase: true);
    }

    [Fact]
    public void Normalize_XEngineFile_ResolvesToParentDirectoryAndReadsName()
    {
        var project = MakeProject("Named", xengineJson: """{"name": "我的游戏"}""");
        string file = Path.Combine(project, "Named.xengine");

        var location = ProjectPath.Normalize(file);

        Assert.Equal(project, location.RootPath, ignoreCase: true);
        Assert.Equal("我的游戏", location.Name);
        Assert.Single(location.XEngineFiles);
    }

    [Fact]
    public void Normalize_MultipleXEngineFiles_ReportsAmbiguity()
    {
        string root = MakeProject("Ambiguous", xengineJson: """{"name":"a"}""");
        File.WriteAllText(Path.Combine(root, "second.xengine"), """{"name":"b"}""");

        var location = ProjectPath.Normalize(root);

        Assert.Equal(2, location.XEngineFiles.Count);
    }

    [Fact]
    public void Normalize_AssetsOnlyFolder_IsLegacyLayout()
    {
        var project = MakeProject("Legacy");

        var location = ProjectPath.Normalize(project);

        Assert.True(location.DirectoryExists);
        Assert.True(location.HasAssets);
        Assert.Empty(location.XEngineFiles);
    }

    [Fact]
    public void Normalize_NonexistentPath_ReportsMissing()
    {
        var location = ProjectPath.Normalize(Path.Combine(_dir, "does-not-exist"));
        Assert.False(location.DirectoryExists);
        Assert.False(location.HasAssets);
    }

    [Fact]
    public void Normalize_EmptyInput_ReturnsEmptyLocation()
    {
        Assert.False(ProjectPath.Normalize("").DirectoryExists);
        Assert.False(ProjectPath.Normalize(null).DirectoryExists);
        Assert.False(ProjectPath.Normalize("   ").DirectoryExists);
    }

    [Fact]
    public void Normalize_EnvironmentVariables_Expanded()
    {
        string dirName = Guid.NewGuid().ToString("N").Substring(0, 8);
        string dir = Path.Combine(_dir, dirName);
        Directory.CreateDirectory(Path.Combine(dir, "Assets"));
        Environment.SetEnvironmentVariable("XHUB_TEST_PROJ_ROOT", _dir);

        var location = ProjectPath.Normalize("%XHUB_TEST_PROJ_ROOT%\\" + dirName);

        Assert.True(location.DirectoryExists);
    }

    [Fact]
    public void SamePath_IsCaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return; // ordinal comparison on POSIX is correct there
        Assert.True(ProjectPath.SamePath(@"C:\A\B", @"c:\a\b"));
        Assert.False(ProjectPath.SamePath(@"C:\A\B", @"C:\A\C"));
    }

    [Fact]
    public void ReadProjectVersion_ParsesStamp()
    {
        var project = MakeProject("Stamped", version: "V1.0.0a1");
        Assert.Equal("V1.0.0a1", ProjectPath.ReadProjectVersion(project));
        Assert.Null(ProjectPath.ReadProjectVersion(_dir));
    }
}
