using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

/// <summary>
/// Settings page. Language applies live; install locations (editors root, download cache) are
/// persisted and take effect on the next launch — running services already hold their roots.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<LanguageOption> _languages;

    public SettingsViewModel(SettingsService settings, IDialogService? dialogs = null)
    {
        _settings = settings;
        _dialogs = dialogs ?? new NullDialogService();
        _languages =
        [
            new LanguageOption(HubLanguage.System, Loc.Instance["common.system"]),
            new LanguageOption(HubLanguage.SimplifiedChinese, "简体中文"),
            new LanguageOption(HubLanguage.English, "English"),
        ];
        _selectedLanguageIndex = IndexOf(settings.Current.Language);
        _editorsRoot = settings.Current.EditorsRootOverride;
        _downloadsRoot = settings.Current.DownloadsRootOverride;
    }

    public IReadOnlyList<LanguageOption> Languages => _languages;

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private string _editorsRoot;

    [ObservableProperty]
    private string _downloadsRoot;

    /// <summary>A location change was saved and waits for the next launch.</summary>
    [ObservableProperty]
    private bool _restartPending;

    /// <summary>Locations currently in effect for this run.</summary>
    public string EffectiveEditorsRoot => AppPaths.EditorsRoot;
    public string EffectiveDownloadsRoot => AppPaths.DownloadCache;

    public string HubVersion => VersionInfo.DisplayVersion;

    public string LogsDirectory => AppPaths.LogsDir;

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (value < 0 || value >= _languages.Count) return;
        HubLanguage language = _languages[value].Language;
        if (language == _settings.Current.Language) return;

        _settings.Current.Language = language;
        Loc.Instance.Apply(language);
        _settings.Save();
        HubLog.Info($"Language switched to {language}.");
        OnPropertyChanged(nameof(LogsDirectory));
    }

    private int IndexOf(HubLanguage language)
    {
        for (int i = 0; i < _languages.Count; i++)
            if (_languages[i].Language == language) return i;
        return 0;
    }

    [RelayCommand]
    private async Task BrowseEditorsRootAsync()
    {
        string? folder = await _dialogs.PickAnyFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
            EditorsRoot = folder;
    }

    [RelayCommand]
    private async Task BrowseDownloadsRootAsync()
    {
        string? folder = await _dialogs.PickAnyFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
            DownloadsRoot = folder;
    }

    [RelayCommand]
    private void ResetEditorsRoot() => EditorsRoot = "";

    [RelayCommand]
    private void ResetDownloadsRoot() => DownloadsRoot = "";

    /// <summary>Persists the location overrides. The roots in effect for this run stay as-is
    /// (running services hold them); <see cref="AppPaths.ConfigureLocationOverrides"/> applies
    /// the saved values on the next launch.</summary>
    [RelayCommand]
    private void SaveLocations()
    {
        string editors = EditorsRoot?.Trim() ?? "";
        string downloads = DownloadsRoot?.Trim() ?? "";

        string? error = ValidateLocation(editors, "settings.locations.error.editors")
                        ?? ValidateLocation(downloads, "settings.locations.error.downloads");
        if (error != null)
        {
            _ = _dialogs.ShowErrorAsync("settings.locations.error.title", error, "");
            return;
        }

        _settings.Current.EditorsRootOverride = editors;
        _settings.Current.DownloadsRootOverride = downloads;
        _settings.Save();
        RestartPending = true;
        HubLog.Info("Install locations updated — applied on next launch.");
        OnPropertyChanged(nameof(EffectiveEditorsRoot));
        OnPropertyChanged(nameof(EffectiveDownloadsRoot));
    }

    /// <summary>Empty (default) is always valid; otherwise the path must be absolute and not a
    /// drive root itself (installs live in per-version subfolders, never the drive top).</summary>
    private static string? ValidateLocation(string value, string errorKey)
    {
        if (value.Length == 0)
            return null;
        if (!Path.IsPathRooted(value)
            || value.Equals(Path.GetPathRoot(Path.GetFullPath(value)), StringComparison.OrdinalIgnoreCase))
            return errorKey;
        return null;
    }

    [RelayCommand]
    private void OpenLogsDirectory()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            Shell.OpenFolder(AppPaths.LogsDir);
        }
        catch (Exception ex)
        {
            HubLog.Error("Failed to open log directory.", ex);
        }
    }
}

public sealed record LanguageOption(HubLanguage Language, string DisplayName);
