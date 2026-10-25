// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Opaque path for a Universal renderer (M21 / U17-M1). Forward is the default; Deferred writes a
/// GBuffer then accumulates LightBVH lighting. ForwardPlus / DeferredPlus are the URP 17 clustered
/// names — XEngine still uses LightBVH (not Unity tile+Z-bin). Transparents stay Forward. Overlay
/// stacking is disabled on Deferred and DeferredPlus. Ignored when <see cref="RendererKind"/> is
/// <see cref="RendererKind.Renderer2D"/>.
/// </summary>
public enum RenderingPath : byte
{
    Forward = 0,
    Deferred = 1,
    ForwardPlus = 2,
    DeferredPlus = 3,
}

/// <summary>
/// Which built-in renderer a <see cref="UniversalRenderData"/> selects (M22). Universal is the 3D
/// Forward/Deferred path; Renderer2D lights sprites with Light2D (not LightBVH).
/// </summary>
public enum RendererKind : byte
{
    Universal = 0,
    Renderer2D = 1,
}

/// <summary>
/// Per-renderer settings for the Universal pipeline (M3, Unity 6.7-aligned naming: the asset a
/// "..._Renderer.renderer" file carries). Feature lists stay on
/// <see cref="UniversalRenderPipeline"/> until serialized feature assets land.
/// </summary>
[CreateAssetMenu("Rendering/Universal Render Data", Extension = ".renderer", Order = 41)]
public sealed class UniversalRenderData : ScriptableObject
{
    /// <summary>Reserved for M4+ depth priming. Harmless while unused.</summary>
    public bool DepthPriming;

    /// <summary>
    /// Forward / Deferred / ForwardPlus / DeferredPlus. Echo-patchable on <c>.renderer</c>.
    /// Plus paths still use LightBVH clustered lighting.
    /// </summary>
    public RenderingPath RenderingPath = RenderingPath.Forward;

    /// <summary>Universal 3D (default) or 2D sprite lighting. Echo-patchable on <c>.renderer</c>.</summary>
    public RendererKind Kind = RendererKind.Universal;

    /// <summary>None (default) / Dither / Stencil LOD cross-fade (U17-M8).</summary>
    public LodCrossFadeMode LodCrossFade = LodCrossFadeMode.None;

    public override void OnValidate()
    {
        int path = (int)RenderingPath;
        if (path < 0 || path > 3)
            RenderingPath = RenderingPath.Forward;
        int kind = (int)Kind;
        if (kind < 0 || kind > 1)
            Kind = RendererKind.Universal;
        int fade = (int)LodCrossFade;
        if (fade < 0 || fade > 2)
            LodCrossFade = LodCrossFadeMode.None;
    }
}
