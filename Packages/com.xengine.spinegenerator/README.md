# Spine Generator

Official optional XEngine package. It ships with the Editor but is **not** added to a project until you install it from Package Manager (`Window / Package Management / Package Manager`).

AI production pipeline for Spine skeletal characters, modeled after human-in-the-loop generators:

1. **Text-to-image** — a character concept illustration via the engine AI gateway (`gen` providers; GPT Image 2 by default, mock for offline runs).
2. **Auto slicing** — split the single PNG into layered parts, inpaint occluded pixels per part (3 scored candidates), and pack a Spine `.atlas`.
3. **One-click rigging + skinning** — bone hierarchy template inference from part geometry, mesh hulls, distance-falloff weights.
4. **Text-to-animation** — built-in procedural motion library plus an LLM keyframe loop validated against the vendored spine-csharp runtime.

Output is standard **Spine 4.3 JSON + `.atlas` + page PNG**, playable by `com.xengine.spine` at runtime and editable in the official Spine editor.

The heavy pipeline lives headless in the engine AI tooling (`xengine-cli spinegen-*`, MCP tools) so agents can drive it without the editor open; this package contributes the four-step wizard window.
