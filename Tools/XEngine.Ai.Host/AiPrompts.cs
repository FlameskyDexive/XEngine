using System.ComponentModel;
using ModelContextProtocol.Server;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ P2-3 — MCP Prompts: reusable, server-provided prompt templates an AI client can fetch via
/// <c>prompts/list</c> + <c>prompts/get</c> to bootstrap a common task with the right tool sequence
/// and guardrails baked in. Unlike tools (which DO things) and resources (which ARE data), prompts
/// are guidance — they tell the agent "here's how to approach X in this engine".
///
/// <para>Each prompt method returns its guidance text; the SDK wraps it as a prompt message the
/// client can inject before letting the agent work.</para>
/// </summary>
[McpServerPromptType]
public static class AiPrompts
{
    [McpServerPrompt(Name = "diagnose_build_failure")]
    [Description("Step-by-step playbook for diagnosing a failed XEngine build: run xengine_doctor, read the build output tail, check the editor exe path, and re-run with a fix.")]
    public static string DiagnoseBuildFailure() => BuildFailureText;

    private const string BuildFailureText = """
        To diagnose a failed XEngine build, follow this sequence:
        1. Call `xengine_doctor` to check .NET SDK, the solution, and the project for environment issues.
        2. Read the `build_project` result's `tail` (last 4 KiB of output) for the compiler/linker error.
        3. If the error is "Executable not found", the `--editor` argument must be the absolute path to a
           PUBLISHED XEngine.Editor executable — publish it first with `dotnet publish XEngine.Editor -c Release`.
        4. If the error is a C# compile error, use `code_symbols` to locate the symbol, fix the source,
           then rebuild.
        5. Re-run `build_project` to confirm green. Use `run_headless` next to smoke-test the build
           before opening it in the editor.
        Always pass a stable `idempotencyKey` to every write so a retry doesn't double-apply.
        """;

    [McpServerPrompt(Name = "edit_scene_safely")]
    [Description("The read-check-write loop for editing a scene without clobbering concurrent edits: read hierarchy (get hash) → patch with expectedBeforeHash → verify.")]
    public static string EditSceneSafely() => EditSceneText;

    private const string EditSceneText = """
        To edit a scene safely (no lost updates from concurrent edits):
        1. Call `scene_hierarchy` to read the current state. Note the returned `contentHash`.
        2. Apply your change with `scene_object_patch` (or `scene_component_add`/`remove`), passing the
           hash as `expectedBeforeHash`. If the scene changed since your read, you'll get a
           `stale_state` error — re-read and retry with the fresh hash.
        3. Pass a stable `idempotencyKey` (e.g. "move-player-to-spawn") so a network-retry doesn't
           double-apply the patch.
        4. Call `scene_hierarchy` again to verify your change landed as intended.
        Use `dryRun: true` first to preview the delta without writing.
        """;

    [McpServerPrompt(Name = "import_and_reference_asset")]
    [Description("Workflow for importing a new asset and wiring it into a material/scene: import → read GUID → patch the reference.")]
    public static string ImportAndReferenceAsset() => ImportAssetText;

    private const string ImportAssetText = """
        To import a new asset and reference it:
        1. Call `asset_import` with the source file's absolute path and a destination under Assets/
           (e.g. Assets/Textures/Grass.png). Note the returned `guid`.
        2. To bind it into a material: call `material_patch` with a patch path
           `_properties/_textures/_MainTex/AssetID` and the new GUID as the value. Pass the
           `contentHash` from a prior `material_inspect` as `expectedBeforeHash`.
        3. To reference it from a script/component, the GUID is what `AssetID` fields hold — use
           `scene_component_add`/`scene_object_patch` to set the field.
        Asset moves preserve the GUID (references survive); deletes break references, so check
        `journal_query` first to see if anything points at the asset.
        """;

    [McpServerPrompt(Name = "trace_history")]
    [Description("How to use the operation journal to answer 'how did this project/scene get to its current state?'.")]
    public static string TraceHistory() => TraceHistoryText;

    private const string TraceHistoryText = """
        To understand how a project evolved:
        1. Call `journal_query` with a `targetAsset` filter (e.g. "Assets/Scenes/Main.scene") to see
           the change history for that file, newest-first. Each entry has author, intent, timestamp,
           and a replayable delta.
        2. Filter by `byAuthor` to see only AI-driven changes (e.g.
           "xengine-cli.scene-object-patch") vs human editor changes ("editor").
        3. The `intent` field is the natural-language reason recorded at write time — search it with
           `intentContains` to find a specific change.
        4. The `delta` is field-level (set/delete ops on paths), so you can see exactly what changed
           without diffing whole files.
        """;

    [McpServerPrompt(Name = "edit_pipeline_safely")]
    [Description("Read-check-write loop for Universal UniversalRenderPipelineAsset / UniversalRenderData Echo files: inspect hash, patch with expectedBeforeHash, apply quality.")]
    public static string EditPipelineSafely() => EditPipelineText;

    private const string EditPipelineText = """
        To edit a Universal pipeline or renderer asset safely:
        1. Call `asset_data_inspect` on a `.pipeline` or `.renderer` path (Echo text under Assets/,
           never Library/cache). Note `contentHash`.
        2. Patch with `asset_data_patch`, passing that hash as `expectedBeforeHash` and a stable
           `idempotencyKey`. Typical paths: RenderScale, Hdr, ShadowsEnabled, ShadowAtlasSize,
           SoftShadowQuality (0/1/2), Antialiasing (0 None / 1 MSAA / 2 TAA), MsaaSamples,
           Renderer/AssetID, RenderingPath (0 Forward / 1 Deferred / 2 ForwardPlus / 3 DeferredPlus), Kind (0 Universal / 1 Renderer2D).
        3. After a live editor picks up the file (`runtime_refresh`), call `UniversalRenderPipelineAsset.ApplyQuality()`
           (or re-apply Graphics settings so UniversalPipelineAssignment re-resolves the chain
           quality-tier override → Graphics default) so shadow knobs hit QualitySettings.
        4. Do not replace LightBVH with a per-object additional-light cap. GTAO/SSR/Decal/SS-shadows
           are runtime RendererFeatures, not `.renderer` fields — see inspect-pipeline.
        Use `dryRun: true` first. Skills: inspect-pipeline / edit-pipeline.
        """;

