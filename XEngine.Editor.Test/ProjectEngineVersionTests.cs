using System.IO;

using XEngine.Editor.Projects;

using Xunit;

namespace XEngine.Editor.Test;

public class ProjectEngineVersionTests : IDisposable
{
    private readonly string _dir;

    public ProjectEngineVersionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xengine-projver", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void MissingFile_ReadsNull_AndDoesNotMatch()
    {
        Assert.Null(ProjectEngineVersion.Read(_dir));
        Assert.False(ProjectEngineVersion.Matches(_dir, "1.0.0"));
    }

    [Fact]
    public void WriteThenRead_RoundTrips_AndMatches()
    {
        ProjectEngineVersion.Write(_dir, "1.0-preview-2");
        Assert.Equal("1.0-preview-2", ProjectEngineVersion.Read(_dir));
        Assert.True(ProjectEngineVersion.Matches(_dir, "1.0-preview-2"));
        Assert.False(ProjectEngineVersion.Matches(_dir, "2.0.0"));
    }

    [Fact]
    public void Read_ParsesUnityStyleMarker()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_dir, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 6000.7.0a4\n");
        Assert.Equal("6000.7.0a4", ProjectEngineVersion.Read(_dir));
    }

    [Fact]
    public void Canonicalize_UnifiesDisplayAndSemanticForms()
    {
        // Binding decision (XHub plan §4.2): the record format is "1.0.0-a1".
        Assert.Equal("1.0.0-a1", ProjectEngineVersion.Canonicalize("V1.0.0a1"));
        Assert.Equal("1.0.0-a1", ProjectEngineVersion.Canonicalize("1.0.0a1"));
        Assert.Equal("1.0.0-a1", ProjectEngineVersion.Canonicalize("1.0.0-a1"));
        Assert.Equal("1.0.0-a1", ProjectEngineVersion.Canonicalize("V1.0.0a1+build.42"));
        Assert.Equal("1.0.0", ProjectEngineVersion.Canonicalize("V1.0.0"));
        Assert.Equal("1.0.0", ProjectEngineVersion.Canonicalize("1.0.0"));
    }

    [Fact]
    public void Write_StoresCanonicalForm()
    {
        ProjectEngineVersion.Write(_dir, "V1.0.0a1");
        Assert.Equal("1.0.0-a1", ProjectEngineVersion.Read(_dir));
    }

    [Fact]
    public void Matches_LegacyDisplayStamp_StillMatchesCanonicalRunningVersion()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_dir, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: V1.0.0a1\n");
        Assert.True(ProjectEngineVersion.Matches(_dir, "1.0.0-a1"));
        Assert.True(ProjectEngineVersion.Matches(_dir, "V1.0.0a1"));
        Assert.False(ProjectEngineVersion.Matches(_dir, "1.0.0-a2"));
    }
}
