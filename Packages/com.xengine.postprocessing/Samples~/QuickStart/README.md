# Quick Start

After installing `com.xengine.postprocessing`:

1. Open a scene with a Camera.
2. Add **Post-process Layer** to the camera (`Rendering / Post-process Layer`).
3. **Assets / Create / Rendering / Post-process Profile**.
4. On the profile, add **Bloom** (intensity > 0) and **Color Grading** (tonemapper AgX).
5. **GameObject / Volume / Post-process Volume**, enable **Is Global**, assign the profile.

The camera should pick up Bloom and tonemap on the next frame. Local volumes use the GameObject scale as an AABB (same as the built-in `Volume` component) and `blendDistance` for a soft edge.
