// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;

using XEngine.Ads;

using Xunit;

namespace XEngine.Ads.Tests;

// Zero-delay mock paths complete synchronously, so tests block via GetAwaiter().GetResult()
// (the XTaskAsyncMethodBuilderTests convention) and need no frame loop.
public sealed class MockAdProviderTests
{
    private static AdsSettings CreateSettings() => new() { ProviderId = AdProviderIds.Mock };

    [Fact]
    public void Initialize_MarksProviderReady()
    {
        var provider = new MockAdProvider();

        Assert.False(provider.IsInitialized);
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();

        Assert.True(provider.IsInitialized);
        Assert.Equal(AdProviderIds.Mock, provider.Id);
    }

    [Fact]
    public void Load_ThenShow_RewardVideoEarnsRewardAndConsumesAd()
    {
        var provider = new MockAdProvider();
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();
        var showEnds = new List<(AdType Type, bool RewardEarned)>();
        provider.ShowEnd += (type, rewardEarned) => showEnds.Add((type, rewardEarned));

        AdLoadResult load = provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();
        Assert.True(load.Success);
        Assert.True(provider.IsLoaded(AdType.RewardVideo));

        AdShowResult show = provider.ShowAsync(AdType.RewardVideo).GetAwaiter().GetResult();
        Assert.True(show.Success);
        Assert.True(show.RewardEarned);
        Assert.False(provider.IsLoaded(AdType.RewardVideo), "show must consume the cached ad");
        Assert.Equal(new[] { (AdType.RewardVideo, true) }, showEnds);
    }

    [Fact]
    public void Show_InterstitialNeverReportsReward()
    {
        var provider = new MockAdProvider();
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();

        provider.LoadAsync(AdType.Interstitial).GetAwaiter().GetResult();
        AdShowResult show = provider.ShowAsync(AdType.Interstitial).GetAwaiter().GetResult();

        Assert.True(show.Success);
        Assert.False(show.RewardEarned);
    }

    [Fact]
    public void Show_WithoutLoad_FailsWithResultNotThrow()
    {
        var provider = new MockAdProvider();
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();
        var errors = new List<(AdType Type, string Message)>();
        provider.Error += (type, message) => errors.Add((type, message));

        AdShowResult show = provider.ShowAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.False(show.Success);
        Assert.Contains("nothing loaded", show.Error);
        Assert.Equal(new[] { (AdType.RewardVideo, show.Error) }, errors);
    }

    [Fact]
    public void Load_BeforeInitialize_Fails()
    {
        var provider = new MockAdProvider();

        AdLoadResult load = provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.False(load.Success);
        Assert.Contains("not initialized", load.Error);
    }

    [Fact]
    public void Load_IsIdempotentWhileCached()
    {
        var provider = new MockAdProvider();
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();

        AdLoadResult first = provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();
        AdLoadResult second = provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(provider.IsLoaded(AdType.RewardVideo));
    }

    [Fact]
    public void AdTypes_TrackStateIndependently()
    {
        var provider = new MockAdProvider();
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();

        provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.True(provider.IsLoaded(AdType.RewardVideo));
        Assert.False(provider.IsLoaded(AdType.Interstitial));
    }

    [Fact]
    public void FailNextLoad_InjectsDeterministicNoFill()
    {
        var provider = new MockAdProvider().FailNextLoad(1);
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();
        var errors = new List<AdType>();
        provider.Error += (type, _) => errors.Add(type);

        AdLoadResult load = provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.False(load.Success);
        Assert.False(provider.IsLoaded(AdType.RewardVideo));
        Assert.Equal(new[] { AdType.RewardVideo }, errors);
        // The injection is consumed: the next load succeeds.
        AdLoadResult retry = provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();
        Assert.True(retry.Success);
    }

    [Fact]
    public void FailNextShow_PresentationFailureConsumesTheAd()
    {
        var provider = new MockAdProvider().FailNextShow(1);
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();
        provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        AdShowResult show = provider.ShowAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.False(show.Success);
        Assert.False(provider.IsLoaded(AdType.RewardVideo));
    }

    [Fact]
    public void LoadedAndShowStart_EventsFireInOrder()
    {
        var provider = new MockAdProvider();
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();
        var events = new List<string>();
        provider.Loaded += _ => events.Add("loaded");
        provider.ShowStart += _ => events.Add("show-start");
        provider.ShowEnd += (_, _) => events.Add("show-end");

        provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();
        provider.ShowAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.Equal(new[] { "loaded", "show-start", "show-end" }, events);
    }

    [Fact]
    public void Close_ClearsInitializedAndCachedAds()
    {
        var provider = new MockAdProvider();
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();
        provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        provider.Close();

        Assert.False(provider.IsInitialized);
        Assert.False(provider.IsLoaded(AdType.RewardVideo));
    }

    [Fact]
    public void ZeroDelayPaths_CompleteSynchronously()
    {
        var provider = new MockAdProvider();

        XEngine.Async.XTask initialize = provider.InitializeAsync(CreateSettings());
        Assert.Equal(XEngine.Async.XTaskStatus.Succeeded, initialize.Status);

        XEngine.Async.XTask<AdLoadResult> load = provider.LoadAsync(AdType.RewardVideo);
        Assert.Equal(XEngine.Async.XTaskStatus.Succeeded, load.Status);

        XEngine.Async.XTask<AdShowResult> show = provider.ShowAsync(AdType.RewardVideo);
        Assert.Equal(XEngine.Async.XTaskStatus.Succeeded, show.Status);
        Assert.True(show.GetAwaiter().GetResult().RewardEarned);
    }

    [Fact]
    public void Load_CancelledToken_FailsTheTask()
    {
        var provider = new MockAdProvider().WithLoadDelay(TimeSpan.FromMilliseconds(50));
        provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            provider.LoadAsync(AdType.RewardVideo, cts.Token).GetAwaiter().GetResult());
    }
}
