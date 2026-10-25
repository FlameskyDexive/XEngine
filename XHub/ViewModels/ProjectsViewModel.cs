using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;
using XHub.Resources;
using XHub.Services;
namespace XHub.ViewModels;

/// <summary>
/// Projects page (planning doc §3.1): import by folder or .xengine file, search, status filter,
/// favorites-first ordering, background path liveness probing, path repair and record removal.
/// The persistent store is <see cref="ProjectLibrary"/>; this class only projects it for the UI.
/// </summary>
public partial class ProjectsViewModel : ViewModelBase
{
    private readonly ProjectLibrary _library;
    private readonly IDialogService _dialogs;
    private readonly List<ProjectItemViewModel> _allItems = [];

    public ProjectsViewModel(ProjectLibrary library, IDialogService dialogs, InstallationLibrary? installations = null)
    {
        _library = library;
        _dialogs = dialogs;
        _installations = installations;
        _library.Changed += RefreshFromLibrary;
        if (_installations != null)
            _installations.Changed += RecomputeMatches;

        Loc.Instance.PropertyChanged += (_, _) =>
        {
            foreach (ProjectItemViewModel item in _allItems)
                item.OnLanguageChanged();
        };

        RefreshFromLibrary();
    }

    private readonly InstallationLibrary? _installations;

    /// <summary>Kicks the background liveness probe (called once the shell is up).</summary>
    public void StartLivenessProbe() => _ = ProbeAllAsync();

    public IReadOnlyList<ProjectItemViewModel> VisibleItems { get; private set; } = [];

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>0 = all, 1 = usable (unknown reads usable), 2 = missing.</summary>
    [ObservableProperty]
    private int _statusFilterIndex;

    public bool HasNoProjectsAtAll => _allItems.Count == 0;

    public bool HasNoSearchResults => _allItems.Count > 0 && VisibleItems.Count == 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnStatusFilterIndexChanged(int value) => ApplyFilter();

    private void RefreshFromLibrary()
    {
        // Reuse row VMs by id so item containers (and their scroll state) survive updates.
        var byId = new Dictionary<string, ProjectItemViewModel>(_allItems.Count);
        foreach (ProjectItemViewModel item in _allItems)
            byId[item.Id] = item;

        _allItems.Clear();
        foreach (Models.ProjectRecord record in _library.Projects)
        {
            if (!byId.TryGetValue(record.Id, out ProjectItemViewModel? item))
                item = new ProjectItemViewModel(record);
            else
                item.Refresh(record);
            _allItems.Add(item);
        }

        ApplyFilter();
        OnPropertyChanged(nameof(HasNoProjectsAtAll));
        _ = ProbeMissingAsync();
        RecomputeMatches();
    }

    /// <summary>
    /// §3.3 匹配提示: re-evaluates every row's installation match (preferred pin → exact
    /// version) and appends localized hints to unsatisfied rows. Runs on the UI thread.
    /// </summary>
    private void RecomputeMatches()
    {
        if (_installations == null) return;
        void Run()
        {
            foreach (ProjectItemViewModel item in _allItems)
            {
                InstallationMatch match = _installations.MatchForProject(item.PreferredInstallationId, item.EngineVersionStamp);
                item.ApplyMatch(match.Kind, match.HintKey);
            }
        }
        if (Dispatcher.UIThread.CheckAccess()) Run();
        else Dispatcher.UIThread.Post(Run);
    }

    /// <summary>Probes rows that never saw a probe result (new imports, restarts).</summary>
    private async Task ProbeMissingAsync()
    {
        List<ProjectItemViewModel> pending = new();
        foreach (ProjectItemViewModel item in _allItems)
            if (!item.Probed) pending.Add(item);
        await ProbeAsync(pending);
    }

    private async Task ProbeAllAsync()
    {
        List<ProjectItemViewModel> snapshot = new(_allItems);
        await ProbeAsync(snapshot);
    }

