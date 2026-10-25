using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B4-cont) — Maps a tool name + JSON args to the matching Host service call, for use by
/// <see cref="WorkflowRunner"/>. This is the same idea as XMcp's XMcpToolDispatcher (a name->handler
/// switch) but typed. Covers the tools workflows typically chain (build/code/scene/asset); unknown
/// tools throw so the step fails cleanly.
/// </summary>
internal sealed class HostToolDispatcher : WorkflowToolDispatcher
{
    private readonly IServiceProvider _services;
    public HostToolDispatcher(IServiceProvider services) => _services = services;

    public async Task<object> InvokeAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        var project = _services.GetRequiredService<ProjectDescriptor>();

        switch (tool)
        {
            case "build_project":
                return await BuildRunner.BuildAsync(
                    RequireString(arguments, "editorExe"),
                    project.ProjectRoot,
                    RequireString(arguments, "outputDir"),
                    GetString(arguments, "buildProfile"),
                    GetString(arguments, "platform"),
                    cancellationToken);

            case "run_headless":
                return await BuildRunner.RunHeadlessAsync(
                    RequireString(arguments, "playerExe"),
                    GetString(arguments, "scene"),
                    GetInt(arguments, "maxFrames", 60),
                    GetInt(arguments, "maxSeconds", 0),
                    cancellationToken);

            case "code_symbols":
                return await _services.GetRequiredService<CodeIndexService>().FindSymbolsAsync(
                    project.SolutionPath ?? throw new FileNotFoundException("No solution file at project root."),
                    RequireString(arguments, "query"),
                    GetInt(arguments, "offset", 0),
                    GetInt(arguments, "limit", 100),
                    cancellationToken);

            case "scene_hierarchy":
                return SceneReader.ReadHierarchy(project.ResolveAssetPath(RequireString(arguments, "scenePath")),
                    maxObjects: GetInt(arguments, "maxObjects", 500),
                    tagLayer: TagLayerResolver.Load(project.ProjectRoot));

            case "prefab_hierarchy":
                // ★ P2-8 — prefab read path for workflows. Same shape as scene_hierarchy.
                return SceneReader.ReadPrefabHierarchy(project.ResolveAssetPath(RequireString(arguments, "prefabPath")),
                    maxObjects: GetInt(arguments, "maxObjects", 500),
                    tagLayer: TagLayerResolver.Load(project.ProjectRoot));

            case "prefab_object_patch":
            {
                // ★ P2-8 — prefab write path for workflows. Same shape as scene_object_patch but
                // recursive-search (prefab GOs nest via Children, not the scene's flat array).
                var writer = _services.GetRequiredService<SceneWriter>();
                var patches = ParsePatches(arguments);
                return await writer.PatchPrefabAsync(
                    project.ResolveAssetPath(RequireString(arguments, "prefabPath")),
                    RequireString(arguments, "prefabPath"),
                    RequireString(arguments, "objectIdentifier"),
                    patches,
                    XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.prefab-object-patch",
                        GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                    GetBool(arguments, "dryRun", false),
                    GetString(arguments, "expectedBeforeHash"),
                    cancellationToken);
            }

            case "scene_list":
                return SceneReader.ListScenes(project.AssetsRoot(), GetString(arguments, "query"),
                    offset: GetInt(arguments, "offset", 0),
                    limit: GetInt(arguments, "limit", 100));

            case "asset_search":
                return AssetReader.Search(project.AssetsRoot(), GetString(arguments, "query"),
                    GetString(arguments, "extension"), GetInt(arguments, "limit", 200));

            case "asset_inspect":
                return AssetReader.Inspect(project.ResolveAssetPath(RequireString(arguments, "assetPath")),
                    RequireString(arguments, "assetPath"));

            case "asset_subassets":
                // ★ Gen-P0.5 — lets a workflow read the deterministic mesh/material GUIDs of a
                // just-imported model and feed them into scene_component_add/patch steps.
                return SubAssetResolver.Resolve(project.ResolveAssetPath(RequireString(arguments, "assetPath")),
                    RequireString(arguments, "assetPath"));

            case "asset_mesh_stats":
                return _services.GetRequiredService<PlayablePropService>().MeshStats(
                    RequireString(arguments, "assetPath"),
                    GetNullableInt(arguments, "maxTriangles"));

            case "scene_collider_add":
                return await _services.GetRequiredService<PlayablePropService>().AddColliderAsync(
                    RequireString(arguments, "scenePath"),
                    RequireString(arguments, "objectIdentifier"),
                    RequireString(arguments, "modelAssetPath"),
                    GetString(arguments, "shape") ?? "box",
                    GetBool(arguments, "convex", false),
                    RequireString(arguments, "idempotencyKey"),
                    GetString(arguments, "intent"),
                    GetBool(arguments, "dryRun", false),
                    GetString(arguments, "expectedBeforeHash"),
                    cancellationToken);

            case "prefab_create":
                return await _services.GetRequiredService<PlayablePropService>().CreatePrefabAsync(
                    RequireString(arguments, "scenePath"),
                    RequireString(arguments, "objectIdentifier"),
                    RequireString(arguments, "destinationPath"),
                    RequireString(arguments, "idempotencyKey"),
                    GetString(arguments, "intent"),
                    GetBool(arguments, "overwrite", false),
                    GetBool(arguments, "dryRun", false),
                    cancellationToken);

            case "gen_candidates":
                return GenCandidateTools.List(
                    _services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                    GetString(arguments, "kind"),
                    GetString(arguments, "query"),
                    GetBool(arguments, "importedOnly", true),
                    GetBool(arguments, "includeDiscarded", false));

            case "gen_candidate_choose":
                return GenCandidateTools.Choose(
                    _services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                    RequireString(arguments, "taskId"));

            case "gen_candidate_discard":
                return GenCandidateTools.Discard(
                    _services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                    RequireString(arguments, "taskId"));

            case "style_anchor_list":
                return StyleAnchorTools.List(
                    _services.GetRequiredService<ProjectDescriptor>().ProjectRoot);

            case "style_anchor_upsert":
                return StyleAnchorTools.Upsert(
                    _services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                    RequireString(arguments, "id"),
                    GetString(arguments, "loraModelId"),
                    GetString(arguments, "palette"),
                    GetString(arguments, "negativePrompt"),
                    GetString(arguments, "referenceImageGuids"));

            case "sprite_slice":
                return await _services.GetRequiredService<SpriteWireService>().SliceAsync(
                    RequireString(arguments, "assetPath"),
                    GetInt(arguments, "columns", 4),
                    GetInt(arguments, "rows", 1),
                    GetInt(arguments, "cellWidth", 0),
                    GetInt(arguments, "cellHeight", 0),
                    GetOptFloat(arguments, "pixelsPerUnit") ?? 100f,
                    RequireString(arguments, "idempotencyKey"),
                    GetString(arguments, "intent"),
                    GetBool(arguments, "dryRun", false),
                    cancellationToken);

            case "sprite_wire":
                return await _services.GetRequiredService<SpriteWireService>().WireAsync(
                    RequireString(arguments, "scenePath"),
                    RequireString(arguments, "objectIdentifier"),
                    RequireString(arguments, "assetPath"),
                    GetInt(arguments, "columns", 4),
                    GetInt(arguments, "rows", 1),
                    GetInt(arguments, "cellWidth", 0),
                    GetInt(arguments, "cellHeight", 0),
                    GetOptFloat(arguments, "pixelsPerUnit") ?? 100f,
                    GetOptFloat(arguments, "framesPerSecond") ?? 12f,
                    RequireString(arguments, "idempotencyKey"),
                    GetString(arguments, "intent"),
                    GetBool(arguments, "dryRun", false),
                    cancellationToken);

            case "character_wire":
                return await _services.GetRequiredService<CharacterWireService>().WireAsync(
                    RequireString(arguments, "scenePath"),
                    RequireString(arguments, "objectIdentifier"),
                    RequireString(arguments, "modelAssetPath"),
                    GetString(arguments, "controllerPath"),
                    RequireString(arguments, "idempotencyKey"),
                    GetString(arguments, "intent"),
                    GetBool(arguments, "dryRun", false),
                    cancellationToken);

            case "gen_ensure_budget":
                return await _services.GetRequiredService<PlayablePropService>().EnsureBudgetAsync(
                    RequireString(arguments, "assetPath"),
                    GetInt(arguments, "maxTriangles", 10000),
                    GetString(arguments, "taskId"),
                    RequireString(arguments, "idempotencyKey"),
                    GetString(arguments, "intent"),
                    GetBool(arguments, "dryRun", false),
                    cancellationToken);

            // ★ SpineGen pipeline — one-shot character workflows (2026-08-27 spec).
            case "three_validate":
                return ThreeAuthoringTools.Validate(
                    _services,
                    RequireString(arguments, "sourcePath"));

            case "three_preview":
                return ThreeAuthoringTools.RunHost(
                    _services,
                    new XEngine.Ai.Protocol.ThreeAuthoringRequest(
                        XEngine.Ai.Protocol.ThreeAuthoringProtocol.Version,
                        "wf-" + Guid.NewGuid().ToString("N")[..12],
                        "Preview",
                        RequireString(arguments, "sourcePath"),
                        null,
                        null,
                        arguments.TryGetProperty("time", out JsonElement t) && t.TryGetDouble(out double sec) ? sec : 0,
                        GetNullableInt(arguments, "width") ?? 512,
                        GetNullableInt(arguments, "height") ?? 512,
                        GetNullableInt(arguments, "timeoutMs") ?? XEngine.Ai.Protocol.ThreeAuthoringProtocol.DefaultTimeoutMs,
                        GetString(arguments, "intent"),
                        null));

            case "three_export":
                return ThreeAuthoringTools.ExportCoreAsync(
                    _services,
                    RequireString(arguments, "sourcePath"),
                    RequireString(arguments, "outputDirectory"),
                    RequireString(arguments, "idempotencyKey"),
                    GetString(arguments, "intent"),
                    arguments.TryGetProperty("time", out JsonElement t2) && t2.TryGetDouble(out double sec2) ? sec2 : 0,
                    GetNullableInt(arguments, "timeoutMs") ?? XEngine.Ai.Protocol.ThreeAuthoringProtocol.DefaultTimeoutMs,
                    GetBool(arguments, "dryRun", false),
                    cancellationToken);

            case "spinegen_slice":
                return _services.GetRequiredService<SpineGenSliceService>().Slice(
                    RequireString(arguments, "image"),
                    GetNullableInt(arguments, "alphaThreshold"),
                    GetNullableInt(arguments, "minAreaPixels"),
                    GetNullableInt(arguments, "cropPadding"),
                    GetNullableInt(arguments, "packingPadding"),
                    GetString(arguments, "split"),
                    GetNullableInt(arguments, "markerMinDepth"),
                    GetNullableInt(arguments, "markerSpacing"),
                    GetBool(arguments, "dryRun", false), GetString(arguments, "recipe"));

            case "spinegen_fill":
                return await _services.GetRequiredService<SpineGenFillService>().FillAsync(
                    RequireString(arguments, "slice"),
                    GetString(arguments, "provider"),
                    cancellationToken).ConfigureAwait(false);

            case "spinegen_rig":
                return _services.GetRequiredService<SpineGenRigService>().Rig(
                    RequireString(arguments, "slice"));

            case "spinegen_skin":
                return _services.GetRequiredService<SpineGenSkinService>().Skin(
                    RequireString(arguments, "slice"),
                    GetNullableInt(arguments, "simplifyEpsilonX100"),
                    GetNullableInt(arguments, "maxHullVertices"),
                    GetNullableInt(arguments, "minMeshPixels"));

            case "spinegen_animate":
                return await _services.GetRequiredService<SpineGenAnimateService>().AnimateAsync(
                    RequireString(arguments, "slice"),
                    GetString(arguments, "motion"),
                    GetString(arguments, "prompt"),
                    GetString(arguments, "provider"),
                    GetString(arguments, "model"),
                    cancellationToken, GetString(arguments, "plan")).ConfigureAwait(false);

            // ★ Gen-P1.2 — the text-to-asset workflows chain gen_submit → gen_status → gen_fetch
            // → scene wiring inside one declarative DAG.
            case "gen_submit":
                return await _services.GetRequiredService<GenService>().SubmitAsync(
                    RequireString(arguments, "provider"),
                    RequireString(arguments, "kind"),
                    RequireString(arguments, "prompt"),
                    GetString(arguments, "model"),
                    GetNullableInt(arguments, "seed"),
                    GetString(arguments, "styleAnchor"),
                    GetString(arguments, "extra"),
                    GetBool(arguments, "dryRun", false),
                    cancellationToken);

            case "gen_status":
                return await _services.GetRequiredService<GenService>().StatusAsync(
                    RequireString(arguments, "taskId"), cancellationToken);

            case "gen_fetch":
                return await _services.GetRequiredService<GenService>().FetchAsync(
                    RequireString(arguments, "taskId"),
                    GetString(arguments, "destinationFolder") ?? "Assets/GenArt",
                    GetString(arguments, "baseName"),
                    GetBool(arguments, "overwrite", false),
                    GetString(arguments, "intent"),
                    RequireString(arguments, "idempotencyKey"),
                    cancellationToken);

            case "asset_import":
                // ★ Gen-P0.4 — the generated-asset pipeline chains download → asset_import →
                // scene/material wiring inside one declarative workflow; without this case the DAG
                // broke at the import step.
                return await _services.GetRequiredService<AssetWriter>().ImportAsync(
                    RequireString(arguments, "sourcePath"),
                    project.ResolveAssetPath(RequireString(arguments, "destinationPath")),
                    RequireString(arguments, "destinationPath"),
                    XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.asset-import",
                        GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                    GetBool(arguments, "overwrite", false),
                    GetNullableInt(arguments, "animationType"),
                    cancellationToken);

            case "material_patch":
            {
                // ★ Gen-P0.4 — wiring a generated texture GUID into a .mat is a standard step of the
                // text-to-prop workflow (…/_textures/_MainTex/AssetID).
                var writer = _services.GetRequiredService<SceneWriter>();
                return await writer.PatchMaterialAsync(
                    project.ResolveAssetPath(RequireString(arguments, "materialPath")),
                    RequireString(arguments, "materialPath"),
                    ParsePatches(arguments),
                    XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.material-patch",
                        GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                    GetBool(arguments, "dryRun", false),
                    GetString(arguments, "expectedBeforeHash"),
                    cancellationToken);
            }

            case "scene_component_patch":
            {
                // ★ Gen-P0.4 — after scene_component_add creates a sparse MeshRenderer, the workflow
                // patches Mesh/AssetID + Materials on THAT component without knowing its list index.
                var writer = _services.GetRequiredService<SceneWriter>();
                return await writer.PatchComponentAsync(
                    project.ResolveAssetPath(RequireString(arguments, "scenePath")),
                    RequireString(arguments, "scenePath"),
                    RequireString(arguments, "objectIdentifier"),
                    GetString(arguments, "componentIdentifier"),
                    GetString(arguments, "componentType"),
                    ParsePatches(arguments),
                    GetNullableBool(arguments, "enable"),
                    XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.scene-component-patch",
                        GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                    GetBool(arguments, "dryRun", false),
                    GetString(arguments, "expectedBeforeHash"),
                    recursive: false,
                    cancellationToken);
            }

            case "journal_query":
                return JournalReader.Query(project.ProjectRoot, new JournalReadQuery(
                    GetString(arguments, "targetAsset"), null, null,
                    GetString(arguments, "byAuthor"), GetString(arguments, "intentContains"),
                    GetInt(arguments, "offset", 0), GetInt(arguments, "limit", 100)));

            case "scene_object_patch":
                {
                    var writer = _services.GetRequiredService<SceneWriter>();
                    var patches = ParsePatches(arguments);

                    return await writer.PatchAsync(
                        project.ResolveAssetPath(RequireString(arguments, "scenePath")),
                        RequireString(arguments, "scenePath"),
                        RequireString(arguments, "objectIdentifier"),
                        patches,
                        XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.scene-object-patch",
                            GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                        GetBool(arguments, "dryRun", false),
                        GetString(arguments, "expectedBeforeHash"),
                        cancellationToken);
                }

            case "scene_object_create":
                {
                    var writer = _services.GetRequiredService<SceneWriter>();
                    return await writer.CreateAsync(
                        project.ResolveAssetPath(RequireString(arguments, "scenePath")),
                        RequireString(arguments, "scenePath"),
                        RequireString(arguments, "name"),
                        GetString(arguments, "parentIdentifier"),
                        XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.scene-object-create",
                            GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                        GetBool(arguments, "dryRun", false),
                        cancellationToken);
                }

            case "scene_object_delete":
                {
                    var writer = _services.GetRequiredService<SceneWriter>();
                    return await writer.DeleteAsync(
                        project.ResolveAssetPath(RequireString(arguments, "scenePath")),
                        RequireString(arguments, "scenePath"),
                        RequireString(arguments, "objectIdentifier"),
                        XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.scene-object-delete",
                            GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                        GetBool(arguments, "dryRun", false),
                        cancellationToken);
                }

            case "scene_component_add":
                {
                    var writer = _services.GetRequiredService<SceneWriter>();
                    var patches = ParsePatches(arguments);
                    return await writer.AddComponentAsync(
                        project.ResolveAssetPath(RequireString(arguments, "scenePath")),
                        RequireString(arguments, "scenePath"),
                        RequireString(arguments, "objectIdentifier"),
                        RequireString(arguments, "componentType"),
                        patches,
                        XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.scene-component-add",
                            GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                        GetBool(arguments, "dryRun", false),
                        GetString(arguments, "expectedBeforeHash"),
                        cancellationToken);
                }

            case "animator_component_add":
                {
                    var writer = _services.GetRequiredService<SceneWriter>();
                    return await AnimatorComponentAdd.RunAsync(
                        project, writer,
                        RequireString(arguments, "scenePath"),
                        RequireString(arguments, "objectIdentifier"),
                        XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.animator-component-add",
                            GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                        GetBool(arguments, "createController", false),
                        GetString(arguments, "clipGuid"),
                        GetBool(arguments, "dryRun", false),
                        GetString(arguments, "expectedBeforeHash"),
                        cancellationToken);
                }

            case "scene_component_remove":
                {
                    var writer = _services.GetRequiredService<SceneWriter>();
                    return await writer.RemoveComponentAsync(
                        project.ResolveAssetPath(RequireString(arguments, "scenePath")),
                        RequireString(arguments, "scenePath"),
                        RequireString(arguments, "objectIdentifier"),
                        GetString(arguments, "componentIdentifier"),
                        GetString(arguments, "componentType"),
                        XEngine.Data.Provenance.ChangeProvenance.ForTool("workflow.scene-component-remove",
                            GetString(arguments, "intent"), RequireString(arguments, "idempotencyKey")),
                        GetBool(arguments, "dryRun", false),
                        GetString(arguments, "expectedBeforeHash"),
                        cancellationToken);
                }

            default:
                if (tool.StartsWith("animator_", StringComparison.Ordinal)
                    || tool.StartsWith("clip_", StringComparison.Ordinal)
                    || tool.StartsWith("blendtree_", StringComparison.Ordinal)
                    || tool.StartsWith("overridecontroller_", StringComparison.Ordinal)
                    || tool.StartsWith("avatarmask_", StringComparison.Ordinal))
                {
                    return await InvokeAnimation(tool, arguments, cancellationToken).ConfigureAwait(false);
                }
                throw new KeyNotFoundException($"workflow tool not implemented in HostToolDispatcher: {tool}");
        }
    }

    private static string RequireString(JsonElement o, string name)
        => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : throw new ArgumentException($"workflow arg '{name}' missing or not a string.");

    private static string? GetString(JsonElement o, string name)
        => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement o, string name, int def)
        => o.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : def;

    private static bool GetBool(JsonElement o, string name, bool def)
        => o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.False ? false
         : o.TryGetProperty(name, out JsonElement v2) && v2.ValueKind == JsonValueKind.True ? true : def;

    private static int? GetNullableInt(JsonElement o, string name)
        => o.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : null;

    private static bool? GetNullableBool(JsonElement o, string name)
        => o.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True : null;

    /// <summary>Parse the <c>patches</c> (or <c>initialFields</c>) array from a workflow step's args
    /// into <see cref="SceneFieldPatch"/> records. Accepts either key name so scene_object_patch
    /// (patches) and scene_component_add (initialFields) share one path.</summary>
    private static List<SceneFieldPatch> ParsePatches(JsonElement arguments)
    {
        var patches = new List<SceneFieldPatch>();
        foreach (string key in new[] { "patches", "initialFields" })
        {
            if (!arguments.TryGetProperty(key, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement p in arr.EnumerateArray())
            {
                patches.Add(new SceneFieldPatch(
                    p.GetProperty("path").GetString() ?? "",
                    p.GetProperty("value").GetString() ?? ""));
            }
            break; // only one of the keys is expected per call
        }
        return patches;
    }

    private static string[] ParseNames(JsonElement arguments)
    {
        if (arguments.TryGetProperty("names", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (JsonElement n in arr.EnumerateArray())
            {
                if (n.ValueKind == JsonValueKind.String)
                    list.Add(n.GetString() ?? "");
            }
            return list.ToArray();
        }
        string? csv = GetString(arguments, "names");
        if (string.IsNullOrEmpty(csv))
            throw new ArgumentException("workflow arg 'names' missing.");
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<object> InvokeAnimation(string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        string? intent = GetString(arguments, "intent");
        string? hash = GetString(arguments, "expectedBeforeHash");
        bool dry = GetBool(arguments, "dryRun", false);
        string key = tool.Contains("inspect", StringComparison.Ordinal)
            || tool.Contains("validate", StringComparison.Ordinal)
            ? ""
            : RequireString(arguments, "idempotencyKey");

        return tool switch
        {
            "animator_inspect" => AnimationTools.InspectAnimator(_services, RequireString(arguments, "controllerPath")),
            "animator_validate" => AnimationTools.ValidateAnimator(_services, RequireString(arguments, "controllerPath")),
            "clip_inspect" => AnimationTools.InspectClip(_services, RequireString(arguments, "clipPath"),
                GetString(arguments, "scenePath"), GetString(arguments, "objectIdentifier")),
            "blendtree_inspect" => AnimationTools.InspectBlendTree(_services, RequireString(arguments, "assetPath"),
                GetString(arguments, "stateName")),
            "animator_state_create" => await AnimationTools.CreateState(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"), key, intent,
                GetString(arguments, "layerName"), GetString(arguments, "clipGuid"), hash, dry, cancellationToken),
            "animator_state_patch" => await AnimationTools.PatchState(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"),
                ParsePatches(arguments).ToArray(), key, intent, GetString(arguments, "layerName"), hash, dry, cancellationToken),
            "animator_state_delete" => await AnimationTools.DeleteState(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"), key, intent,
                GetString(arguments, "layerName"), hash, dry, cancellationToken),
            "animator_transition_create" => await AnimationTools.CreateTransition(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "fromState"),
                RequireString(arguments, "toState"), key, GetString(arguments, "name"), intent,
                GetString(arguments, "layerName"), hash, dry, cancellationToken),
            "animator_transition_patch" => await AnimationTools.PatchTransition(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"),
                ParsePatches(arguments).ToArray(), key, intent, GetString(arguments, "layerName"), hash, dry, cancellationToken),
            "animator_transition_delete" => await AnimationTools.DeleteTransition(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"), key, intent,
                GetString(arguments, "layerName"), hash, dry, cancellationToken),
            "animator_transition_reorder" => await AnimationTools.ReorderTransitions(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "stateName"),
                ParseNames(arguments), key, intent, GetString(arguments, "layerName"), hash, dry, cancellationToken),
            "animator_parameter_add" => await AnimationTools.AddParameter(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"), key,
                GetString(arguments, "type") ?? "Float", intent, hash, dry, cancellationToken),
            "animator_parameter_remove" => await AnimationTools.RemoveParameter(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"), key, intent, hash, dry, cancellationToken),
            "animator_layer_add" => await AnimationTools.AddLayer(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"), key, intent, hash, dry, cancellationToken),
            "animator_layer_patch" => await AnimationTools.PatchLayer(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"),
                ParsePatches(arguments).ToArray(), key, intent, hash, dry, cancellationToken),
            "animator_statemachine_create" => await AnimationStateMachineTools.CreateStateMachine(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"), key, intent,
                GetString(arguments, "layerName"), GetString(arguments, "parentPath"),
                GetOptFloat(arguments, "posX") ?? 200f, GetOptFloat(arguments, "posY") ?? 80f,
                hash, dry, cancellationToken),
            "animator_statemachine_delete" => await AnimationStateMachineTools.DeleteStateMachine(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "name"), key, intent,
                GetString(arguments, "layerName"), GetString(arguments, "parentPath"), hash, dry, cancellationToken),
            "animator_statemachine_move" => await AnimationStateMachineTools.MoveStateIntoMachine(_services,
                RequireString(arguments, "controllerPath"), RequireString(arguments, "stateName"),
                RequireString(arguments, "targetStateMachine"), key, intent,
                GetString(arguments, "layerName"), GetString(arguments, "sourcePath"), hash, dry, cancellationToken),
            "clip_retarget" => await AnimationTools.RetargetClip(_services,
                RequireString(arguments, "clipPath"), key,
                GetString(arguments, "likeClipPath"), intent, hash, dry, cancellationToken),
            "clip_patch" => await AnimationTools.PatchClip(_services,
                RequireString(arguments, "clipPath"), ParsePatches(arguments).ToArray(), key,
                intent, hash, dry, cancellationToken),
            "clip_curve_patch" => await AnimationTools.PatchCurve(_services,
                RequireString(arguments, "clipPath"), RequireString(arguments, "bone"),
                RequireString(arguments, "channel"), RequireString(arguments, "op"),
                GetFloat(arguments, "time"), key, GetOptFloat(arguments, "value"),
                GetString(arguments, "typeName"), intent, hash, dry, cancellationToken),
            "clip_event_add" => await AnimationTools.AddClipEvent(_services,
                RequireString(arguments, "clipPath"), GetFloat(arguments, "time"),
                RequireString(arguments, "functionName"), key,
                GetOptFloat(arguments, "floatParam") ?? 0f, GetInt(arguments, "intParam", 0),
                GetString(arguments, "stringParam"), GetString(arguments, "objectGuid"),
                intent, hash, dry, cancellationToken),
            "clip_event_patch" => await AnimationTools.PatchClipEvent(_services,
                RequireString(arguments, "clipPath"), GetInt(arguments, "index", 0),
                ParsePatches(arguments).ToArray(), key, intent, hash, dry, cancellationToken),
            "clip_event_remove" => await AnimationTools.RemoveClipEvent(_services,
                RequireString(arguments, "clipPath"), GetInt(arguments, "index", 0),
                key, intent, hash, dry, cancellationToken),
            "blendtree_patch" => await AnimationTools.PatchBlendTree(_services,
                RequireString(arguments, "assetPath"), ParsePatches(arguments).ToArray(), key,
                GetString(arguments, "stateName"), intent, hash, dry, cancellationToken),
            "overridecontroller_inspect" => AnimationTools.InspectOverrideController(
                _services, RequireString(arguments, "assetPath")),
            "overridecontroller_patch" => await AnimationTools.PatchOverrideController(_services,
                RequireString(arguments, "assetPath"), ParsePatches(arguments).ToArray(), key,
                intent, hash, dry, cancellationToken),
            "avatarmask_inspect" => AnimationTools.InspectAvatarMask(
                _services, RequireString(arguments, "assetPath")),
            "avatarmask_patch" => await AnimationTools.PatchAvatarMask(_services,
                RequireString(arguments, "assetPath"), ParsePatches(arguments).ToArray(), key,
                intent, hash, dry, cancellationToken),
            "animator_component_add" => throw new InvalidOperationException("animator_component_add is handled above."),
            _ => throw new KeyNotFoundException($"workflow animation tool not implemented: {tool}"),
        };
    }

    private static float GetFloat(JsonElement o, string name)
        => o.TryGetProperty(name, out JsonElement v) && v.TryGetSingle(out float f) ? f
         : o.TryGetProperty(name, out JsonElement v2) && v2.TryGetDouble(out double d) ? (float)d
         : throw new ArgumentException($"workflow arg '{name}' missing or not a number.");

    private static float? GetOptFloat(JsonElement o, string name)
        => o.TryGetProperty(name, out JsonElement v) && v.TryGetSingle(out float f) ? f
         : o.TryGetProperty(name, out JsonElement v2) && v2.TryGetDouble(out double d) ? (float)d
         : null;
}
