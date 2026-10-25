// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;

namespace XEngine.Runtime;

/// <summary>
/// Per-light Universal pipeline overrides (Unity <c>UniversalAdditionalLightData</c>).
/// Rendering layers stay on <see cref="Light"/>. Missing component keeps pipeline defaults.
/// </summary>
[AddComponentMenu("Rendering/Universal Additional Light Data")]
[ComponentIcon("\uf0eb")]
public sealed class UniversalAdditionalLightData : MonoBehaviour
{
    [Tooltip("When true, additional-light shadow resolution follows the pipeline asset.")]
    [SerializeField] public bool UsePipelineSettings = true;

    [Tooltip("Shadow map tier when Use Pipeline Settings is off (Low / Medium / High).")]
    [SerializeField] public AdditionalLightsShadowResolutionTier AdditionalLightsShadowResolutionTier
        = AdditionalLightsShadowResolutionTier.Medium;

    [Tooltip("Override pipeline soft-shadow quality for this light.")]
    [SerializeField] public SoftShadowQuality SoftShadowQuality = SoftShadowQuality.Medium;

    public override void OnValidate()
    {
        int tier = (int)AdditionalLightsShadowResolutionTier;
        if (tier < 0 || tier > 2)
            AdditionalLightsShadowResolutionTier = AdditionalLightsShadowResolutionTier.Medium;

        int soft = (int)SoftShadowQuality;
        if (soft < 0 || soft > 2)
            SoftShadowQuality = SoftShadowQuality.Medium;
    }

    public static UniversalAdditionalLightData? Find(Light? light)
        => light == null ? null : light.GetComponent<UniversalAdditionalLightData>();

    public static UniversalAdditionalLightData GetOrAdd(Light light)
    {
        ArgumentNullException.ThrowIfNull(light);
        UniversalAdditionalLightData? existing = Find(light);
        if (existing != null)
            return existing;
        return light.AddComponent<UniversalAdditionalLightData>();
    }
}

/// <summary>Additional-light shadow map size tier (Unity URP Low / Medium / High).</summary>
public enum AdditionalLightsShadowResolutionTier : byte
{
    Low = 0,
    Medium = 1,
    High = 2,
}
