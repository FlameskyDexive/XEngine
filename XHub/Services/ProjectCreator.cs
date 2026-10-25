using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XHub.Infrastructure;
using XHub.Models;

namespace XHub.Services;

/// <summary>First-version template catalog (§3.2): the existing Universal 3D template only.
/// 2D, sample projects and a remote template market are later-version scope.</summary>
public static class TemplateCatalog
{
    public sealed record Template(string Id, string NameKey, string DescriptionKey, string MinEngineVersion, string PreviewKind);

    public static readonly Template Universal3D =
        new("universal-3d", "template.universal3d.name", "template.universal3d.description", "0.0.1", "universal3d");

    public static readonly Template[] Templates = [Universal3D];
}

/// <summary>
/// New-project flow (§3.2): 选择引擎版本 → 选择模板 → 名称与父目录 → 校验 → 创建 → 登记并打开.
/// The create itself reuses the editor's own <c>Project.Create</c>/<c>UniversalProjectTemplate.Seed</c>
/// through the hub protocol — XHub never re-implements project initialization. The write happens
/// in a temporary staging directory that is committed (moved) into place only after the editor
/// reports success; any failure removes the staging tree and no success record is written.
/// </summary>
public sealed class ProjectCreator
{
    private readonly InstallationLibrary _installations;

    /// <summary>The editor invocation seam — tests replace it; production runs --hub-command.</summary>
    public Func<InstallationRecord, string, string, CancellationToken, Task<(bool Ok, string Code, string Message)>>?
        CreateViaEditorOverride = null;

    public ProjectCreator(InstallationLibrary installations) => _installations = installations;

    /// <summary>Validates the display name as a single safe directory/file name.</summary>
    public static string? ValidateProjectName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "wizard.error.nameRequired";
        foreach (char c in name)
            foreach (char invalid in Path.GetInvalidFileNameChars())
                if (c == invalid) return "wizard.error.nameInvalid";
        if (name.TrimEnd().Length != name.Length || name.StartsWith('.')) return "wizard.error.nameInvalid";
        return null;
    }

    /// <summary>Validates the parent directory: must exist and must not contain a conflicting target.</summary>
    public static string? ValidateParent(string? parent, string projectName)
    {
        if (string.IsNullOrWhiteSpace(parent)) return "wizard.error.parentRequired";
        if (!Directory.Exists(parent)) return "wizard.error.parentMissing";
        string final = Path.Combine(parent, projectName);
        if (Directory.Exists(final) && Directory.GetFileSystemEntries(final).Length > 0)
            return "wizard.error.targetExists";
        return null;
    }

    /// <summary>
    /// Creates a project from a template under <paramref name="parentFolder"/>. Returns the final
    /// project root, or (null, errorKey, detail) on failure. Cancellation removes staging.
    /// </summary>
    public async Task<(string? ProjectRoot, string? ErrorKey, string Detail)> CreateAsync(
        InstallationRecord installation, string templateId, string parentFolder, string projectName,
        CancellationToken ct = default)
    {
        string? nameError = ValidateProjectName(projectName);
        if (nameError != null) return (null, nameError, projectName);
        string? parentError = ValidateParent(parentFolder, projectName);
        if (parentError != null) return (null, parentError, parentFolder);

        string staging = Path.Combine(parentFolder, $".xhub-new-{Guid.NewGuid():N}");
        string stagingProject = Path.Combine(staging, projectName);
        try
        {
            Directory.CreateDirectory(staging);
            (bool ok, string code, string message) = await RunEditorCreate(installation, staging, projectName, ct);
            if (ct.IsCancellationRequested)
                return (null, "wizard.error.canceled", "");
            if (!ok || !Directory.Exists(stagingProject))
                return (null, "wizard.error.createFailed", $"{code} {message}".Trim());

            string final = Path.Combine(parentFolder, projectName);
            if (Directory.Exists(final))
                return (null, "wizard.error.targetExists", final); // raced: refuse instead of overwrite
            Directory.Move(stagingProject, final);
            HubLog.Info($"Project created from template '{templateId}': {final}");
            return (final, null, message);
        }
        catch (Exception ex)
        {
            HubLog.Error("Project creation failed.", ex);
            return (null, "wizard.error.createFailed", ex.Message);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task<(bool Ok, string Code, string Message)> RunEditorCreate(
        InstallationRecord installation, string stagingParent, string projectName, CancellationToken ct)
    {
        if (CreateViaEditorOverride != null)
            return await CreateViaEditorOverride(installation, stagingParent, projectName, ct);

        var progress = new Progress<HubCommandEvent>();
        EditorCommandClient.CommandOutcome? outcome = await EditorCommandClient.ExecuteAsync(
            installation, "createProject",
            new { parentFolder = stagingParent, projectName },
            progress, timeoutMs: 180_000);
        if (outcome == null)
            return (false, "HUB_COMMAND_UNAVAILABLE", "editor did not report a terminal event");
        if (!outcome.Success)
            return (false, outcome.Code, ReadMessage(outcome.Data));
        return (true, "", ReadMessage(outcome.Data));
    }

    internal static string ReadMessage(System.Text.Json.JsonElement data) =>
        data.ValueKind == System.Text.Json.JsonValueKind.Object
            && data.TryGetProperty("message", out var message)
            && message.ValueKind == System.Text.Json.JsonValueKind.String
            ? message.GetString() ?? ""
            : "";

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            HubLog.Warning($"Staging cleanup failed for {directory}: {ex.Message}");
        }
    }
}
