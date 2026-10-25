# Simple Loop

Import this sample from Package Manager → Visual Effect Graph → Samples → **Simple Loop**. It copies `Loop.vfx` into `Assets/Samples/Visual Effect Graph/Simple Loop/`.

1. Create an empty GameObject and add **Effects / Visual Effect**.
2. Assign `Loop.vfx` to `VisualEffect.Graph`.
3. Enter Play. The effect emits continuously (`SpawnRate` 64) with gravity.

The graph is Echo text. Inspect or compile it offline:

```bash
xengine-cli vfxgraph-inspect --project <root> --graph "Assets/Samples/Visual Effect Graph/Simple Loop/Loop.vfx"
xengine-cli vfxgraph-compile --project <root> --graph "Assets/Samples/Visual Effect Graph/Simple Loop/Loop.vfx"
```

GPU simulation requires Vulkan (or another compute backend). OpenGL / WebGL2 Play/Stop only.
