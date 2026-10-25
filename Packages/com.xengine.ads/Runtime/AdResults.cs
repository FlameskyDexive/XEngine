// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Ads;

/// <summary>Outcome of an <c>Load</c> request.</summary>
public sealed class AdLoadResult
{
    public static AdLoadResult Ok(AdType type) => new() { Type = type, Success = true };

    public static AdLoadResult Fail(AdType type, string error) => new() { Type = type, Success = false, Error = error };

    public AdType Type { get; private init; }
    public bool Success { get; private init; }

    /// <summary>Human-readable failure reason; empty on success.</summary>
    public string Error { get; private init; } = string.Empty;
}

/// <summary>Outcome of a <c>Show</c> request.</summary>
public sealed class AdShowResult
{
    public static AdShowResult Ok(AdType type, bool rewardEarned) =>
        new() { Type = type, Success = true, RewardEarned = rewardEarned };

    public static AdShowResult Fail(AdType type, string error) =>
        new() { Type = type, Success = false, Error = error };

    public AdType Type { get; private init; }
    public bool Success { get; private init; }

    /// <summary>
    /// Whether the reward condition was met (watched to the qualifying point). Only meaningful
    /// for <see cref="AdType.RewardVideo"/>; interstitial shows report <c>false</c>.
    /// </summary>
    public bool RewardEarned { get; private init; }

    /// <summary>Human-readable failure reason; empty on success.</summary>
    public string Error { get; private init; } = string.Empty;
}
