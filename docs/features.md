# XEngine — Feature Tour

Detailed module inventory for XEngine V1.0.0f1. For the project overview, quick start and
platform matrix, see the [README](../README.md). Everything listed here is implemented in this
repository and covered by the automated test suite (6,000+ xUnit test methods).

---

## Core runtime & editor

| Module | What you get |
|---|---|
| **Object model** | GameObject + MonoBehaviour components, tags & layers, Inspector-configurable persistent callbacks, full lifecycle semantics |
| **Scenes & prefabs** | Scene system with fog and ambient lighting; nested prefabs with Apply / Revert / Break Instance and override tracking |
| **Editor** | Scene View, Hierarchy, Inspector, Project Browser, Console, Game View, Profiler, Package Manager, Preferences, Build Profiles; dockable & resizable panels with persisted layouts; drag & drop; multi-select; search and filtering; asset thumbnails and 3D previews; transform gizmos; full undo/redo; rebindable shortcuts; editor themes; in-editor playtest; script hot reload |
| **Localization** | Editor UI in 12 languages (EN, DE, ES, FR, IT, JA, KO, PL, PT, RU, TR, ZH) |
| **Scripting** | C# on .NET 10 / CoreCLR; `dotnet build` compiles Game and Editor assemblies; assembly definitions; managed and native plugins |
| **XTask** | Zero-allocation async / coroutine library replacing `Task` and `IEnumerator` |
| **Serialization** | Echo text serializer with a Roslyn source generator |
| **Input** | Input Action system with `.inputactions` assets and a dedicated editor: action phases, composites (WASD → Float2, D-pad), processors; keyboard, mouse and gamepad |
| **Math** | 64-bit math library — `Float4x4`, quaternions, `Transform2D`, AABB, Bounds, Frustum, Cone, Ray, Plane, LineSegment, Rect |

## Gameplay systems

| Module | What you get |
|---|---|
| **3D physics** | Rigid bodies, box / sphere / capsule / cylinder / cone / convex hull / mesh / terrain colliders, wheel colliders with suspension and grip slip, joints and constraints (ball-socket, hinge, hinge-angle, fixed-angle, cone limit, distance limit, twist angle, prismatic, universal, point-on-line, point-on-plane, angular and linear motors), character controller, triggers, layer filtering, raycasts and shape queries |
| **2D physics** | Per-scene `PhysicsWorld2D` (1 unit = 1 m, stepped in FixedUpdate), `Rigidbody2D`, box / circle / capsule / polygon / composite colliders, collision & trigger events, `PhysicsMaterial2D`, NonAlloc `Raycast` / `OverlapCircle` / `OverlapBox`, debug drawing |
| **Animation** | Skeletal animation and blendshapes; Animator Controller state machines with layers, AvatarMask, root motion, CrossFade and blend trees; humanoid bone mapping and retargeting; animation clip editor with curves and events |
| **Playable & Timeline** | Unity-compatible `PlayableGraph` / `AnimationPlayableOutput` / custom playables; visual Timeline editor with tracks, clips, blending and signals |
| **Navigation** | Recast/Detour-style navmesh baking, `NavMeshSurface` / `NavMeshAgent` / `NavMeshObstacle` / `NavMeshLink` / modifiers & volumes, tile cache for dynamic rebuilds, crowd simulation, path queries and a Navigation window |
| **Audio** | Spatial 3D audio with attenuation and Doppler; WAV / MP3 / OGG / FLAC; effect chains (delay, distortion, biquad filter, reverb, phaser) and custom `IAudioEffect` |
| **UI** | GameObject-driven UI including World Space; `RectTransform` layout, buttons, sliders, layout groups, drag & drop events; **XGUI** retained-mode package with rich text, hyperlinks, inline sprites, safe area, IME input and batch merging |
| **2D** | Sprites, sprite atlas, sorting groups, 9-slice draw modes, tilemaps with palettes and rule tiles, SpriteShape splines, pixel-perfect camera, 2D skeletal animation (SpriteSkin / Bone2D / Sprite Library / IK), Spine runtime, PSD and Aseprite importers |
| **Terrain** | Quadtree LOD heightmap terrain, splatmap painting, GPU-instanced grass, tree rendering with LOD distance, holes, and a dedicated sculpt / paint / grass / tree / settings editor |
| **VFX** | Node-based GPU Visual Effect Graph (spawn, initialize, update, output blocks; flipbooks, mesh output, lit particles, SDF and plane collision, turbulence, soft particles, subgraphs) plus the classic CPU particle system with GPU-instanced rendering |

