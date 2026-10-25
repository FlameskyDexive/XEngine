// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Project-level URP global settings (Unity 6.7 <c>UniversalRenderPipelineGlobalSettings</c>
/// parity), seeded at Assets root by the project template and referenced from Graphics.yaml.
/// Concentrates the shader-variant stripping switches consulted at player-build collect time;
/// a missing/unassigned asset keeps every engine default behavior identical (legacy projects
/// strip exactly as before). No CreateAssetMenu on purpose — Unity only creates it via the
/// template/package, never by hand.
/// </summary>
public sealed class UniversalRenderPipelineGlobalSettings : ScriptableObject
{
    /// <summary>False skips shader-variant collection for player builds entirely (Unity m_ExportShaderVariants).</summary>
    public bool ExportShaderVariants = true;

    /// <summary>False keeps every shader and keyword during collection (Unity m_StripUnusedVariants).</summary>
    public bool StripUnusedVariants = true;

    /// <summary>
    /// Strip debug-only shader variants (Unity m_StripDebugVariants). Recorded for Unity 6.7
    /// serialization parity; XEngine debug views are uniform branches, not variants, so no
    /// stripping branch exists yet.
    /// </summary>
    public bool StripDebugVariants = true;

    /// <summary>
    /// Strip unused post-processing variants (Unity m_StripUnusedPostProcessingVariants).
    /// Recorded for parity; XEngine post-processing is effect-driven (no PP shader variants),
    /// so no stripping branch exists yet.
    /// </summary>
    public bool StripUnusedPostProcessingVariants;

    /// <summary>
    /// False keeps 2D renderer shaders (SpriteLit / Light2D / Shadow2D) even when no Renderer2D
    /// is active (Unity m_Strip2DUnusedVariants, default off). A missing settings asset keeps
    /// the pre-settings behavior: strip by the Renderer2D feature mask.
    /// </summary>
    public bool Strip2DUnusedVariants;

    /// <summary>0 = silent, 1 = summary, 2 = per stripped shader, 3 = per stripped keyword set.</summary>
    public int ShaderVariantLogLevel;

    /// <summary>
    /// Runtime cache of the project-assigned settings (Graphics.yaml → GraphicsSettings /
    /// PlayerSettingsLoader). Null until a project wires one, or after it clears the reference.
    /// </summary>
    internal static UniversalRenderPipelineGlobalSettings? Active { get; set; }

    public override void OnValidate()
    {
        if (ShaderVariantLogLevel < 0) ShaderVariantLogLevel = 0;
        if (ShaderVariantLogLevel > 3) ShaderVariantLogLevel = 3;
    }
}
