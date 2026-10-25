// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Ads;

/// <summary>Stable provider identifiers used by <see cref="AdsSettings"/> and receipts.</summary>
public static class AdProviderIds
{
    /// <summary>Editor/desktop mock with configurable success and delays.</summary>
    public const string Mock = "mock";

    /// <summary>Huawei Ads Kit (鲸鸿动能) — HarmonyOS system kit, ArkTS <c>@kit.AdsKit</c>.</summary>
    public const string HuaweiAdsKit = "huawei-adskit";

    /// <summary>Pangle / 穿山甲 — HarmonyOS <c>openadsdk.har</c> SDK.</summary>
    public const string Pangle = "pangle";
}