## Content pipeline

| Module | What you get |
|---|---|
| **Asset database** | GUID references with `.meta` files, import cache and file watching with auto-reimport, custom importers via attributes, sub-assets with deterministic GUIDs, forward and reverse dependency tracking, multi-threaded loading |
| **Formats** | Models: GLTF / GLB / OBJ / FBX · Textures: PNG / JPG / BMP / TGA / PSD / HDR / DDS / EXR · Audio: WAV / MP3 / OGG / FLAC · 2D: Aseprite, layered PSD with bones |
| **AssetBundles & hot update** | Bundle build pipeline, provider/handle reference counting, manifest versioning, virtual file system with asset streaming, LZ4 compression, downloader and decryption layering |
| **Build system** | Standalone player export, packed asset files (`.xenginepak`), used-assets-only export, per-platform build configurations, build profiles UI |
| **Provenance** | Every edit records author / intent / delta into a queryable journal — for humans and agents alike |

## Graphics & rendering

**Six graphics backends behind one RHI abstraction**: Direct3D 12 · Vulkan · OpenGL · Metal ·
WebGPU · WebGL2. Vulkan, D3D12 and OpenGL are the fully accepted desktop paths today; Metal,
WebGPU and WebGL2 are in active bring-up.

- **Dedicated render thread** and an extensible, scriptable render pipeline
- **Universal Render Pipeline (URP)** — `PipelineAsset` / `ScriptableRenderer` / `RendererFeature` / Volume / RenderGraph / RTHandle / camera stacking, aligned to URP 17.7 (Forward+, STP, SSGI, APV, LOD cross-fade)
- **Render paths** — Forward-Lit, Forward+, deferred GBuffer, Renderer2D
- **RenderGraph** with transient resources and raster / compute / unsafe passes
- **Realtime GI** — screen-space GI plus DDGI dynamic irradiance probe volumes, accepted across all six backends
- **Virtual Geometry** — Nanite-class GPU culling, cluster selection and residency management
- **Shader Graph** — node editor plus CLI `inspect` / `patch` / `compile`, emitting GLSL and Slang
- **Custom shader language** with `#include`, multi-pass, keywords and variants
- **Lighting** — HDR + PBR metallic workflow, point / spot / directional lights, shadow maps for every light type, up to 4 cascades, point-light cube shadows, a dynamically packed shadow atlas, light cookies, rendering layers, reflection probes, baked light probes, progressive CPU lightmap baking and UV unwrapping
- **Post-processing** — ACES / Reinhard / Uncharted / Filmic / Melon / AgX tonemapping, bloom, FXAA / SMAA / TAA, GTAO, stochastic SSR, Bokeh depth of field, volumetric fog, lens flare, screen-space shadows, screen-space decals, cinematic grain / vignette / chromatic aberration; optional Post Processing package with a PPv2-style Layer / Volume / Profile workflow
- **Volume framework** — global and local blending via `.volumeprofile` assets
- **Renderers** — mesh, skinned mesh (skeletal + blendshapes), line, sprite, GPU instancing, frustum culling, LOD cross-fade with stencil
- **Also** — render textures, Texture3D, grab pass (depth-aware refraction / heat haze / frosted glass), transparency, procedural / cubemap / gradient skyboxes, STP temporal upscaling, MSAA
- **Debugging** — Frame Debugger (event tree, RT preview, full state inspection) and Rendering Debugger

## First-party packages

Core 2D and rendering primitives are built in; everything below ships as an optional package
installable from the in-editor Package Manager (38 first-party packages total under `Packages/`).

