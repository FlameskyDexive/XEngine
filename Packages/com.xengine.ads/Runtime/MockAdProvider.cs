// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

using XEngine.Async;

namespace XEngine.Ads;

/// <summary>
/// Editor/desktop stand-in implementing the full load → show → reward contract without a
/// vendor SDK. Delays, success rates, and reward behavior are configurable; the defaults
/// complete synchronously (zero-delay paths return completed tasks, so unit tests need no
/// frame loop). Failure injection (<see cref="FailNextLoad"/> / <see cref="FailNextShow"/>)
/// drives deterministic error-path tests.
/// </summary>
/// <remarks>
/// Public methods are non-async wrappers: entry validation throws synchronously and a
/// canceled token returns <c>XTask.FromCanceled</c>. <c>async XTask</c> bodies must never
/// throw before their first await — the pooled runner is only wired at suspension, so a
/// pre-await throw leaves the task permanently Pending.
/// </remarks>
public sealed class MockAdProvider : IAdProvider
{
    private readonly object _gate = new();
    private readonly bool[] _loaded = new bool[AdTypeCount];

    private TimeSpan _loadDelay = TimeSpan.Zero;
    private TimeSpan _showDelay = TimeSpan.Zero;
    private double _loadSuccessRate = 1.0;
    private double _showSuccessRate = 1.0;
    private bool _rewardAlways = true;
    private int _failNextLoads;
    private int _failNextShows;

    private const int AdTypeCount = 2; // RewardVideo, Interstitial

    public string Id => AdProviderIds.Mock;

    public bool IsInitialized { get; private set; }

    public bool IsLoaded(AdType type)
    {
        lock (_gate)
        {
            return IsInitialized && _loaded[TypeIndex(type)];
        }
    }

    /// <summary>Wall-clock pause before a load completes; zero (default) completes synchronously.</summary>
    public MockAdProvider WithLoadDelay(TimeSpan delay)
    {
        _loadDelay = delay;
        return this;
    }

    /// <summary>Wall-clock pause before a show completes; zero (default) completes synchronously.</summary>
    public MockAdProvider WithShowDelay(TimeSpan delay)
    {
        _showDelay = delay;
        return this;
    }

    /// <summary>Load success probability in [0,1]; defaults to always succeed.</summary>
    public MockAdProvider WithLoadSuccessRate(double rate)
    {
        _loadSuccessRate = rate;
        return this;
    }

    /// <summary>Show success probability in [0,1]; defaults to always succeed.</summary>
    public MockAdProvider WithShowSuccessRate(double rate)
    {
        _showSuccessRate = rate;
        return this;
    }

    /// <summary>Whether reward-video shows always count the reward; defaults to true.</summary>
    public MockAdProvider WithRewardAlways(bool rewardAlways)
    {
        _rewardAlways = rewardAlways;
        return this;
    }

    /// <summary>Makes the next <paramref name="count"/> load requests fail deterministically.</summary>
    public MockAdProvider FailNextLoad(int count)
    {
        lock (_gate)
        {
            _failNextLoads += count;
        }
        return this;
    }

    /// <summary>Makes the next <paramref name="count"/> show requests fail deterministically.</summary>
    public MockAdProvider FailNextShow(int count)
    {
        lock (_gate)
        {
            _failNextShows += count;
        }
        return this;
    }

    public XTask InitializeAsync(AdsSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (cancellationToken.IsCancellationRequested)
            return XTask.FromCanceled(cancellationToken);
        return InitializeCoreAsync(cancellationToken);
    }

    public XTask<AdLoadResult> LoadAsync(AdType type, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return XTask.FromCanceled<AdLoadResult>(cancellationToken);
        return LoadCoreAsync(type, cancellationToken);
    }

    public XTask<AdShowResult> ShowAsync(AdType type, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return XTask.FromCanceled<AdShowResult>(cancellationToken);
        return ShowCoreAsync(type, cancellationToken);
    }

    public void Close()
    {
        lock (_gate)
        {
            IsInitialized = false;
            Array.Clear(_loaded);
        }
    }

    public event Action<AdType>? Loaded;
    public event Action<AdType>? ShowStart;
    public event Action<AdType, bool>? ShowEnd;
    public event Action<AdType, string>? Error;

    private XTask InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // Initialization is instantaneous; the configured delays model vendor fill/presentation
        // latency and apply to load/show only.
        IsInitialized = true;
        return XTask.CompletedTask;
    }

    private async XTask<AdLoadResult> LoadCoreAsync(AdType type, CancellationToken cancellationToken)
    {
        if (!IsInitialized)
            return AdLoadResult.Fail(type, "mock provider is not initialized");
        if (IsLoaded(type))
            return AdLoadResult.Ok(type); // idempotent: a cached ad stays showable

        bool injectFailure;
        lock (_gate)
        {
            injectFailure = _failNextLoads > 0;
            if (injectFailure) _failNextLoads--;
        }
        bool success = !injectFailure && Random.Shared.NextDouble() < _loadSuccessRate;
        if (_loadDelay > TimeSpan.Zero)
            await XTask.Delay(_loadDelay, DelayType.Realtime, cancellationToken: cancellationToken);
        if (!success)
        {
            string error = "mock no-fill for " + type;
            Error?.Invoke(type, error);
            return AdLoadResult.Fail(type, error);
        }

        lock (_gate)
        {
            _loaded[TypeIndex(type)] = true;
        }
        Loaded?.Invoke(type);
        return AdLoadResult.Ok(type);
    }

    private async XTask<AdShowResult> ShowCoreAsync(AdType type, CancellationToken cancellationToken)
    {
        if (!IsLoaded(type))
        {
            string error = "mock show with nothing loaded for " + type;
            Error?.Invoke(type, error);
            return AdShowResult.Fail(type, error);
        }

        // The ad is consumed the moment presentation opens, matching vendor behavior.
        lock (_gate)
        {
            _loaded[TypeIndex(type)] = false;
        }

        bool injectFailure;
        lock (_gate)
        {
            injectFailure = _failNextShows > 0;
            if (injectFailure) _failNextShows--;
        }
        bool success = !injectFailure && Random.Shared.NextDouble() < _showSuccessRate;

        ShowStart?.Invoke(type);
        if (_showDelay > TimeSpan.Zero)
            await XTask.Delay(_showDelay, DelayType.Realtime, cancellationToken: cancellationToken);
        if (!success)
        {
            string error = "mock presentation failed for " + type;
            Error?.Invoke(type, error);
            return AdShowResult.Fail(type, error);
        }

        bool rewardEarned = type == AdType.RewardVideo && _rewardAlways;
        ShowEnd?.Invoke(type, rewardEarned);
        return AdShowResult.Ok(type, rewardEarned);
    }

    private static int TypeIndex(AdType type) => type switch
    {
        AdType.RewardVideo => 0,
        AdType.Interstitial => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown ad type."),
    };
}
