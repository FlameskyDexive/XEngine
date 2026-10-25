# Changelog

## [0.1.3] - 2026-09-20

- Preserve valid skeletons whose setup pose has no visible attachments, allowing effect animations and later skin selection to reveal their contents.
- Cover reveal/hide animation transitions and skeleton/cache identity with a regression test.

## [0.1.2] - 2026-09-20

- Accept validated Spine 3.8.75 JSON and binary exports, including the Icey conversion assets (04bc987).
- Cover both readers with 3.8.75 regression tests and update the documented compatibility contract.

## [0.1.1] - 2026-09-16

- Bind atlas page textures 1:1 and keep runtime `Texture2D` instances even when AssetID is empty.
- Pose `SkeletonAnimation` / `SkeletonGraphic` in edit mode so `AnimationName=walk` actually plays.
- Inspector preview on the skeleton asset and on scene components.
- Getting Started scenes pin Spineboy; sample atlas/json/png ship with `.meta`.
- Walk acceptance samples the PMA atlas instead of a solid silhouette.

## [0.1.0] - 2026-09-15

- Official optional Spine 3.8 package. Vendored spine-csharp 3.8 (`8b4844bd4`, 3.8.95+).
- Mutually exclusive with `com.xengine.spine` via package.json `conflicts`.
- Importers for `.atlas` / `*.atlas.txt`, `.skel` / `*.skel.bytes`, and Spine 3.8 JSON.
- `SkeletonRenderer` / `SkeletonAnimation`, PMA shaders, `SkeletonGraphic`, `BoneFollower`.
- Getting Started sample: Spineboy ESS + PMA walk scenes.
- Users of this package must hold their own Spine Editor license.