| Package | What it adds |
|---|---|
| `com.xengine.xgui` | Retained-mode GameObject UI |
| `com.xengine.xui` | AI-first UI application layer on top of XGUI — compact `.xui` XML screens |
| `com.xengine.timeline` | Cutscene / sequence editor on the PlayableGraph runtime |
| `com.xengine.vfx` | Node-based GPU Visual Effect Graph |
| `com.xengine.terrain` | Heightmap terrain with splat layers, grass, trees and sculpting tools |
| `com.xengine.ai.navigation` | Navmesh bake, query, agents and the Navigation window |
| `com.xengine.spine` | Esoteric Spine skeletal 2D animation runtime |
| `com.xengine.postprocessing` | PPv2-style post-processing layer / volume / profile |
| `com.xengine.cinemachine` | Virtual camera system |
| `com.xengine.feature.2d` | Meta-package pulling in every first-party 2D subpackage |
| `com.xengine.2d.*` | `common`, `sprite`, `tilemap`, `tilemap.extras`, `animation`, `spriteshape`, `pixel-perfect`, `psdimporter`, `aseprite`, `tooling` |

## AI-native tooling (MCP)

XEngine exposes itself to AI agents and CLIs over **MCP on stdio**, in two channels:

- **Offline host — 120+ tools** (`Tools/XEngine.Ai.Host`, the `xengine-cli`). Works on the project
  on disk with no editor running: scene and prefab editing, asset import / search / inspect /
  patch, animator state machines, blend trees, avatar masks, clip curves and events, navmesh,
  shader graph, VFX graph, volumes, materials, script create / edit / delete, code symbol search,
  package management, builds, headless runs, test runs, workflows and the generation gateway.
- **In-editor runtime — 20+ tools** (`--serve`). Requires a live editor: playmode control, live
  scene mutation, selection, menus, expression eval, screenshots and panel screenshots, render
  stats, frame debug, render debug, animator and animation preview, navmesh bake and agent
  control, Spine playback and skin switching, VFX preview.

**Safety rails** are part of the protocol, not an afterthought: a capability catalog with a
four-level `RiskLevel`, `dryRun` previews, hash pre-checks against concurrent edits, idempotency
keys, and a provenance journal recording author, intent and delta for every mutation.

**40+ authoring skills** (`cli/skills/`) document each workflow — editing scenes, animators,
shader graphs, VFX graphs, timelines, materials, volumes, pipelines, XUI screens; inspecting
frames and render state; generating assets; running tests, workflows and live scenes — so an
agent can read the skill and get it right the first time.

**Generative asset gateway** — `gen_submit` / `gen_status` / `gen_fetch` / `gen_candidates` and
friends front six providers: **Meshy** (3D models), **PixelLab** (sprites), **Blockade Labs**
(skyboxes), **ElevenLabs** (audio), **OpenAI GPT Image**, and self-hosted **ComfyUI**. The
generation recipe is written into the asset's `.meta`, so any asset can be regenerated
reproducibly.

## Performance culture

Performance decisions are made from **measured frame time, throughput, allocation and GPU cost** —
never from intuition. The repository enforces that:

- **`XEngine.Analyzers` (PR0001–PR0007)** — Roslyn analyzers for per-frame CPU hot paths: banned
  LINQ, `[HotPath]` detection, allocation detection. Relaxing a rule or widening an allow-list
  without benchmark evidence and matching tests is not accepted.
- **Performance contract tests** — e.g. `FrameLoopPerformanceContractTests`,
  `StageAPerformanceContractTests`, plus zero-allocation probes that reproduce allocation byte
  counts round over round.
- **Byte-exact screenshot regression** across graphics backends, so a rendering change that
  alters pixels cannot land unnoticed.

Representative measurements from the engine's development: `XTask` synchronous completion at
**0.93 ns / 0 B**; six Transform paths at **0 B** allocation; editor idle at **0.365 % of one CPU
core** (empty scene, idle, Release + Vulkan, 30 s sample).
