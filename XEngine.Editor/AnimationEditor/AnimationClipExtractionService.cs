// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Animation;
using XEngine.Editor;
using XEngine.Editor.Projects;
using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Extracts imported <see cref="AnimationClip"/> sub-assets into standalone <c>.anim</c> files.
///
/// Model-imported clips are read-only because their stable identity is derived from the model
/// file. The Project window's Ctrl+D path uses this service instead of copying the model file:
/// the clone has no model AssetID/AssetPath, receives a fresh .meta GUID, and therefore has no
/// implicit dependency on the source FBX's mesh or prefab family.
/// </summary>
public static class AnimationClipExtractionService
{
    public const string DefaultOutputFolderName = "Animations";

    /// <summary>One source sub-asset to standalone-asset mapping.</summary>
    public sealed record ExtractionResult(
        Guid SourceGuid,
        Guid ExtractedGuid,
        string SourcePath,
        string ExtractedPath,
        string ClipName);

    /// <summary>Result of a controller-scoped batch extraction.</summary>
    public sealed class BatchExtractionResult
    {
        private readonly List<ExtractionResult> _extracted = new();
        private readonly List<string> _errors = new();

        public IReadOnlyList<ExtractionResult> Extracted => _extracted;
        public IReadOnlyList<string> Errors => _errors;
        public int ExtractedCount => _extracted.Count;
        public int ErrorCount => _errors.Count;

        internal void Add(ExtractionResult result) => _extracted.Add(result);
        internal void AddError(string error) => _errors.Add(error);

