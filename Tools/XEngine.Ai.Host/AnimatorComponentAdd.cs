using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// Adds an <c>Animator</c> to a scene GameObject and optionally mints a sibling .controller.
/// </summary>
internal static class AnimatorComponentAdd
{
    public static Task<SceneWriteResult> RunAsync(
        ProjectDescriptor project,
        SceneWriter writer,
        string scenePath,
        string objectIdentifier,
        ChangeProvenance provenance,
        bool createController,
        string? clipGuid,
        bool dryRun,
        string? expectedBeforeHash,
        CancellationToken cancellationToken)
    {
        var fields = new List<SceneFieldPatch>();
        if (!string.IsNullOrEmpty(clipGuid))
            fields.Add(new SceneFieldPatch("Clip/AssetID", clipGuid));

        if (createController)
        {
            string destRel = ControllerRelativePath(scenePath, objectIdentifier);
            string abs = project.ResolveAssetPath(destRel);
            string guid = AssetWriter.WriteAnimatorController(
                abs, Path.GetFileNameWithoutExtension(destRel) ?? "NewAnimator");
            fields.Add(new SceneFieldPatch("Controller/AssetID", guid));
        }

        return writer.AddComponentAsync(
            project.ResolveAssetPath(scenePath),
            scenePath,
            objectIdentifier,
            "Animator",
            fields.Count > 0 ? fields : null,
            provenance,
            dryRun,
            expectedBeforeHash,
            cancellationToken);
    }

    private static string ControllerRelativePath(string scenePath, string objectIdentifier)
    {
        string dir = Path.GetDirectoryName(scenePath)?.Replace('\\', '/') ?? "Assets";
        string stem = objectIdentifier.Length >= 8 ? objectIdentifier[..8] : objectIdentifier;
        stem = stem.Replace("-", "", StringComparison.Ordinal);
        return string.IsNullOrEmpty(dir) ? $"{stem}.controller" : $"{dir}/{stem}.controller";
    }
}
