# XEngine AI Skills

This directory holds **agent-facing skills** (one `SKILL.md` per task) that teach an AI how to
operate an XEngine project via the `xengine-cli` / MCP tool surface. They use progressive
disclosure: an agent reads this index first, then the specific skill it needs.

> These are distinct from the engine's developer-facing performance skills in
> [`/skills/`](../../skills/) (LINQ/hot-path/rendering rules for engine *contributors*). These
> AI skills are for operating a *project* as an agent.

## ⚠️ File format basics (read this first)

XEngine source assets are **Echo text** (human-readable, git-diffable) — you can read and edit them
directly. The engine also writes **binary caches** that look similar but must NOT be touched:

| Location | Format | AI-editable? |
|---|---|---|
| `Assets/**/*.scene`, `.prefab`, `.mat`, `.asset`, `.mesh`, `.pipeline`, `.renderer`, `.volumeprofile`, `.shadergraph`, `.vfx`, `.navmesh` | **Echo text** | ✅ Yes — all read/patch tools target these |
| `Assets/**/*.meta` | **Echo text** | ✅ Yes (but usually auto-managed) |
| `Library/cache/*.asset` | **Echo BINARY** | ❌ No — import cache, same `.asset` extension but binary |
| `Library/metadata.db` | Echo text (internal index) | ❌ No — engine-managed |

**Common pitfall:** `Library/cache/{guid}.asset` shares the `.asset` extension with source assets but
is binary. If a read tool errors with "Invalid value found while reading", you're pointing at a cache
file — re-path to the `Assets/` source. `asset_search` excludes `Library/` to prevent this.

## Available skills

