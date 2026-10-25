using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

/// <summary>One selectable engine installation in the wizard (§3.2 选择引擎版本).</summary>
public sealed record InstallationOption(string Id, string Display, string CanonicalVersion)
{
    public override string ToString() => Display;
}

/// <summary>
/// New-project wizard state (§3.2): engine version → template (preselected) → name + parent
/// folder → validation → create (editor reuses its own template seeding) → register + open.
/// </summary>
public partial class NewProjectViewModel : ViewModelBase
{
    private readonly ProjectCreator _creator;
    private readonly ProjectLibrary _library;
    private readonly InstallationLibrary _installations;
    private readonly TemplateCatalog.Template _template;
    private readonly IDialogService _dialogs;
    private readonly TaskCompletionSource<ProjectRecord?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _createCts;

    public NewProjectViewModel(ProjectCreator creator, ProjectLibrary library,
        InstallationLibrary installations, TemplateCatalog.Template template, IDialogService dialogs)
    {
        _creator = creator;
        _library = library;
        _installations = installations;
        _template = template;
        _dialogs = dialogs;

        var options = new List<InstallationOption>();
        foreach (InstallationRecord record in installations.Installations)
        {
            if (record.IsBroken) continue;
            string display = record.VersionDisplay.Length > 0 ? record.VersionDisplay : Loc.Instance["installations.version.unknown"];
            options.Add(new InstallationOption(record.Id, $"{display} — {record.Path}", record.CanonicalVersion));
        }
        Installations = options;
        _selectedInstallationIndex = options.Count > 0 ? 0 : -1;

        // Friendly default: the user profile's project folder when it exists.
        string defaultParent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "XEngine Projects");
        ParentFolder = Directory.Exists(defaultParent) ? defaultParent : "";
    }

    public TemplateCatalog.Template Template => _template;

    public IReadOnlyList<InstallationOption> Installations { get; }

    [ObservableProperty]
    private int _selectedInstallationIndex;

    [ObservableProperty]
    private string _projectName = "";

    [ObservableProperty]
    private string _parentFolder = "";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _isCreating;

    public Task<ProjectRecord?> Completion => _completion.Task;

    partial void OnProjectNameChanged(string value) => ErrorText = "";
    partial void OnParentFolderChanged(string value) => ErrorText = "";

    [RelayCommand]
    private async Task BrowseParentAsync()
    {
        string? folder = await _dialogs.PickAnyFolderAsync();
        if (folder != null) ParentFolder = folder;
    }

    [RelayCommand]
    private void Cancel()
    {
        _createCts?.Cancel();
        _completion.TrySetResult(null);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsCreating) return;
        string name = ProjectName.Trim();
        string? validation = ProjectCreator.ValidateProjectName(name) ?? ProjectCreator.ValidateParent(ParentFolder, name);
        if (validation != null)
        {
            ErrorText = Loc.Instance[validation];
            return;
        }
        if (SelectedInstallationIndex < 0 || SelectedInstallationIndex >= Installations.Count)
        {
            ErrorText = Loc.Instance["wizard.error.noInstallation"];
            return;
        }
        InstallationRecord? install = _installations.FindById(Installations[SelectedInstallationIndex].Id);
        if (install == null)
        {
            ErrorText = Loc.Instance["wizard.error.noInstallation"];
            return;
        }

        IsCreating = true;
        ErrorText = "";
        _createCts = new CancellationTokenSource();
        try
        {
            (string? root, string? errorKey, string detail) = await _creator.CreateAsync(
                install, _template.Id, ParentFolder, name, _createCts.Token);
            if (root == null)
            {
                ErrorText = Loc.Instance[errorKey ?? "wizard.error.createFailed"]
                            + (detail.Length > 0 ? $"\n{detail}" : "");
                return;
            }

            ProjectRecord record = _library.Add(root, name, metadataIncomplete: false);
            _completion.TrySetResult(record);
        }
        finally
        {
            IsCreating = false;
        }
    }
}
