using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Gateway;
using XEngine.Ai.Protocol;
using XEngine.Echo;

using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Gen-P2.1 — The playable-prop vertical loop: auto collider from a model's AABB / mesh,
/// triangle-budget remesh (overwrite, parent GUID preserved), and extract a wired GameObject
/// to a standalone <c>.prefab</c>. All writes go through the existing journaled writers.
/// </summary>
internal sealed class PlayablePropService
{
    private readonly ProjectDescriptor _project;
    private readonly SceneWriter _sceneWriter;
    private readonly AssetWriter _assetWriter;
    private readonly GenService _genService;
    private readonly GenTaskStore _store;

    public PlayablePropService(
        ProjectDescriptor project,
        SceneWriter sceneWriter,
        AssetWriter assetWriter,
        GenService genService,
        GenTaskStore store)
    {
        _project = project;
        _sceneWriter = sceneWriter;
        _assetWriter = assetWriter;
        _genService = genService;
        _store = store;
    }

    public MeshStatsResult MeshStats(string assetPath, int? maxTriangles)
    {
        string absolute = _project.ResolveAssetPath(assetPath);
        return MeshStatsResolver.Resolve(absolute, assetPath, maxTriangles);
    }

    public async Task<ColliderAddResult> AddColliderAsync(
        string scenePath,
        string objectIdentifier,
        string modelAssetPath,
        string shape,
        bool convex,
        string idempotencyKey,
        string? intent,
        bool dryRun,
        string? expectedBeforeHash,
        CancellationToken cancellationToken)
    {
        string normalized = (shape ?? "box").Trim().ToLowerInvariant();
        if (normalized is not ("box" or "mesh" or "convex"))
        {
            throw new ArgumentException("shape must be 'box', 'mesh', or 'convex'.");
        }

        MeshStatsResult stats = MeshStats(modelAssetPath, maxTriangles: null);
        string componentType;
        List<SceneFieldPatch> fields;
        if (normalized == "box")
        {
            componentType = "BoxCollider";
            fields = new List<SceneFieldPatch>
            {
                new("size/X", Format(stats.SuggestedBoxSizeX)),
                new("size/Y", Format(stats.SuggestedBoxSizeY)),
                new("size/Z", Format(stats.SuggestedBoxSizeZ)),
                new("Center/X", Format(stats.SuggestedBoxCenterX)),
                new("Center/Y", Format(stats.SuggestedBoxCenterY)),
                new("Center/Z", Format(stats.SuggestedBoxCenterZ)),
            };
        }
        else
        {
            MeshStatEntry? largest = stats.Meshes.Count == 0
                ? null
                : stats.Meshes.OrderByDescending(m => m.Triangles).First();
            if (largest is null || string.IsNullOrEmpty(largest.Guid))
            {
                throw new InvalidOperationException(
                    "Mesh/convex collider needs a model with a .meta so the mesh sub-asset GUID can be derived. Import the file first.");
            }

            componentType = "MeshCollider";
            bool useConvex = normalized == "convex" || convex;
            fields = new List<SceneFieldPatch>
            {
                new("mesh/AssetID", largest.Guid!),
                new("convex", useConvex ? "true" : "false"),
            };
        }

        SceneWriteResult write = await _sceneWriter.AddComponentAsync(
            _project.ResolveAssetPath(scenePath), scenePath, objectIdentifier, componentType, fields,
            ChangeProvenance.ForTool("xengine-cli.scene-collider-add",
                intent ?? $"auto {normalized} collider from {modelAssetPath}", idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken).ConfigureAwait(false);

        return new ColliderAddResult(
            write.ChangeId, scenePath, objectIdentifier, write.CreatedIdentifier ?? "",
            normalized == "convex" ? "convex" : normalized,
            write.BeforeHash, write.AfterHash, write.DryRun, write.Summary, stats);
    }

    public async Task<PrefabCreateResult> CreatePrefabAsync(
        string scenePath,
        string objectIdentifier,
        string destinationPath,
        string idempotencyKey,
        string? intent,
        bool overwrite,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        string sceneAbs = _project.ResolveAssetPath(scenePath);
        if (!File.Exists(sceneAbs))
        {
            throw new FileNotFoundException($"Scene not found: {sceneAbs}");
        }

        string destRel = NormalizePrefabPath(destinationPath);
        string destAbs = Path.Combine(_project.ProjectRoot, destRel.Replace('/', Path.DirectorySeparatorChar));

        EchoObject scene = EchoObject.ReadFromString(File.ReadAllText(sceneAbs));
        EchoObject go = EchoFacade.FindGameObject(scene, objectIdentifier)
            ?? throw new KeyNotFoundException(
                $"GameObject '{objectIdentifier}' not found in {scenePath}. Call scene_hierarchy first.");

        EchoObject clone = go.Clone();
        string contents = clone.WriteToString();

        if (dryRun)
        {
            string existingGuid = File.Exists(destAbs + ".meta")
                ? (ReadGuid(destAbs + ".meta") ?? "")
                : "";
            return new PrefabCreateResult(destRel, existingGuid, scenePath, objectIdentifier, "", true, File.Exists(destAbs));
        }

        string temp = Path.Combine(Path.GetTempPath(), "xengine-prefab-" + Guid.NewGuid().ToString("N") + ".prefab");
        File.WriteAllText(temp, contents);
        try
        {
            AssetImportResult import = await _assetWriter.ImportAsync(
                temp, destAbs, destRel,
                ChangeProvenance.ForTool("xengine-cli.prefab-create",
                    intent ?? $"extract '{objectIdentifier}' from {scenePath}", idempotencyKey),
                overwrite, animationType: null, cancellationToken).ConfigureAwait(false);
            return new PrefabCreateResult(destRel, import.Guid, scenePath, objectIdentifier, "", false, import.Overwritten);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    public async Task<BudgetEnsureResult> EnsureBudgetAsync(
        string assetPath,
        int maxTriangles,
        string? taskId,
        string idempotencyKey,
        string? intent,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (maxTriangles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTriangles), "maxTriangles must be ≥ 1.");
        }

        string absolute = _project.ResolveAssetPath(assetPath);
        MeshStatsResult stats = MeshStatsResolver.Resolve(absolute, assetPath, maxTriangles);
        string guid = stats.ParentGuid ?? "";
        var warnings = new List<string>(stats.Warnings);

        if (!stats.OverBudget)
        {
            return new BudgetEnsureResult(assetPath, guid, stats.TotalTriangles, maxTriangles,
                false, false, false, dryRun, null, stats, warnings);
        }

        if (dryRun)
        {
            return new BudgetEnsureResult(assetPath, guid, stats.TotalTriangles, maxTriangles,
                true, false, true, true, null, stats, warnings);
        }

        GenTaskRecord? original = string.IsNullOrWhiteSpace(taskId) ? null : _store.Get(taskId);
        string provider = original?.Provider ?? "mock";
        string? remeshTaskId = null;

        if (string.Equals(provider, "meshy", StringComparison.OrdinalIgnoreCase) && original is not null)
        {
            remeshTaskId = await RemeshViaProvider(original, maxTriangles, assetPath, idempotencyKey, intent, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Offline / mock path: overwrite the imported file with the 1-triangle placeholder.
            // Parent GUID is preserved (AssetWriter overwrite), so scene refs to the *model*
            // survive. Sub-asset names may change — run this *before* wiring MeshRenderer GUIDs.
            string temp = Path.Combine(Path.GetTempPath(), "xengine-remesh-" + Guid.NewGuid().ToString("N") + ".glb");
            File.WriteAllBytes(temp, MockArtifacts.MinimalGlb());
            try
            {
                AssetImportResult import = await _assetWriter.ImportAsync(
                    temp, absolute, assetPath,
                    ChangeProvenance.ForTool("xengine-cli.gen-ensure-budget",
                        intent ?? $"decimate {assetPath} to ≤{maxTriangles} tris (was {stats.TotalTriangles})",
                        idempotencyKey),
                    overwrite: true, animationType: null, cancellationToken).ConfigureAwait(false);
                guid = import.Guid;
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }

            warnings.Add(
                "Used the mock/offline decimate path (1-triangle GLB overwrite). " +
                "For Meshy remesh pass the original gen taskId so gen_ensure_budget can call mode=remesh.");
        }

        MeshStatsResult after = MeshStatsResolver.Resolve(absolute, assetPath, maxTriangles);
        return new BudgetEnsureResult(assetPath, after.ParentGuid ?? guid, after.TotalTriangles, maxTriangles,
            after.OverBudget, Remeshed: true, WouldRemesh: false, false, remeshTaskId, after, warnings);
    }

    private async Task<string> RemeshViaProvider(
        GenTaskRecord original,
        int maxTriangles,
        string assetPath,
        string idempotencyKey,
        string? intent,
        CancellationToken cancellationToken)
    {
        string extra = $"{{\"mode\":\"remesh\",\"inputTaskId\":\"{original.TaskId}\",\"targetPolycount\":\"{maxTriangles}\"}}";
        GenSubmitToolResult submitted = await _genService.SubmitAsync(
            original.Provider, GenRecipe.Kinds.Model3d, original.Recipe.Prompt,
            original.Recipe.Model, original.Recipe.Seed, original.Recipe.StyleAnchor,
            extra, dryRun: false, cancellationToken).ConfigureAwait(false);

        // Poll a bounded number of times — Meshy remesh is usually seconds, not minutes.
        for (int i = 0; i < 60; i++)
        {
            GenStatusToolResult status = await _genService.StatusAsync(submitted.TaskId, cancellationToken)
                .ConfigureAwait(false);
            if (status.State == "succeeded")
            {
                break;
            }

            if (status.State == "failed")
            {
                throw new InvalidOperationException($"Meshy remesh failed: {status.Message}");
            }

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }

        string? folder = Path.GetDirectoryName(assetPath.Replace('\\', '/'));
        string? baseName = Path.GetFileNameWithoutExtension(assetPath);
        await _genService.FetchAsync(
            submitted.TaskId, folder ?? "Assets/GenArt", baseName, overwrite: true,
            intent ?? $"remesh {assetPath} to ≤{maxTriangles} tris",
            idempotencyKey + "-fetch", cancellationToken).ConfigureAwait(false);
        return submitted.TaskId;
    }

    private static string Format(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string NormalizePrefabPath(string destinationPath)
    {
        string trimmed = destinationPath.Replace('\\', '/').Trim('/');
        if (!trimmed.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += ".prefab";
        }

        return trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : "Assets/" + trimmed;
    }

    private static string? ReadGuid(string metaPath)
    {
        try
        {
            EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
            return meta.TryGet("guid", out EchoObject? g) && g!.TagType == EchoType.String ? g.StringValue : null;
        }
        catch
        {
            return null;
        }
    }
}
