// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text.Json;

using XEngine.Ads;
using XEngine.Ads.HarmonyOS;

using Xunit;

namespace XEngine.Ads.Tests;

public sealed class HarmonyOSAdProviderTests
{
    private sealed class FakeAdsNative : IHarmonyOSAdsNative
    {
        public List<string> Requests { get; } = new();
        public Action<string>? Sink { get; private set; }
        public int StartResult { get; set; }
        public int NextRequestResult { get; set; }
        public bool Stopped { get; private set; }

        public int Start(Action<string> onEvent)
        {
            Sink = onEvent;
            return StartResult;
        }

        public void Stop()
        {
            Stopped = true;
            Sink = null;
        }

        public int Request(string json)
        {
            Requests.Add(json);
            return NextRequestResult;
        }

        public void Deliver(string json) => Sink?.Invoke(json);

        public int LastRequestId()
        {
            using var document = JsonDocument.Parse(Requests[^1]);
            return document.RootElement.GetProperty("id").GetInt32();
        }
    }

    private static AdsSettings CreateSettings() => new()
    {
        ProviderId = AdProviderIds.HuaweiAdsKit,
        UseTestAds = true,
        Providers =
        {
            [AdProviderIds.HuaweiAdsKit] = new AdsProviderConfig
            {
                AppId = "app",
                RewardVideoSlotId = "reward-slot",
                InterstitialSlotId = "inter-slot",
            },
        },
    };

    private static int Initialize(FakeAdsNative native, HarmonyOSAdProvider provider, AdsSettings? settings = null)
    {
        XEngine.Async.XTask task = provider.InitializeAsync(settings ?? CreateSettings());
        Assert.False(task.IsCompleted, "init stays pending until the glue answers");

        native.Deliver($"{{\"v\":1,\"id\":{native.LastRequestId()},\"type\":\"init-result\",\"ok\":true}}");

        Assert.True(task.IsCompleted, "init completes inline once the init-result event arrives");
        Assert.True(provider.IsInitialized);
        return native.LastRequestId();
    }

    [Fact]
    public void Initialize_RequiresRegisteredBridge()
    {
        var native = new FakeAdsNative { StartResult = 0, NextRequestResult = -2 };
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);

