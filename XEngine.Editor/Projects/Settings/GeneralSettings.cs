using System;
using System.IO;

using XEngine.Editor.Core;
using XEngine.Editor.Inspector;
using XEngine.Editor.Projects;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Editor.Theming;
using XEngine.Runtime;

using XEngine.Editor.GUI;
namespace XEngine.Editor.Projects.Settings;

[ProjectSettings("General", EditorIcons.Gear, order: 0)]
public class GeneralSettings : ProjectSettingsBase
{
    public string CompanyName = "DefaultCompany";
    public string ProductName = "XEngine";
    public string Version = "0.1.0";

    /// <summary>Project-relative PNG used as the player launcher icon. Empty uses XEngine branding.</summary>
    public string ApplicationIconPath = "";

    // Note: the last-opened scene path is deliberately NOT a setting. It is per-project ephemeral
    // session state and lives in the git-ignored Library/ cache (see EditorSceneManager's
    // LastScene.txt handling), so it no longer dirties ProjectSettings/General.yaml on every
    // scene open/save nor risks being wiped by settings-save ordering issues.

    public override void OnGUI(Paper paper, float width)
    {
        Origami.Header(paper, "gen_header", $"{EditorIcons.Gear}  General").Underline().Show();

        EditorGUI.SettingsTextField(paper, "gen_company", "Company Name", CompanyName, v => CompanyName = v);
        EditorGUI.SettingsTextField(paper, "gen_product", "Product Name", ProductName, v => ProductName = v);
        EditorGUI.SettingsTextField(paper, "gen_version", "Version", Version, v => Version = v);
        EditorGUI.SettingsRow(paper, "gen_icon", "Application Icon", () =>
        {
            using (paper.Row("gen_icon_row").Height(28).RowBetween(6).Enter())
            {
                Origami.TextField(paper, "gen_icon_path", ApplicationIconPath,
                    value =>
                    {
                        ApplicationIconPath = value.Replace('\\', '/');
                        EditorRegistries.SaveSettings();
                    }).Show();
                Origami.Button(paper, "gen_icon_browse", EditorIcons.FolderOpen, SelectApplicationIcon)
                    .Width(32).Tooltip("Choose a PNG in this project").Show();
            }
        }, separator: false);
    }

    /// <summary>Gets the selected project PNG, or null so the build can use the XEngine default.</summary>
    public string? ResolveApplicationIconPath(Project project)
    {
        if (project == null || string.IsNullOrWhiteSpace(ApplicationIconPath))
            return null;

        string root = Path.GetFullPath(project.RootPath);
        string candidate = Path.GetFullPath(Path.Combine(root, ApplicationIconPath));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
               Path.GetExtension(candidate).Equals(".png", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(candidate)
            ? candidate
            : null;
    }

    private void SelectApplicationIcon()
    {
        Project? project = Project.Current;
        if (project == null)
            return;

        EditorApplication.OpenFileDialog(FileDialogMode.Open, path =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string absolute = Path.GetFullPath(path);
            string root = Path.GetFullPath(project.RootPath);
            string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetExtension(absolute).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("[General Settings] Application Icon must be a PNG inside the project directory.");
                return;
            }

            ApplicationIconPath = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            EditorRegistries.SaveSettings();
        }, project.AssetsPath, ["*.png"], ["PNG Images (*.png)"]);
    }
}
