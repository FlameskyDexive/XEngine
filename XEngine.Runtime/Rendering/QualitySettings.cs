// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Global quality knobs for player and editor preview tiers.
/// Editor initializes a lighter default (smaller shadow atlas) via <see cref="ApplyEditorDefaults"/>.
/// Each tier may also override the render pipeline asset (Unity 6.7 customRenderPipeline per
/// quality level); <see cref="UniversalPipelineAssignment"/> resolves tier override → graphics
/// default whenever it changes.
/// </summary>
public static class QualitySettings
{
    private static int s_shadowAtlasSize = 4096;
    private static float s_shadowDistance = 100f;
    private static int s_maxShadowCascades = 4;
    private static bool s_shadowsEnabled = true;
    private static SoftShadowQuality s_softShadowQuality = SoftShadowQuality.Medium;
    private static bool s_imageEffectsEnabled = true;
    private static AntiAliasingMode s_antiAliasing = AntiAliasingMode.Disabled;
    private static QualityTier s_tier = QualityTier.Full;
    private static long s_version;
    private static readonly UniversalRenderPipelineAsset?[] s_pipelineOverrides = new UniversalRenderPipelineAsset?[2];

    /// <summary>Raised when the effective pipeline override changes (assignment or tier switch).</summary>
    internal static event Action? PipelineOverrideChanged;

    public enum QualityTier
    {
        /// <summary>Editor Scene/Game preview: reduced atlas, optional feature skips later.</summary>
        Preview,
        /// <summary>Full player / play-mode quality.</summary>
        Full,
    }

    /// <summary>
    /// Project-wide post-process anti-aliasing. Cameras that explicitly contain an anti-aliasing
    /// effect keep that effect and are never overridden by this default.
    /// </summary>
    public enum AntiAliasingMode
    {
        Disabled,
        FXAA,
        SMAA,
    }

    /// <summary>Current quality tier (Preview vs Full).</summary>
    public static QualityTier Tier
    {
        get => s_tier;
        set
        {
            if (s_tier == value) return;
            s_tier = value;
            ApplyTierDefaults(value);
            Touch();
            PipelineOverrideChanged?.Invoke();
        }
    }

    /// <summary>Changes whenever a quality setting changes.</summary>
    public static long Version => Interlocked.Read(ref s_version);

    /// <summary>
    /// Preferred shadow atlas edge length (power-of-two). Applied when shadows next need an
    /// atlas, resizing the existing atlas when its effective size differs.
    /// Clamped to [512, 8192].
    /// </summary>
    public static int ShadowAtlasSize
    {
        get => s_shadowAtlasSize;
        set
        {
            int v = value;
            if (v < 512) v = 512;
            if (v > 8192) v = 8192;
            // Snap down to power of two.
            int pot = 512;
            while (pot * 2 <= v) pot *= 2;
            bool changed = s_shadowAtlasSize != pot;
            s_shadowAtlasSize = pot;
            ShadowAtlas.PreferredSize = s_shadowAtlasSize;
            if (changed)
                Touch();
        }
    }

    /// <summary>
    /// Maximum world-space distance covered by directional-light shadows. Individual lights may
    /// request a shorter range; the effective range is the smaller of the two values.
    /// </summary>
    public static float ShadowDistance
    {
        get => s_shadowDistance;
        set
        {
            float v = float.IsFinite(value) ? value : 100f;
            v = Math.Clamp(v, 1f, 1000f);
            if (MathF.Abs(s_shadowDistance - v) < 0.001f) return;
            s_shadowDistance = v;
            Touch();
        }
    }

    /// <summary>Quality cap on directional-light cascade count; valid levels are 1, 2 and 4.</summary>
    public static int MaxShadowCascades
    {
        get => s_maxShadowCascades;
        set
        {
            int count = SnapShadowCascades(value);
            if (s_maxShadowCascades == count) return;
            s_maxShadowCascades = count;
            Touch();
        }
    }

    internal static int SnapShadowCascades(int count) => count <= 1 ? 1 : count <= 2 ? 2 : 4;

