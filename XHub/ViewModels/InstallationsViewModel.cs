using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

/// <summary>
/// Engine-versions page (§3.3): the installed list with version / channel / architecture /
/// source / availability columns, default pinning, discovery rescan and manual locate.
/// The installable section and download flow arrive with node N6; module management with N7.
/// </summary>
public partial class InstallationsViewModel : ViewModelBase
{
    private readonly InstallationLibrary _library;
    private readonly IDialogService _dialogs;
    private EditorInstaller? _installer;
    private readonly List<InstallationItemViewModel> _allItems = [];

    public InstallationsViewModel(InstallationLibrary library, IDialogService dialogs,
        EditorInstaller? installer = null)
    {
        _library = library;
        _dialogs = dialogs;
        _installer = installer;
        _library.Changed += RefreshFromLibrary;

        Loc.Instance.PropertyChanged += (_, _) =>
        {
            foreach (InstallationItemViewModel item in _allItems)
                item.OnLanguageChanged();
        };

        RefreshFromLibrary();
    }

    /// <summary>The registry this page projects (shared with the projects-page matching).</summary>
    public InstallationLibrary Library => _library;

    /// <summary>
    /// Late wiring for the feed-driven installable section: the <see cref="EditorInstaller"/>
    /// is composed after this VM (it needs the Library and the Projects VM), so the shell
    /// attaches it once assembled. Refresh stays inert until then.
    /// </summary>
    internal void AttachInstaller(EditorInstaller installer) => _installer = installer;

    /// <summary>可安装 section (§3.3) — populated from the release feed when one is configured.</summary>
    public IReadOnlyList<ReleaseEntryViewModel> Installable { get; private set; } = [];

    public bool HasInstallable => Installable.Count > 0;

    /// <summary>Kicks the background probe of all registered installs + feed refresh (shell startup).</summary>
    public void StartProbe()
    {
        Task.Run(() => _library.ProbeAll());
        _ = RefreshInstallableAsync();
    }

    [RelayCommand]
    public async Task RefreshInstallableAsync()
    {
        if (_installer == null) return;
        IReadOnlyList<ReleaseEntry> entries = await _installer.GetInstallableAsync(forceRefresh: true);
        var rows = new List<ReleaseEntryViewModel>();
        foreach (ReleaseEntry entry in entries)
        {
            bool installed = false;
            foreach (InstallationRecord record in _library.Installations)
                if (record.CanonicalVersion == entry.CanonicalVersion && !record.IsBroken) installed = true;
            if (!installed) rows.Add(new ReleaseEntryViewModel(entry));
        }
        // Callers are on the UI thread (shell startup post / Refresh List command) and the
        // await resumes on the dispatcher — publish directly instead of re-posting a frame late.
        Installable = rows;
        OnPropertyChanged(nameof(Installable));
        OnPropertyChanged(nameof(HasInstallable));
    }

    [RelayCommand]
    public async Task InstallAsync(ReleaseEntryViewModel? item)
    {
        if (item == null || _installer == null) return;
        _installer.StartInstall(item.Entry);
        HubLog.Info($"Install queued: {item.Entry.DisplayVersion} ({item.Entry.CanonicalVersion})");
        await _dialogs.ShowErrorAsync("install.queued.title", "install.queued.hint", item.Entry.DisplayVersion);
    }

    [RelayCommand]
    public void Uninstall(InstallationItemViewModel? item)
    {
        if (item == null || _installer == null) return;
        (bool ok, string errorKey) = _installer.Uninstall(
            _library.FindById(item.Id) ?? throw new InvalidOperationException("record vanished"));
        if (!ok)
            _ = _dialogs.ShowErrorAsync("uninstall.error.title", errorKey, item.Path);
    }

    public IReadOnlyList<InstallationItemViewModel> VisibleItems { get; private set; } = [];

    public bool HasNoInstallations => _allItems.Count == 0;

    private void RefreshFromLibrary()
    {
        var byId = new Dictionary<string, InstallationItemViewModel>(_allItems.Count);
        foreach (InstallationItemViewModel item in _allItems)
            byId[item.Id] = item;

        _allItems.Clear();
        string defaultId = _library.DefaultInstallationId ?? "";
        foreach (InstallationRecord record in SortedRecords())
        {
            if (!byId.TryGetValue(record.Id, out InstallationItemViewModel? item))
                item = new InstallationItemViewModel(record);
            item.Refresh(record, isDefault: record.Id == defaultId);
            _allItems.Add(item);
        }

        RebuildVisible();
        OnPropertyChanged(nameof(HasNoInstallations));
    }

    /// <summary>Semantic version ordering, newest first; unknown versions last (§4.2).</summary>
    private IEnumerable<InstallationRecord> SortedRecords()
    {
        var list = new List<InstallationRecord>(_library.Installations);
        list.Sort((a, b) =>
        {
            bool aKnown = EngineVersion.TryParse(a.CanonicalVersion, out EngineVersion? av);
            bool bKnown = EngineVersion.TryParse(b.CanonicalVersion, out EngineVersion? bv);
            if (!aKnown && !bKnown) return string.CompareOrdinal(a.Path, b.Path);
            if (!aKnown) return 1;
            if (!bKnown) return -1;
            int byVersion = bv!.CompareTo(av); // newest first
            if (byVersion != 0) return byVersion;
            return string.CompareOrdinal(a.Path, b.Path);
        });
        return list;
    }

