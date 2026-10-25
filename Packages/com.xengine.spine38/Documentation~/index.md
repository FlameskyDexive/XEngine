# Spine 3.8

Esoteric Spine 3.8 skeletal 2D animation for XEngine. Optional package: install from Package Manager (`Window / Package Management / Package Manager`).

**Cannot be installed together with `com.xengine.spine` (4.x).** The two runtimes share the `Spine` namespace.

Each user must hold their own [Spine Editor license](https://esotericsoftware.com/spine-purchase).

The XEngine adapter (`XEngine.Spine38`) is MIT. Vendored `spine-csharp` 3.8 and official sample assets stay under the [Spine Runtimes License](https://esotericsoftware.com/spine-runtimes-license).

## Runtime

Vendored `spine-csharp` keeps the `Spine` namespace.

| Type | Role |
|------|------|
| `SkeletonDataAsset` / `SpineAtlasAsset` / `AnimationReferenceAsset` | Imported skeleton, atlas, and named clip refs. |
| `SkeletonRenderer` | Posed mesh via `Mesh.AssignStreams` + `Upload` |
| `SkeletonAnimation` | LateUpdate `AnimationState` |
| `SkeletonGraphic` | xgui `OnPopulateMesh` |
| `BoneFollower` / `BoneFollowerGraphic` | Follow a bone in world or Canvas space |

Shaders: `Spine/Skeleton` (PMA), `Spine/SkeletonLit`, `Spine/SkeletonAdditive`.

## Authoring

Drop Spine **3.8** export files into `Assets/`:

- `.atlas` or `*.atlas.txt` → `SpineAtlasAsset`
- `.skel` or `*.skel.bytes` → `SkeletonDataAsset`
- `.json` that sniffs as Spine 3.8 (`"spine": "3.8…"`) → `SkeletonDataAsset`

4.x JSON is not claimed. Starting with package `0.1.2`, validated `3.8.75` exports are accepted by the XEngine reader patch. The version alone does not establish asset correctness; validate animation, attachments, and atlas pages on import. Default scale is `0.01`.

Create from **GameObject / Spine 3.8 / Skeleton Animation**.

## Samples

**Getting Started** (`Samples~/GettingStarted`) — Spineboy ESS + PMA.

| Scene | What it shows |
|-------|----------------|
| 1 The Spine GameObject | `SkeletonAnimation` + walk loop |
| 2 Controlling Animation | `SetAnimation` / `AddAnimation` |
| 3 SkeletonGraphic | xgui `SkeletonGraphic` + Spineboy |

## Out of scope (v1)

`SkeletonMecanim`, Timeline, atlas restyle, Goblins / Mix-and-Match, Spine 4.x data. Not pulled in by `com.xengine.feature.2d`.
