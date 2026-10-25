using System;
using CommunityToolkit.Mvvm.ComponentModel;
using XHub.Models;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

public enum ProjectStatus
{
    /// <summary>Not probed yet (optimistically rendered as available, like the editor cache).</summary>
    Unknown,
    Available,
    Missing,
    Incomplete,
}

public enum ProjectOpenState
{
    /// <summary>No active session — the status chip shows path availability.</summary>
    Idle,
    Launching,
    Ready,
    Failed,
    /// <summary>Legacy editor without hub protocol: launched, success unconfirmable (§4.4).</summary>
    LaunchedUnconfirmed,
}

/// <summary>
/// Row state for one project record: persistent fields plus live probe results (existence,
/// engine version stamp). Updated in place so the ListBox never rebuilds item containers
/// while scrolling (planning doc §2.2: virtualized, idle pages must not redraw).
/// </summary>
public partial class ProjectItemViewModel : ViewModelBase
{
    private readonly ProjectRecord _record;

    public ProjectItemViewModel(ProjectRecord record)
    {
        _record = record;
        Refresh(record);
    }

    public string Id => _record.Id;

    public string Name { get; private set; } = "";

    public string Path { get; private set; } = "";

    public bool Favorite { get; private set; }

    /// <summary>Stable ordering key for the favorites-first sort.</summary>
    public int FavoriteRank => Favorite ? 0 : 1;

    public DateTime? LastOpenedUtc => _record.LastOpenedUtc;

    public string LastOpenedText { get; private set; } = "";

    public string EngineVersionText { get; private set; } = "—";

    /// <summary>Raw ProjectVersion.txt stamp ("V1.0.0a1" legacy / "1.0.0-a1" canonical), or "".</summary>
    public string EngineVersionStamp { get; private set; } = "";

    /// <summary>PreferredInstallationId pinned by the user (N4 uses it when launching).</summary>
    public string? PreferredInstallationId => _record.PreferredInstallationId;

    /// <summary>Match hint against the installation registry ("未安装此版本" etc.), or "" when satisfied.</summary>
    public string MatchHintText { get; private set; } = "";

    /// <summary>True when the folder has Assets/ but no .xengine metadata (legacy layout).</summary>
    public bool IsIncomplete { get; private set; }

    public bool IsMissing { get; private set; }

    /// <summary>False only after a probe proved the path gone; unknown reads as usable.</summary>
    public bool IsAvailable { get; private set; } = true;

    public ProjectStatus Status { get; private set; } = ProjectStatus.Unknown;

    public string StatusText { get; private set; } = "";

    /// <summary>False until the first background probe result lands on this row.</summary>
    public bool Probed { get; private set; }

    private ProjectOpenState _openState = ProjectOpenState.Idle;

    public ProjectOpenState OpenState
    {
        get => _openState;
        private set
        {
            _openState = value;
            OnPropertyChanged(nameof(OpenState));
            UpdateLocalizedText();
        }
    }

    /// <summary>Launch failure detail (message / error code key), or "" while healthy.</summary>
    public string OpenFailureDetail { get; private set; } = "";

    /// <summary>True when the last open attempt failed (drives the retry affordance).</summary>
    public bool IsFailed => OpenState == ProjectOpenState.Failed;

    /// <summary>Applies a session-state transition from the open flow (UI thread).</summary>
    public void ApplyOpenState(ProjectOpenState state, string failureDetail = "")
    {
        OpenFailureDetail = failureDetail;
        OpenState = state;
    }

    private bool DirectoryExists { get; set; }

    /// <summary>Copies persistent record fields (library changed) without touching probe state.</summary>
    public void Refresh(ProjectRecord record)
    {
        Name = record.Name;
        Path = record.Path;
        Favorite = record.Favorite;
        IsIncomplete = Probed && DirectoryExists && record.MetadataIncomplete;
        UpdateLocalizedText();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Favorite));
        OnPropertyChanged(nameof(FavoriteRank));
        OnPropertyChanged(nameof(LastOpenedUtc));
    }

    /// <summary>Applies background probe results (existence, version stamp) on the UI thread.</summary>
    public void ApplyProbe(bool directoryExists, bool hasAssets, string? engineVersion)
    {
        Probed = true;
        DirectoryExists = directoryExists;
        EngineVersionStamp = engineVersion ?? "";
        IsIncomplete = directoryExists && _record.MetadataIncomplete;
        UpdateLocalizedText();
        UpdateVersionText();
    }

    /// <summary>
    /// Applies the installation-match result (§3.3 匹配提示): a satisfied match renders the
    /// version alone; a miss appends the localized hint after the version.
    /// </summary>
    public void ApplyMatch(InstallationMatchKind kind, string? hintKey)
    {
        _matchHintKey = kind == InstallationMatchKind.None ? hintKey ?? "" : "";
        UpdateVersionText();
    }

    private string _matchHintKey = "";

    /// <summary>Re-renders localized strings after a language switch.</summary>
    public void OnLanguageChanged()
    {
        UpdateLocalizedText();
        UpdateVersionText();
    }

    private void UpdateVersionText()
    {
        string version = EngineVersionStamp.Length > 0 ? EngineVersionStamp : "—";
        MatchHintText = _matchHintKey.Length > 0 ? Loc.Instance[_matchHintKey] : "";
        EngineVersionText = _matchHintKey.Length > 0
            ? $"{version} · {MatchHintText}"
            : version;
        OnPropertyChanged(nameof(EngineVersionText));
        OnPropertyChanged(nameof(MatchHintText));
    }

    private void UpdateLocalizedText()
    {
        StatusText = OpenState switch
        {
            ProjectOpenState.Launching => Loc.Instance["session.launching"],
            ProjectOpenState.Ready => Loc.Instance["session.ready"],
            ProjectOpenState.Failed => Loc.Instance["session.failed"],
            ProjectOpenState.LaunchedUnconfirmed => Loc.Instance["session.unconfirmed"],
            _ => Loc.Instance[!Probed ? "projects.status.available"
                : !DirectoryExists ? "projects.status.missing"
                : IsIncomplete ? "projects.status.incomplete"
                : "projects.status.available"],
        };

        LastOpenedText = _record.LastOpenedUtc is { } opened
            ? opened.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : Loc.Instance["projects.neverOpened"];

        Status = !Probed ? ProjectStatus.Unknown
            : !DirectoryExists ? ProjectStatus.Missing
            : IsIncomplete ? ProjectStatus.Incomplete
            : ProjectStatus.Available;

        // Session state dominates the chip: an open project reads green/amber/red per its
        // session, and the availability flag only shows when no session is active.
        switch (OpenState)
        {
            case ProjectOpenState.Launching:
            case ProjectOpenState.Ready:
                IsMissing = false;
                IsAvailable = true;
                IsIncomplete = false;
                break;
            case ProjectOpenState.Failed:
                IsMissing = true;
                IsAvailable = false;
                IsIncomplete = false;
                break;
            case ProjectOpenState.LaunchedUnconfirmed:
                IsMissing = false;
                IsAvailable = false;
                IsIncomplete = true;
                break;
            default:
                IsMissing = Status == ProjectStatus.Missing;
                IsAvailable = Status is ProjectStatus.Available or ProjectStatus.Unknown;
                IsIncomplete = Status == ProjectStatus.Incomplete;
                break;
        }

        OnPropertyChanged(nameof(LastOpenedText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsIncomplete));
        OnPropertyChanged(nameof(IsFailed));
    }
}