    [McpServerPrompt(Name = "compile_shadergraph")]
    [Description("Inspect, optionally patch, then compile an Echo .shadergraph to dual GLSLPROGRAM + SLANGPROGRAM.")]
    public static string CompileShaderGraph() => CompileShaderGraphText;

    private const string CompileShaderGraphText = """
        To compile a Shader Graph:
        1. Call `shadergraph_inspect` on a `.shadergraph` path. Confirm Target (0 Unlit / 1 Lit) and
           that a MasterUnlit or MasterLit node exists. Note `contentHash`.
        2. If you need to change nodes/edges/properties, `shadergraph_patch` with expectedBeforeHash
           and a stable idempotencyKey. Paths: ShaderName, Nodes/$values/<i>/..., Edges/$values/<i>/...
        3. Call `shadergraph_compile` (optional `--out` `.shader`). The source must contain both
           GLSLPROGRAM and SLANGPROGRAM, `#include "ShaderLibrary/Core"`, and for Lit
           CalculateForwardLighting / StandardCalculateLighting (LightBVH — do not emit a 8-light
           Forward+ loop).
        4. Missing master or a cycle fails closed. After writing, `runtime_refresh` if the editor is live.
        Skills: inspect-shader-graph / edit-shader-graph.
        """;

    [McpServerPrompt(Name = "compile_vfxgraph")]
    [Description("Inspect, optionally patch, then compile an Echo .vfx Visual Effect Graph to compute + output Shader DSL.")]
    public static string CompileVfxGraph() => CompileVfxGraphText;

    private const string CompileVfxGraphText = """
        To compile a Visual Effect Graph:
        1. Call `vfxgraph_nodes` if you need the context/block/operator catalog.
        2. `vfxgraph_create` with template Empty/Loop/Burst/Trail/Mesh/Subgraph/Decal into Assets/ if the .vfx does not exist yet.
        3. Call `vfxgraph_inspect` on the `.vfx` path. Note `contentHash`, GraphName, Template, Capacity.
        4. If you need to change contexts/blocks/operators/edges, `vfxgraph_patch` with expectedBeforeHash
           and a stable idempotencyKey. Paths: GraphName, Capacity, Blocks/$values/<i>/..., Edges/$values/<i>/...
        5. Call `vfxgraph_compile` (optional `--out` `.shader`). Source contains SLANGPROGRAM compute
           Init/Update/WriteArgs plus Output, and `// VFXGRAPH_INIT_BLOCKS` / `// VFXGRAPH_UPDATE_BLOCKS`.
        6. Live editor (`--serve`): `runtime_vfx_state`, then `runtime_vfx_preview` (Play + PNG captures).
           Also `runtime_panel_screenshot` of `XEngine.VFX.Editor.VfxGraphWindow`.
        Skills: inspect-vfx-graph / edit-vfx-graph.
        """;

    [McpServerPrompt(Name = "author_animator_controller")]
    [Description("Build a locomotion AnimatorController from idle/walk clips: states, Speed parameter, BlendTree or transition, then validate.")]
    public static string AuthorAnimatorController() => AuthorAnimatorText;

    private const string AuthorAnimatorText = """
        To author a locomotion AnimatorController:
        1. Inspect clips with `clip_inspect`. Note GUIDs and durations.
        2. `animator_parameter_add` Speed (Float) on the .controller. Pass expectedBeforeHash + idempotencyKey.
        3. `animator_state_create` Idle and Walk (or one Locomotion state) with `--clip-guid`.
        4. For a 1D BlendTree: `blendtree_patch` on the locomotion state (ParameterName=Speed, children thresholds).
           Otherwise `animator_transition_create` Idle→Walk with a Speed Greater condition.
        5. `animator_validate` and fix missing-motion / missing-parameter / dead-transition.
        6. Live check: `runtime_animator_play` / `runtime_animator_set_parameter` / `runtime_animator_state`,
           then `runtime_panel_screenshot` of AnimatorWindow.
        Copy cli/samples/workflows/author-locomotion.json into the project's .xengine/workflows/ to run this as a DAG.
        """;

    [McpServerPrompt(Name = "debug_animation_state")]
    [Description("Diagnose a live Animator: snapshot state/parameters, optionally set a parameter or play a state, then screenshot.")]
    public static string DebugAnimationState() => DebugAnimationStateText;

    private const string DebugAnimationStateText = """
        To debug a live Animator (editor --serve):
        1. `runtime_selection` query to get the GameObject identifier, or pick it from the hierarchy.
        2. `runtime_animator_state` for per-layer current state, normalizedTime, IsInTransition, parameter snapshot.
        3. If stuck: `runtime_animator_play` the expected state, or `runtime_animator_set_parameter` (Float/Int/Bool/Trigger).
        4. To inspect a clip pose, `runtime_animation_preview` with time in seconds (time = frame / frameRate).
        5. `runtime_panel_screenshot` AnimatorWindow or AnimationClipWindow, and `runtime_logs` for errors.
        Offline graph issues: `animator_validate` on the .controller.
        """;
}
