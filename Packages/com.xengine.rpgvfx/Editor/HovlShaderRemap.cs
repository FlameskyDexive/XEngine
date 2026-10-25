// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.RpgVfx.Editor;

/// <summary>
/// Maps the Hovl Studio built-in-pipeline shaders (by Unity asset GUID and by name) onto the
/// ported engine shaders in this package, for the Unity material conversion path
/// (com.xengine.unityimporter's UnityMatImporter consults this table).
///
/// Split-shader notes:
///  - Add_CenterGlow's serialized <c>_Blend2</c> selects One (1) vs OneMinusSrcAlpha (6);
///    the engine has no per-material blend override, so the two ship as separate shaders
///    ("Hovl/Add_CenterGlow" and "Hovl/Add_CenterGlowFade") — pick by reading _Blend2.
///  - Blend_Normals / BlendDistort / Lit_CenterGlow (6 materials combined) currently fall
///    back to the closest ported shader and emit a conversion warning each.
/// </summary>
public static class HovlShaderRemap
{
    /// <summary>Unity shader GUID → Hovl shader short name (see <see cref="ShadersByName"/>).</summary>
    public static readonly Dictionary<string, string> ShadersByGuid = new()
    {
        { "AddTrail", "1bc9b14ff230e2242b248daff64546f6" },
        { "Add_CenterGlow", "bd6af5d577c618947b73aa60db554e51" },
        { "BlendDistort", "ca825309dbbb03640874997c5db5cb48" },
        { "Blend_CenterGlow", "b68b99c2e57cef4419465c63c527201b" },
        { "Blend_Normals", "c6a2ded4c4b574a46babd82f68db8f4d" },
        { "Blend_TwoSides", "56bedbcbfe68e1345a5e87fe73790dab" },
        { "DissolveNoise", "145bae762d8de8f4c8fb1b284128fae7" },
        { "Distortion", "36121c42b945b624fb938cb9637f28d0" },
        { "Electricity", "86b3f0465bcb0c941893722f46ef6eeb" },
        { "LightGlow", "68f568f887edc9d449e3a18dafbe5027" },
        { "Lit_CenterGlow", "13a43378542625f489f7c1c85ba6a4c4" },
        { "SmoothSmoke", "734ad3a5e0f9f384683a822995bcecd3" },
    };

    /// <summary>Guid → ported engine shader name (with per-material blend disambiguation
    /// resolved by <see cref="ResolveEngineShader"/>).</summary>
    public static readonly Dictionary<string, string> EngineShaderByGuid = new()
    {
        { "1bc9b14ff230e2242b248daff64546f6", "Hovl/AddTrail" },
        { "bd6af5d577c618947b73aa60db554e51", "Hovl/Add_CenterGlow" },        // + Fade by _Blend2
        { "ca825309dbbb03640874997c5db5cb48", "Hovl/Blend_CenterGlow" },      // fallback (warn)
        { "b68b99c2e57cef4419465c63c527201b", "Hovl/Blend_CenterGlow" },
        { "c6a2ded4c4b574a46babd82f68db8f4d", "Hovl/Blend_CenterGlow" },      // fallback (warn)
        { "56bedbcbfe68e1345a5e87fe73790dab", "Hovl/Blend_TwoSides" },
        { "145bae762d8de8f4c8fb1b284128fae7", "Hovl/DissolveNoise" },
        { "36121c42b945b624fb938cb9637f28d0", "Hovl/Distortion" },
        { "86b3f0465bcb0c941893722f46ef6eeb", "Hovl/Electricity" },
        { "68f568f887edc9d449e3a18dafbe5027", "Hovl/LightGlow" },
        { "13a43378542625f489f7c1c85ba6a4c4", "Default/Standard" },           // lit mesh (warn)
        { "734ad3a5e0f9f384683a822995bcecd3", "Hovl/SmoothSmoke" },

        // Unity built-in Standard (legacy builtin shader guid) — demo-environment materials
        // (Floor1-3, Gray, Wall in Hovl Resources) plus the Lit_CenterGlow crystal above.
        { "0000000000000000f000000000000000", "Default/Standard" },
    };

    /// <summary>Resolves the ported engine shader name for a Unity shader GUID, applying the
    /// Add_CenterGlow _Blend2 split. Returns false for unknown GUIDs.</summary>
    public static bool TryResolveEngineShader(string unityShaderGuid, float blend2,
        out string engineShaderName, out string? warning)
    {
        warning = null;
        if (!EngineShaderByGuid.TryGetValue(unityShaderGuid, out engineShaderName!))
            return false;

        if (unityShaderGuid == "bd6af5d577c618947b73aa60db554e51")
        {
            // Add_CenterGlow: _Blend2 == 6 → OneMinusSrcAlpha (Fade shader).
            engineShaderName = (int)blend2 == 6 ? "Hovl/Add_CenterGlowFade" : "Hovl/Add_CenterGlow";
        }
        else if (unityShaderGuid == "ca825309dbbb03640874997c5db5cb48")
        {
            warning = "BlendDistort converted to Hovl/Blend_CenterGlow without grab distortion";
        }
        else if (unityShaderGuid == "c6a2ded4c4b574a46babd82f68db8f4d")
        {
            warning = "Blend_Normals converted to Hovl/Blend_CenterGlow without lit normals";
        }
        else if (unityShaderGuid == "13a43378542625f489f7c1c85ba6a4c4")
        {
            warning = "Lit_CenterGlow converted to Default/Standard (lit mesh)";
        }
        return true;
    }
}
