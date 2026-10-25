using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Protocol;
using XEngine.Echo;

using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Gen-P2.2 — Turn a model's imported AnimationClip sub-assets into a playable Animator:
/// mint a .controller, one state per clip, cyclic exit-time transitions, bind Animator on the GO.
/// Same-skeleton clips (Meshy BYOC / mock AnimatedGlb) play without engine-side retarget.
/// </summary>
internal sealed class CharacterWireService
{
    private readonly ProjectDescriptor _project;
    private readonly SceneWriter _sceneWriter;

    public CharacterWireService(ProjectDescriptor project, SceneWriter sceneWriter)
    {
        _project = project;
        _sceneWriter = sceneWriter;
    }

    public async Task<CharacterWireResult> WireAsync(
        string scenePath,
        string objectIdentifier,
        string modelAssetPath,
        string? controllerPath,
        string idempotencyKey,
        string? intent,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        string modelAbs = _project.ResolveAssetPath(modelAssetPath);
        SubAssetList subs = SubAssetResolver.Resolve(modelAbs, modelAssetPath);
        var clips = new List<SubAssetInfo>();
        foreach (SubAssetInfo s in subs.SubAssets)
        {
            if (string.Equals(s.Kind, "AnimationClip", StringComparison.Ordinal))
            {
                clips.Add(s);
            }
        }

        if (clips.Count == 0)
        {
            throw new InvalidOperationException(
                $"Model '{modelAssetPath}' has no AnimationClip sub-assets. " +
                "Generate with kind=motion (mock AnimatedGlb, or Meshy rig+animate) and re-fetch.");
        }

        string destRel = NormalizeControllerPath(controllerPath, scenePath, objectIdentifier);
        var bindings = new List<CharacterClipBinding>(clips.Count);
        var warnings = new List<string>(subs.Warnings);
        var diagnostics = new List<string>();

        if (dryRun)
        {
            foreach (SubAssetInfo clip in clips)
            {
                bindings.Add(new CharacterClipBinding(SanitizeStateName(clip.StableId), clip.Guid, clip.StableId));
            }

            return new CharacterWireResult(scenePath, objectIdentifier, "", destRel, "", bindings, diagnostics, true, warnings);
        }

        string destAbs = Path.Combine(_project.ProjectRoot, destRel.Replace('/', Path.DirectorySeparatorChar));
        string controllerGuid = AssetWriter.WriteAnimatorController(
            destAbs, Path.GetFileNameWithoutExtension(destRel) ?? "Generated");

        await _sceneWriter.MutateAssetDataAsync(
            destAbs, destRel,
            root =>
            {
                AnimationAssetOps.AddParameter(root, "Speed", "Float");
                for (int i = 0; i < clips.Count; i++)
                {
                    string state = SanitizeStateName(clips[i].StableId);
                    AnimationAssetOps.CreateState(root, state, layerName: null, clips[i].Guid);
                }

                for (int i = 0; i < clips.Count; i++)
                {
                    string from = SanitizeStateName(clips[i].StableId);
                    string to = SanitizeStateName(clips[(i + 1) % clips.Count].StableId);
                    if (from != to)
                    {
                        AnimationAssetOps.CreateTransition(root, from, to, name: null, layerName: null);
                    }
                }

                // WriteAnimatorController seeds DefaultStateName = "New State"; replace with the
                // first imported clip so animator_validate does not mark every state unreachable
                // when there is only one clip (no cyclic transition).
                root["DefaultStateName"] = new EchoObject(SanitizeStateName(clips[0].StableId));
            },
            $"Wired {clips.Count} clip(s) from {modelAssetPath} into {destRel}.",
            ChangeProvenance.ForTool("xengine-cli.character-wire",
                intent ?? $"character controller from {modelAssetPath}", idempotencyKey + "-ctrl"),
            dryRun: false, expectedBeforeHash: null, cancellationToken).ConfigureAwait(false);

        SceneWriteResult added = await _sceneWriter.AddComponentAsync(
            _project.ResolveAssetPath(scenePath), scenePath, objectIdentifier, "Animator",
            new[] { new SceneFieldPatch("Controller/AssetID", controllerGuid) },
            ChangeProvenance.ForTool("xengine-cli.character-wire",
                intent ?? $"bind Animator to {destRel}", idempotencyKey + "-anim"),
            dryRun: false, expectedBeforeHash: null, cancellationToken).ConfigureAwait(false);

        foreach (SubAssetInfo clip in clips)
        {
            bindings.Add(new CharacterClipBinding(SanitizeStateName(clip.StableId), clip.Guid, clip.StableId));
        }

        AnimatorValidateResult validate = AnimationAssetOps.ValidateController(File.ReadAllText(destAbs), destRel);
        foreach (AnimatorDiagnostic d in validate.Diagnostics)
        {
            diagnostics.Add($"{d.Severity}: {d.Code} — {d.Message}");
        }

        return new CharacterWireResult(
            scenePath, objectIdentifier, added.CreatedIdentifier ?? "", destRel, controllerGuid,
            bindings, diagnostics, false, warnings);
    }

    private static string SanitizeStateName(string stableId)
    {
        string name = stableId.Replace('/', '_').Replace('\\', '_').Trim();
        return string.IsNullOrEmpty(name) ? "Clip" : name;
    }

    private static string NormalizeControllerPath(string? controllerPath, string scenePath, string objectIdentifier)
    {
        if (!string.IsNullOrWhiteSpace(controllerPath))
        {
            string trimmed = controllerPath.Replace('\\', '/').Trim('/');
            if (!trimmed.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                trimmed += ".controller";
            }

            return trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? trimmed : "Assets/" + trimmed;
        }

        string dir = Path.GetDirectoryName(scenePath)?.Replace('\\', '/') ?? "Assets";
        string stem = objectIdentifier.Length >= 8 ? objectIdentifier[..8].Replace("-", "", StringComparison.Ordinal) : objectIdentifier;
        return string.IsNullOrEmpty(dir) ? $"{stem}.controller" : $"{dir}/{stem}.controller";
    }
}
