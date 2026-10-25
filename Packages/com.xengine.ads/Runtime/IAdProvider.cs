// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

using XEngine.Async;

namespace XEngine.Ads;

/// <summary>
/// A per-platform advertising backend behind <see cref="AdsService"/>. Implementations own the
/// vendor SDK lifecycle; the interface is intentionally small — initialize once, load per ad
/// type, show what is loaded. All tasks complete on the provider's configured thread (native
/// providers marshal to the main thread before completing).
/// </summary>
public interface IAdProvider
{
    /// <summary>Stable provider identifier (see <see cref="AdProviderIds"/>).</summary>
    string Id { get; }

    /// <summary>Whether <see cref="InitializeAsync"/> completed successfully.</summary>
    bool IsInitialized { get; }

    /// <summary>Whether an ad of <paramref name="type"/> is preloaded and ready to show.</summary>
    bool IsLoaded(AdType type);

    /// <summary>Initializes the vendor SDK with the matching entry from <paramref name="settings"/>.</summary>
    XTask InitializeAsync(AdsSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests and preloads an ad. Showing must happen within the vendor validity window
    /// (Huawei Ads Kit: 1 hour); calling this again replaces the cached ad.
    /// </summary>
    XTask<AdLoadResult> LoadAsync(AdType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the preloaded ad. Fails (does not throw) when nothing is loaded for
    /// <paramref name="type"/>.
    /// </summary>
    XTask<AdShowResult> ShowAsync(AdType type, CancellationToken cancellationToken = default);

    /// <summary>Releases cached ads and tears down the vendor SDK session.</summary>
    void Close();

    /// <summary>Raised when a load for <paramref name="type"/> succeeded.</summary>
    event Action<AdType>? Loaded;

    /// <summary>Raised when a full-screen presentation for <paramref name="type"/> opens.</summary>
    event Action<AdType>? ShowStart;

    /// <summary>Raised when a presentation closes; <paramref name="rewardEarned"/> is only
    /// meaningful for <see cref="AdType.RewardVideo"/>.</summary>
    event Action<AdType, bool>? ShowEnd;

    /// <summary>Raised for load/show failures that a caller may not observe through the tasks
    /// (e.g. background refresh attempts).</summary>
    event Action<AdType, string>? Error;
}
