using System.Collections.Generic;
using System.Threading.Tasks;
using XHub.Models;

namespace XHub.Services;

/// <summary>
/// UI dialogs and pickers the view models need. Kept as an interface so VM logic stays
/// unit-testable; the implementation lives in the view layer.
/// </summary>
public interface IDialogService
{
    /// <summary>Picks a project folder. Null = canceled.</summary>
    Task<string?> PickProjectFolderAsync();

    /// <summary>Picks a .xengine marker file. Null = canceled.</summary>
    Task<string?> PickXEngineFileAsync();

    /// <summary>Picks an editor executable for manual installation locate (§3.3). Null = canceled.</summary>
    Task<string?> PickEditorInstallAsync();

    /// <summary>Picks any existing directory (new-project parent). Null = canceled.</summary>
    Task<string?> PickAnyFolderAsync();

    /// <summary>
    /// Asks the user to pick one file from a set of equally valid <c>.xengine</c> marker files
    /// (ambiguous project identity, §3.1). Returns the chosen file, or null when canceled.
    /// </summary>
    Task<string?> ShowXEngineChoiceAsync(IReadOnlyList<string> xengineFiles);

    /// <summary>Non-blocking error surface with a localized title/hint plus the offending path.</summary>
    Task ShowErrorAsync(string titleKey, string hintKey, string detail);

    /// <summary>
    /// Runs the new-project wizard (§3.2). Returns the registered project record, or null when
    /// canceled / creation failed.
    /// </summary>
    Task<Models.ProjectRecord?> ShowNewProjectWizardAsync(
        ProjectCreator creator, ProjectLibrary library, InstallationLibrary installations,
        TemplateCatalog.Template template);

    /// <summary>Opens the manage-modules dialog for an installation (§3.4).</summary>
    Task ShowModulesAsync(InstallationRecord installation);
}
