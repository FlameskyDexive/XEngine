# Getting Started

Official [spine-unity Getting Started](https://esotericsoftware.com/spine-unity-examples) scenes, ported to XEngine Echo `.scene` files.

Import from Package Manager → Spine → Samples after the package is installed. The copy lands under `Assets/Samples/`. Scripts compile only after that import (`Samples~` is skipped by ScriptCompiler).

Each user of these samples must hold a [Spine Editor license](https://esotericsoftware.com/spine-purchase). Official skeletons and ports stay under the Spine Runtimes License — see `LICENSE-SPINE-EXAMPLES.txt`.

| Scene | Role | Skeleton |
|-------|------|----------|
| `Scenes/1 The Spine GameObject.scene` | `SkeletonAnimation` + `SkeletonDataAsset` | Spineboy ESS + PMA |
| `Scenes/2 Controlling Animation.scene` | `SetAnimation` / `AddAnimation` + `HandleEventWithAudioExample` | Spineboy |
| `Scenes/3 Controlling Animation Continued.scene` | Raptor dual-track + `AnimationReferenceAsset` | Raptor pro + PMA |
| `Scenes/4 Object Oriented Sample.scene` | Spineboy MVC (WASD / jump / aim) | Spineboy |
| `Scenes/5 Basic Platformer.scene` | Hero + `CharacterController` + animation handle | Hero ESS + PMA |
| `Scenes/6 SkeletonGraphic.scene` | xgui `GameCanvas` + Doi / Spineboy + `BoneFollowerGraphic` | Doi + Spineboy |

After import, assign the imported `SkeletonDataAsset` on each skeleton component (atlas PNG + JSON live under `Skeletons/`). Scene 2 Space/WASD is not required — `SpineBeginnerTwo` auto-plays. Scene 4: A/D move, W/Space jump, LMB shoot, RMB aim. Scene 5: A/D walk/run, S crouch, Space jump.