    /// <summary>
    /// Probes one path at a time on the thread pool (an unreachable network share must never be
    /// probed in parallel en masse). Results land on the UI thread in chunks of 32 with a single
    /// filter pass per chunk — one ApplyFilter per row would be O(n²) at 1000 records.
    /// </summary>
    private async Task ProbeAsync(List<ProjectItemViewModel> items)
    {
        const int chunkSize = 32;
        List<ProjectItemViewModel> chunk = new(chunkSize);
        List<(bool Exists, bool Assets, string? Version)> results = new(chunkSize);

        foreach (ProjectItemViewModel item in items)
        {
            (bool exists, bool assets, string? version) = await Task.Run(() => Inspect(item.Path));
            chunk.Add(item);
            results.Add((exists, assets, version));
            if (chunk.Count < chunkSize) continue;

            Flush(chunk, results);
            chunk = new List<ProjectItemViewModel>(chunkSize);
            results = new List<(bool, bool, string?)>(chunkSize);
        }
        if (chunk.Count > 0)
            Flush(chunk, results);

        void Flush(List<ProjectItemViewModel> itemsChunk, List<(bool Exists, bool Assets, string? Version)> probeResults)
        {
            Dispatcher.UIThread.Post(() =>
            {
                for (int i = 0; i < itemsChunk.Count; i++)
                    itemsChunk[i].ApplyProbe(probeResults[i].Exists, probeResults[i].Assets, probeResults[i].Version);
                ApplyFilter();
                RecomputeMatches();
            });
        }
    }

    private static (bool Exists, bool HasAssets, string? Version) Inspect(string path)
    {
        try
        {
            bool exists = Directory.Exists(path);
            bool assets = exists && Directory.Exists(System.IO.Path.Combine(path, "Assets"));
            return (exists, assets, assets ? ProjectPath.ReadProjectVersion(path) : null);
        }
        catch
        {
            return (false, false, null);
        }
    }

