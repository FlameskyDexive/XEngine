---
name: inspect-pipeline
description: Read a Universal UniversalRenderPipelineAsset (.pipeline) or UniversalRenderData (.renderer) offline via asset_data_inspect. Echo text, journal-safe.
---

# inspect-pipeline

Use when an AI needs to inspect URP pipeline defaults (HDR, render scale, shadows) or renderer data.

These assets are Echo text. Do **not** read `Library/cache/`.

A **new Blank project** already contains this layout (Unity 6.7 URP template naming):

- `Assets/Settings/Pipeline/New Universal Render Pipeline Asset.pipeline` — HDR pipeline asset (class `UniversalRenderPipelineAsset`)
- `Assets/Settings/Pipeline/New Universal Render Pipeline Asset_Renderer.renderer` — Forward Universal renderer (class `UniversalRenderData`)
- `Assets/UniversalRenderPipelineGlobalSettings.asset` — shader-variant stripping switches
- `Assets/Settings/DefaultVolumeProfile.volumeprofile` — AgX tonemap
- `Assets/Scenes/SampleScene.scene` — Main Camera (`UniversalAdditionalCameraData` + AudioListener), Directional Light (`UniversalAdditionalLightData`), Global Volume
- `ProjectSettings/Graphics.yaml` — `DefaultPipeline/AssetID` + `PipelineGlobalSettings/AssetID`
- `ProjectSettings/Quality.yaml` — per-tier `PreviewPipeline` / `FullPipeline` overrides (Unity 6.7 customRenderPipeline per level)

Effective pipeline = quality-tier override → Graphics default → engine built-in (resolved by `UniversalPipelineAssignment`; tier switches re-resolve immediately).

Pre-rename files (`Settings/Universal.pipeline` / `$type ...Scriptable.PipelineAsset|RendererData`) still load — `UniversalPipelineTypeCompat` aliases the old Echo type names.

## Inspect
```bash
xengine-cli asset-data-inspect --project <root> --asset Assets/Settings/Universal.pipeline
```

Returns `{ $type, fields[], contentHash }`. Pass `contentHash` to `asset_data_patch`.

### UniversalRenderPipelineAsset paths (.pipeline)
- `Hdr` — `true` / `false`
- `RenderScale` — 0.1–2
- `ShadowAtlasSize` — power of two 512–8192
- `ShadowDistance` — 1–1000
- `ShadowsEnabled` — `true` / `false`
- `SoftShadowQuality` — `0` Low (4 taps) / `1` Medium (8 taps, default) / `2` High (16 taps). Soft PCF kernel only; per-light Hard/Soft is unchanged.
- `Antialiasing` — `0` None (default, M1 pixel floor) / `1` MSAA / `2` TAA. MSAA renders at native resolution into a real multisample color+depth target and resolves it to a single-sample target after transparents (D3D12 `ResolveSubresource` / Vulkan `vkCmdResolveImage`); it no longer inflates the render scale. TAA injects the existing temporal effect on Base cameras and upsamples when `RenderScale` < 1. Camera FXAA/SMAA/TAA still win over the pipeline default.
- `MsaaSamples` — `2` / `4` / `8` (default `4`). Used only when `Antialiasing` is MSAA, and clamped to what the device reports (`GraphicsDeviceCapabilities.MaxSampleCount`). MSAA silently falls back to 1× for configurations whose single-sample interop the RHI cannot serve: the 2D and Deferred renderers, `Depth`/`Nothing` clear flags, frames needing the editor grid/gizmo depth copy, and cameras with after-opaques image effects (SSAO/SSR/SSGI sample SceneColor before the resolve). `RenderStats.Last.MsaaSamples` reports the sample count actually used.
- `UpscalingFilter` — `0` Auto (default) / `1` Linear / `2` Point / `3` FSR / `4` STP. Auto becomes Linear at native scale and STP when TAA or STP is selected. STP injects the existing TAA history path on Base cameras (still 7 built-in passes). FSR is recorded as a spatial filter (bilinear until an FSR shader lands).
- `AllowPostProcessAlphaOutput` — `true` / `false` (default false). When HDR is on, keep destination alpha through post.
- `Renderer/AssetID` — GUID of a `.renderer` asset (may be empty)

