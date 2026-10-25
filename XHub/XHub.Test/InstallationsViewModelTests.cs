using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XHub.Models;
using XHub.Services;
using XHub.ViewModels;
using Xunit;

namespace XHub.Test;

[Collection("Avalonia")]
public class InstallationsViewModelTests : IDisposable
{
    private readonly string _dir;

    public InstallationsViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private InstallationRecord Record(string name, string display, string canonical, string channel = "preview") =>
        new()
        {
            Path = Path.Combine(_dir, name),
            VersionDisplay = display,
            CanonicalVersion = canonical,
            Channel = channel,
        };

    [Fact]
    public void Rows_SortSemantically_NewestFirst_UnknownLast()
    {
        var library = new InstallationLibrary(Path.Combine(_dir, "inst.json"));
        library.AddRecord(Record("Old", "V1.0.0a1", "1.0.0-a1"));
        library.AddRecord(Record("Newer", "1.0.1", "1.0.1", "stable"));
        library.AddRecord(Record("Mystery", "", ""));
        library.AddRecord(Record("Ten", "1.0.10", "1.0.10", "stable"));

        var vm = new InstallationsViewModel(library, new NullDialogService());
        Assert.Equal(["1.0.10", "1.0.1", "V1.0.0a1", "Version unknown"],
            vm.VisibleItems.Select(i => i.VersionText).ToArray());
    }

    [Fact]
    public void SetDefault_UpdatesBadge()
    {
        var library = new InstallationLibrary(Path.Combine(_dir, "inst.json"));
        var a = library.AddRecord(Record("A", "V1.0.0a1", "1.0.0-a1"));
        var b = library.AddRecord(Record("B", "1.0.0", "1.0.0", "stable"));

        var vm = new InstallationsViewModel(library, new NullDialogService());
        var rowA = vm.VisibleItems.Single(i => i.Id == a.Id);
        var rowB = vm.VisibleItems.Single(i => i.Id == b.Id);
        Assert.True(rowA.IsDefault); // the first registered installation is the initial default
        Assert.False(rowB.IsDefault);

        vm.SetDefaultCommand.Execute(rowB);
        Assert.Equal(b.Id, library.DefaultInstallationId);
        Assert.True(vm.VisibleItems.Single(i => i.Id == b.Id).IsDefault);
        Assert.False(vm.VisibleItems.Single(i => i.Id == a.Id).IsDefault);
    }

    [Fact]
    public void Remove_DropsRow()
    {
        var library = new InstallationLibrary(Path.Combine(_dir, "inst.json"));
        var a = library.AddRecord(Record("Only", "V1.0.0a1", "1.0.0-a1"));
        var vm = new InstallationsViewModel(library, new NullDialogService());
        Assert.Single(vm.VisibleItems);

        vm.RemoveCommand.Execute(vm.VisibleItems.Single(i => i.Id == a.Id));

        Assert.Empty(library.Installations);
        Assert.Empty(vm.VisibleItems);
    }

    [Fact]
    public void ChannelChips_MapFromRecord()
    {
        var library = new InstallationLibrary(Path.Combine(_dir, "inst.json"));
        library.AddRecord(Record("Prev", "V1.0.0a1", "1.0.0-a1", channel: "preview"));
        library.AddRecord(Record("Stab", "1.0.0", "1.0.0", channel: "stable"));

        var vm = new InstallationsViewModel(library, new NullDialogService());
        Assert.True(vm.VisibleItems.Single(i => i.IsPreview).VersionText == "V1.0.0a1");
        Assert.True(vm.VisibleItems.Single(i => i.IsStable).VersionText == "1.0.0");
    }

    /// <summary>
    /// Regression for the release-pipeline wiring bug: the shell composes the
    /// <see cref="EditorInstaller"/> after this VM, and without an explicit attach the
    /// installable refresh silently no-ops (<c>_installer == null</c>) — the shipped
    /// Engine Versions page never listed any release. Attach must make the feed refresh live.
    /// </summary>
    [Fact]
    public async Task RefreshInstallable_AfterAttachInstaller_PopulatesFromFeed()
    {
        var library = new InstallationLibrary(Path.Combine(_dir, "inst.json"));
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "canonicalVersion": "1.0.0-a1",
          "displayVersion": "V1.0.0a1",
          "channel": "preview",
          "publishedAt": "2026-09-13T16:00:00Z",
          "rids": { "win-x64": { "url": "https://feed.test/x.zip", "size": 1, "sha256": "aa" } },
          "editorPath": "XEngine/XEngine.Editor.exe",
          "minHubProtocol": 1,
          "notesUrl": "https://feed.test/notes"
        }
        """;
        var feed = new ReleaseFeedService("http://feed.test/manifest.json",
            new HttpClient(new StubJsonHandler(manifestJson)));
        var installer = new EditorInstaller(library, feed: feed);

        var vm = new InstallationsViewModel(library, new NullDialogService());
        Assert.Empty(vm.Installable); // inert before the installer is attached

        vm.AttachInstaller(installer);
        await vm.RefreshInstallableAsync();

        var entry = Assert.Single(vm.Installable);
        Assert.Equal("V1.0.0a1", entry.Entry.DisplayVersion);
    }

    private sealed class StubJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