    /// <summary>Search + status filter + favorites-first / recency ordering.</summary>
    private void ApplyFilter()
    {
        string search = SearchText.Trim();
        List<ProjectItemViewModel> visible = new(_allItems.Count);
        foreach (ProjectItemViewModel item in _allItems)
        {
            if (StatusFilterIndex == 2 && !item.IsMissing) continue;
            if (StatusFilterIndex == 1 && item.IsMissing) continue;
            if (search.Length > 0 &&
                !item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !item.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            visible.Add(item);
        }

        visible.Sort(static (a, b) =>
        {
            int byFavorite = a.FavoriteRank.CompareTo(b.FavoriteRank);
            if (byFavorite != 0) return byFavorite;
            int byRecency = (b.LastOpenedUtc ?? DateTime.MinValue).CompareTo(a.LastOpenedUtc ?? DateTime.MinValue);
            if (byRecency != 0) return byRecency;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        VisibleItems = visible;
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoProjectsAtAll));
    }

    // ── Import ────────────────────────────────────────────────────

    [RelayCommand]
    public async Task AddProjectAsync()
    {
        string? folder = await _dialogs.PickProjectFolderAsync();
        if (folder != null)
            await ImportPathAsync(folder);
    }

    [RelayCommand]
    public async Task AddProjectFileAsync()
    {
        string? file = await _dialogs.PickXEngineFileAsync();
        if (file != null)
            await ImportPathAsync(file);
    }

    /// <summary>
    /// Imports any user-supplied location (folder, .xengine file, or <c>--open</c> argument):
    /// normalize → validate → resolve ambiguous identity → register.
    /// </summary>
    public async Task ImportPathAsync(string rawPath)
    {
        ProjectLocation location = ProjectPath.Normalize(rawPath);
        if (!location.DirectoryExists || !location.HasAssets)
        {
            HubLog.Warning($"Rejected non-project path: {rawPath}");
            await _dialogs.ShowErrorAsync("dialog.invalid.title", "dialog.invalid.hint", rawPath);
            return;
        }

        string? name = location.Name;
        if (location.XEngineFiles.Count > 1)
        {
            string? chosen = await _dialogs.ShowXEngineChoiceAsync(location.XEngineFiles);
            if (chosen == null) return; // user canceled
            name = ProjectPath.ReadName(chosen) ?? name;
        }

        ProjectRecord record = _library.Add(location.RootPath, name ?? "",
            metadataIncomplete: location.XEngineFiles.Count == 0);
        HubLog.Info($"Project registered: {record.Name} ({record.Path})");
    }

    // ── Row actions ───────────────────────────────────────────────

    [RelayCommand]
    public async Task RepairPathAsync(ProjectItemViewModel? item)
    {
        if (item == null) return;
        string? folder = await _dialogs.PickProjectFolderAsync();
        if (folder == null) return;

        ProjectLocation location = ProjectPath.Normalize(folder);
        if (!location.DirectoryExists || !location.HasAssets)
        {
            await _dialogs.ShowErrorAsync("dialog.invalid.title", "dialog.invalid.hint", folder);
            return;
        }

        Models.ProjectRecord? repaired = _library.RepairPath(item.Id, location.RootPath);
        if (repaired == null)
        {
            await _dialogs.ShowErrorAsync("dialog.repairClash.title", "dialog.repairClash.hint", location.RootPath);
            return;
        }
        HubLog.Info($"Repaired project {repaired.Name} → {repaired.Path}");
    }

    [RelayCommand]
    public void Remove(ProjectItemViewModel? item)
    {
        if (item == null) return;
        // Only the XHub record is deleted — the project directory stays untouched (§3.1).
        _library.Remove(item.Id);
        HubLog.Info($"Project record removed: {item.Path}");
    }

    [RelayCommand]
    public void ToggleFavorite(ProjectItemViewModel? item)
    {
        if (item == null) return;
        _library.SetFavorite(item.Id, !item.Favorite);
    }

    // ── Open flow (§3.3 / §4.4) ───────────────────────────────────

    /// <summary>Running editor sessions keyed by project path — one editor per project.</summary>
    private readonly Dictionary<string, HubSessionWatcher> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    public async Task OpenAsync(ProjectItemViewModel? item)
    {
        if (item == null) return;

        // The startup probe may not have landed yet (--open opens before liveness probing);
        // the version stamp is a cheap local read, so fetch it on demand.
        if (item.EngineVersionStamp.Length == 0 && Directory.Exists(item.Path))
        {
            bool hasAssets = Directory.Exists(System.IO.Path.Combine(item.Path, "Assets"));
            item.ApplyProbe(true, hasAssets, ProjectPath.ReadProjectVersion(item.Path));
        }

        // §4.4: a project that already has a running editor is activated, never opened twice.
        if (_sessions.TryGetValue(item.Path, out HubSessionWatcher? existing))
        {
            if (existing.State == SessionState.Ready &&
                Platform.WindowActivation.ActivateMainWindow(existing.Session.ProcessId))
            {
                HubLog.Info($"Activated running editor for {item.Path}");
                return;
            }
            if (existing.State == SessionState.Launching)
                return; // already opening — do not double-launch
            _sessions.Remove(item.Path);
        }

        InstallationMatch match = _installations?.MatchForProject(item.PreferredInstallationId, item.EngineVersionStamp)
            ?? InstallationMatch.None("projects.match.noInstallations");
        if (match.Kind == InstallationMatchKind.None)
        {
            string hint = match.HintKey ?? "projects.match.missing";
            HubLog.Warning($"Open refused ({hint}): {item.Path}");
            await _dialogs.ShowErrorAsync("launch.error.noMatch.title", hint, item.Path);
            return;
        }

        InstallationRecord install = match.Installation!;
        string? error = null;
        EditorSession? session = EditorLauncher.Launch(install, item.Path, out error);
        if (session == null)
        {
            await _dialogs.ShowErrorAsync("launch.error.startFailed.title", error ?? "launch.error.startFailed", item.Path);
            return;
        }

        item.ApplyOpenState(ProjectOpenState.Launching);
        HubSessionWatcher? watcher = null;
        watcher = new HubSessionWatcher(session, (state, evt, code) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (state)
                {
                    case SessionState.Ready:
                        item.ApplyOpenState(ProjectOpenState.Ready);
                        // §4.4: only the editor's own projectReady updates the recency stamp.
                        _library.MarkOpened(item.Id);
                        break;
                    case SessionState.LockConflict:
                        item.ApplyOpenState(ProjectOpenState.Failed, evt?.Code ?? "PROJECT_LOCKED");
                        break;
                    case SessionState.Failed:
                        item.ApplyOpenState(ProjectOpenState.Failed, evt?.Code ?? "");
                        break;
                    case SessionState.LaunchedUnconfirmed:
                        item.ApplyOpenState(ProjectOpenState.LaunchedUnconfirmed);
                        break;
                    case SessionState.Closed:
                        item.ApplyOpenState(ProjectOpenState.Idle);
                        _sessions.Remove(item.Path);
                        watcher!.Dispose();
                        break;
                }
            });
        });
        _sessions[item.Path] = watcher;
    }

    /// <summary>True while a launched editor session runs against this installation (§5 正在使用禁止卸载).</summary>
    public bool IsInstallationInUse(InstallationRecord record)
    {
        foreach (HubSessionWatcher watcher in _sessions.Values)
            if (watcher.Session.Installation.Id == record.Id) return true;
        return false;
    }

    [RelayCommand]
    public void OpenContainingFolder(ProjectItemViewModel? item)
    {
        if (item == null) return;
        try
        {
            Platform.Shell.OpenFolder(item.Path);
        }
        catch (Exception ex)
        {
            HubLog.Error("Failed to open project folder.", ex);
        }
    }
}

/// <summary>Templates page state. Template query and the new-project wizard arrive with node N5.</summary>
public partial class TemplatesViewModel : ViewModelBase
{
}

/// <summary>Download tasks page state. The download queue arrives with node N6.</summary>
public partial class DownloadsViewModel : ViewModelBase
{
}
