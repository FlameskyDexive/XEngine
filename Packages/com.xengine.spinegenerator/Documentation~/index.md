# Spine Generator

AI Spine asset production: text-to-image → auto slicing (+occlusion inpainting) → one-click
rigging/skinning → text-to-animation.

## Usage

Open `Window / Package Management / Package Manager`, install *Spine Generator*, then launch the
wizard (menu added by the package). Each step maps to a headless command so it can also be driven
by agents:

| Step | CLI | Result |
|---|---|---|
| Character | `gen-submit --provider openai --kind sprite` | Source PNG in `Assets/` |
| Slice | `spinegen slice <png>` | parts manifest + packed atlas |
| Rig/Skin | `spinegen rig <slice>` / `spinegen skin` | skeleton JSON (Spine 4.3) |
| Animate | `spinegen animate --prompt "idle breathing"` | animation entries |

See `docs/superpowers/specs/2026-08-27-spine-generator-package.md` for the full design.
