# Changelog

## [0.2.0] - 2026-09-08

- npm registry distribution: published as `@xengine/spine` on npm-protocol registries (official
  optional package, dual-channel — the Editor bundle keeps shipping this version as the offline
  fallback). Install/update/downgrade through the Package Manager or
  `xengine-cli package-install com.xengine.spine`.
- Version markers aligned (`SpineRuntime.Version` was 0.1.0), `xengine` keyword added for registry
  search.

## [0.1.1] - 2026-08-27

- Asset/database integration fixes (see git history); first version mirrored to npm as `@xengine/spine`.

## [0.1.0] - 2026-08-16

- Official optional package skeleton: listed in Package Manager, copied to `Library/PackageCache` on Install.
- Runtime / Editor asmdefs, Documentation~, Samples~/GettingStarted, CHANGELOG, LICENSE.
- Users of this package must hold their own Spine Editor license.
- Vendored spine-csharp 4.2 (`b81e5a58`) under `Runtime/spine-csharp/` for headless SkeletonJson / AnimationState.
- Importers for `.atlas` / `*.atlas.txt`, `.skel` / `*.skel.bytes`, and Spine JSON (content sniff). Produce `SpineAtlasAsset`, `SkeletonDataAsset`, and `AnimationReferenceAsset` sub-assets.
- `SkeletonRenderer` uploads posed meshes (`Mesh.AssignStreams` + `Upload`) with PMA / lit / additive shaders and Sprite-style sorting.
- `SkeletonAnimation` LateUpdate applies `AnimationState` (SetAnimation / AddAnimation, multi-track, events). `AnimationReferenceAsset.GetAnimation` resolves named clips.
- Inspectors, `PreviewRenderer.ReplaceSubject` preview, and `GameObject / Spine` create menu.
- `SkeletonGraphic` (xgui), `BoneFollower`, and `BoneFollowerGraphic`.
- Getting Started sample: six Echo scenes plus official Spineboy / Raptor / Hero / Doi exports and XEngine script ports.
- Other Examples sample: Goblins skin swap, Mix-and-Match skins, runtime load from exports, atlas restyle (4.2 data). `SpineSkinMixer` / `SpineAtlasRestyle` / `SpineAtlasSwap` for authored skins and AI page replace.
- Atlas restyle gateway: `gen_submit` `kind=texture` extra `atlasPath`/`region` writes a layout-preserving page (mock-tint / pack-crop). ComfyUI submit injects the unrotated crop (`inputImageNode`). Built-in `openai` provider defaults to GPT Image 2. See `Documentation~/atlas-restyle.md`.
- Documentation~, README, and unity67-alignment-roadmap closeout for the optional Spine package.
