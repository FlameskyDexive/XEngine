using System;
using System.IO;
using System.Linq;
using XHub.Services;
using XHub.ViewModels;
using Xunit;

namespace XHub.Test;

[Collection("Avalonia")]
public class ProjectsViewModelTests : IDisposable
{
    private readonly string _dir;

    public ProjectsViewModelTests()
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

    private (ProjectsViewModel Vm, ProjectLibrary Library) Create()
    {
        var library = new ProjectLibrary(
            Path.Combine(_dir, "projects.json"),
            Path.Combine(_dir, "no-legacy.json"));
        var vm = new ProjectsViewModel(library, new NullDialogService());
        return (vm, library);
    }

    [Fact]
    public async void ImportPathAsync_ValidProject_Registers()
    {
        string root = MakeProject("Valid");
        var (vm, library) = Create();

        await vm.ImportPathAsync(root);

        Assert.Single(library.Projects);
        Assert.Equal("Valid", library.Projects[0].Name);
        Assert.Single(vm.VisibleItems);
    }

    [Fact]
    public async void ImportPathAsync_Duplicate_IsNoOp()
    {
        string root = MakeProject("Dup");
        var (vm, library) = Create();

        await vm.ImportPathAsync(root);
        await vm.ImportPathAsync(root.ToUpperInvariant());

        Assert.Single(library.Projects);
    }

    [Fact]
    public async void ImportPathAsync_InvalidPath_ShowsErrorAndRegistersNothing()
    {
        var (vm, library) = Create();

        await vm.ImportPathAsync(Path.Combine(_dir, "ghost"));

        Assert.Empty(library.Projects);
    }

    [Fact]
    public void Search_MatchesNameAndPath_CaseInsensitive()
    {
        string alpha = MakeProject("Alpha");
        MakeProject("Beta");
        var (vm, _) = Create();
        vm.ImportPathAsync(alpha).GetAwaiter().GetResult();
        vm.ImportPathAsync(Path.Combine(_dir, "Beta")).GetAwaiter().GetResult();

        vm.SearchText = "alp";
        Assert.Single(vm.VisibleItems);
        Assert.Equal("Alpha", vm.VisibleItems[0].Name);

        vm.SearchText = "BETA";
        Assert.Single(vm.VisibleItems);
        Assert.Equal("Beta", vm.VisibleItems[0].Name);

        vm.SearchText = _dir; // both live under the same parent → both match by path
        Assert.Equal(2, vm.VisibleItems.Count);

        vm.SearchText = "没有的词";
        Assert.Equal(0, vm.VisibleItems.Count);
        Assert.True(vm.HasNoSearchResults);
        Assert.False(vm.HasNoProjectsAtAll);
    }

    [Fact]
    public void Sort_FavoritesFirst_ThenRecencyDescending()
    {
        string a = MakeProject("A"); // never opened
        string b = MakeProject("B"); // opened yesterday
        string c = MakeProject("C"); // opened today
        var (vm, library) = Create();
        vm.ImportPathAsync(a).GetAwaiter().GetResult();
        vm.ImportPathAsync(b).GetAwaiter().GetResult();
        vm.ImportPathAsync(c).GetAwaiter().GetResult();

        var recB = library.FindByPath(b)!;
        var recC = library.FindByPath(c)!;
        library.MarkOpened(recB.Id, new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc));
        library.MarkOpened(recC.Id, new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc));
        library.SetFavorite(recB.Id, true);

        Assert.Equal(["B", "C", "A"], vm.VisibleItems.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void Remove_DropsRowFromList()
    {
        string root = MakeProject("Removable");
        var (vm, library) = Create();
        vm.ImportPathAsync(root).GetAwaiter().GetResult();
        var row = vm.VisibleItems.Single();

        vm.RemoveCommand.Execute(row);

        Assert.Empty(library.Projects);
        Assert.Empty(vm.VisibleItems);
        Assert.True(vm.HasNoProjectsAtAll);
    }

    [Fact]
    public void ToggleFavorite_PersistsThroughLibrary()
    {
        string root = MakeProject("FavToggle");
        var (vm, library) = Create();
        vm.ImportPathAsync(root).GetAwaiter().GetResult();
        var row = vm.VisibleItems.Single();

        vm.ToggleFavoriteCommand.Execute(row);
        Assert.True(library.FindById(row.Id)!.Favorite);

        vm.ToggleFavoriteCommand.Execute(row);
        Assert.False(library.FindById(row.Id)!.Favorite);
    }

    [Fact]
    public void ApplyProbe_MissingPath_FlipsStatusAndFilter()
    {
        string root = MakeProject("WillVanish");
        var (vm, _) = Create();
        vm.ImportPathAsync(root).GetAwaiter().GetResult();
        var row = vm.VisibleItems.Single();
        Assert.True(row.IsAvailable); // optimistic before the probe

        row.ApplyProbe(directoryExists: false, hasAssets: false, engineVersion: null);

        Assert.False(row.IsAvailable);
        Assert.True(row.IsMissing);
        Assert.Equal(ProjectStatus.Missing, row.Status);

        vm.StatusFilterIndex = 2; // "missing" filter
        Assert.Single(vm.VisibleItems);

        vm.StatusFilterIndex = 1; // "usable" filter
        Assert.Empty(vm.VisibleItems);
    }

    [Fact]
    public void ApplyProbe_IncompleteMetadata_MarksRow()
    {
        string root = Path.Combine(_dir, "AssetsOnly");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        var (vm, library) = Create();
        vm.ImportPathAsync(root).GetAwaiter().GetResult();
        Assert.True(library.Projects[0].MetadataIncomplete);

        var row = vm.VisibleItems.Single();
        row.ApplyProbe(directoryExists: true, hasAssets: true, engineVersion: null);
        Assert.Equal(ProjectStatus.Incomplete, row.Status);
        Assert.True(row.IsIncomplete);
    }
}
