using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;
using XEngine.Runtime.Rendering;
using XEngine.VFX;

namespace XEngine.Ai.Host;

/// <summary>
/// The MCP tool surface for XEngine (Layer 1). Each tool is a static method decorated with
/// <see cref="McpServerToolAttribute"/>; the SDK reflects over the assembly
/// (<c>WithToolsFromAssembly()</c> in Program) and auto-generates the JSON input schema from the
/// C# parameter signatures + <see cref="DescriptionAttribute"/> — no hand-written schema.
/// <c>UseStructuredContent</c> makes each return a typed record serialized to structured JSON.
/// Real work is delegated to DI singletons (XMcp.XMcpTools pattern).
///
/// This is the B0 MVP set. scene/asset/code/build/journal/runtime tools are appended here as
/// their stages ship.
/// </summary>
[McpServerToolType]
public static class AiTools
{
    [McpServerTool(Name = "xengine_health", UseStructuredContent = true)]
    [Description("Report the Host, target project and engine-instance connection state.")]
    public static HostHealthSnapshot Health(IServiceProvider services)
        => services.GetRequiredService<HealthService>().GetSnapshot();

    [McpServerTool(Name = "xengine_capabilities", UseStructuredContent = true)]
    [Description("List all tools, capability packs, risk levels, engine dependencies and response limits.")]
    public static CapabilitySnapshot Capabilities(IServiceProvider services)
        => services.GetRequiredService<CapabilityCatalog>().GetSnapshot();

    [McpServerTool(Name = "project_inspect", UseStructuredContent = true)]
    [Description("Read the target project's stable identity, engine version and solution path.")]
    public static ProjectDescriptor InspectProject(IServiceProvider services)
        => services.GetRequiredService<ProjectDescriptor>();

