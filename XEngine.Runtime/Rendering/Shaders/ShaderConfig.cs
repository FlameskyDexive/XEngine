// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Compile-time shader capacity knobs shared by C#, GLSL, and HLSL/Slang.
/// Keep <c>Assets/Defaults/ShaderLibrary/ShaderConfig.{glsl,hlsl}</c> in lockstep (M12).
/// </summary>
public static class ShaderConfig
{
    /// <summary>Local lights (point + spot) that can hold a shadow-atlas slot in one frame.</summary>
    public const int MaxShadowCasters = 4;

    /// <summary>Per-fragment LightBVH walk cap so a corrupt node texture cannot infinite-loop.</summary>
    public const int MaxBvhNodeVisits = 4096;

    /// <summary>RGBA32F texels per light slot. Must match <c>LightBVH.glsl</c> / <c>LightBVH.hlsl</c>.</summary>
    public const int LightBvhTexelsPerLight = 6;

    /// <summary>RGBA32F texels per BVH node (min/hit, max/miss).</summary>
    public const int LightBvhTexelsPerNode = 2;

    /// <summary>IBL probes uploaded per camera (primary + blend).</summary>
    public const int MaxReflectionProbes = 2;

    // ── Clustered light lists (P3.2) ───────────────────────────────────────────
    // Screen-normalized tile grid x exponential view-Z slices. The fragment shader reads the
    // grid dimensions from uniforms (_ClusterGridParams), so these are CPU-side knobs only; no
    // GLSL/HLSL lockstep constant is required.

    /// <summary>Cluster tiles across the screen width (normalized NDC space, resolution independent).</summary>
    public const int ClusterTilesX = 32;

    /// <summary>Cluster tiles across the screen height.</summary>
    public const int ClusterTilesY = 18;

    /// <summary>Exponential view-space depth slices between camera near and far.</summary>
    public const int ClusterZSlices = 16;

    /// <summary>Named lighting/decal bits. Independent of <c>LayerMask</c> (culling/physics).</summary>
    public const int MaxRenderingLayers = 8;

    /// <summary>Soft PCF Poisson taps at High. Low=4, Medium=8, High=16. Keep GLSL/HLSL lockstep.</summary>
    public const int MaxSoftShadowTaps = 16;

    /// <summary>Edge length (texels) of one probe's octahedral irradiance tile in the GI probe atlas.
    /// Keep GLSL/HLSL lockstep (<c>GI_PROBE_TILE_SIZE</c>).</summary>
    public const int GiProbeTileSize = 8;

    /// <summary>Probe tiles refreshed per frame (round-robin). CPU-side scheduling bound only.</summary>
    public const int MaxGiProbeUpdatesPerFrame = 32;
}
