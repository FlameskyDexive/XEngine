// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Pipeline-owned anti-aliasing (M23). None keeps the M1 pixel floor.
/// MSAA renders at native resolution into a real multisample color+depth target and resolves it
/// to a single-sample target after transparents (P3.3; it no longer raises the internal scale).
/// The requested sample count is clamped to the device's reported capability and falls back to 1×
/// for configurations the RHI cannot serve — see
/// <see cref="UniversalRenderPipeline.GetEffectiveMsaaSampleCount"/>.
/// TAA injects the existing temporal effect and upsamples
/// when <see cref="UniversalRenderPipelineAsset.RenderScale"/> is below 1.
/// </summary>
public enum PipelineAntialiasingMode : byte
{
    None = 0,
    MSAA = 1,
    TAA = 2,
}

/// <summary>
/// Spatial / temporal upscaler (U17-M3). STP reuses the existing TAA history path;
/// FSR is recorded as a spatial filter (bilinear until an FSR shader lands). Auto
/// picks Linear at native scale and STP when TAA or STP is requested.
/// </summary>
public enum PipelineUpscalingFilter : byte
{
    Auto = 0,
    Linear = 1,
    Point = 2,
    FSR = 3,
    STP = 4,
}

/// <summary>
/// Project-wide Universal Render Pipeline defaults (Unity 6.7-aligned naming). Echo-serialized as
/// <c>.pipeline</c> so AI CLI can inspect/patch via <c>asset_data_inspect</c> / <c>asset_data_patch</c>.
/// QualitySettings remains the Preview/Full overlay: Preview still caps the atlas and skips FX.
/// </summary>
[CreateAssetMenu("Rendering/Universal Render Pipeline Asset", Extension = ".pipeline", Order = 40)]
public sealed class UniversalRenderPipelineAsset : ScriptableObject
{
    public bool Hdr;
    public float RenderScale = 1f;
    public int ShadowAtlasSize = 4096;
    public float ShadowDistance = 100f;
    public int MaxShadowCascades = 4;
    public bool ShadowsEnabled = true;
    public SoftShadowQuality SoftShadowQuality = SoftShadowQuality.Medium;
    /// <summary>None (default) / MSAA / TAA. Echo-patchable. Independent of camera FXAA/SMAA/TAA.</summary>
    public PipelineAntialiasingMode Antialiasing = PipelineAntialiasingMode.None;
    /// <summary>MSAA sample count when <see cref="Antialiasing"/> is MSAA. Snapped to 2, 4, or 8.</summary>
    public int MsaaSamples = 4;
    /// <summary>Auto (default) / Linear / Point / FSR / STP. Echo-patchable. Independent of MSAA.</summary>
    public PipelineUpscalingFilter UpscalingFilter = PipelineUpscalingFilter.Auto;
    /// <summary>Keep destination alpha through post when <see cref="Hdr"/> is on (U17-M10).</summary>
    public bool AllowPostProcessAlphaOutput;
    public AssetRef<UniversalRenderData> Renderer;

    /// <summary>Snap a requested MSAA sample count to 2, 4, or 8.</summary>
    public static int SnapMsaaSamples(int samples)
    {
        if (samples <= 2)
            return 2;
        if (samples <= 4)
            return 4;
        return 8;
    }

    public override void OnValidate()
    {
        if (!float.IsFinite(RenderScale))
            RenderScale = 1f;
        RenderScale = Math.Clamp(RenderScale, 0.1f, 2f);

        int size = ShadowAtlasSize;
        if (size < 512) size = 512;
        if (size > 8192) size = 8192;
        int pot = 512;
        while (pot * 2 <= size)
            pot *= 2;
        ShadowAtlasSize = pot;

        if (!float.IsFinite(ShadowDistance))
            ShadowDistance = 100f;
        ShadowDistance = Math.Clamp(ShadowDistance, 1f, 1000f);
        MaxShadowCascades = QualitySettings.SnapShadowCascades(MaxShadowCascades);

        int softTier = (int)SoftShadowQuality;
        if (softTier < 0 || softTier > 2)
            SoftShadowQuality = global::XEngine.Runtime.Rendering.SoftShadowQuality.Medium;

        int aa = (int)Antialiasing;
        if (aa < 0 || aa > 2)
            Antialiasing = PipelineAntialiasingMode.None;

        MsaaSamples = SnapMsaaSamples(MsaaSamples);

        int filter = (int)UpscalingFilter;
        if (filter < 0 || filter > 4)
            UpscalingFilter = PipelineUpscalingFilter.Auto;
    }

    /// <summary>
    /// Push shadow knobs into <see cref="QualitySettings"/>. Preview tier still caps atlas at 2048
    /// and does not re-enable image effects.
    /// </summary>
    // ApplyQuality runs per camera per frame from ExecuteSetup. The steady state is a no-op,
    // so skip the OnValidate + five setters unless this asset's fields or the global
    // QualitySettings changed. Including QualitySettings.Version keeps the "asset wins over
    // external changes" semantics: an external write bumps Version, the next apply pushes the
    // asset's values back and the key re-stabilizes.
    private (float, bool, SoftShadowQuality, int, int, int, int, QualitySettings.QualityTier, long) _appliedQualityKey;
    private bool _qualityApplied;

    public void ApplyQuality()
    {
        var key = (ShadowDistance, ShadowsEnabled, SoftShadowQuality, ShadowAtlasSize,
            MaxShadowCascades, MsaaSamples, (int)UpscalingFilter, QualitySettings.Tier, QualitySettings.Version);
        if (_qualityApplied && key.Equals(_appliedQualityKey))
            return;

        OnValidate();
        QualitySettings.ShadowDistance = ShadowDistance;
        QualitySettings.MaxShadowCascades = MaxShadowCascades;
        QualitySettings.ShadowsEnabled = ShadowsEnabled;
        QualitySettings.SoftShadowQuality = SoftShadowQuality;
        int size = ShadowAtlasSize;
        if (QualitySettings.Tier == QualitySettings.QualityTier.Preview && size > 2048)
            size = 2048;
        QualitySettings.ShadowAtlasSize = size;

        _appliedQualityKey = (ShadowDistance, ShadowsEnabled, SoftShadowQuality, ShadowAtlasSize,
            MaxShadowCascades, MsaaSamples, (int)UpscalingFilter, QualitySettings.Tier, QualitySettings.Version);
        _qualityApplied = true;
    }
}