| Skill | What it does |
|---|---|
| [inspect-scene](inspect-scene/SKILL.md) | Read a `.scene`'s hierarchy/transforms/components offline. |
| [inspect-animator](inspect-animator/SKILL.md) | Read a `.controller` (`animator_inspect` / `animator_validate`) and optionally screenshot the Animator window. |
| [edit-animator](edit-animator/SKILL.md) | Patch `.controller` states/transitions/parameters/layers/nested state machines/BlendTrees (journaled). |
| [edit-animation-clip](edit-animation-clip/SKILL.md) | Inspect/patch `.anim` curve keyframes (journaled). |
| [edit-timeline](edit-timeline/SKILL.md) | Bind / scrub / trim Timeline clips in the live Timeline window. |
| [edit-scene](edit-scene/SKILL.md) | Create / patch / delete GameObjects; every change is journaled with provenance. |
| [edit-prefab](edit-prefab/SKILL.md) | Read / patch `.prefab` files (recursive GO search, structured edits). |
| [trace-history](trace-history/SKILL.md) | Query the operation journal — how a scene/project evolved (author/intent/delta). |
| [inspect-pipeline](inspect-pipeline/SKILL.md) | Read Universal `.pipeline` / `.renderer` Echo assets (HDR, scale, shadows). |
| [edit-pipeline](edit-pipeline/SKILL.md) | Patch pipeline/renderer assets via `asset_data_patch` (journaled). |
| [inspect-volume](inspect-volume/SKILL.md) | Read Universal `.volumeprofile` Echo assets (volume components). |
| [inspect-shader-graph](inspect-shader-graph/SKILL.md) | Read `.shadergraph` Echo assets (nodes/edges/properties + content hash). |
| [edit-shader-graph](edit-shader-graph/SKILL.md) | Patch `.shadergraph` (journaled) and compile dual GLSL+Slang Shader DSL. |
| [inspect-vfx-graph](inspect-vfx-graph/SKILL.md) | Read `.vfx` Echo assets (contexts/blocks/operators/edges + content hash). |
| [edit-vfx-graph](edit-vfx-graph/SKILL.md) | Create / patch `.vfx` (journaled) and compile compute + output Shader DSL. |
| [inspect-render-debug](inspect-render-debug/SKILL.md) | Live Rendering Debugger snapshot (`runtime_render_debug`) + panel screenshot. |
| [inspect-frame-debug](inspect-frame-debug/SKILL.md) | Live Frame Debugger (`runtime_frame_debug`): per-draw GPU events + RT/shader state. |
| [edit-volume](edit-volume/SKILL.md) | Patch `.volumeprofile` via `volume_patch`, or scene Volume knobs via `scene_component_patch`. |
| [navigate](navigate/SKILL.md) | Inspect / query / export / patch `.navmesh` offline; bake and steer agents live. |
| [inspect-assets](inspect-assets/SKILL.md) | List assets + read GUID/importer from `.meta` sidecars. |
| [build-project](build-project/SKILL.md) | Headless build + headless run of a built player. |
| [run-scene-live](run-scene-live/SKILL.md) | Drive a LIVE editor (runtime state, logs, playmode, live scene) via the in-process server. |
| [observe-runtime](observe-runtime/SKILL.md) | Observe/act on a live editor: screenshot, perf stats, selection, menu, eval. |
| [run-workflow](run-workflow/SKILL.md) | Define/run declarative tool DAGs (.xengine/workflows/*.json). |
| [generate-asset](generate-asset/SKILL.md) | Generate art (3D/motion/sprites/skybox/sfx) from prompts via gen_submit→status→fetch; playable-prop loop adds budget remesh, AABB collider, prefab extract. |
| [restyle-spine-atlas](restyle-spine-atlas/SKILL.md) | Same Spine rig, new paint: `gen_submit` `kind=texture` + `atlasPath`/`region`; mock / PixelLab / ComfyUI / openai (GPT Image 2). |
| [play-spine-animation](play-spine-animation/SKILL.md) | Drive live Spine skeletons: `runtime_spine_state/play/queue/set_skin`; offline `scene_component_add --type SkeletonAnimation`. |
| [generate-spine](generate-spine/SKILL.md) | AI Spine character production: `spinegen_slice/fill/rig/skin/animate` + one-shot `spinegen-pipeline` workflow; editor wizard under Window/AI. |
| [diagnose](diagnose/SKILL.md) | Diagnose environment/build/code issues with xengine_doctor + code_symbols. |
| [manage-assets](manage-assets/SKILL.md) | Import / move / delete assets + mint .meta sidecars (full asset lifecycle). |
| [edit-material](edit-material/SKILL.md) | Read + patch .mat files (shader, colors, floats, texture GUIDs). |
| [edit-scene-settings](edit-scene-settings/SKILL.md) | Query + patch scene-level lighting/skybox (Fog/Ambient/Skybox). |
| [write-code](write-code/SKILL.md) | Create / edit / delete C# scripts (auto hot-reload via Roslyn). |
| [search-code](search-code/SKILL.md) | Find symbol declarations (Roslyn) or any text via regex. (regex `find_in_file` ships in both editions; the Roslyn `code_symbols` half is engine-dev only.) |
| [run-tests](run-tests/SKILL.md) | Run xUnit tests via `dotnet test` with structured pass/fail counts. (engine-dev only — targets the engine solution's test suite.) |
| [manage-packages](manage-packages/SKILL.md) | Search/install/update/downgrade packages from the npm registry (`package_search/versions` + `package_add/remove`), or publish a package directory (`package-publish`). |
| [publish-npm-package](publish-npm-package/SKILL.md) | npm publishing rules for package authors: pre-publish checklist, granular token / OTP, dry-run, append semantics + CDN lag, post-publish verification, dual-channel discipline. |
| [install-platform-toolchain](install-platform-toolchain/SKILL.md) | Install a platform build toolchain from its module manifest (Android JDK/SDK/NDK via sdkmanager, HarmonyOS ohos-sdk/hdc, iOS Xcode locate/preflight) into the managed SDK root; editor-level module store (npm, no project needed) with launcher Settings UI + CLI auto-install; offline reruns, license consent, actionable guidance. |

## Tool surface
The skills map onto these `xengine-cli` commands / MCP tools (116 offline + 29 runtime descriptors
= 145 advertised — call `xengine_capabilities` for the live list, and `prompts/list` for reusable
prompt templates):

> **Editions (引擎开发版 vs 用户版):** the same `xengine-cli` binary serves two audiences, resolved
> from the project layout (engine repo → `engine-dev`, game project → `user`) or pinned with
> `--edition`. The **user** edition registers game-development tools only — engine-repo tools
> (`code_symbols`, `run_tests`) never register there, the capability snapshot reflects the
> registered surface truthfully, and `xengine-cli harness-install` seeds user projects with
> `.mcp.json` + `AGENTS.md` + this skill set.

- **system** (offline): `xengine_health`, `xengine_capabilities`, `xengine_doctor`, `project_inspect`, `project_roots`
- **scene-read** (offline): `scene_list`, `scene_hierarchy`
- **scene-write** (offline, journaled): `scene_object_patch`, `scene_object_create`, `scene_object_delete`, `scene_component_add`, `scene_component_remove`, `scene_settings_query`, `scene_settings_patch`, `animator_component_add`, `sprite_wire`
- **prefab** (offline, journaled): `prefab_hierarchy`, `prefab_object_patch`
- **asset** (offline, journaled): `asset_search`, `asset_inspect`, `asset_import`, `asset_move`, `asset_delete`, `asset_subassets`, `asset_mesh_stats`, `sprite_slice`
- **generate** (offline, journaled fetch): `gen_submit`, `gen_status`, `gen_fetch`, `gen_ensure_budget`, `style_anchor_list`, `style_anchor_upsert`, `gen_candidates`, `gen_candidate_choose`, `gen_candidate_discard`
- **playable-prop** (offline, journaled): `scene_collider_add`, `prefab_create`
- **asset-data** (offline, journaled): `asset_data_inspect`, `asset_data_patch` (generic ScriptableObject data)
- **volume** (offline, journaled): `volume_inspect`, `volume_patch` (`.volumeprofile`)
- **shadergraph** (offline, journaled patch): `shadergraph_inspect`, `shadergraph_patch`, `shadergraph_compile` (`.shadergraph` → dual GLSL+Slang)
- **vfxgraph** (offline, journaled patch): `vfxgraph_inspect`, `vfxgraph_patch`, `vfxgraph_compile`, `vfxgraph_nodes`, `vfxgraph_create` (`.vfx` → compute + output Shader DSL)
- **navigation** (offline, journaled settings patch): `nav_inspect`, `nav_query_path`, `nav_export`, `nav_settings_patch` (`.navmesh`)
- **animation** (offline, journaled writes): `character_wire` (clips → `.controller` + scene Animator), `animator_inspect`, `animator_validate`, `animator_state_create` / `animator_state_patch` / `animator_state_delete`, `animator_transition_create` / `animator_transition_patch` / `animator_transition_delete` / `animator_transition_reorder`, `animator_parameter_add` / `animator_parameter_remove`, `animator_layer_add` / `animator_layer_patch`, `animator_statemachine_create` / `animator_statemachine_delete` / `animator_statemachine_move`, `clip_inspect`, `clip_retarget`, `clip_patch`, `clip_curve_patch`, `clip_event_add` / `clip_event_patch` / `clip_event_remove`, `blendtree_inspect` / `blendtree_patch`, `overridecontroller_inspect` / `overridecontroller_patch`, `avatarmask_inspect` / `avatarmask_patch`
- **journal**: `journal_query`
- **code**: `find_in_file` (regex content search); `code_symbols` (Roslyn symbol index — **engine-dev only**)
- **script** (offline, journaled): `script_create`, `script_delete`, `script_apply_edits`
- **build/run/test**: `build_project`, `run_headless`, `run_tests` (**engine-dev only**, xUnit via
  `dotnet test`); editor CLI `XEngine.Editor --refresh-assets --project <root>` (headless import → `Library/cache`)
- **harness**: `xengine-cli harness-install / harness-status / harness-upgrade` (seed the agent
  scaffolding — `.mcp.json` / `AGENTS.md` / skills — into a game project; both editions)
- **workflow**: `workflow_list`, `workflow_run`
- **prompts**: `diagnose_build_failure`, `edit_scene_safely`, `import_and_reference_asset`, `trace_history`, `edit_pipeline_safely`, `compile_shadergraph`, `compile_vfxgraph`, `author_animator_controller`, `debug_animation_state`
- **runtime** (in-process server only — editor running with `--serve`): `runtime_state`,
  `runtime_logs`, `runtime_playmode`, `runtime_live_scene`, `runtime_screenshot`, `runtime_stats`,
  `runtime_render_debug`, `runtime_panel_screenshot`, `runtime_selection`, `runtime_menu`,
  `runtime_eval`, `runtime_refresh` (force asset-db refresh), `runtime_animation_bind`,
  `runtime_animation_record_key`, `runtime_animator_state`, `runtime_animator_set_parameter`,
  `runtime_animator_play`, `runtime_animation_preview`, `runtime_nav_state`, `runtime_nav_bake`,
  `runtime_nav_agent_move`, `runtime_nav_agent_state`, `runtime_vfx_state`, `runtime_vfx_preview`

## How an agent should work
1. **Read before write.** Use `inspect-scene` / `inspect-assets` / `project_roots` to learn current state.
2. **Dry-run edits.** Preview with `--dry-run`, then apply with a meaningful `--intent`.
3. **Edit safely.** Pass the content hash from your last read as `expectedBeforeHash` to refuse
   clobbering concurrent edits, and a stable `idempotencyKey` so a retry doesn't double-apply.
4. **Write code.** Use `write-code` to create/edit C# scripts; the editor auto-recompiles + hot-reloads.
5. **Refresh + test.** After batch file writes, `runtime_refresh` (live editor) to pick them up;
   `run-tests` to verify nothing broke.
6. **Trace history.** Use `trace-history` to understand *why* things are as they are, and to
   make changes that build on documented provenance rather than blind edits.
7. **Live testing.** Use `run-scene-live` to verify changes in a running editor (playmode + logs).
8. **Chain.** Use `run-workflow` to make multi-step pipelines reproducible.
