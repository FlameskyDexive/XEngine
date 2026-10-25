// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Ads;

/// <summary>Per-provider slot configuration (app id plus one ad-unit id per supported format).</summary>
public sealed class AdsProviderConfig
{
    /// <summary>Vendor media/app identifier issued by the ad platform console.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Reward-video ad slot/ad-unit id; empty when the format is unused.</summary>
    public string RewardVideoSlotId { get; set; } = string.Empty;

    /// <summary>Interstitial ad slot/ad-unit id; empty when the format is unused.</summary>
    public string InterstitialSlotId { get; set; } = string.Empty;
}

/// <summary>
/// Provider-agnostic ads configuration handed to <see cref="IAdProvider.InitializeAsync"/>.
/// Kept a plain serializable type so the editor can persist it as an Echo text asset in a
/// later release without changing the runtime surface.
/// </summary>
public sealed class AdsSettings
{
    /// <summary>Which provider <see cref="AdsService"/> should activate.</summary>
    public string ProviderId { get; set; } = AdProviderIds.Mock;

    /// <summary>
    /// Use vendor test ad slots. Test slots need no monetization agreement and never bill —
    /// keep <c>true</c> until the app is cleared for release.
    /// </summary>
    public bool UseTestAds { get; set; } = true;

    /// <summary>Slot configuration per provider id (see <see cref="AdProviderIds"/>).</summary>
    public Dictionary<string, AdsProviderConfig> Providers { get; } = new(StringComparer.Ordinal);

    /// <summary>Returns the config for <paramref name="providerId"/>, creating an empty one when absent.</summary>
    public AdsProviderConfig GetOrCreateProvider(string providerId)
    {
        if (!Providers.TryGetValue(providerId, out AdsProviderConfig? config))
        {
            config = new AdsProviderConfig();
            Providers.Add(providerId, config);
        }
        return config;
    }

    /// <summary>Returns the config for <paramref name="providerId"/> or null when absent.</summary>
    public AdsProviderConfig? GetProvider(string providerId) =>
        Providers.TryGetValue(providerId, out AdsProviderConfig? config) ? config : null;
}