    /// <summary>
    /// Global soft-shadow PCF kernel (Low=4 taps, Medium=8, High=16). Does not replace per-light
    /// Hard/Soft; only the soft path reads this.
    /// </summary>
    public static SoftShadowQuality SoftShadowQuality
    {
        get => s_softShadowQuality;
        set
        {
            int raw = (int)value;
            if (raw < 0 || raw > 2)
                raw = 1;
            var next = (global::XEngine.Runtime.Rendering.SoftShadowQuality)raw;
            if (s_softShadowQuality == next) return;
            s_softShadowQuality = next;
            Touch();
        }
    }

    /// <summary>When false, pipelines should skip shadow atlas work even if lights cast shadows.</summary>
    public static bool ShadowsEnabled
    {
        get => s_shadowsEnabled;
        set
        {
            if (s_shadowsEnabled == value) return;
            s_shadowsEnabled = value;
            Touch();
        }
    }

    /// <summary>Whether image effects should execute for the current quality tier.</summary>
    public static bool ImageEffectsEnabled
    {
        get => s_imageEffectsEnabled;
        set
        {
            if (s_imageEffectsEnabled == value) return;
            s_imageEffectsEnabled = value;
            Touch();
        }
    }

    /// <summary>
    /// Default anti-aliasing used when a camera has no explicit FXAA, SMAA, or TAA effect.
    /// </summary>
    public static AntiAliasingMode AntiAliasing
    {
        get => s_antiAliasing;
        set
        {
            if (s_antiAliasing == value) return;
            s_antiAliasing = value;
            Touch();
        }
    }

    /// <summary>Pipeline override of the current tier (Unity 6.7 customRenderPipeline per level).</summary>
    public static UniversalRenderPipelineAsset? PipelineOverride => PipelineOverrideFor(s_tier);

    /// <summary>Pipeline override of a specific tier; null falls back to the graphics default.</summary>
    public static UniversalRenderPipelineAsset? PipelineOverrideFor(QualityTier tier)
        => s_pipelineOverrides[(int)tier];

    /// <summary>
    /// Set a tier's pipeline override (null = fall back to the graphics default). Re-resolves the
    /// active pipeline immediately when the current tier is affected.
    /// </summary>
    public static void SetPipelineOverride(QualityTier tier, UniversalRenderPipelineAsset? asset)
    {
        int index = (int)tier;
        if (index < 0 || index >= s_pipelineOverrides.Length)
            return;
        if (ReferenceEquals(s_pipelineOverrides[index], asset))
            return;
        s_pipelineOverrides[index] = asset;
        Touch();
        if (tier == s_tier)
            PipelineOverrideChanged?.Invoke();
    }

    /// <summary>Editor startup: Preview tier + 2048 atlas (VRAM / clear bandwidth).</summary>
    public static void ApplyEditorDefaults()
    {
        Tier = QualityTier.Preview;
        ShadowAtlasSize = 2048;
        ShadowDistance = 100f;
        MaxShadowCascades = 4;
        ShadowsEnabled = true;
        SoftShadowQuality = global::XEngine.Runtime.Rendering.SoftShadowQuality.Medium;
        ImageEffectsEnabled = false;
        AntiAliasing = AntiAliasingMode.Disabled;
    }

    /// <summary>Player / play-mode: Full tier + 4096 atlas default.</summary>
    public static void ApplyPlayerDefaults()
    {
        Tier = QualityTier.Full;
        ShadowAtlasSize = 4096;
        ShadowDistance = 100f;
        MaxShadowCascades = 4;
        ShadowsEnabled = true;
        SoftShadowQuality = global::XEngine.Runtime.Rendering.SoftShadowQuality.Medium;
        ImageEffectsEnabled = true;
        AntiAliasing = AntiAliasingMode.Disabled;
    }

    private static void ApplyTierDefaults(QualityTier tier)
    {
        if (tier == QualityTier.Preview)
        {
            if (s_shadowAtlasSize > 2048)
                ShadowAtlasSize = 2048;
            ImageEffectsEnabled = false;
        }
        else
        {
            if (s_shadowAtlasSize < 4096)
                ShadowAtlasSize = 4096;
            ImageEffectsEnabled = true;
        }
    }

    private static void Touch() => Interlocked.Increment(ref s_version);
}
