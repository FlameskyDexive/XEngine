# Other Examples

Ports of the official spine-unity Other Examples that fit XEngine + spine-csharp **4.2**. Import from Package Manager → Spine → Samples.

The 4.3 `Samples~` tree cannot be copied as-is: every official 4.3 JSON is `"spine": "4.3.74-beta"`, and this package vendors 4.2. Skeletons here are the 4.2 exports from [spine-runtimes/4.2/examples](https://github.com/EsotericSoftware/spine-runtimes/tree/4.2/examples).

Each user of these samples must hold a [Spine Editor license](https://esotericsoftware.com/spine-purchase). See `LICENSE-SPINE-EXAMPLES.txt`.

| Scene | Role | Controls |
|-------|------|----------|
| `Scenes/1 Goblins.scene` | Authored skin swap (`goblin` / `goblingirl`) | LMB toggle |
| `Scenes/2 MixAndMatchSkins.scene` | Combine Mix-and-Match skins | H hair, E eyes, N nose, C clothes, P pants |
| `Scenes/3 RuntimeLoadFromExports.scene` | `SkeletonDataAsset.CreateRuntime` from JSON + atlas + PNG | Set paths after import |
| `Scenes/4 AtlasRestyle.scene` | Same bones, new page texture (mock AI tint) | R re-apply |

After import, assign the imported `SkeletonDataAsset` on scenes 1, 2, and 4. Scene 3: point `JsonPath` / `AtlasPath` / `TexturePath` at the imported Goblins files.

Not ported (Unity-only or 4.3): SkeletonMecanim, Timeline, full SkeletonUtility, `AtlasUtilities.Repack` / Sprite `SetRegion`, shader-mask / render-texture examples.

AI atlas replace (GPT Image 2, ComfyUI, …) is documented in `Documentation~/atlas-restyle.md`. Do not regenerate bones — unpack each region, img2img at the same size, pack back into the same `.atlas` layout.
