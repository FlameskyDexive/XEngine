// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Ads;

using Xunit;

namespace XEngine.Ads.Tests;

public sealed class AdsServiceTests
{
    [Fact]
    public void Configure_ThenFullFlow_RoundTripsThroughTheFacade()
    {
        Assert.Null(AdsService.Provider);
        Assert.False(AdsService.IsInitialized);

        var provider = new MockAdProvider();
        AdsService.Configure(provider);
        Assert.Same(provider, AdsService.Provider);

        try
        {
            AdsService.Initialize(new AdsSettings { ProviderId = AdProviderIds.Mock }).GetAwaiter().GetResult();
            Assert.True(AdsService.IsInitialized);

            AdsService.Load(AdType.RewardVideo).GetAwaiter().GetResult();
            Assert.True(AdsService.IsLoaded(AdType.RewardVideo));

            AdShowResult show = AdsService.Show(AdType.RewardVideo).GetAwaiter().GetResult();
            Assert.True(show.Success);
            Assert.True(show.RewardEarned);
            Assert.False(AdsService.IsLoaded(AdType.RewardVideo));
        }
        finally
        {
            AdsService.Close();
        }

        Assert.Null(AdsService.Provider);
        Assert.False(AdsService.IsInitialized);
    }

    [Fact]
    public void Close_DelegatesToTheProvider()
    {
        var provider = new MockAdProvider();
        AdsService.Configure(provider);
        AdsService.Initialize(new AdsSettings()).GetAwaiter().GetResult();
        AdsService.Load(AdType.Interstitial).GetAwaiter().GetResult();

        AdsService.Close();

        Assert.False(provider.IsInitialized);
        Assert.False(provider.IsLoaded(AdType.Interstitial));
    }

    [Fact]
    public void Methods_BeforeConfigure_Throw()
    {
        try
        {
            Assert.Throws<InvalidOperationException>(() => AdsService.Initialize(new AdsSettings()));
            Assert.Throws<InvalidOperationException>(() => AdsService.Load(AdType.RewardVideo));
            Assert.Throws<InvalidOperationException>(() => AdsService.Show(AdType.RewardVideo));
            Assert.False(AdsService.IsLoaded(AdType.RewardVideo));
        }
        finally
        {
            AdsService.Close();
        }
    }

    [Fact]
    public void Configure_NullProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AdsService.Configure(null!));
    }

    [Fact]
    public void GetOrCreateProvider_RoundTripsConfiguration()
    {
        var settings = new AdsSettings { ProviderId = AdProviderIds.HuaweiAdsKit, UseTestAds = true };

        AdsProviderConfig huawei = settings.GetOrCreateProvider(AdProviderIds.HuaweiAdsKit);
        huawei.AppId = "test-app";
        huawei.RewardVideoSlotId = "test-reward";

        AdsProviderConfig? fetched = settings.GetProvider(AdProviderIds.HuaweiAdsKit);
        Assert.NotNull(fetched);
        Assert.Equal("test-app", fetched!.AppId);
        Assert.Equal("test-reward", fetched.RewardVideoSlotId);
        Assert.Null(settings.GetProvider(AdProviderIds.Pangle));
        Assert.Same(huawei, settings.GetOrCreateProvider(AdProviderIds.HuaweiAdsKit));
    }
}
