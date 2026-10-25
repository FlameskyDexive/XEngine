using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

/// <summary>
/// Shell state: which page is selected, and the per-page view models. The left navigation is
/// split into a main group (Projects / Installations / Templates) and a bottom group
/// (Downloads / Settings); <see cref="SelectedPage"/> is the single source of truth both
/// ListBox bindings round-trip through.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;

    public MainWindowViewModel(SettingsService settings, ProjectLibrary? projectLibrary = null,
        IDialogService? dialogs = null, InstallationLibrary? installationLibrary = null,
        string? feedOverride = null)
    {
        _settings = settings;
        _dialogs = dialogs ?? new NullDialogService();
        IDialogService dialogsResolved = _dialogs;
        Installations = new InstallationsViewModel(
            installationLibrary ?? new InstallationLibrary(), dialogsResolved);
        ProjectLibrary library = projectLibrary ?? new ProjectLibrary();
        Creator = new ProjectCreator(Installations.Library);
        Projects = new ProjectsViewModel(library, dialogsResolved, Installations.Library);
        Installer = new EditorInstaller(Installations.Library,
            isInUse: record => Projects.IsInstallationInUse(record),
            feed: feedOverride != null ? new ReleaseFeedService(feedOverride) : null);
        // The installer composes after this page (it needs the Library and Projects); attach it
        // so the installable feed refresh (StartProbe / Refresh List) actually runs.
        Installations.AttachInstaller(Installer);
        Templates = new TemplatesViewModel(dialogsResolved, Creator, library, Installations.Library,
            onProjectCreated: OpenCreatedProjectAsync);
        Downloads = new DownloadsViewModel(Installer, dialogsResolved);
        Settings = new SettingsViewModel(settings, dialogsResolved);
    }

    private EditorInstaller Installer { get; }

    private ProjectCreator Creator { get; }

    /// <summary>§3.2 登记并打开: the wizard registered the project — jump to Projects and launch.</summary>
    private async Task OpenCreatedProjectAsync(ProjectRecord record)
    {
        SelectedPage = HubPageId.Projects;
        foreach (ProjectItemViewModel item in Projects.VisibleItems)
        {
            if (ProjectPath.SamePath(item.Path, record.Path))
            {
                await Projects.OpenAsync(item);
                return;
            }
        }
    }

    /// <summary>Projects-page "新建项目" shortcut with the Universal 3D template preselected.</summary>
    [RelayCommand]
    public async Task NewProjectAsync()
    {
        SelectedPage = HubPageId.Templates;
        await Templates.StartWizardAsync(TemplateCatalog.Universal3D);
    }

    public Loc Loc => Loc.Instance;

    /// <summary>Main nav group: Projects / Installations / Templates (indices match <see cref="HubPageId"/>).</summary>
    public IReadOnlyList<NavItemViewModel> MainNavItems { get; } =
    [
        new NavItemViewModel(HubPageId.Projects, "nav.projects",
            "M3,5 C3,3.9 3.9,3 5,3 L9.6,3 C10.1,3 10.6,3.2 11,3.6 L12.4,5 L19,5 C20.1,5 21,5.9 21,7 L21,17 C21,18.1 20.1,19 19,19 L5,19 C3.9,19 3,18.1 3,17 Z"),
        new NavItemViewModel(HubPageId.Installations, "nav.installations",
            "M12,2 L21,7 L21,17 L12,22 L3,17 L3,7 Z"),
        new NavItemViewModel(HubPageId.Templates, "nav.templates",
            "M4,4 L10,4 L10,10 L4,10 Z M13,4 L20,4 L20,10 L13,10 Z M4,13 L10,13 L10,20 L4,20 Z M13,13 L20,13 L20,20 L13,20 Z"),
    ];

    /// <summary>Bottom nav group: Downloads / Settings (offset from <see cref="HubPageId.Downloads"/>).</summary>
    public IReadOnlyList<NavItemViewModel> BottomNavItems { get; } =
    [
        new NavItemViewModel(HubPageId.Downloads, "nav.downloads",
            "M11,3 L13,3 L13,11 L16,8 L17.5,9.5 L12,15 L6.5,9.5 L8,8 L11,11 Z M4,17 L20,17 L20,19 L4,19 Z"),
        new NavItemViewModel(HubPageId.Settings, "nav.settings",
            "M4,6 L20,6 L20,7.6 L4,7.6 Z M7,4.8 L9.5,4.8 L9.5,8.8 L7,8.8 Z M4,11.2 L20,11.2 L20,12.8 L4,12.8 Z M14,10 L16.5,10 L16.5,14 L14,14 Z M4,16.4 L20,16.4 L20,18 L4,18 Z M9,15.2 L11.5,15.2 L11.5,19.2 L9,19.2 Z"),
    ];

    public ProjectsViewModel Projects { get; }
    public InstallationsViewModel Installations { get; }
    public TemplatesViewModel Templates { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private HubPageId _selectedPage = HubPageId.Projects;

    /// <summary>Selected index inside the main nav ListBox (-1 = none of them).</summary>
    [ObservableProperty]
    private int _mainNavIndex;

    /// <summary>Selected index inside the bottom nav ListBox (-1 = none of them).</summary>
    [ObservableProperty]
    private int _bottomNavIndex = -1;

    partial void OnSelectedPageChanged(HubPageId value)
    {
        // Keep both nav groups consistent without re-entering OnSelectedPageChanged: writing the
        // index properties only updates the ListBox visuals.
        _mainNavIndex = value is HubPageId.Projects or HubPageId.Installations or HubPageId.Templates
            ? (int)value : -1;
        _bottomNavIndex = value is HubPageId.Downloads or HubPageId.Settings
            ? (int)value - (int)HubPageId.Downloads : -1;
        OnPropertyChanged(nameof(MainNavIndex));
        OnPropertyChanged(nameof(BottomNavIndex));
    }

    partial void OnMainNavIndexChanged(int value)
    {
        if (value >= 0 && (HubPageId)value != SelectedPage)
            SelectedPage = (HubPageId)value;
    }

    partial void OnBottomNavIndexChanged(int value)
    {
        if (value >= 0)
        {
            var page = (HubPageId)((int)HubPageId.Downloads + value);
            if (page != SelectedPage)
                SelectedPage = page;
        }
    }

    public ViewModelBase CurrentPage => SelectedPage switch
    {
        HubPageId.Installations => Installations,
        HubPageId.Templates => Templates,
        HubPageId.Downloads => Downloads,
        HubPageId.Settings => Settings,
        _ => Projects,
    };

    /// <summary>
    /// Routes a CLI or IPC request (deep links, forwarded file association opens) onto the
    /// current UI state. Unhandled-but-recognized pieces are logged so behavior is traceable.
    /// </summary>
    public void HandleRequest(CliOptions request)
    {
        if (request.Page.HasValue)
            SelectedPage = request.Page.Value;
        if (request.InstallationId != null)
            HubLog.Info($"--installation deep link: {request.InstallationId} (installations page)");
        if (request.SearchSeed != null)
            Projects.SearchText = request.SearchSeed;
            if (request.OpenPath != null)
            {
                SelectedPage = HubPageId.Projects;
                _ = OpenAfterImportAsync(request.OpenPath);
            }
            if (request.InstallVersion != null)
            {
                SelectedPage = HubPageId.Installations;
                _ = InstallVersionAsync(request.InstallVersion);
            }
            if (request.Modules)
            {
                SelectedPage = HubPageId.Installations;
                Models.InstallationRecord? target = Installer.Installations.Default;
                if (target != null)
                    _ = _dialogs.ShowModulesAsync(target);
            }
        }

        /// <summary>--install deep link (controlled-source acceptance): queue download+install.</summary>
        private async System.Threading.Tasks.Task InstallVersionAsync(string canonicalVersion)
        {
            IReadOnlyList<ReleaseEntry> entries = await Installer.GetInstallableAsync(forceRefresh: true);
            foreach (ReleaseEntry entry in entries)
            {
                if (string.Equals(entry.CanonicalVersion, canonicalVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Installer.StartInstall(entry);
                    HubLog.Info($"--install queued: {canonicalVersion}");
                    return;
                }
            }
            HubLog.Warning($"--install: version {canonicalVersion} not found in feed.");
        }

    /// <summary>File-association open: import (dedupe makes re-imports a no-op) then launch.</summary>
    private async System.Threading.Tasks.Task OpenAfterImportAsync(string rawPath)
    {
        await Projects.ImportPathAsync(rawPath);
        string normalized = Services.ProjectPath.Normalize(rawPath).RootPath;
        ProjectItemViewModel? row = null;
        foreach (ProjectItemViewModel candidate in Projects.VisibleItems)
        {
            if (Services.ProjectPath.SamePath(candidate.Path, normalized))
            {
                row = candidate;
                break;
            }
        }
        if (row != null)
            await Projects.OpenAsync(row);
    }
}

/// <summary>
/// Headless stand-in used by tests and screenshot runs: every interactive dialog resolves to
/// "canceled" and error surfaces are logged instead of shown.
/// </summary>
public sealed class NullDialogService : IDialogService
{
    public Task<string?> PickProjectFolderAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickXEngineFileAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickEditorInstallAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickAnyFolderAsync() => Task.FromResult<string?>(null);
    public Task<string?> ShowXEngineChoiceAsync(IReadOnlyList<string> xengineFiles) => Task.FromResult<string?>(null);

    public Task<Models.ProjectRecord?> ShowNewProjectWizardAsync(
        ProjectCreator creator, ProjectLibrary library, InstallationLibrary installations,
        TemplateCatalog.Template template) => Task.FromResult<Models.ProjectRecord?>(null);

    public Task ShowModulesAsync(InstallationRecord installation) => Task.CompletedTask;

    public Task ShowErrorAsync(string titleKey, string hintKey, string detail)
    {
        HubLog.Warning($"Dialog suppressed (headless): {titleKey}/{hintKey}: {detail}");
        return Task.CompletedTask;
    }
}
