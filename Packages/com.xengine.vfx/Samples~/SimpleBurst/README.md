# Simple Burst

Import this sample from Package Manager → Visual Effect Graph → Samples → **Simple Burst**. It copies `Burst.vfx` into `Assets/Samples/Visual Effect Graph/Simple Burst/`.

1. Create an empty GameObject and add **Effects / Visual Effect**.
2. Assign `Burst.vfx` to `VisualEffect.Graph`.
3. Enter Play. The effect emits 256 particles once (`BurstCount`) then dies out.

The graph is Echo text. Inspect or compile it offline:

```bash
xengine-cli vfxgraph-inspect --project <root> --graph "Assets/Samples/Visual Effect Graph/Simple Burst/Burst.vfx"
xengine-cli vfxgraph-compile --project <root> --graph "Assets/Samples/Visual Effect Graph/Simple Burst/Burst.vfx"
```

GPU simulation requires Vulkan (or another compute backend). OpenGL / WebGL2 Play/Stop only.