        Assert.Throws<InvalidOperationException>(() => provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult());
        Assert.False(provider.IsInitialized);
    }

    [Fact]
    public void Initialize_NativeStartFailure_Throws()
    {
        var native = new FakeAdsNative { StartResult = -1 };
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);

        Assert.Throws<InvalidOperationException>(() => provider.InitializeAsync(CreateSettings()).GetAwaiter().GetResult());
        Assert.Empty(native.Requests);
    }

    [Fact]
    public void Initialize_SucceedsOnInitResultEvent()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);

        XEngine.Async.XTask task = provider.InitializeAsync(CreateSettings());
        int id = native.LastRequestId();
        Assert.False(task.IsCompleted, "init stays pending until the glue answers");

        native.Deliver($"{{\"v\":1,\"id\":{id},\"type\":\"init-result\",\"ok\":true}}");

        Assert.True(task.IsCompleted);
        Assert.True(provider.IsInitialized);
        Assert.Contains("\"op\":\"initialize\"", native.Requests[0]);
    }

    [Fact]
    public void Initialize_InitResultFailure_FaultsWithVendorError()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);

        XEngine.Async.XTask task = provider.InitializeAsync(CreateSettings());
        native.Deliver($"{{\"v\":1,\"id\":{native.LastRequestId()},\"type\":\"init-result\",\"ok\":false,\"error\":\"no context\"}}");

        Assert.True(task.IsCompleted);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(task.GetAwaiter().GetResult);
        Assert.Contains("no context", error.Message);
    }

    [Fact]
    public void Load_PostsRequestAndMarksLoadedOnResult()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);
        Initialize(native, provider);
        var loaded = new List<AdType>();
        provider.Loaded += type => loaded.Add(type);

        XEngine.Async.XTask<AdLoadResult> task = provider.LoadAsync(AdType.RewardVideo);
        Assert.False(task.IsCompleted);
        Assert.Contains("reward-slot", native.Requests[^1]);
        Assert.Contains("\"test\":true", native.Requests[^1]);

        native.Deliver($"{{\"v\":1,\"id\":{native.LastRequestId()},\"type\":\"load-result\",\"ok\":true,\"adType\":\"reward\"}}");

        Assert.True(task.IsCompleted);
        Assert.True(task.GetAwaiter().GetResult().Success);
        Assert.True(provider.IsLoaded(AdType.RewardVideo));
        Assert.Equal(new[] { AdType.RewardVideo }, loaded);
    }

    [Fact]
    public void Load_MissingSlotConfig_FailsWithoutPosting()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.Pangle, native);
        Initialize(native, provider, new AdsSettings { ProviderId = AdProviderIds.Pangle });

        AdLoadResult result = provider.LoadAsync(AdType.Interstitial).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Contains("slot id", result.Error);
        Assert.Single(native.Requests); // only initialize was posted
    }

    [Fact]
    public void Load_RequestPostFailure_FailsTheResult()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);
        Initialize(native, provider);
        native.NextRequestResult = -4;

        AdLoadResult result = provider.LoadAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Contains("-4", result.Error);
    }

    [Fact]
    public void Show_FullFlow_ClearsLoadedAndReportsReward()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);
        Initialize(native, provider);

        XEngine.Async.XTask<AdLoadResult> load = provider.LoadAsync(AdType.RewardVideo);
        native.Deliver($"{{\"v\":1,\"id\":{native.LastRequestId()},\"type\":\"load-result\",\"ok\":true,\"adType\":\"reward\"}}");
        Assert.True(load.GetAwaiter().GetResult().Success);

        var events = new List<string>();
        provider.ShowStart += type => events.Add("show-start:" + type);
        provider.ShowEnd += (type, reward) => events.Add($"show-end:{type}:{reward}");

        XEngine.Async.XTask<AdShowResult> task = provider.ShowAsync(AdType.RewardVideo);
        native.Deliver("{\"v\":1,\"type\":\"state\",\"state\":\"show-start\",\"adType\":\"reward\"}");
        Assert.False(provider.IsLoaded(AdType.RewardVideo), "show-start consumes the cached ad");
        native.Deliver("{\"v\":1,\"type\":\"state\",\"state\":\"show-end\",\"adType\":\"reward\",\"reward\":true}");
        native.Deliver($"{{\"v\":1,\"id\":{native.LastRequestId()},\"type\":\"show-result\",\"ok\":true,\"reward\":true,\"adType\":\"reward\"}}");

        Assert.True(task.IsCompleted);
        AdShowResult show = task.GetAwaiter().GetResult();
        Assert.True(show.Success);
        Assert.True(show.RewardEarned);
        Assert.Equal(new[] { "show-start:RewardVideo", "show-end:RewardVideo:True" }, events);
    }

    [Fact]
    public void Show_WithoutLoadedAd_FailsWithoutPosting()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);
        Initialize(native, provider);

        AdShowResult show = provider.ShowAsync(AdType.RewardVideo).GetAwaiter().GetResult();

        Assert.False(show.Success);
        Assert.Single(native.Requests); // only initialize
    }

    [Fact]
    public void Show_VendorFailure_FailsTheResult()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);
        Initialize(native, provider);

        XEngine.Async.XTask<AdLoadResult> load = provider.LoadAsync(AdType.Interstitial);
        native.Deliver($"{{\"v\":1,\"id\":{native.LastRequestId()},\"type\":\"load-result\",\"ok\":true,\"adType\":\"interstitial\"}}");
        Assert.True(load.GetAwaiter().GetResult().Success);

        XEngine.Async.XTask<AdShowResult> task = provider.ShowAsync(AdType.Interstitial);
        int showId = native.LastRequestId();
        native.Deliver($"{{\"v\":1,\"id\":{showId},\"type\":\"show-result\",\"ok\":false,\"error\":\"errorCode=21800003\"}}");

        AdShowResult show = task.GetAwaiter().GetResult();
        Assert.False(show.Success);
        Assert.Contains("21800003", show.Error);
    }

    [Fact]
    public void Close_StopsTheBridgeAndClearsState()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);
        Initialize(native, provider);

        provider.Close();

        Assert.True(native.Stopped);
        Assert.False(provider.IsInitialized);
        Assert.False(provider.IsLoaded(AdType.RewardVideo));
    }

    [Fact]
    public void UnknownEventType_DoesNotCrashTheSink()
    {
        var native = new FakeAdsNative();
        var provider = new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit, native);
        Initialize(native, provider);

        native.Deliver("{\"v\":1,\"type\":\"future-event\",\"ok\":true}");
        native.Deliver("garbage");

        Assert.True(provider.IsInitialized);
    }
}
