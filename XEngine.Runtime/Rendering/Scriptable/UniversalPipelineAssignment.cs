// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Single assignment point for <see cref="UniversalRenderPipeline.Default.Asset"/> (Unity 6.7
/// resolution chain): the current quality tier's pipeline override wins over the Graphics
/// default, and a missing asset degrades to the engine's built-in Universal/Forward defaults.
/// GraphicsSettings (editor) and PlayerSettingsLoader (player) push the graphics default here;
/// tier switches and per-tier overrides re-resolve automatically.
/// </summary>
internal static class UniversalPipelineAssignment
{
    private static UniversalRenderPipelineAsset? s_graphicsDefault;

    /// <summary>The Graphics.yaml default (Unity m_CustomRenderPipeline). Never the tier override.</summary>
    internal static UniversalRenderPipelineAsset? GraphicsDefault
    {
        get => s_graphicsDefault;
        set
        {
            if (ReferenceEquals(s_graphicsDefault, value))
                return;
            s_graphicsDefault = value;
            Apply();
        }
    }

    /// <summary>The pipeline asset currently in effect (tier override ?? graphics default).</summary>
    internal static UniversalRenderPipelineAsset? Effective
        => QualitySettings.PipelineOverride ?? s_graphicsDefault;

    static UniversalPipelineAssignment()
    {
        QualitySettings.PipelineOverrideChanged += Apply;
    }

    /// <summary>Re-point <see cref="UniversalRenderPipeline.Default"/> at the effective asset.</summary>
    internal static void Apply()
    {
        UniversalRenderPipelineAsset? asset = Effective;
        if (asset != null)
            asset.Renderer.EnsureLoaded();
        UniversalRenderPipeline.Default.Asset = asset;
    }
}
