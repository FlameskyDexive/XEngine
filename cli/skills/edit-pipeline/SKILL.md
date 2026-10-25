---
name: edit-pipeline
description: Patch a Universal UniversalRenderPipelineAsset (.pipeline) or UniversalRenderData (.renderer) via asset_data_patch. Journaled, drift-protected, Echo text.
---

# edit-pipeline

Use when an AI needs to change pipeline defaults (render scale, HDR, shadow atlas) without a dedicated pipeline tool. Reuses generic ScriptableObject patch.

## Patch
```bash
xengine-cli asset-data-inspect --project <root> --asset "Assets/Settings/Pipeline/New Universal Render Pipeline Asset.pipeline"
# note contentHash

xengine-cli asset-data-patch --project <root> --asset "Assets/Settings/Pipeline/New Universal Render Pipeline Asset.pipeline" \
  --patches '[{"path":"RenderScale","value":"0.75"},{"path":"Hdr","value":"true"}]' \
  --expected-before-hash <hash> \
  --idempotency-key <key> --intent "lower scale and enable HDR"
```

`Antialiasing` is `0` None / `1` MSAA / `2` TAA. `MsaaSamples` is `2` / `4` / `8`. TAA upscale is `Antialiasing=2` plus `RenderScale` below 1.

Paths match [inspect-pipeline](../inspect-pipeline/SKILL.md). Same drift protection / idempotency as `material_patch`.

After patching, the running editor must reload the asset (or `runtime_refresh`) before Play mode picks up values. Call `UniversalRenderPipelineAsset.ApplyQuality()` (or re-apply Graphics settings so `UniversalPipelineAssignment` re-resolves quality-tier override → Graphics default) for shadow knobs (`ShadowAtlasSize`, `ShadowDistance`, `ShadowsEnabled`, `SoftShadowQuality`) to hit `QualitySettings`.

GTAO / SSR / ScreenSpace Decal / ScreenSpace shadows are runtime `RendererFeature`s (`AddRendererFeature`), not `.renderer` fields — see [inspect-pipeline](../inspect-pipeline/SKILL.md).
`SoftShadowQuality` is Echo on `.pipeline` (`0` Low / `1` Medium / `2` High). Screen-space shadows stay a Feature and do not replace the atlas.
Execute-path CopyColor / CopyDepth are RenderGraph nodes: no Feature `ConfigureInput` → both copies culled (7 executed). ShaderConfig capacities are compile-time C#/HLSL/GLSL constants, not `.pipeline` fields.
Player builds strip ScreenSpace Decal / screen-space shadow shaders unless those Features are enabled on the pipeline used at collect time (see [inspect-pipeline](../inspect-pipeline/SKILL.md)).
Forward (default) and ForwardPlus (`2`) also strip `Default/DeferredLighting`; set `.renderer` `RenderingPath` to `1` (Deferred) or `3` (DeferredPlus) to keep it. Overlay stacking is disabled on Deferred and DeferredPlus.
With an assigned `UniversalRenderPipelineGlobalSettings.asset`, `Strip2DUnusedVariants` (default **false**) keeps `Default/SpriteLit` / `Default/Light2D` / `Default/Shadow2D` even when `.renderer` `Kind` is `0` (Universal); without a settings asset the old feature-mask stripping applies. Renderer2D (`1`) does not use LightBVH.

Or MCP: `asset_data_patch`.
