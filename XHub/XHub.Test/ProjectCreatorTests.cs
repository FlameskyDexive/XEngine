using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XHub.Models;
using XHub.Services;
using Xunit;

namespace XHub.Test;

public class EditorCommandClientTests
{
    [Theory]
    [InlineData("""{"protocolVersion":1,"requestId":"r1","type":"progress","code":"","data":{"stage":"creating"}}""", HubCommandPhase.Progress, "r1")]
    [InlineData("""{"protocolVersion":1,"requestId":"r1","type":"success","code":"","data":{"projectRoot":"C:/p"}}""", HubCommandPhase.Success, "r1")]
    [InlineData("""{"protocolVersion":1,"requestId":"r1","type":"failure","code":"CREATE_PROJECT_FAILED","data":{}}""", HubCommandPhase.Failure, "CREATE_PROJECT_FAILED")]
    public void ParseEvent_ReadsCommandEvents(string line, HubCommandPhase phase, string tag)
    {
        var evt = EditorCommandClient.ParseEvent(line);
        Assert.NotNull(evt);
        Assert.Equal(phase, evt.Phase);
        Assert.Equal(tag, phase == HubCommandPhase.Failure ? evt.Code : evt.RequestId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"protocolVersion":1,"requestId":"r1","type":"unknownShape"}""")]
    public void ParseEvent_RejectsMalformedLines(string line)
    {
        Assert.Null(EditorCommandClient.ParseEvent(line));
    }
}

[Collection("Avalonia")]
public class ProjectCreatorTests : IDisposable
{
    private readonly string _dir;

    public ProjectCreatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private InstallationRecord FakeInstall() => new()
    {
        Path = _dir,
        EditorExePath = Path.Combine(_dir, "fake-editor.exe"),
        CanonicalVersion = "1.0.0-a1",
    };

    private ProjectCreator CreatorWith(bool simulate) => new(new InstallationLibrary(
        Path.Combine(_dir, $"inst-{Guid.NewGuid():N}.json")))
    {
        CreateViaEditorOverride = (_, stagingParent, projectName, _) =>
        {
            if (simulate)
                Directory.CreateDirectory(Path.Combine(stagingParent, projectName, "Assets"));
            return Task.FromResult((simulate, simulate ? "" : "CREATE_PROJECT_FAILED", "detail"));
        },
    };

    [Theory]
    [InlineData(null, "wizard.error.nameRequired")]
    [InlineData("", "wizard.error.nameRequired")]
    [InlineData("my/project", "wizard.error.nameInvalid")]
    [InlineData("a<b", "wizard.error.nameInvalid")]
    [InlineData(".hidden", "wizard.error.nameInvalid")]
    public void ValidateProjectName_RejectsBadNames(string? name, string expectedKey)
    {
        Assert.Equal(expectedKey, ProjectCreator.ValidateProjectName(name));
        Assert.Null(ProjectCreator.ValidateProjectName("MyGame1"));
    }

    [Fact]
    public void ValidateParent_RejectsMissingOrConflicting()
    {
        Assert.Equal("wizard.error.parentRequired", ProjectCreator.ValidateParent("", "A"));
        Assert.Equal("wizard.error.parentMissing", ProjectCreator.ValidateParent(Path.Combine(_dir, "ghost"), "A"));

        string occupied = Path.Combine(_dir, "occupied");
        Directory.CreateDirectory(Path.Combine(occupied, "A", "keep"));
        Assert.Equal("wizard.error.targetExists", ProjectCreator.ValidateParent(occupied, "A"));
        Assert.Null(ProjectCreator.ValidateParent(occupied, "B")); // empty dir A exists → fine
    }

    [Fact]
    public async Task CreateAsync_Success_CommitsStagingIntoFinalLocation()
    {
        var creator = CreatorWith(simulate: true);
        var (root, errorKey, _) = await creator.CreateAsync(FakeInstall(), "universal-3d", _dir, "Committed");

        Assert.Null(errorKey);
        Assert.NotNull(root);
        Assert.True(Directory.Exists(Path.Combine(_dir, "Committed")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "Committed", "Assets"))); // seeded payload moved
        Assert.True(Directory.Exists(root)); // the committed project lives at the final location
        Assert.StartsWith(_dir, root);        // …inside the parent, not the staging dir
        Assert.DoesNotContain(".xhub-new-", root);
    }

    [Fact]
    public async Task CreateAsync_Failure_CleansStagingAndReports()
    {
        var creator = CreatorWith(simulate: false);
        var (root, errorKey, _) = await creator.CreateAsync(FakeInstall(), "universal-3d", _dir, "Failed");

        Assert.Null(root);
        Assert.Equal("wizard.error.createFailed", errorKey);
        Assert.Empty(Directory.GetDirectories(_dir, ".xhub-new-*")); // staging removed
    }

    [Fact]
    public async Task CreateAsync_TargetRacedAfterCreate_IsRefusedNotOverwritten()
    {
        var creator = CreatorWith(simulate: true);
        // Simulate another writer creating the target between validation and commit.
        creator.CreateViaEditorOverride = (_, stagingParent, projectName, _) =>
        {
            Directory.CreateDirectory(Path.Combine(stagingParent, projectName));
            Directory.CreateDirectory(Path.Combine(_dir, "Raced")); // the conflict appears mid-flight
            return Task.FromResult((true, "", ""));
        };

        var (root, errorKey, _) = await creator.CreateAsync(FakeInstall(), "universal-3d", _dir, "Raced");

        Assert.Null(root);
        Assert.Equal("wizard.error.targetExists", errorKey);
    }
}
