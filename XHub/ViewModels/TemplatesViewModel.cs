using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using XHub.Models;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

/// <summary>One template card on the templates page (§3.2: 名称、说明、适用版本).</summary>
public sealed class TemplateItemViewModel : ViewModelBase
{
    public TemplateItemViewModel(TemplateCatalog.Template template, string engineVersionText)
    {
        Template = template;
        EngineVersionText = engineVersionText;
    }

    public TemplateCatalog.Template Template { get; }

    public string Name => Loc.Instance[Template.NameKey];

    public string Description => Loc.Instance[Template.DescriptionKey];

    public string EngineVersionText { get; }

    /// <summary>Re-renders localized strings after a language switch.</summary>
    public void OnLanguageChanged() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// Templates page (§3.2). The first version ships the existing Universal 3D template, reused
/// through the editor's own seeding; the wizard collects engine version / name / parent.
/// </summary>
public partial class TemplatesViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly ProjectCreator _creator;
    private readonly ProjectLibrary _library;
    private readonly InstallationLibrary _installations;
    private readonly Func<ProjectRecord, Task>? _onProjectCreated;

    public TemplatesViewModel(IDialogService dialogs, ProjectCreator creator, ProjectLibrary library,
        InstallationLibrary installations, Func<ProjectRecord, Task>? onProjectCreated = null)
    {
        _dialogs = dialogs;
        _creator = creator;
        _library = library;
        _installations = installations;
        _onProjectCreated = onProjectCreated;

        var items = new List<TemplateItemViewModel>();
        foreach (TemplateCatalog.Template template in TemplateCatalog.Templates)
            items.Add(new TemplateItemViewModel(template, "≥ 0.0.1"));
        Templates = items;

        Loc.Instance.PropertyChanged += (_, _) =>
        {
            foreach (TemplateItemViewModel item in Templates)
                item.OnLanguageChanged();
        };
    }

    public IReadOnlyList<TemplateItemViewModel> Templates { get; }

    /// <summary>True when an unbroken installation exists (wizard needs one).</summary>
    public bool HasUsableInstallation
    {
        get
        {
            foreach (InstallationRecord record in _installations.Installations)
                if (!record.IsBroken) return true;
            return false;
        }
    }

    [RelayCommand]
    public async Task UseTemplateAsync(TemplateItemViewModel? item)
    {
        if (item == null) return;
        await StartWizardAsync(item.Template);
    }

    public async Task StartWizardAsync(TemplateCatalog.Template template)
    {
        ProjectRecord? record = await _dialogs.ShowNewProjectWizardAsync(_creator, _library, _installations, template);
        if (record == null) return;
        if (_onProjectCreated != null)
            await _onProjectCreated(record);
    }
}
