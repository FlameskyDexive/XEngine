// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Ads;

/// <summary>
/// Ad formats supported by v1. Both are full-screen, SDK-hosted presentations, which is why
/// they need no game-side rendering surface; banner and native formats (which do) are planned
/// additions.
/// </summary>
public enum AdType : byte
{
    /// <summary>Full-screen video the user watches in exchange for an in-game reward.</summary>
    RewardVideo = 0,

    /// <summary>Full-screen static or video ad shown at natural breaks; no reward semantics.</summary>
    Interstitial = 1,
}
