# XEngine patches to Clay (model import: glTF/GLB/FBX/OBJ)

## Skin influence selection (2026-10-13)

`Clay/PostProcess/LimitBoneWeightsStep.cs` scans the complete source stride
instead of truncating to32 entries before selecting strongest weights. Sorted
destination insertion retains source-order ties, normalization and unweighted
vertices; baked storage remains four influences. The editor/runtime adapter
exposes only1/2/4. Regressions and native acceptance are retained under
`docs/superpowers/evidence/unity7-editor-alignment/n3/n3d4b/` in the parent repo.

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Clay/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Clay/<path>`.

## Status

- Was vendored at **v2.8.6**; **synced to v3.3.0 on 2026-08-31** (M1.4 of the prowl-sync plan).
- Upstream v2.8.6→v3.3.0: 62 files, +4.8k/−641 — GLTF import hardening (embedded textures/metadata/
  extras, camera/light import, primitive merge, specular-glossiness), OBJ smoothing groups, new
  normal-generation post-process steps, NodeGraph intermediate, `411a3716` **AnimationCurve moved
  out of Clay into Prowl.Vector** ( Clay's `AnimationCurve.cs`, `GenerateBoundsStep.cs`,
  `SplitByBoneCountStep.cs`, `AnimationCurveTests.cs` deleted upstream; adopted here too).
- `XEngine.Echo`-style stale sln note: build `Clay/Clay.csproj` / `Tests/Clay.Tests.csproj` directly.

## Local patches (preserved through the sync)

### 1. `BindPoseConsistencyStep` (XEngine-only post-process step)

- **Files**: `Clay/PostProcess/BindPoseConsistencyStep.cs` (new, XEngine-only) + enum member
  `PostProcessFlags.ValidateBindPoses` + its inclusion in the default flag compositions.
- **Motive**: vendored FBX rigs (e.g. Claymore) arrived with bind poses inconsistent with the
  rest pose, breaking skinned meshes downstream (see the zonezero bone-scale investigation).
- **Merged with upstream 2026-08-31**: upstream's `GameQuality`/`Raw` composition rewrite dropped
  `GenerateBounds` (step deleted upstream); conflict resolved as **keep `ValidateBindPoses`,
  drop `GenerateBounds`**. Pipeline.cs/SceneBaker.cs merged cleanly with the local step's
  registration intact.

### 2. Minor local tweaks (kept)

- `Clay/Clay.csproj` (+2 lines), `Formats/Fbx/FbxAnimationMapper.cs` (2 lines),
  `DiffDebugger/ModelDiff.cs` (1), README (1) — merged with upstream changes where both touched.

## Engine-side adaptation (this sync)

- `XEngine.Runtime/AssetImporting/ClayBackedImporter.cs`: `ClayCurve` now aliases
  `XEngine.Vector.AnimationCurve` (Clay's own curve type is gone upstream). `SampleComponent`
  rewritten from packed `Times[]`/`Values[]` arrays to the new `IReadOnlyList<Keyframe>` /
  `Keyframe.Value4` shape (same semantics: value component at key time; cubic-spline tangents
  dropped both before and after).

## Verification (2026-08-31 sync)

- `Clay.Tests` (net10.0 via roll-forward): **158/158 pass** (now includes upstream
  AnimationFidelity/NodeGraph/NormalGeneration/OptimizeSteps/SkinBake/SmoothingGroup/
  ImportRobustness/Gltf* test suites).
- Engine import regression `XEngine.Runtime.Test --filter Import|Model`: **14/14 pass**.
- Full `XEngine.sln` build: **0 errors**.
