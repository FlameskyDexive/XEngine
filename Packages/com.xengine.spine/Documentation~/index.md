# Spine

Esoteric Spine skeletal 2D animation for XEngine. This package is **optional**: it ships with the Editor but is not mounted until you install it from Package Manager (`Window / Package Management / Package Manager`).

Install copies the folder into `Library/PackageCache/com.xengine.spine@0.1.0/` and pins it in `Packages/manifest.json`. Uninstall removes the cache copy and the lockfile pin.

**Each user of this package must hold their own [Spine Editor license](https://esotericsoftware.com/spine-purchase).**

The XEngine adapter (`XEngine.Spine`) is MIT. Vendored `spine-csharp` 4.2 and official sample assets stay under the [Spine Runtimes License](https://esotericsoftware.com/spine-runtimes-license).

## Runtime

Types live in this package. Vendored `spine-csharp` keeps the `Spine` namespace.

| Type | Role |
|------|------|
| `SkeletonDataAsset` / `SpineAtlasAsset` / `AnimationReferenceAsset` | Imported skeleton, atlas, and named clip refs. `CreateRuntime` builds them from export bytes. |
| `SpineSkinMixer` | Combine authored skins (`Skin.AddSkin`) |
| `SpineAtlasLayout` / `SpineAtlasRestyle` / `SpineAtlasSwap` | Parse `.atlas`, unpack/pack regions, swap page textures (AI restyle) |
| `SkeletonRenderer` | Posed mesh via `Mesh.AssignStreams` + `Upload`, Sprite-style sorting |
| `SkeletonAnimation` | LateUpdate `AnimationState` (`SetAnimation` / `AddAnimation`, multi-track, events) |
| `SkeletonGraphic` | xgui `OnPopulateMesh` |
| `BoneFollower` / `BoneFollowerGraphic` | Follow a bone in world or Canvas space |

Shaders: `Spine/Skeleton` (PMA `Src One` / `Dst OneMinusSrcAlpha`), `Spine/SkeletonLit`, `Spine/SkeletonAdditive`. Two-color / dark tint is not implemented (single `Colors32`).

Playables / `AnimationPlayableOutput` drive Transform poses and are the wrong path for Spine. Use the component LateUpdate pump.

## Authoring

After Install, drop Spine 4.2 export files into `Assets/`:

- `.atlas` or `*.atlas.txt` → `SpineAtlasAsset` (page PNG GUIDs from sibling `.meta`)
- `.skel` or `*.skel.bytes` → `SkeletonDataAsset` (binary)
- `.json` that sniffs as Spine (`skeleton` + `bones` + `slots`) → `SkeletonDataAsset`

Other `.json` / `.txt` / `.bytes` files stay on `DefaultImporter`. Default skeleton scale is `0.01` (spine-unity).

Create from **GameObject / Spine / Skeleton Animation** (also Renderer and Graphic). Inspectors expose animation / skin dropdowns. `PreviewRenderer.ReplaceSubject` previews a posed skeleton in the inspector (skipped when `Graphics.IsHeadless`).

## Samples

Package Manager → Spine → Samples. Scripts compile only after import (`Samples~` is skipped by ScriptCompiler).

**Getting Started** (`Samples~/GettingStarted`) — official six scenes (Spineboy, Raptor, Hero, Doi).

| Scene | What it shows |
|-------|----------------|
| 1 The Spine GameObject | `SkeletonAnimation` + Spineboy |
| 2 Controlling Animation | `SetAnimation` / `AddAnimation` + footstep events |
| 3 Controlling Animation Continued | Raptor dual-track + `AnimationReferenceAsset` |
| 4 Object Oriented Sample | Spineboy MVC (WASD / jump / aim) |
| 5 Basic Platformer | Hero + `CharacterController` |
| 6 SkeletonGraphic | xgui `GameCanvas` + Doi / Spineboy + `BoneFollowerGraphic` |

**Other Examples** (`Samples~/OtherExamples`) — 4.2 Goblins + Mix-and-Match (not 4.3 JSON).

| Scene | What it shows |
|-------|----------------|
| 1 Goblins | Skin swap `goblin` / `goblingirl` (LMB) |
| 2 MixAndMatchSkins | Combine authored skins (H/E/N/C/P) |
| 3 RuntimeLoadFromExports | `CreateRuntime` from JSON + atlas + PNG |
| 4 AtlasRestyle | Same bones, tinted page (mock AI; R to re-apply) |

After import, assign the imported `SkeletonDataAsset` on each skeleton component.

AI character swap: keep bones, replace atlas pixels via `gen_submit` `kind=texture` extra `atlasPath`/`region`. See [atlas-restyle.md](atlas-restyle.md) and the [M10 plan](../../../docs/superpowers/plans/2026-08-16-spine-atlas-restyle.md).

## Out of scope (v1)

`SkeletonMecanim`, full `SkeletonUtility`, Unity `AtlasUtilities.Repack` / Sprite `SetRegion`, Timeline tracks, Spine 4.3. This package is **not** pulled in by `com.xengine.feature.2d`.