    [McpServerTool(Name = "project_roots", UseStructuredContent = true)]
    [Description("Enumerate a workspace folder's children (the server-side equivalent of MCP Roots, which is natively a client capability). With no folder, lists the project root's top-level entries (Assets/, ProjectSettings/, .xengine/, ...). Lets the AI discover where things live before calling asset_search/scene_list. Capped at maxEntries (default 500, hard limit 2000) with HasMore when truncated.")]
    public static WorkspaceListing ListProjectRoots(
        IServiceProvider services,
        [Description("Optional project-relative folder to list (e.g. 'Assets', 'Assets/Textures'). Omit to list the project root.")] string? folder = null,
        [Description("Max entries to return (1-2000). Default 500.")] int maxEntries = 500)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        return ListWorkspace(project.ProjectRoot, folder, maxEntries);
    }

    internal static WorkspaceListing ListWorkspace(string projectRoot, string? folder, int maxEntries)
    {
        string target = string.IsNullOrEmpty(folder)
            ? projectRoot
            : Path.Combine(projectRoot, folder);
        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"Workspace folder not found: {folder ?? "(project root)"}");
        }

        int cap = Math.Clamp(maxEntries, 1, ProtocolConstants.Limits.MaxObjectsPerHierarchy);
        var entries = new List<WorkspaceFolder>(cap);
        foreach (string path in Directory.EnumerateFileSystemEntries(target).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            bool isDir = Directory.Exists(path);
            string name = Path.GetFileName(path);
            string relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
            entries.Add(new WorkspaceFolder(name, relative, path, isDir));
            if (entries.Count >= cap) break;
        }
        // HasMore is approximate: if we hit the cap exactly, assume more exist.
        bool hasMore = entries.Count >= cap && Directory.EnumerateFileSystemEntries(target).Skip(cap).Any();
        return new WorkspaceListing(folder ?? ".", entries, hasMore);
    }

    [McpServerTool(Name = "xengine_doctor", UseStructuredContent = true)]
    [Description("Diagnose .NET, the XEngine project, the solution and the AI tooling protocol.")]
    public static Task<DoctorReport> Doctor(
        IServiceProvider services,
        CancellationToken cancellationToken)
        => services.GetRequiredService<DoctorService>().RunAsync(cancellationToken);

    // ---- B1 scene-read tools (offline, no running engine needed) ----

    [McpServerTool(Name = "scene_list", UseStructuredContent = true)]
    [Description("List all .scene files in the project's Assets directory, optionally filtered by name. Paged (offset+limit, returns total).")]
    public static SceneFileList ListScenes(
        IServiceProvider services,
        [Description("Optional case-insensitive substring filter on the scene path.")] string? query = null,
        [Description("Page offset, >= 0.")] int offset = 0,
        [Description("Page size, 1-500.")] int limit = 100)
        => SceneReader.ListScenes(
            services.GetRequiredService<ProjectDescriptor>().AssetsRoot(),
            query, offset, Math.Clamp(limit, 1, 500));

    [McpServerTool(Name = "scene_hierarchy", UseStructuredContent = true)]
    [Description("Read a scene's GameObject hierarchy offline (no engine loaded). Returns root objects, transforms, components. Tag/Layer are resolved to names from ProjectSettings/Tags & Layers.yaml (TagIndex/LayerIndex carry the raw indices for patching). Response is capped at maxObjects root nodes (HasMore=true when truncated); narrow the query or filter by parent if you need a specific subtree.")]
    public static SceneHierarchy GetSceneHierarchy(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Max root objects to return (1-2000). Default 500. TotalObjects still reports the true count when truncated.")] int maxObjects = 500)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(scenePath);
        return SceneReader.ReadHierarchy(absolute, maxObjects: maxObjects, tagLayer: TagLayerResolver.Load(project.ProjectRoot));
    }

    // ---- prefab tools (offline, .prefab = single GO tree) ----

    [McpServerTool(Name = "prefab_hierarchy", UseStructuredContent = true)]
    [Description("Read a .prefab's GameObject hierarchy offline. A prefab is a single root GO with nested children. Tag/Layer resolved to names. Response is capped at maxObjects root nodes (HasMore=true when truncated).")]
    public static SceneHierarchy GetPrefabHierarchy(
        IServiceProvider services,
        [Description("Project-relative prefab path (e.g. Prefabs/Player.prefab) or absolute.")] string prefabPath,
        [Description("Max root objects to return (1-2000). Default 500.")] int maxObjects = 500)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(prefabPath);
        return SceneReader.ReadPrefabHierarchy(absolute, maxObjects: maxObjects, tagLayer: TagLayerResolver.Load(project.ProjectRoot));
    }

    [McpServerTool(Name = "prefab_object_patch", UseStructuredContent = true)]
    [Description("Patch fields on a GameObject in a .prefab file (by Identifier, recursive search through children). Records a provenance-bearing change to the journal.")]
    public static Task<SceneWriteResult> PatchPrefabObject(
        IServiceProvider services,
        [Description("Project-relative prefab path (e.g. Prefabs/Player.prefab) or absolute.")] string prefabPath,
        [Description("Identifier of the GameObject to patch (searched recursively).")] string objectIdentifier,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from prefab_hierarchy. Rejects the write if the prefab changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchPrefabAsync(
            project.ResolveAssetPath(prefabPath), prefabPath, objectIdentifier, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.prefab-object-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    // ---- B1 asset-read tools (offline, read .meta sidecars) ----

    [McpServerTool(Name = "asset_search", UseStructuredContent = true)]
    [Description("List assets under the project's Assets directory with their GUID/importer (from .meta sidecars). Optional name/extension filter.")]
    public static AssetList SearchAssets(
        IServiceProvider services,
        [Description("Optional case-insensitive substring filter on the asset path.")] string? query = null,
        [Description("Optional extension filter (e.g. .png, .scene).")] string? extension = null,
        [Description("Max results, 1-500.")] int limit = 200)
        => AssetReader.Search(
            services.GetRequiredService<ProjectDescriptor>().AssetsRoot(),
            query, extension, Math.Clamp(limit, 1, 500));

    [McpServerTool(Name = "asset_inspect", UseStructuredContent = true)]
    [Description("Read a single asset's .meta detail: GUID, importer, importer version, settings.")]
    public static AssetDetail InspectAsset(
        IServiceProvider services,
        [Description("Project-relative asset path (e.g. Textures/Grass.png) or absolute.")] string assetPath)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(assetPath);
        return AssetReader.Inspect(absolute, assetPath);
    }

    // ---- Gen-P1.2 generation gateway tools (submit → status → fetch) ----

    [McpServerTool(Name = "gen_submit", UseStructuredContent = true)]
    [Description("Submit a generation request (text-to-3D/texture/skybox/sfx/motion) to a provider ('mock' is always available; real providers need an API key via {PROVIDER}_API_KEY or .xengine/secrets.json). Idempotent: an unchanged recipe reuses the existing task instead of billing again (Deduplicated=true). dryRun computes the input hash and routing without any provider call. Poll with gen_status; ingest with gen_fetch.")]
    public static Task<GenSubmitToolResult> GenSubmit(
        IServiceProvider services,
        [Description("Provider name: mock (offline) | meshy (kind=model3d: text/image-to-3d/remesh; kind=motion: rig/animate — extra keys mode/previewTaskId/inputTaskId/rigTaskId/actionId) | pixellab (kind=sprite|texture: pixel art — extra width/height/noBackground) | blockade (kind=skybox — extra styleId/fetchDepthMap) | elevenlabs (kind=sfx — extra durationSeconds/promptInfluence) | comfyui (local ComfyUI HTTP: kind=model3d|texture|sprite|skybox — extra preset=trellis2|hunyuan3d or workflow/workflowJson, promptNode, inputImageNode+inputImagePath, baseUrl) | openai (kind=texture|sprite: GPT Image 2 — extra size/quality/inputImagePath; OPENAI_API_KEY).")] string provider,
        [Description("Asset kind: model3d | motion | texture | sprite | skybox | sfx.")] string kind,
        [Description("The generation prompt.")] string prompt,
        [Description("Provider model/version id (e.g. 'meshy-5').")] string? model = null,
        [Description("Deterministic seed, when supported.")] int? seed = null,
        [Description("Project style anchor id injected into the recipe (see P3.2).")] string? styleAnchor = null,
        [Description("Provider-specific extras as a JSON object of strings, e.g. {\"topology\":\"quad\",\"polycount\":\"3000\"}.")] string? extra = null,
        [Description("If true, compute the recipe hash + validation without calling the provider.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<GenService>()
            .SubmitAsync(provider, kind, prompt, model, seed, styleAnchor, extra, dryRun, cancellationToken);

    [McpServerTool(Name = "style_anchor_list", UseStructuredContent = true)]
    [Description("List project style anchors from .xengine/style-anchors.json (LoRA, palette, negatives, reference image GUIDs). Pass an id to gen_submit --style-anchor to inject them.")]
    public static StyleAnchorListResult ListStyleAnchors(IServiceProvider services)
        => StyleAnchorTools.List(services.GetRequiredService<ProjectDescriptor>().ProjectRoot);

    [McpServerTool(Name = "style_anchor_upsert", UseStructuredContent = true)]
    [Description("Create or replace a project style anchor in .xengine/style-anchors.json. Subsequent gen_submit with this id injects LoRA/palette/negatives/refs into the recipe.")]
    public static StyleAnchorInfo UpsertStyleAnchor(
        IServiceProvider services,
        [Description("Stable anchor id (e.g. scenario-default).")] string id,
        [Description("Scenario / LoRA model id.")] string? loraModelId = null,
        [Description("Comma-separated hex palette, e.g. #2b2b2b,#e8d5a3.")] string? palette = null,
        [Description("Negative prompt appended as 'Negative: …'.")] string? negativePrompt = null,
        [Description("Comma-separated reference image asset GUIDs.")] string? referenceImageGuids = null)
        => StyleAnchorTools.Upsert(
            services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
            id, loraModelId, palette, negativePrompt, referenceImageGuids);

    [McpServerTool(Name = "gen_candidates", UseStructuredContent = true)]
    [Description("List imported generation candidates from .xengine/gen-tasks.json for the editor gallery. Filter by kind/query; discarded tasks are hidden unless includeDiscarded.")]
    public static GenCandidateListResult ListGenCandidates(
        IServiceProvider services,
        [Description("Optional kind filter: model3d | motion | texture | sprite | skybox | sfx.")] string? kind = null,
        [Description("Case-insensitive substring match on prompt, task id, or provider.")] string? query = null,
        [Description("If true (default), only tasks that gen_fetch imported.")] bool importedOnly = true,
        [Description("If true, include discarded gallery cards.")] bool includeDiscarded = false)
        => GenCandidateTools.List(
            services.GetRequiredService<ProjectDescriptor>().ProjectRoot, kind, query, importedOnly, includeDiscarded);

    [McpServerTool(Name = "gen_candidate_choose", UseStructuredContent = true)]
    [Description("Mark a generation task as the chosen gallery candidate (writes .xengine/gen-gallery.json). Does not delete other imports.")]
    public static GenCandidateInfo ChooseGenCandidate(
        IServiceProvider services,
        [Description("Task id from gen_submit / gen_candidates.")] string taskId)
        => GenCandidateTools.Choose(services.GetRequiredService<ProjectDescriptor>().ProjectRoot, taskId);

    [McpServerTool(Name = "gen_candidate_discard", UseStructuredContent = true)]
    [Description("Hide a generation task from the candidate gallery without deleting imported assets.")]
    public static GenCandidateInfo DiscardGenCandidate(
        IServiceProvider services,
        [Description("Task id from gen_submit / gen_candidates.")] string taskId)
        => GenCandidateTools.Discard(services.GetRequiredService<ProjectDescriptor>().ProjectRoot, taskId);

    [McpServerTool(Name = "gen_status", UseStructuredContent = true)]
    [Description("Poll a generation task's normalized status (pending/running/succeeded/failed + progress). Terminal states answer from the local task ledger without a provider round-trip.")]
    public static Task<GenStatusToolResult> GenStatus(
        IServiceProvider services,
        [Description("Task id returned by gen_submit.")] string taskId,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<GenService>().StatusAsync(taskId, cancellationToken);

    [McpServerTool(Name = "gen_fetch", UseStructuredContent = true)]
    [Description("Download a succeeded generation task's artifacts and import them into the project: journaled asset_import (GUID preserved when overwriting — the re-roll path), recipe embedded into .meta settings, and for models the derived sub-asset GUID table (meshes/materials/clips) ready for scene wiring.")]
    public static Task<GenFetchToolResult> GenFetch(
        IServiceProvider services,
        [Description("Task id returned by gen_submit.")] string taskId,
        [Description("Unique operation key for exactly-once semantics (e.g. a UUID).")] string idempotencyKey,
        [Description("Destination folder under Assets/ (default Assets/GenArt).")] string destinationFolder = "Assets/GenArt",
        [Description("File base name; defaults to a slug of the prompt.")] string? baseName = null,
        [Description("Replace existing files (GUID preserved so scene references survive — use for re-rolls).")] bool overwrite = false,
        [Description("Natural-language intent recorded in the journal.")] string? intent = null,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<GenService>()
            .FetchAsync(taskId, destinationFolder, baseName, overwrite, intent, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "asset_subassets", UseStructuredContent = true)]
    [Description("Derive the sub-assets (meshes, materials, animation clips, embedded textures) a model file (.gltf/.glb/.obj/.fbx) will produce when the editor imports it, with their deterministic GUIDs — offline, before any editor import. Use after asset_import to wire a model's meshes/materials into scenes (e.g. a MeshRenderer's Mesh AssetID) in the same headless session; the GUIDs match what the editor assigns later (SHA256 of parent GUID + stable name).")]
    public static SubAssetList SubAssets(
        IServiceProvider services,
        [Description("Project-relative model path (e.g. GenArt/sword.glb) or absolute.")] string assetPath)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(assetPath);
        return SubAssetResolver.Resolve(absolute, assetPath);
    }

    [McpServerTool(Name = "asset_mesh_stats", UseStructuredContent = true)]
    [Description("Offline mesh budget + AABB for a model file (.gltf/.glb/.obj/.fbx): per-mesh vertex/triangle counts, combined bounds, and a suggested BoxCollider size/center. Pass maxTriangles to set OverBudget. Same Clay replay as asset_subassets, so the numbers match the editor import.")]
    public static MeshStatsResult MeshStats(
        IServiceProvider services,
        [Description("Project-relative model path (e.g. Assets/GenArt/sword.glb) or absolute.")] string assetPath,
        [Description("Optional triangle budget. When set, OverBudget is true if TotalTriangles exceeds it.")] int? maxTriangles = null)
        => services.GetRequiredService<PlayablePropService>().MeshStats(assetPath, maxTriangles);

    [McpServerTool(Name = "scene_collider_add", UseStructuredContent = true)]
    [Description("Add a collider to a scene GameObject derived from a model file: shape=box uses the combined AABB as BoxCollider size/center; shape=mesh|convex adds a MeshCollider wired to the largest mesh sub-asset GUID. Journaled. Run after gen_ensure_budget (budget before wiring).")]
    public static Task<ColliderAddResult> AddCollider(
        IServiceProvider services,
        [Description("Project-relative .scene path.")] string scenePath,
        [Description("Target GameObject Identifier.")] string objectIdentifier,
        [Description("Project-relative model path used to derive AABB / mesh GUID.")] string modelAssetPath,
        [Description("Unique operation key for exactly-once semantics.")] string idempotencyKey,
        [Description("box (default) | mesh | convex.")] string shape = "box",
        [Description("When shape=mesh, set convex=true to build a convex hull.")] bool convex = false,
        [Description("Natural-language intent recorded in the journal.")] string? intent = null,
        [Description("If true, compute the collider fields without writing the scene.")] bool dryRun = false,
        [Description("Optional SHA-256 content hash from scene_hierarchy. Rejects the write if the scene changed.")] string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<PlayablePropService>().AddColliderAsync(
            scenePath, objectIdentifier, modelAssetPath, shape, convex, idempotencyKey, intent, dryRun,
            expectedBeforeHash, cancellationToken);

    [McpServerTool(Name = "prefab_create", UseStructuredContent = true)]
    [Description("Extract a GameObject (and its inline children/components) from a .scene into a standalone .prefab, minting a .meta GUID. Journaled. Use after the GO is wired (renderer + collider) to produce a drop-in playable prefab.")]
    public static Task<PrefabCreateResult> CreatePrefab(
        IServiceProvider services,
        [Description("Project-relative .scene path.")] string scenePath,
        [Description("Identifier of the GameObject to extract.")] string objectIdentifier,
        [Description("Destination .prefab path (e.g. Assets/Prefabs/Sword.prefab).")] string destinationPath,
        [Description("Unique operation key for exactly-once semantics.")] string idempotencyKey,
        [Description("Natural-language intent recorded in the journal.")] string? intent = null,
        [Description("Replace an existing prefab (GUID preserved).")] bool overwrite = false,
        [Description("If true, do not write the file.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<PlayablePropService>().CreatePrefabAsync(
            scenePath, objectIdentifier, destinationPath, idempotencyKey, intent, overwrite, dryRun, cancellationToken);

    [McpServerTool(Name = "gen_ensure_budget", UseStructuredContent = true)]
    [Description("Triangle-budget gate for an imported model. If TotalTriangles > maxTriangles, remesh: Meshy (pass the original gen taskId) calls mode=remesh then overwrite-fetches; mock/offline overwrites with a 1-triangle GLB. Parent GUID is preserved. Run BEFORE wiring MeshRenderer sub-asset GUIDs — remesh can change mesh names. dryRun reports WouldRemesh without writing.")]
    public static Task<BudgetEnsureResult> EnsureBudget(
        IServiceProvider services,
        [Description("Project-relative model path.")] string assetPath,
        [Description("Maximum allowed triangles.")] int maxTriangles,
        [Description("Unique operation key for exactly-once semantics.")] string idempotencyKey,
        [Description("Original gen_submit task id. Required for Meshy remesh; omit for the mock/offline decimate path.")] string? taskId = null,
        [Description("Natural-language intent recorded in the journal.")] string? intent = null,
        [Description("If true, report OverBudget/WouldRemesh without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<PlayablePropService>().EnsureBudgetAsync(
            assetPath, maxTriangles, taskId, idempotencyKey, intent, dryRun, cancellationToken);

    [McpServerTool(Name = "character_wire", UseStructuredContent = true)]
    [Description("Build a playable Animator from a model's AnimationClip sub-assets: mint a .controller, one looping state per clip, cyclic exit-time transitions, Speed parameter, and bind Animator on the GameObject. Use after gen_fetch of kind=motion (same-skeleton clips — no engine retarget).")]
    public static Task<CharacterWireResult> WireCharacter(
        IServiceProvider services,
        [Description("Project-relative .scene path.")] string scenePath,
        [Description("GameObject Identifier to receive the Animator.")] string objectIdentifier,
        [Description("Project-relative model path with animation clips.")] string modelAssetPath,
        [Description("Unique operation key for exactly-once semantics.")] string idempotencyKey,
        [Description("Optional .controller destination (default next to the scene).")] string? controllerPath = null,
        [Description("Natural-language intent recorded in the journal.")] string? intent = null,
        [Description("If true, list clips without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<CharacterWireService>().WireAsync(
            scenePath, objectIdentifier, modelAssetPath, controllerPath, idempotencyKey, intent, dryRun, cancellationToken);

    [McpServerTool(Name = "sprite_slice", UseStructuredContent = true)]
    [Description("Grid-slice a PNG spritesheet into TextureImporter Multiple slices written into the .meta (same {base}_{i} names and SHA256 sub-asset GUIDs the editor import will assign). KeepEmpty is on (no GPU alpha). Use after gen_fetch of kind=sprite. dryRun lists frames without writing.")]
    public static Task<SpriteSliceResult> SliceSprite(
        IServiceProvider services,
        [Description("Project-relative PNG path.")] string assetPath,
        [Description("Unique operation key for exactly-once semantics.")] string idempotencyKey,
        [Description("Grid columns (used when cellWidth is 0). Default 4.")] int columns = 4,
        [Description("Grid rows (used when cellHeight is 0). Default 1.")] int rows = 1,
        [Description("Cell width in pixels. 0 = textureWidth / columns.")] int cellWidth = 0,
        [Description("Cell height in pixels. 0 = textureHeight / rows.")] int cellHeight = 0,
        [Description("Pixels per unit stamped into the sprite settings. Default 100.")] float pixelsPerUnit = 100f,
        [Description("Natural-language intent recorded in the journal.")] string? intent = null,
        [Description("If true, list slices without writing the .meta.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<SpriteWireService>().SliceAsync(
            assetPath, columns, rows, cellWidth, cellHeight, pixelsPerUnit, idempotencyKey, intent, dryRun, cancellationToken);

    [McpServerTool(Name = "sprite_wire", UseStructuredContent = true)]
    [Description("Slice a PNG spritesheet (sprite_slice) then add SpriteRenderer (first frame) + SpriteFlipbook (all frame GUIDs, fps) on a GameObject. Pixel-art 2D playable loop. dryRun slices in memory only.")]
    public static Task<SpriteWireResult> WireSprite(
        IServiceProvider services,
        [Description("Project-relative .scene path.")] string scenePath,
        [Description("GameObject Identifier to receive SpriteRenderer + SpriteFlipbook.")] string objectIdentifier,
        [Description("Project-relative PNG spritesheet.")] string assetPath,
        [Description("Unique operation key for exactly-once semantics.")] string idempotencyKey,
        [Description("Grid columns. Default 4.")] int columns = 4,
        [Description("Grid rows. Default 1.")] int rows = 1,
        [Description("Cell width in pixels. 0 = divide texture width.")] int cellWidth = 0,
        [Description("Cell height in pixels. 0 = divide texture height.")] int cellHeight = 0,
        [Description("Pixels per unit. Default 100.")] float pixelsPerUnit = 100f,
        [Description("Flipbook frames per second. Default 12.")] float framesPerSecond = 12f,
        [Description("Natural-language intent recorded in the journal.")] string? intent = null,
        [Description("If true, list frames without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<SpriteWireService>().WireAsync(
            scenePath, objectIdentifier, assetPath, columns, rows, cellWidth, cellHeight,
            pixelsPerUnit, framesPerSecond, idempotencyKey, intent, dryRun, cancellationToken);

    // ---- P0-5 asset file ops (import/move/delete + .meta minting, offline) ----

    [McpServerTool(Name = "asset_import", UseStructuredContent = true)]
    [Description("Copy a source file (absolute path) into the project's Assets/ tree and mint a .meta sidecar with a fresh GUID and the importer inferred from the extension. Records a provenance-bearing change to the journal. Use overwrite=true to replace an existing asset (its GUID is preserved so references survive).")]
    public static Task<AssetImportResult> ImportAsset(
        IServiceProvider services,
        [Description("Absolute path to the source file to import.")] string sourcePath,
        [Description("Project-relative destination (e.g. Textures/Grass.png). Must be under Assets/.")] string destinationPath,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("If true, replace an existing destination file (preserves its GUID).")] bool overwrite = false,
        [Description("For model imports (.fbx/.gltf/.glb/.obj): Rig Animation Type. 0 Generic (default, attaches Animator), 1 Humanoid, 2 Legacy (AnimationComponent). Written into the .meta settings.")] int? animationType = null,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<AssetWriter>();
        return writer.ImportAsync(
            sourcePath,
            project.ResolveAssetPath(destinationPath),
            destinationPath,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.asset-import", intent, idempotencyKey),
            overwrite, animationType, cancellationToken);
    }

    [McpServerTool(Name = "asset_move", UseStructuredContent = true)]
    [Description("Move (rename/relocate) an asset file and its .meta sidecar within the project. Refuses to clobber an existing destination. Records a provenance-bearing change to the journal.")]
    public static Task<AssetMoveResult> MoveAsset(
        IServiceProvider services,
        [Description("Project-relative source asset path (e.g. Textures/Old.png).")] string sourcePath,
        [Description("Project-relative destination (e.g. Textures/New.png).")] string destinationPath,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<AssetWriter>();
        return writer.MoveAsync(
            project.ResolveAssetPath(sourcePath),
            project.ResolveAssetPath(destinationPath),
            sourcePath, destinationPath,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.asset-move", intent, idempotencyKey),
            cancellationToken);
    }

    [McpServerTool(Name = "asset_delete", UseStructuredContent = true)]
    [Description("Delete an asset file and its .meta sidecar. Records a provenance-bearing change to the journal.")]
    public static Task<AssetDeleteResult> DeleteAsset(
        IServiceProvider services,
        [Description("Project-relative asset path (e.g. Textures/Old.png) or absolute.")] string assetPath,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<AssetWriter>();
        return writer.DeleteAsync(
            project.ResolveAssetPath(assetPath),
            assetPath,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.asset-delete", intent, idempotencyKey),
            cancellationToken);
    }

    // ---- P0-6 material tools (offline .mat read + patch) ----

    [McpServerTool(Name = "material_inspect", UseStructuredContent = true)]
    [Description("Read a .mat file offline: shader GUID + categorized property list (color/float/int/vector/texture) + content hash. The content hash feeds material_patch's expectedBeforeHash for drift protection.")]
    public static MaterialDetail InspectMaterial(
        IServiceProvider services,
        [Description("Project-relative material path (e.g. Materials/Player.mat) or absolute.")] string materialPath)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(materialPath);
        return SceneReader.ReadMaterial(absolute, materialPath);
    }

    [McpServerTool(Name = "material_patch", UseStructuredContent = true)]
    [Description("Patch fields on a .mat file. Patches are slash-path pointers into the material's Echo tree: _shader/AssetID (change shader), _properties/_floats/_WindStrength (a float), _properties/_colors/_MainColor/R (one color channel), _properties/_textures/_MainTex/AssetID (bind a texture GUID). Records a provenance-bearing change to the journal.")]
    public static Task<SceneWriteResult> PatchMaterial(
        IServiceProvider services,
        [Description("Project-relative material path (e.g. Materials/Player.mat) or absolute.")] string materialPath,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from material_inspect. Rejects the write if the material changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report the change without writing to disk or the journal.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchMaterialAsync(
            project.ResolveAssetPath(materialPath), materialPath, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.material-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    // ---- C1 generic .asset data inspect/patch (ScriptableObject instances) ----

    [McpServerTool(Name = "asset_data_inspect", UseStructuredContent = true)]
    [Description("Read a generic .asset file (ScriptableObject instance) offline: its $type + a flat path/value list of every leaf scalar field + content hash. Lets an AI inspect any Echo-serialized data asset (game configs, item tables, skill data) without knowing the concrete runtime type. Pass the content hash to asset_data_patch for drift protection.")]
    public static AssetDataDetail InspectAssetData(
        IServiceProvider services,
        [Description("Project-relative .asset path (e.g. Assets/Data/PlayerSettings.asset) or absolute.")] string assetPath)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(assetPath);
        return SceneReader.ReadAssetData(absolute, assetPath);
    }

    [McpServerTool(Name = "asset_data_patch", UseStructuredContent = true)]
    [Description("Patch a generic .asset file (ScriptableObject instance). Patches are slash-paths into the .asset root: top-level fields ('MaxHealth'), nested ('Damage/Fire'), list indices ('Costs/$values/0'). Same drift-protected + journaled mechanism as material_patch / scene_settings_patch.")]
    public static Task<SceneWriteResult> PatchAssetData(
        IServiceProvider services,
        [Description("Project-relative .asset path (e.g. Assets/Data/PlayerSettings.asset) or absolute.")] string assetPath,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from asset_data_inspect. Rejects the write if the asset changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchAssetDataAsync(
            project.ResolveAssetPath(assetPath), assetPath, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.asset-data-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    // ---- M8 VolumeProfile inspect/patch (Echo .volumeprofile, same leaf-path patch as asset_data) ----

    [McpServerTool(Name = "volume_inspect", UseStructuredContent = true)]
    [Description("Read a VolumeProfile (.volumeprofile) offline: $type, a flat path/value list of every leaf scalar (Weight lives on the scene Volume component; this asset holds Components[]), and a content hash. Pass the hash to volume_patch.")]
    public static AssetDataDetail InspectVolumeProfile(
        IServiceProvider services,
        [Description("Project-relative .volumeprofile path (e.g. Assets/Settings/Default.volumeprofile) or absolute.")] string volumePath)
    {
        RequireVolumeProfilePath(volumePath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(volumePath);
        return SceneReader.ReadAssetData(absolute, volumePath);
    }

    [McpServerTool(Name = "volume_patch", UseStructuredContent = true)]
    [Description("Patch a VolumeProfile (.volumeprofile). Paths are slash-separated into the Echo root (e.g. Components/$values/0/Intensity). Same drift-protected + journaled mechanism as asset_data_patch.")]
    public static Task<SceneWriteResult> PatchVolumeProfile(
        IServiceProvider services,
        [Description("Project-relative .volumeprofile path or absolute.")] string volumePath,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from volume_inspect. Rejects the write if the profile changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        RequireVolumeProfilePath(volumePath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchAssetDataAsync(
            project.ResolveAssetPath(volumePath), volumePath, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.volume-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    internal static void RequireVolumeProfilePath(string volumePath)
    {
        if (string.IsNullOrWhiteSpace(volumePath)
            || !volumePath.EndsWith(".volumeprofile", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("volume_inspect / volume_patch require a .volumeprofile path.");
        }
    }

    // ---- M27 Shader Graph inspect/patch/compile (Echo .shadergraph) ----

    [McpServerTool(Name = "shadergraph_inspect", UseStructuredContent = true)]
    [Description("Read a Shader Graph (.shadergraph) offline: $type, a flat path/value list of ShaderName/Target/Nodes/Edges/Properties, and a content hash. Pass the hash to shadergraph_patch.")]
    public static AssetDataDetail InspectShaderGraph(
        IServiceProvider services,
        [Description("Project-relative .shadergraph path (e.g. Assets/Shaders/UnlitAlbedo.shadergraph) or absolute.")] string graphPath)
    {
        RequireShaderGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(graphPath);
        return SceneReader.ReadAssetData(absolute, graphPath);
    }

    [McpServerTool(Name = "shadergraph_patch", UseStructuredContent = true)]
    [Description("Patch a Shader Graph (.shadergraph). Paths are slash-separated into the Echo root (e.g. Nodes/$values/0/PosX, ShaderName). Same drift-protected + journaled mechanism as asset_data_patch.")]
    public static Task<SceneWriteResult> PatchShaderGraph(
        IServiceProvider services,
        [Description("Project-relative .shadergraph path or absolute.")] string graphPath,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from shadergraph_inspect. Rejects the write if the graph changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        RequireShaderGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchAssetDataAsync(
            project.ResolveAssetPath(graphPath), graphPath, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.shadergraph-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    [McpServerTool(Name = "shadergraph_compile", UseStructuredContent = true)]
    [Description("Compile a Shader Graph (.shadergraph) to dual GLSLPROGRAM + SLANGPROGRAM Shader DSL. Optionally write a .shader file with outputPath. Lit graphs use LightBVH CalculateForwardLighting / StandardCalculateLighting.")]
    public static ShaderGraphCompileResult CompileShaderGraph(
        IServiceProvider services,
        [Description("Project-relative .shadergraph path or absolute.")] string graphPath,
        [Description("Optional project-relative or absolute .shader path to write. Omit to return source only.")] string? outputPath = null)
    {
        RequireShaderGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(graphPath);
        if (!File.Exists(absolute))
            throw new FileNotFoundException("Shader graph not found: " + graphPath, absolute);

        string text = File.ReadAllText(absolute);
        ShaderGraphEcho.Load(text, out string shaderName, out var target, out var properties, out var nodes, out var edges);
        string source;
        try
        {
            source = ShaderGraphSourceCompiler.CompileToSource(shaderName, target, properties, nodes, edges);
        }
        catch (ShaderGraphCompileException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        bool written = false;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            string outAbsolute = project.ResolveAssetPath(outputPath);
            string? dir = Path.GetDirectoryName(outAbsolute);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(outAbsolute, source);
            written = true;
        }

        return new ShaderGraphCompileResult(graphPath, shaderName, target.ToString(), source, outputPath, written);
    }

    internal static void RequireShaderGraphPath(string graphPath)
    {
        if (string.IsNullOrWhiteSpace(graphPath)
            || !graphPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("shadergraph_inspect / shadergraph_patch / shadergraph_compile require a .shadergraph path.");
        }
    }

    // ---- M5 Visual Effect Graph inspect/patch/compile/nodes/create (Echo .vfx) ----

    [McpServerTool(Name = "vfxgraph_inspect", UseStructuredContent = true)]
    [Description("Read a Visual Effect Graph (.vfx) offline: $type, a flat path/value list of GraphName/Template/Capacity/Contexts/Blocks/Operators/Edges, and a content hash. Pass the hash to vfxgraph_patch.")]
    public static AssetDataDetail InspectVfxGraph(
        IServiceProvider services,
        [Description("Project-relative .vfx path (e.g. Assets/VFX/Loop.vfx) or absolute.")] string graphPath)
    {
        RequireVfxGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(graphPath);
        return SceneReader.ReadAssetData(absolute, graphPath);
    }

    [McpServerTool(Name = "vfxgraph_patch", UseStructuredContent = true)]
    [Description("Patch a Visual Effect Graph (.vfx). Paths are slash-separated into the Echo root (e.g. GraphName, Blocks/$values/0/Fields/$values/0/Value). Same drift-protected + journaled mechanism as asset_data_patch.")]
    public static Task<SceneWriteResult> PatchVfxGraph(
        IServiceProvider services,
        [Description("Project-relative .vfx path or absolute.")] string graphPath,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from vfxgraph_inspect. Rejects the write if the graph changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        RequireVfxGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchAssetDataAsync(
            project.ResolveAssetPath(graphPath), graphPath, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.vfxgraph-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    [McpServerTool(Name = "vfxgraph_compile", UseStructuredContent = true)]
    [Description("Compile a Visual Effect Graph (.vfx) to compute Init/Update/WriteArgs plus Output Shader DSL. Optionally write a .shader file with outputPath.")]
    public static VfxGraphCompileResult CompileVfxGraph(
        IServiceProvider services,
        [Description("Project-relative .vfx path or absolute.")] string graphPath,
        [Description("Optional project-relative or absolute .shader path to write. Omit to return source only.")] string? outputPath = null)
    {
        RequireVfxGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(graphPath);
        if (!File.Exists(absolute))
            throw new FileNotFoundException("VFX graph not found: " + graphPath, absolute);

        string text = File.ReadAllText(absolute);
        VfxGraphEcho.Load(text, out string graphName, out VfxTemplateKind template, out _,
            out _, out _, out _, out _, out _);
        string source;
        VfxGraphLoweredSettings settings;
        try
        {
            settings = VfxGraphEcho.CompileSettings(text, project.ProjectRoot);
            source = VfxGraphEcho.CompileToSource(text, project.ProjectRoot);
        }
        catch (VfxGraphCompileException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        bool written = false;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            string outAbsolute = project.ResolveAssetPath(outputPath);
            string? dir = Path.GetDirectoryName(outAbsolute);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(outAbsolute, source);
            written = true;
        }

        return new VfxGraphCompileResult(
            graphPath, graphName, template.ToString(), settings.Capacity,
            settings.SpawnRate, settings.BurstCount, source, outputPath, written);
    }

    [McpServerTool(Name = "vfxgraph_nodes", UseStructuredContent = true)]
    [Description("List Visual Effect Graph context, block, and operator type names from VfxNodeLibrary.")]
    public static VfxNodeCatalog ListVfxGraphNodes()
        => new(VfxNodeLibrary.ContextTypes, VfxNodeLibrary.BlockTypes, VfxNodeLibrary.OperatorTypes);

    [McpServerTool(Name = "vfxgraph_create", UseStructuredContent = true)]
    [Description("Copy a packaged Empty/Loop/Burst/Trail/Mesh/Subgraph/Decal .vfx template into the project. Does not journal; use vfxgraph_patch afterwards for edits.")]
    public static VfxGraphCreateResult CreateVfxGraph(
        IServiceProvider services,
        [Description("Project-relative .vfx path to write (e.g. Assets/VFX/Hero.vfx).")] string graphPath,
        [Description("Template kind: Empty, Loop, Burst, Trail, Mesh, Subgraph, or Decal.")] string template = "Loop",
        [Description("If true, report without writing.")] bool dryRun = false)
    {
        RequireVfxGraphPath(graphPath);
        if (!TryParseVfxTemplate(template, out VfxTemplateKind kind))
            throw new ArgumentException("vfxgraph_create template must be Empty, Loop, Burst, Trail, Mesh, Subgraph, or Decal.");

        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(graphPath);
        string source = VfxGraphSourceCompiler.FindTemplateFile(kind);
        if (dryRun)
            return new VfxGraphCreateResult(graphPath, kind.ToString(), false, true);

        string? dir = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(source, absolute, overwrite: false);
        return new VfxGraphCreateResult(graphPath, kind.ToString(), true, false);
    }

    internal static void RequireVfxGraphPath(string graphPath)
    {
        if (string.IsNullOrWhiteSpace(graphPath)
            || !graphPath.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("vfxgraph_inspect / vfxgraph_patch / vfxgraph_compile / vfxgraph_create require a .vfx path.");
        }
    }

    internal static bool TryParseVfxTemplate(string? raw, out VfxTemplateKind kind)
    {
        kind = VfxTemplateKind.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return Enum.TryParse(raw.Trim(), ignoreCase: true, out kind)
            && Enum.IsDefined(kind);
    }

    // ---- P2-2 scene settings tools (fog/ambient/skybox on the Scene root) ----

    [McpServerTool(Name = "scene_settings_query", UseStructuredContent = true)]
    [Description("Read a scene's top-level lighting/skybox settings offline: Fog (Mode/Color/Start/End/Density), Ambient (Mode/Strength/Color/SkyColor/GroundColor), Skybox (Mode/SolidColor/GradientTop/GradientBottom/CustomMaterial). Returns a flat path/value list grouped by category + content hash for drift protection.")]
    public static SceneSettings QuerySceneSettings(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string absolute = project.ResolveAssetPath(scenePath);
        return SceneReader.ReadSceneSettings(absolute, scenePath);
    }

    [McpServerTool(Name = "scene_settings_patch", UseStructuredContent = true)]
    [Description("Patch a scene's top-level Fog/Ambient/Skybox settings. Patches are slash-paths into the scene root: Fog/Mode, Fog/Density, Ambient/Color/R, Skybox/SolidColor/G, Skybox/Mode. Records a provenance-bearing change to the journal.")]
    public static Task<SceneWriteResult> PatchSceneSettings(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Setting patches: each has a Path (slash-separated, e.g. 'Fog/Density') and a Value (literal, e.g. '0.02').")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from scene_settings_query. Rejects the write if the scene changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchSceneSettingsAsync(
            project.ResolveAssetPath(scenePath), scenePath, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.scene-settings-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    // ---- code tools moved: code_symbols is engine-repo-only → EngineDevTools.cs ----
    // (find_in_file stays here: plain text search is useful in any game project.)

    [McpServerTool(Name = "find_in_file", UseStructuredContent = true)]
    [Description("Search file CONTENTS with a regex. Scope is a single file (path) or a glob under Assets/ (e.g. '**/*.cs', 'Scripts/**/*.cs', '.shader'). Returns line numbers + excerpts. Unlike code_symbols (which finds symbol declarations via Roslyn), this finds any text — usages, comments, string literals. Capped at limit matches (default 100, max 500) with HasMore.")]
    public static FileSearchResult FindInFile(
        IServiceProvider services,
        [Description("Regex pattern to search for (e.g. 'Debug\\.Log', 'class.*Player').")] string pattern,
        [Description("Project-relative path to a single file to search (e.g. 'Assets/Scripts/Player.cs'). Omit to search a glob.")] string? path = null,
        [Description("Glob pattern under Assets/ (e.g. '**/*.cs', 'Scripts/**/*.cs', '.cs'). Default '**/*' (all files).")] string? glob = null,
        [Description("Max matches to return, 1-500. Default 100.")] int limit = 100)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        return FileSearcher.Search(project.AssetsRoot(), pattern, path, glob, limit);
    }

    // ---- script management (create/delete/apply_edits) — let the AI write code ----

    [McpServerTool(Name = "script_create", UseStructuredContent = true)]
    [Description("Create a new C# script file under Assets/ and mint a .meta sidecar (fresh GUID, ScriptImporter). The engine's AssetWatcher auto-detects it and triggers a Roslyn recompile + hot-reload if the editor is running. Refuses to overwrite an existing script (use script_apply_edits to modify). Records a provenance-bearing change to the journal.")]
    public static Task<ScriptEditResult> CreateScript(
        IServiceProvider services,
        [Description("Project-relative destination (e.g. Assets/Scripts/Player.cs).")] string path,
        [Description("The full C# source code to write.")] string content,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<ScriptWriter>();
        return writer.CreateAsync(
            project.ResolveAssetPath(path), path, content,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.script-create", intent, idempotencyKey),
            cancellationToken);
    }

    [McpServerTool(Name = "script_delete", UseStructuredContent = true)]
    [Description("Delete a .cs script file and its .meta sidecar. Records a provenance-bearing change to the journal.")]
    public static Task<ScriptEditResult> DeleteScript(
        IServiceProvider services,
        [Description("Project-relative script path (e.g. Assets/Scripts/Player.cs).")] string path,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<ScriptWriter>();
        return writer.DeleteAsync(
            project.ResolveAssetPath(path), path,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.script-delete", intent, idempotencyKey),
            cancellationToken);
    }

    [McpServerTool(Name = "script_apply_edits", UseStructuredContent = true)]
    [Description("Apply structured text edits to an existing .cs script. Each edit finds OldText (exact, case-sensitive) and replaces with NewText. occurrence='first' (default) requires OldText to be UNIQUE — provides safety against ambiguous edits; 'all' replaces every match. Returns the new content SHA. Records a provenance-bearing change to the journal. The engine auto-recompiles on the file change.")]
    public static Task<ScriptEditResult> ApplyScriptEdits(
        IServiceProvider services,
        [Description("Project-relative script path (e.g. Assets/Scripts/Player.cs).")] string path,
        [Description("Edits: each has OldText (exact text to find), NewText (replacement), Occurrence ('first' or 'all').")] ScriptTextEdit[] edits,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("Optional SHA-256 of the current content (from a prior read). Rejects the edit if the file changed since.")] string? expectedBeforeSha = null,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<ScriptWriter>();
        return writer.ApplyEditsAsync(
            project.ResolveAssetPath(path), path, edits,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.script-apply-edits", intent, idempotencyKey),
            expectedBeforeSha, cancellationToken);
    }

    // ---- B2 build / run-headless tools ----

    [McpServerTool(Name = "build_project", UseStructuredContent = true)]
    [Description("Build an XEngine project headlessly via XEngine.Editor --buildmode. Returns a structured result (exit code, duration, output tail).")]
    public static Task<ProcessRunResult> BuildProject(
        IServiceProvider services,
        [Description("Absolute path to a published XEngine.Editor executable.")] string editorExe,
        [Description("Absolute path to the output directory for the build.")] string outputDir,
        [Description("Optional .buildprofile asset to build: profile name, project relative path, or asset GUID. Defaults to the project's active profile.")] string? buildProfile = null,
        [Description("Optional pipeline override (desktop, android, ios, harmonyos, web). Redundant when buildProfile is given, and an error when the two disagree.")] string? platform = null,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        return BuildRunner.BuildAsync(editorExe, project.ProjectRoot, outputDir, buildProfile, platform, cancellationToken);
    }

    [McpServerTool(Name = "run_headless", UseStructuredContent = true)]
    [Description("Run a built player headlessly (no window/audio/GPU) up to a frame or time limit. The offline equivalent of Game.RunHeadless.")]
    public static Task<ProcessRunResult> RunHeadless(
        [Description("Absolute path to the built player executable.")] string playerExe,
        [Description("Optional scene to load.")] string? scene = null,
        [Description("Stop after this many frames (0 = no frame limit).")] int maxFrames = 60,
        [Description("Stop after this many seconds (0 = no time limit).")] int maxSeconds = 0,
        CancellationToken cancellationToken = default)
        => BuildRunner.RunHeadlessAsync(playerExe, scene, maxFrames, maxSeconds, cancellationToken);

    // ---- run_tests moved: engine test suite is engine-repo-only → EngineDevTools.cs ----

    // ---- B4 workflow catalog (declarative DAG of tool calls) ----

    [McpServerTool(Name = "workflow_list", UseStructuredContent = true)]
    [Description("List declarative workflow definitions (.xengine/workflows/*.json). Each chains tools as a DAG with deps + failure policy. Execution (workflow_run) lands with the operation store.")]
    public static WorkflowListResult ListWorkflows(IServiceProvider services)
        => services.GetRequiredService<WorkflowCatalog>().List();

    [McpServerTool(Name = "workflow_run", UseStructuredContent = true)]
    [Description("Execute a declarative workflow (DAG of tool calls) synchronously. Each step runs to completion inline (XEngine has no Domain Reload, so no ticket/polling needed). Steps support ${inputs.x} templating and dependsOn.")]
    public static async Task<WorkflowRunResult> RunWorkflow(
        IServiceProvider services,
        [Description("Workflow name (must exist in .xengine/workflows/*.json).")] string name,
        [Description("JSON object of workflow inputs.")] string inputs = "{}",
        [Description("If true, validate + report each step without executing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var catalog = services.GetRequiredService<WorkflowCatalog>();
        WorkflowDefinition? def = catalog.Get(name)
            ?? throw new KeyNotFoundException($"Workflow '{name}' not found.");
        using JsonDocument doc = JsonDocument.Parse(inputs);
        return await services.GetRequiredService<WorkflowRunner>().RunAsync(def, doc.RootElement.Clone(), dryRun, cancellationToken);
    }

    // ---- B1 journal-query tool (exposes Layer 0 process history to the AI) ----

    [McpServerTool(Name = "journal_query", UseStructuredContent = true)]
    [Description("Query the operation journal to read how a project/scene evolved (provenance: author, intent, timestamp, replayable delta).")]
    public static JournalPage QueryJournal(
        IServiceProvider services,
        [Description("Optional target asset path filter (e.g. Scenes/Main.scene).")] string? targetAsset = null,
        [Description("Optional author/tool filter (e.g. xengine-cli.scene-object-patch).")] string? byAuthor = null,
        [Description("Optional case-insensitive substring filter on the change intent.")] string? intentContains = null,
        [Description("Page offset, >= 0.")] int offset = 0,
        [Description("Page size, 1-200.")] int limit = 100)
    {
        string root = services.GetRequiredService<ProjectDescriptor>().ProjectRoot;
        var query = new JournalReadQuery(targetAsset, null, null, byAuthor, intentContains, offset, Math.Clamp(limit, 1, 200));
        return JournalReader.Query(root, query);
    }

    // ---- B2 scene-write tools (offline edit + write-back + provenance to journal) ----

    [McpServerTool(Name = "scene_object_patch", UseStructuredContent = true)]
    [Description("Patch fields on a scene GameObject (by Identifier). Patches are JSON-pointer paths into the GO's tree (e.g. Transform/_localPosition/Y). Records a provenance-bearing change to the journal.")]
    public static Task<SceneWriteResult> PatchSceneObject(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Identifier of the GameObject to patch.")] string objectIdentifier,
        [Description("Field patches: each has a Path (slash-separated) and a Value (literal).")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Optional SHA-256 content hash from scene_hierarchy. Rejects the write if the scene changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report the change without writing to disk or the journal.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchAsync(
            project.ResolveAssetPath(scenePath), scenePath, objectIdentifier, patches,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.scene-object-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    [McpServerTool(Name = "scene_object_create", UseStructuredContent = true)]
    [Description("Create a new GameObject in a scene. The new GO is appended at the scene ROOT (parentIdentifier is reserved for future reparenting but currently ignored — to nest under a parent, use scene_object_patch to set the parent's Children list after creation). Records a provenance-bearing change to the journal.")]
    public static Task<SceneWriteResult> CreateSceneObject(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Name of the new GameObject.")] string name,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("Identifier of the intended parent GO (reserved; currently creates at root).")] string? parentIdentifier = null,
        [Description("If true, report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.CreateAsync(
            project.ResolveAssetPath(scenePath), scenePath, name, parentIdentifier,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.scene-object-create", intent, idempotencyKey),
            dryRun, cancellationToken);
    }

    [McpServerTool(Name = "scene_object_delete", UseStructuredContent = true)]
    [Description("Delete a GameObject (by Identifier) from a scene. Records a provenance-bearing change to the journal.")]
    public static Task<SceneWriteResult> DeleteSceneObject(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Identifier of the GameObject to delete.")] string objectIdentifier,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("If true, report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.DeleteAsync(
            project.ResolveAssetPath(scenePath), scenePath, objectIdentifier,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.scene-object-delete", intent, idempotencyKey),
            dryRun, cancellationToken);
    }

    // ---- P0-4 component tools (offline Echo tree ops on a scene GO's Components list) ----

    [McpServerTool(Name = "scene_component_add", UseStructuredContent = true)]
    [Description("Add a component to a scene GameObject. componentType is a short name (Camera, MeshRenderer, DirectionalLight, AudioSource, ...) or a full assembly-qualified $type for user MonoBehaviour scripts. Optional initialFields patches set fields on the new component node. Records a provenance-bearing change to the journal. The new component's _identifier is in the summary.")]
    public static Task<SceneWriteResult> AddSceneComponent(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Identifier of the target GameObject.")] string objectIdentifier,
        [Description("Component type: a known short-name (Camera, MeshRenderer, DirectionalLight, ...) or a full $type for user scripts.")] string componentType,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("Optional field patches applied to the new component node (each has Path and Value).")] SceneFieldPatch[]? initialFields = null,
        [Description("Optional SHA-256 content hash from scene_hierarchy. Rejects the write if the scene changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.AddComponentAsync(
            project.ResolveAssetPath(scenePath), scenePath, objectIdentifier, componentType, initialFields,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.scene-component-add", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    [McpServerTool(Name = "animator_component_add", UseStructuredContent = true)]
    [Description("Add an Animator to a scene GameObject. Optionally mint a sibling .controller and bind it, and/or set Clip to a clip GUID. Prefer this over scene_component_add when wiring Mecanim. Records a provenance-bearing journal entry.")]
    public static Task<SceneWriteResult> AddAnimatorComponent(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Identifier of the target GameObject.")] string objectIdentifier,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("If true, write a new .controller next to the scene and bind Animator.Controller.")] bool createController = false,
        [Description("Optional AnimationClip GUID to assign to Animator.Clip.")] string? clipGuid = null,
        [Description("Optional SHA-256 content hash from scene_hierarchy.")] string? expectedBeforeHash = null,
        [Description("If true, report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return AnimatorComponentAdd.RunAsync(
            project, writer, scenePath, objectIdentifier,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.animator-component-add", intent, idempotencyKey),
            createController, clipGuid, dryRun, expectedBeforeHash, cancellationToken);
    }

    [McpServerTool(Name = "scene_component_remove", UseStructuredContent = true)]
    [Description("Remove a component from a scene GameObject. Match by componentIdentifier (exact _identifier GUID) OR by componentType (short type name, removes the first match). Provide exactly one. Records a provenance-bearing change to the journal.")]
    public static Task<SceneWriteResult> RemoveSceneComponent(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Identifier of the target GameObject.")] string objectIdentifier,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("Exact _identifier GUID of the component to remove (preferred — unambiguous).")] string? componentIdentifier = null,
        [Description("Short type name of the component to remove (removes the first match). Use when you don't have the GUID.")] string? componentType = null,
        [Description("Optional SHA-256 content hash from scene_hierarchy. Rejects the write if the scene changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(componentIdentifier) && string.IsNullOrEmpty(componentType))
        {
            throw new ArgumentException(
                "scene_component_remove needs either componentIdentifier (GUID) or componentType (short name).");
        }
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.RemoveComponentAsync(
            project.ResolveAssetPath(scenePath), scenePath, objectIdentifier, componentIdentifier, componentType,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.scene-component-remove", intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }

    [McpServerTool(Name = "scene_component_patch", UseStructuredContent = true)]
    [Description("Patch fields on an EXISTING component of a scene GameObject (e.g. Camera.FieldOfView, Light.Intensity). Locate the component by componentIdentifier (exact _identifier GUID from scene_hierarchy — preferred, unambiguous) OR componentType (short type name, first match). Patch paths are RELATIVE to the component root (e.g. 'FieldOfView', 'ClearColor/R', 'NearClipPlane') — no 'Components/0/' prefix. Optional enable=true/false toggles the component's _enabled flag. Records a provenance-bearing change to the journal.")]
    public static Task<SceneWriteResult> PatchSceneComponent(
        IServiceProvider services,
        [Description("Project-relative scene path (e.g. Scenes/Main.scene) or absolute.")] string scenePath,
        [Description("Identifier of the target GameObject.")] string objectIdentifier,
        [Description("Field patches relative to the component root: each has Path (e.g. 'FieldOfView', 'ClearColor/R') and Value.")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("Exact _identifier GUID of the component to patch (preferred — from scene_hierarchy's component.identifier).")] string? componentIdentifier = null,
        [Description("Short type name of the component to patch (first match). Use when you don't have the GUID.")] string? componentType = null,
        [Description("Optional: true to enable the component, false to disable (sets _enabled). Omit to leave unchanged.")] bool? enable = null,
        [Description("Optional SHA-256 content hash from scene_hierarchy. Rejects the write if the scene changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(componentIdentifier) && string.IsNullOrEmpty(componentType))
        {
            throw new ArgumentException(
                "scene_component_patch needs either componentIdentifier (GUID) or componentType (short name) to locate the component.");
        }
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchComponentAsync(
            project.ResolveAssetPath(scenePath), scenePath, objectIdentifier, componentIdentifier, componentType,
            patches, enable,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.scene-component-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, recursive: false, cancellationToken);
    }

    [McpServerTool(Name = "prefab_component_patch", UseStructuredContent = true)]
    [Description("Patch fields on an EXISTING component of a GameObject in a .prefab file. Same as scene_component_patch but for prefabs (recursive GO search through nested Children). Locate the component by componentIdentifier (GUID) or componentType (short name, first match). Patch paths are relative to the component root. Optional enable=true/false toggles _enabled.")]
    public static Task<SceneWriteResult> PatchPrefabComponent(
        IServiceProvider services,
        [Description("Project-relative prefab path (e.g. Prefabs/Player.prefab) or absolute.")] string prefabPath,
        [Description("Identifier of the target GameObject (searched recursively in the prefab).")] string objectIdentifier,
        [Description("Field patches relative to the component root: each has Path (e.g. 'FieldOfView') and Value.")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change.")] string? intent = null,
        [Description("Exact _identifier GUID of the component to patch (preferred).")] string? componentIdentifier = null,
        [Description("Short type name of the component to patch (first match).")] string? componentType = null,
        [Description("Optional: true to enable, false to disable.")] bool? enable = null,
        [Description("Optional SHA-256 content hash from prefab_hierarchy.")] string? expectedBeforeHash = null,
        [Description("If true, report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(componentIdentifier) && string.IsNullOrEmpty(componentType))
        {
            throw new ArgumentException(
                "prefab_component_patch needs either componentIdentifier (GUID) or componentType (short name).");
        }
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.PatchComponentAsync(
            project.ResolveAssetPath(prefabPath), prefabPath, objectIdentifier, componentIdentifier, componentType,
            patches, enable,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.prefab-component-patch", intent, idempotencyKey),
            dryRun, expectedBeforeHash, recursive: true, cancellationToken);
    }
    // ---- SpineGen pipeline (2026-08-27 spec): slice → fill → rig → skin → animate ----

    [McpServerTool(Name = "spinegen_slice", UseStructuredContent = true)]
    [Description("Spine Generator stage 1: slice a character illustration into layered parts (alpha threshold + connected components), mark occluded pixels per part (they need inpainting before rigging), pack a Spine atlas page, and write parts/*.png + slice_manifest.json + *.atlas.txt under .xengine/spinegen/slice/. Deterministic and offline; identical inputs reuse the same output directory. dryRun only computes the content hash.")]
    public static SpineGenSliceResult SpineGenSlice(
        IServiceProvider services,
        [Description("Character image: project-relative (e.g. Assets/Art/bear.png) or absolute. RGBA PNG.")] string image,
        [Description("Alpha value (0-255) at/above which a pixel counts as character. Default 8.")] int? alphaThreshold = null,
        [Description("Connected components below this pixel count are dropped as anti-alias noise. Default 48.")] int? minAreaPixels = null,
        [Description("Source-pixel padding added around each crop rect. Default 0.")] int? cropPadding = null,
        [Description("Padding between packed atlas regions. Default 2.")] int? packingPadding = null,
        [Description("How solid-touching regions are separated: components (default) keeps whole alpha islands; watershed additionally cuts each island at distance-transform saddles (neck/shoulder/hip joints on solid art).")] string? split = null,
        [Description("Watershed marker minimum thickness in distance-transform px. 0 = auto (max(12, minSide/96)).")] int? markerMinDepth = null,
        [Description("Minimum px between watershed markers. 0 = auto (max(32, maxSide/16)).")] int? markerSpacing = null,
        [Description("If true, compute the input hash without writing anything.")] bool dryRun = false,
        [Description("Optional versioned exploded-sheet assembly recipe JSON, relative to project root or absolute.")] string? recipe = null)
        => services.GetRequiredService<SpineGenSliceService>()
            .Slice(image, alphaThreshold, minAreaPixels, cropPadding, packingPadding, split, markerMinDepth, markerSpacing, dryRun, recipe);

    [McpServerTool(Name = "spinegen_fill", UseStructuredContent = true)]
    [Description("Spine Generator stage 2: complete each sliced part's inpaint seeds so it survives independent motion. Builds a candidate gallery per part — three deterministic offline continuations plus (when provider != mock) one generative completion through a gen image backend (input-image edits path; needs {PROVIDER}_API_KEY) — scores them by seam smoothness and composites the winner into parts/<name>.png. Writes fill_manifest.json (gallery + chosen) next to slice_manifest.json.")]
    public static Task<SpineGenFillResult> SpineGenFill(
        IServiceProvider services,
        [Description("Slice output directory from spinegen_slice (project-relative, e.g. .xengine/spinegen/slice/bear-884ee5bd).")] string slice,
        [Description("Image provider for the generative candidate: mock (offline only) | openai | comfyui | ...")] string? provider = null,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<SpineGenFillService>().FillAsync(slice, provider, cancellationToken);

    [McpServerTool(Name = "spinegen_rig", UseStructuredContent = true)]
    [Description("Spine Generator stage 3: one-click rigging. Classifies sliced parts (torso/head/arms/legs/tail/props), anchors each on a zero-rotation joint bone of a biped template, and exports a Spine 4.x skeleton.json whose default skin reproduces the illustration exactly. Writes rig/skeleton.json + rig/rig_manifest.json inside the slice directory.")]
    public static SpineGenRigResult SpineGenRig(
        IServiceProvider services,
        [Description("Slice output directory from spinegen_slice (usually after spinegen_fill).")] string slice)
        => services.GetRequiredService<SpineGenRigService>().Rig(slice);

    [McpServerTool(Name = "spinegen_skin", UseStructuredContent = true)]
    [Description("Spine Generator stage 4: auto-skinning. Traces each part's alpha silhouette (Moore contour), simplifies it (Douglas-Peucker), ear-clip triangulates the (possibly concave) polygon and upgrades the region attachment to a mesh on its rig bone. Overwrites rig/skeleton.json in place and writes rig/skin_manifest.json (parameters + per-part stats). Parts that are too small stay regions.")]
    public static SpineGenSkinResult SpineGenSkin(
        IServiceProvider services,
        [Description("Slice output directory from spinegen_slice (after spinegen_rig).")] string slice,
        [Description("Douglas-Peucker tolerance in 1/100 px (default 150 = 1.5px). Lower keeps more vertices.")] int? simplifyEpsilonX100 = null,
        [Description("Maximum hull vertices per part (default 64).")] int? maxHullVertices = null,
        [Description("Parts below this opaque pixel count stay plain regions (default 24).")] int? minMeshPixels = null)
        => services.GetRequiredService<SpineGenSkinService>().Skin(slice, simplifyEpsilonX100, maxHullVertices, minMeshPixels);

    [McpServerTool(Name = "spinegen_animate", UseStructuredContent = true)]
    [Description("Spine Generator stage 5: text-to-animation. --motion picks a built-in procedural loop (idle_breath | sway | bob | wave_arm); --prompt sends the request to an LLM that must emit a strict keyframe JSON validated against the rig (unknown bones / out-of-range values / broken loops are fed back for up to 3 repair rounds; provider=mock synthesizes deterministically offline). Appends the animation into rig/skeleton.json and writes rig/motion_manifest.json.")]
    public static Task<SpineGenAnimateResult> SpineGenAnimate(
        IServiceProvider services,
        [Description("Slice output directory (after spinegen_skin).")] string slice,
        [Description("Built-in motion name: idle_breath | sway | bob | wave_arm.")] string? motion = null,
        [Description("Natural-language animation request for the LLM route.")] string? prompt = null,
        [Description("LLM provider: mock (offline) | openai. Default mock when prompted.")] string? provider = null,
        [Description("Chat model id (default gpt-4o-mini).")] string? model = null,
        CancellationToken cancellationToken = default,
        [Description("Optional project-relative MotionPlan JSON; exclusive with motion/prompt.")] string? plan = null)
        => services.GetRequiredService<SpineGenAnimateService>()
            .AnimateAsync(slice, motion, prompt, provider, model, cancellationToken, plan);
}