### UniversalRenderData paths (.renderer)
- `DepthPriming` — `true` / `false` (reserved)
- `RenderingPath` — `0` Forward (default) / `1` Deferred GBuffer / `2` ForwardPlus / `3` DeferredPlus. Overlay cameras are skipped when Deferred or DeferredPlus. Transparents stay Forward. LightBVH is the clustered additional-light loop on every Universal 3D path (including Forward and ForwardPlus). Ignored when `Kind` is Renderer2D.
- `Kind` — `0` Universal 3D (default) / `1` Renderer2D. Renderer2D lights `SpriteRenderer`s with `Light2D` (point / global / sprite cookie) and `ShadowCaster2D`; it does **not** use LightBVH or the 3D shadow atlas. Overlay stacking stays enabled. Player builds strip `Default/SpriteLit`, `Default/Light2D`, and `Default/Shadow2D` unless `Kind` is Renderer2D.
- `LodCrossFade` — `0` None (default) / `1` Dither / `2` Stencil. Stencil uses bits 2–3 (`LodCrossFade.StencilMask`).

## GTAO / SSR / ScreenSpace Decal / ScreenSpace shadows RendererFeatures (runtime)

Not serialized on `.renderer` yet. Registered engine-internally (`UniversalRenderPipeline.AddRendererFeature`, internal like Unity's pipeline internals; tests use the same surface):

```csharp
UniversalRenderPipeline.Default.AddRendererFeature(new GTAORendererFeature());
UniversalRenderPipeline.Default.AddRendererFeature(new ScreenSpaceReflectionRendererFeature());
UniversalRenderPipeline.Default.AddRendererFeature(new ScreenSpaceGlobalIlluminationRendererFeature());
UniversalRenderPipeline.Default.AddRendererFeature(new ScreenSpaceDecalRendererFeature());
UniversalRenderPipeline.Default.AddRendererFeature(new ScreenSpaceShadowsRendererFeature());
```

GTAO/SSR/SSGI: tune `feature.Effect` (Intensity, Radius, UseTemporal, …). An enabled Feature skips `Camera.Effects` of the same type (no double apply). Without the Feature, camera GTAO/SSR still run; SSGI is Feature-only. A disabled Feature does not request prepass or CopyDepth. An empty Feature still leaves 7 built-in passes on Forward (default). Enabled SSGI requests Color+Depth+Normal (and Motion when temporal) so CopyColor and CopyDepth stay live. Deferred (`RenderingPath` = 1) and DeferredPlus (`3`) enqueue 8 built-in passes (GBuffer + LightBVH lighting); Overlay cameras are skipped. ForwardPlus (`2`) stays at 7 built-in passes like Forward. Renderer2D (`Kind` = 1) stays at 7 built-in passes (setup, 2D shadows, 2D lights, after-opaques, sprites, post, final); Overlay cameras still render.

ScreenSpace Decal: not an ImageEffect. `DecalProjector` components register with `DecalSystem`. Feature disabled or zero projectors → 7 built-in passes, no prepass. One or more projectors → extra AfterOpaques pass + CopyDepth (Depth|Normal), prepass required. Does not gate on `QualitySettings.ImageEffectsEnabled`.

Screen-space shadows: optional directional contact shadows from camera depth. Does **not** replace the shadow atlas. Disabled Feature → 7 built-in passes, no extra occlusion. Enabled → CopyDepth (Depth), prepass required; lighting multiplies atlas lit-factor by the SS map (white / enabled=0 keeps pixels identical). Not gated on `QualitySettings.ImageEffectsEnabled`. Tune `Thickness` / `RayLength`; step count follows `SoftShadowQuality` (4/8/16).

## Shader variant stripping (player build)

`ShaderVariantStripper` pre-filters keywords from enabled `RendererFeature`s at **player collect** time (`VulkanShaderVariantCollector.CollectForBuild`). Editor import still compiles every declared variant. `UniversalRenderPipelineGlobalSettings.asset` (Graphics.yaml `PipelineGlobalSettings`) gates the pass, Unity 6.7-style:

- `ExportShaderVariants = false` → collection skipped entirely.
- `StripUnusedVariants = false` → every shader/keyword kept.
- `Strip2DUnusedVariants` (default **false**) → 2D shaders (`SpriteLit`/`Light2D`/`Shadow2D`) kept even without an active Renderer2D. With **no** settings asset assigned (legacy projects) the old feature-mask behavior applies (strip when `Kind` is Universal).
- `ShaderVariantLogLevel` 0–3 → per stripped shader / summary logging from the collector.
- `StripDebugVariants` / `StripUnusedPostProcessingVariants` are serialized for Unity parity; XEngine debug views and post-processing are not variant-driven yet, so they have no branch.

Feature-mask rules (unchanged):

- No enabled `ScreenSpaceDecalRendererFeature` → player package omits `Default/ScreenSpaceDecal` and any `DECAL` keyword combination.
- No enabled `ScreenSpaceShadowsRendererFeature` → omits `Default/ScreenSpaceShadows` and `SCREEN_SPACE_SHADOWS`.
- `RenderingPath` is Forward or ForwardPlus (default Forward) → omits `Default/DeferredLighting`. Set `UniversalRenderData.RenderingPath` to Deferred (`1`) or DeferredPlus (`3`) to keep it.
- `Kind` is Universal (default) and stripping of 2D is on → omits `Default/SpriteLit`, `Default/Light2D`, `Default/Shadow2D`. Set `UniversalRenderData.Kind` to Renderer2D (`1`) to keep them.
- GTAO / SSR stay (they also run from `Camera.Effects` without a Feature).
- Pass `ShaderVariantStripper.Unfiltered` to collect every built-in (editor/debug). Default collect uses `UniversalRenderPipeline.Default`'s Feature list.

## RenderGraph (execute-path DAG)

Runtime `ScriptableRenderer.Execute` compiles built-in + Feature passes plus cullable CopyColor / CopyDepth nodes. A copy runs only when a Feature `ConfigureInput` (or a pass `Input`) reads that imported texture (`CameraOpaqueColor` / `CameraDepth`). No consumer → both copies culled (`RenderStats.CulledGraphPasses` = 2, 7 executed). Depth Feature → CopyDepth kept, CopyColor culled. The unit-test enqueue list still appends copies when `RequestedInputs` is set (7 / 8 / 9); GPU execute uses the graph.

## ShaderConfig / ShaderLibrary (compile-time)

Not an Echo asset. Capacities live in `XEngine.Runtime.Rendering.ShaderConfig` and
`Assets/Defaults/ShaderLibrary/ShaderConfig.{glsl,hlsl}` (must stay in lockstep):

- `MaxShadowCasters` / `MAX_SHADOW_CASTERS` — 4
- `MaxBvhNodeVisits` / `LBVH_MAX_NODE_VISITS` — 4096
- `LightBvhTexelsPerLight` / `LBVH_TEXELS_PER_LIGHT` — 6
- `LightBvhTexelsPerNode` / `LBVH_TEXELS_PER_NODE` — 2
- `MaxReflectionProbes` / `MAX_REFLECTION_PROBES` — 2 (primary + blend; not Echo-patchable)
- `MaxRenderingLayers` / `MAX_RENDERING_LAYERS` — 8 (light/decal membership; independent of `LayerMask`)
- `MaxSoftShadowTaps` / `MAX_SOFT_SHADOW_TAPS` — 16 (High PCF / SS-shadow march cap; Low=4, Medium=8)

Shader Graph (M24) should emit `#include "ShaderLibrary/Core"` then Lighting / StandardLighting from `ShaderLibrary` include names. Do not patch these via `asset_data_patch`.

Or MCP: `asset_data_inspect`.
