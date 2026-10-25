using System;
using System.IO;
using XHub.Services;
using Xunit;

namespace XHub.Test;

public class ProjectLibraryTests : IDisposable
{
    private readonly string _dir;

    public ProjectLibraryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeProject(string name)
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        File.WriteAllText(Path.Combine(root, $"{name}.xengine"), $$"""{"name":"{{name}}"}""");
        return root;
    }

    private string WriteLegacyRecents(string json)
    {
        string file = Path.Combine(_dir, $"RecentProjects-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        return file;
    }

    /// <summary>Library with no legacy file — real %APPDATA% recents must never leak into tests.</summary>
    private ProjectLibrary CreateLibrary(string? storeName = null) => new(
        Path.Combine(_dir, storeName ?? $"projects-{Guid.NewGuid():N}.json"),
        Path.Combine(_dir, "no-legacy-file.json"));

    [Fact]
    public void Add_DeduplicatesByPath_KeepsFavorite()
    {
        string root = MakeProject("Dedupe");
        var library = CreateLibrary();

        var first = library.Add(root, "Dedupe", metadataIncomplete: false);
        library.SetFavorite(first.Id, true);
        var second = library.Add(root.ToUpperInvariant(), "Dedupe", metadataIncomplete: false);

        Assert.Same(first, second); // case-insensitive path identity on Windows
        Assert.Single(library.Projects);
        Assert.True(first.Favorite);
    }

    [Fact]
    public void Remove_DeletesOnlyTheRecord()
    {
        string root = MakeProject("Gone");
        var library = CreateLibrary();
        var record = library.Add(root, "Gone", metadataIncomplete: false);

        Assert.True(library.Remove(record.Id));
        Assert.Empty(library.Projects);
        Assert.True(Directory.Exists(root)); // the project directory is untouched
    }

    [Fact]
    public void RepairPath_MovesRecord_RejectsClash()
    {
        string oldRoot = MakeProject("OldRoot");
        string newRoot = MakeProject("NewRoot");
        var library = CreateLibrary();
        var record = library.Add(oldRoot, "OldRoot", metadataIncomplete: false);

        var repaired = library.RepairPath(record.Id, newRoot);
        Assert.NotNull(repaired);
        Assert.Equal(newRoot, repaired.Path, ignoreCase: true);

        // Repairing a second record onto the same path must be refused.
        string otherRoot = MakeProject("Other");
        var other = library.Add(otherRoot, "Other", metadataIncomplete: false);
        Assert.Null(library.RepairPath(other.Id, newRoot));
    }

    [Fact]
    public void MarkOpened_RecordsTimestamp()
    {
        string root = MakeProject("Opened");
        var library = CreateLibrary();
        var record = library.Add(root, "Opened", metadataIncomplete: false);
        Assert.Null(record.LastOpenedUtc);

        var stamp = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);
        library.MarkOpened(record.Id, stamp);

        Assert.Equal(stamp, library.FindById(record.Id)!.LastOpenedUtc);
    }

    [Fact]
    public void LegacyImport_FirstRun_CarriesOverFavoritesAndRecency()
    {
        string projA = MakeProject("LegacyA");
        string projB = MakeProject("LegacyB");
        string legacy = WriteLegacyRecents($$"""
            [
              {"Path":"{{projA.Replace("\\", "\\\\")}}","Name":"LegacyA","LastOpened":"2026-09-01T10:00:00Z","Favorite":true},
              {"Path":"{{projB.Replace("\\", "\\\\")}}","Name":"LegacyB","LastOpened":"2026-09-10T10:00:00Z","Favorite":false},
              {"Path":"{{Path.Combine(_dir, "missing").Replace("\\", "\\\\")}}","Name":"Ghost","LastOpened":"2026-09-10T10:00:00Z","Favorite":false}
            ]
            """);

        var library = new ProjectLibrary(Path.Combine(_dir, "projects.json"), legacy);

        Assert.Equal(2, library.Projects.Count); // the missing path was skipped
        var a = library.FindByPath(projA)!;
        var b = library.FindByPath(projB)!;
        Assert.True(a.Favorite);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), a.LastOpenedUtc);
        Assert.False(b.Favorite);
    }

    [Fact]
    public void LegacyImport_RunsOnlyOnce_RemovedStaysRemoved()
    {
        string projA = MakeProject("OnceA");
        string projB = MakeProject("OnceB");
        string legacy = WriteLegacyRecents($$"""
            [
              {"Path":"{{projA.Replace("\\", "\\\\")}}","Name":"OnceA","LastOpened":"2026-09-01T10:00:00Z"},
              {"Path":"{{projB.Replace("\\", "\\\\")}}","Name":"OnceB","LastOpened":"2026-09-01T10:00:00Z"}
            ]
            """);
        string store = Path.Combine(_dir, "projects.json");

        var first = new ProjectLibrary(store, legacy);
        Assert.Equal(2, first.Projects.Count);

        // User removes one project in XHub, then restarts.
        var record = first.FindByPath(projA)!;
        first.Remove(record.Id);
        Assert.Single(first.Projects);

        var second = new ProjectLibrary(store, legacy);
        Assert.Single(second.Projects); // the removal survives the restart
    }

    [Fact]
    public void Restart_RestoresAllRecords()
    {
        string projA = MakeProject("RestartA");
        string projB = MakeProject("RestartB");
        string store = Path.Combine(_dir, "projects.json");

        var first = CreateLibrary("projects.json");
        first.Add(projA, "RestartA", metadataIncomplete: false);
        var b = first.Add(projB, "RestartB", metadataIncomplete: false);
        first.SetFavorite(b.Id, true);
        first.MarkOpened(b.Id);

        var second = CreateLibrary("projects.json");
        Assert.Equal(2, second.Projects.Count);
        Assert.True(second.FindByPath(projB)!.Favorite);
        Assert.NotNull(second.FindByPath(projB)!.LastOpenedUtc);
    }

    [Fact]
    public void ImportLegacy_Failure_IsNotFatal()
    {
        string corrupt = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        var library = new ProjectLibrary(Path.Combine(_dir, "projects.json"), corrupt);
        Assert.Empty(library.Projects);
    }
}