    private void RebuildVisible()
    {
        var visible = new List<InstallationItemViewModel>(_allItems.Count);
        foreach (InstallationItemViewModel item in _allItems)
            visible.Add(item);
        VisibleItems = visible;
        OnPropertyChanged(nameof(VisibleItems));
    }

    [RelayCommand]
    public async Task LocateAsync()
    {
        string? picked = await _dialogs.PickEditorInstallAsync();
        if (picked == null) return;

        // Accept either the install root or the editor executable itself.
        string root = picked;
        if (picked.EndsWith("XEngine.Editor.exe", StringComparison.OrdinalIgnoreCase) ||
            picked.EndsWith("XEngine.Editor", StringComparison.OrdinalIgnoreCase))
            root = System.IO.Path.GetDirectoryName(picked) ?? picked;

        InstallationRecord? added = _library.Add(root, source: "manual");
        if (added == null)
        {
            HubLog.Warning($"Manual locate rejected (no runnable editor): {picked}");
            await _dialogs.ShowErrorAsync("dialog.invalidEditor.title", "dialog.invalidEditor.hint", picked);
        }
    }

    [RelayCommand]
    public void Rescan() => Task.Run(() => _library.Rescan());

    [RelayCommand]
    public void SetDefault(InstallationItemViewModel? item)
    {
        if (item == null) return;
        _library.SetDefault(item.Id);
    }

    /// <summary>§3.4: opens the per-installation manage-modules dialog (toolchain CLI reuse).</summary>
    [RelayCommand]
    public async Task ManageModulesAsync(InstallationItemViewModel? item)
    {
        if (item == null) return;
        Models.InstallationRecord? record = _library.FindById(item.Id);
        if (record != null)
            await _dialogs.ShowModulesAsync(record);
    }

    [RelayCommand]
    public void Remove(InstallationItemViewModel? item)
    {
        if (item == null) return;
        // Registry removal only — hub-managed payloads are uninstalled (N6); manual/source
        // records never delete directories (§3.3).
        _library.Remove(item.Id);
        HubLog.Info($"Installation record removed: {item.Path}");
    }

    [RelayCommand]
    public void OpenFolder(InstallationItemViewModel? item)
    {
        if (item == null) return;
        try
        {
            Platform.Shell.OpenFolder(item.Path);
        }
        catch (Exception ex)
        {
            HubLog.Error("Failed to open installation folder.", ex);
        }
    }
}

/// <summary>Row for the 可安装 section: one release entry from the feed (§3.3).</summary>
public sealed class ReleaseEntryViewModel : ViewModelBase
{
    public ReleaseEntryViewModel(ReleaseEntry entry) => Entry = entry;

    public ReleaseEntry Entry { get; }

    public string VersionText => Entry.DisplayVersion.Length > 0 ? Entry.DisplayVersion : Entry.CanonicalVersion;

    public bool IsPreview => Entry.Channel == "preview";

    public bool IsStable => Entry.Channel == "stable";

    public string SizeText
    {
        get
        {
            long bytes = Entry.Size;
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (double)(1024 * 1024 * 1024):F2} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (double)(1024 * 1024):F1} MB";
            return $"{bytes / 1024.0:F0} KB";
        }
    }
}

/// <summary>Row state for one installation record; probe results update it in place.</summary>
public partial class InstallationItemViewModel : ViewModelBase
{
    private readonly InstallationRecord _record;

    public InstallationItemViewModel(InstallationRecord record)
    {
        _record = record;
        Refresh(record, isDefault: false);
    }

    public string Id => _record.Id;
    public string Path => _record.Path;
    public string EditorExePath => _record.EditorExePath;

    public string VersionText { get; private set; } = "";
    public string ChannelText { get; private set; } = "";
    public bool IsPreview { get; private set; }
    public bool IsStable { get; private set; }
    public string ArchitectureText { get; private set; } = "";
    public string SourceText { get; private set; } = "";
    public string StatusText { get; private set; } = "";
    public bool IsBroken { get; private set; }
    public bool IsDefault { get; private set; }

    public void Refresh(InstallationRecord record, bool isDefault)
    {
        IsDefault = isDefault;
        UpdateLocalizedText();
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(Path));
    }

    public void OnLanguageChanged() => UpdateLocalizedText();

    private void UpdateLocalizedText()
    {
        VersionText = _record.VersionDisplay.Length > 0 ? _record.VersionDisplay : Loc.Instance["installations.version.unknown"];
        IsPreview = _record.Channel == "preview";
        IsStable = _record.Channel == "stable";
        ChannelText = Loc.Instance[_record.Channel switch
        {
            "preview" => "installations.channel.preview",
            "stable" => "installations.channel.stable",
            _ => "installations.channel.unknown",
        }];
        ArchitectureText = _record.Architecture.Length > 0 ? _record.Architecture : "—";
        SourceText = Loc.Instance[_record.Source switch
        {
            "hub" => "installations.source.hub",
            "installer" => "installations.source.installer",
            "source" => "installations.source.source",
            _ => "installations.source.manual",
        }];
        StatusText = Loc.Instance[_record.IsBroken ? "installations.status.broken" : "installations.status.ok"];
        IsBroken = _record.IsBroken;

        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(ChannelText));
        OnPropertyChanged(nameof(IsPreview));
        OnPropertyChanged(nameof(IsStable));
        OnPropertyChanged(nameof(ArchitectureText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsBroken));
    }
}