        /// <summary>
        /// Gives runtime_eval a useful one-line result without requiring JSON support in the REPL.
        /// The full mappings remain available through <see cref="Extracted"/>.
        /// </summary>
        public override string ToString()
        {
            if (_extracted.Count == 0 && _errors.Count == 0)
                return "extracted=0 errors=0";

            var lines = new List<string>(_extracted.Count + _errors.Count + 1)
            {
                $"extracted={_extracted.Count} errors={_errors.Count}"
            };
            for (int i = 0; i < _extracted.Count; i++)
            {
                ExtractionResult r = _extracted[i];
                lines.Add($"{r.ClipName}: {r.SourceGuid:N} -> {r.ExtractedGuid:N} ({r.ExtractedPath})");
            }
            for (int i = 0; i < _errors.Count; i++)
                lines.Add($"error: {_errors[i]}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Extract one selected AnimationClip sub-asset. The destination is a sibling
    /// <c>Animations/</c> folder below the parent model's directory.
    /// </summary>
    public static bool TryExtractSubAsset(
        EditorAssetBackend db,
        Guid subAssetGuid,
        out ExtractionResult? result,
        out string? error)
    {
        result = null;
        error = null;
        if (db == null)
        {
            error = "Asset database is not available.";
            return false;
        }
        if (subAssetGuid == Guid.Empty)
        {
            error = "AnimationClip sub-asset has no GUID.";
            return false;
        }
        if (!db.TryGetParentGuid(subAssetGuid, out Guid parentGuid))
        {
            error = $"GUID '{subAssetGuid}' is not a sub-asset.";
            return false;
        }

        if (db.Get(subAssetGuid) is not AnimationClip clip)
        {
            error = $"Sub-asset '{subAssetGuid}' is not an AnimationClip.";
            return false;
        }

        string? parentPath = db.GuidToPath(parentGuid);
        if (string.IsNullOrEmpty(parentPath))
        {
            error = $"Could not resolve parent asset for sub-asset '{subAssetGuid}'.";
            return false;
        }

        return TryExtract(
            db,
            clip,
            parentPath,
            subAssetGuid,
            outputFolderName: DefaultOutputFolderName,
            out result,
            out error);
    }

    /// <summary>
    /// Extract a clip whose source parent path is known. This overload is useful for importer
    /// tests and for tooling that has already resolved a sub-asset; it does not require the source
    /// model to be copied or rewritten.
    /// </summary>
    public static bool TryExtract(
        EditorAssetBackend db,
        AnimationClip source,
        string parentAssetPath,
        Guid sourceGuid,
        string outputFolderName,
        out ExtractionResult? result,
        out string? error)
    {
        result = null;
        error = null;
        if (db == null)
        {
            error = "Asset database is not available.";
            return false;
        }
        if (source == null)
        {
            error = "AnimationClip source is null.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(parentAssetPath))
        {
            error = "Parent asset path is empty.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(outputFolderName)
            || outputFolderName.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || outputFolderName.Contains('/')
            || outputFolderName.Contains('\\')
            || outputFolderName is "." or "..")
        {
            error = $"Invalid output folder name '{outputFolderName}'.";
            return false;
        }
        if (Project.Current == null)
        {
            error = "No active project.";
            return false;
        }

        string parentRelative = NormalizeRelativePath(parentAssetPath);
        if (parentRelative.StartsWith("../", StringComparison.Ordinal)
            || parentRelative.Equals("..", StringComparison.Ordinal)
            || Path.IsPathRooted(parentRelative))
        {
            error = $"Parent asset path '{parentAssetPath}' is outside the Assets folder.";
            return false;
        }

        string? parentDirectory = Path.GetDirectoryName(parentRelative)?.Replace('\\', '/');
        string outputRelativeFolder = string.IsNullOrEmpty(parentDirectory)
            ? outputFolderName
            : $"{parentDirectory}/{outputFolderName}";
        string outputAbsoluteFolder = Path.Combine(
            Project.Current.AssetsPath,
            outputRelativeFolder.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            Directory.CreateDirectory(outputAbsoluteFolder);
            // Keep the generated folder visible to the Project panel and consistent with folders
            // created through Assets/Create/Folder. This does not copy the model's .meta.
            MetaFile.EnsureMeta(outputAbsoluteFolder, "DefaultImporter");

            string baseName = SanitizeFileName(source.Name);
            string fileName = AssetCreateMenu.FindUniqueName(outputAbsoluteFolder, baseName, ".anim");
            string extractedRelative = $"{outputRelativeFolder}/{fileName}";

            // DuplicateClip performs a graph-preserving Echo round-trip and clears the source
            // model identity. Restore the original display name because "(edit)" is specific to
            // the Animation window's Duplicate-to-edit command, not Project Ctrl+D extraction.
            AnimationClip copy = AnimationWindowBinding.DuplicateClip(source);
            copy.Name = source.Name;
            db.CreateAsset(copy, extractedRelative);

            if (copy.AssetID == Guid.Empty)
            {
                error = $"Asset database did not assign a GUID to '{extractedRelative}'.";
                return false;
            }

            string absoluteAsset = Path.Combine(Project.Current.AssetsPath,
                extractedRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absoluteAsset) || !File.Exists(MetaFile.GetMetaPath(absoluteAsset)))
            {
                error = $"Standalone clip was not written to '{extractedRelative}'.";
                return false;
            }

            result = new ExtractionResult(sourceGuid, copy.AssetID, parentRelative, extractedRelative, copy.Name);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to extract '{source.Name}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Extract only AnimationClip sub-assets reachable from one controller. It deliberately does
    /// not scan every model in the project, so unrelated FBX clips are never copied. The method
    /// does not mutate or save the controller; callers can inspect <see cref="BatchExtractionResult"/>
    /// and remap/save the controller in a separate explicit step.
    /// </summary>
    public static BatchExtractionResult ExtractControllerClips(
        EditorAssetBackend db,
        AnimatorController controller,
        string outputFolderName = DefaultOutputFolderName)
    {
        var batch = new BatchExtractionResult();
        if (db == null)
        {
            batch.AddError("Asset database is not available.");
            return batch;
        }
        if (controller == null)
        {
            batch.AddError("AnimatorController is null.");
            return batch;
        }

        var seen = new HashSet<Guid>();
        List<AssetRef<AnimationClip>> references = controller.GetClipReferences();
        for (int i = 0; i < references.Count; i++)
        {
            Guid sourceGuid = references[i].AssetID;
            if (sourceGuid == Guid.Empty || !seen.Add(sourceGuid))
                continue;
            if (!db.TryGetParentGuid(sourceGuid, out _))
                continue; // Standalone clips already have their own file and need no extraction.

            if (!TryExtractSubAsset(db, sourceGuid, out ExtractionResult? result, out string? error))
                batch.AddError(error ?? $"Could not extract '{sourceGuid}'.");
            else if (result != null)
                batch.Add(result);
        }
        return batch;
    }

    /// <summary>
    /// Runtime-eval convenience overload. Resolves a controller by its main-asset GUID and then
    /// delegates to the controller-scoped batch method.
    /// </summary>
    public static BatchExtractionResult ExtractControllerClips(Guid controllerGuid)
    {
        var batch = new BatchExtractionResult();
        EditorAssetBackend? db = EditorAssetBackend.Instance;
        if (db == null)
        {
            batch.AddError("Asset database is not available.");
            return batch;
        }
        if (controllerGuid == Guid.Empty)
        {
            batch.AddError("Controller GUID is empty.");
            return batch;
        }
        if (db.Get(controllerGuid) is not AnimatorController controller)
        {
            batch.AddError($"GUID '{controllerGuid}' is not an AnimatorController.");
            return batch;
        }
        return ExtractControllerClips(db, controller);
    }

    /// <summary>
    /// Explicit migration entry point for runtime_eval: extract the controller's imported clip
    /// references, replace every matching state/blend-tree reference with the new standalone GUID,
    /// and save the controller. This is intentionally separate from
    /// <see cref="ExtractControllerClips(Guid)"/>, which is read-only with respect to the
    /// controller.
    /// </summary>
    public static BatchExtractionResult ExtractAndRemapControllerClips(Guid controllerGuid)
    {
        var batch = new BatchExtractionResult();
        EditorAssetBackend? db = EditorAssetBackend.Instance;
        if (db == null)
        {
            batch.AddError("Asset database is not available.");
            return batch;
        }
        if (controllerGuid == Guid.Empty)
        {
            batch.AddError("Controller GUID is empty.");
            return batch;
        }
        if (db.Get(controllerGuid) is not AnimatorController controller)
        {
            batch.AddError($"GUID '{controllerGuid}' is not an AnimatorController.");
            return batch;
        }

        batch = ExtractControllerClips(db, controller);
        if (batch.ExtractedCount == 0)
            return batch;

        int remapped = RemapControllerClipReferences(controller, batch.Extracted);
        if (remapped > 0)
            db.SaveAsset(controller);
        return batch;
    }

    /// <summary>
    /// Replace controller state/blend-tree references using extraction mappings. Returns the number
    /// of AssetRef slots changed. The controller is not saved by this method.
    /// </summary>
    public static int RemapControllerClipReferences(
        AnimatorController controller,
        IReadOnlyList<ExtractionResult> mappings)
    {
        if (controller == null || mappings == null || mappings.Count == 0)
            return 0;

        var map = new Dictionary<Guid, Guid>(mappings.Count);
        for (int i = 0; i < mappings.Count; i++)
        {
            ExtractionResult mapping = mappings[i];
            if (mapping.SourceGuid != Guid.Empty && mapping.ExtractedGuid != Guid.Empty)
                map[mapping.SourceGuid] = mapping.ExtractedGuid;
        }
        if (map.Count == 0)
            return 0;

        int changed = 0;
        List<AnimatorController.Layer> layers = controller.Layers;
        if (layers.Count > 0)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                AnimatorController.Layer layer = layers[i];
                changed += layer.Root != null
                    ? RemapStateMachine(layer.Root, map)
                    : RemapStates(layer.States, map);
            }
        }
        else
        {
            changed += RemapStates(controller.States, map);
        }

        if (controller is AnimatorOverrideController overrideController)
        {
            List<AnimatorOverrideController.ClipOverride> overrides = overrideController.Overrides;
            for (int i = 0; i < overrides.Count; i++)
            {
                var item = overrides[i];
                changed += RemapReference(ref item.From, map);
                changed += RemapReference(ref item.To, map);
            }
        }
        return changed;
    }

    private static int RemapStateMachine(
        AnimatorController.StateMachine machine,
        Dictionary<Guid, Guid> map)
    {
        int changed = RemapStates(machine.States, map);
        List<AnimatorController.StateMachine> children = machine.StateMachines;
        for (int i = 0; i < children.Count; i++)
            changed += RemapStateMachine(children[i], map);
        return changed;
    }

    private static int RemapStates(
        List<AnimatorController.State> states,
        Dictionary<Guid, Guid> map)
    {
        int changed = 0;
        for (int i = 0; i < states.Count; i++)
        {
            AnimatorController.State state = states[i];
            BlendTree? tree = state.BlendTree?.Res;
            if (tree != null)
                changed += RemapBlendTree(tree, map);
            else
            {
                changed += RemapReference(ref state.Motion, map);
                List<AssetRef<AnimationClip>> extra = state.Motions;
                for (int m = 0; m < extra.Count; m++)
                {
                    AssetRef<AnimationClip> reference = extra[m];
                    changed += RemapReference(ref reference, map);
                    extra[m] = reference;
                }
            }
        }
        return changed;
    }

    private static int RemapBlendTree(
        BlendTree tree,
        Dictionary<Guid, Guid> map)
    {
        int changed = 0;
        List<BlendTree.Child> children = tree.Children;
        for (int i = 0; i < children.Count; i++)
        {
            BlendTree.Child child = children[i];
            BlendTree? nested = child.Tree.Res;
            if (nested != null)
                changed += RemapBlendTree(nested, map);
            else
                changed += RemapReference(ref child.Clip, map);
        }
        return changed;
    }

    private static int RemapReference(
        ref AssetRef<AnimationClip> reference,
        Dictionary<Guid, Guid> map)
    {
        if (!map.TryGetValue(reference.AssetID, out Guid replacement))
            return 0;
        reference = new AssetRef<AnimationClip>(replacement);
        return 1;
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static string SanitizeFileName(string? name)
    {
        string value = string.IsNullOrWhiteSpace(name) ? "Animation" : name.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < invalid.Length; i++)
            value = value.Replace(invalid[i], '_');
        value = value.TrimEnd('.', ' ');
        return string.IsNullOrEmpty(value) ? "Animation" : value;
    }
}
