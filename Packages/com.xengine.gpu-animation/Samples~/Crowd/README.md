# Crowd Sample

A self-contained GPU animated crowd — no pre-baked assets required. The sample script
procedurally builds a small skinned "flag puppet", bakes it to a vertex animation texture
through the package baker **at runtime**, and spawns a GPU-instanced animated grid.

## Use

1. Import this sample into a project (Package Manager → GPU Animation → Samples → Crowd).
2. In any scene, add `Rendering/GPU Animated Crowd (Sample)` to an empty GameObject.
3. Press Play — a 100×100 crowd (10,000 instances) animates with randomized phases.

Component options: `GridSize`, `Spacing`, `Lit` (directional-lambert variant), `RandomizePhase`.

## Baking your own characters

Use `Window/Animation/GPU Animation Baker`: select a character (with a SkinnedMeshRenderer)
in the Hierarchy, tick its clips, press Bake. Three assets land under `Assets/GpuAnim/`:
`<name>.gpuanim` (VAT atlas), `<name>_VAT.mesh` (UV2-encoded), `<name>.mat` (crowd material).
Add a `GPU Animated Crowd` component, assign the `.gpuanim` + `_VAT.mesh` (+ optional `.mat`),
and populate its `Instances` list.
