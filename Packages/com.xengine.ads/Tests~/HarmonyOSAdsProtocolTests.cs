// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text.Json;

using XEngine.Ads;
using XEngine.Ads.HarmonyOS;

using Xunit;

namespace XEngine.Ads.Tests;

public sealed class HarmonyOSAdsProtocolTests
{
    [Fact]
    public void BuildLoad_CarriesSlotAdTypeAndTestFlag()
    {
        string json = HarmonyOSAdsProtocol.BuildLoad(7, AdType.RewardVideo, "testb4zuon2bty", testAds: true);

        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.Equal("load", root.GetProperty("op").GetString());
        Assert.Equal("reward", root.GetProperty("adType").GetString());
        Assert.Equal("testb4zuon2bty", root.GetProperty("slotId").GetString());
        Assert.True(root.GetProperty("test").GetBoolean());
    }

    [Fact]
    public void BuildShow_EscapesAndTokensInterstitial()
    {
        Assert.Equal("interstitial", HarmonyOSAdsProtocol.AdTypeToken(AdType.Interstitial));

        using var document = JsonDocument.Parse(HarmonyOSAdsProtocol.BuildShow(3, AdType.Interstitial));
        JsonElement root = document.RootElement;
        Assert.Equal("show", root.GetProperty("op").GetString());
        Assert.Equal("interstitial", root.GetProperty("adType").GetString());
    }

    [Fact]
    public void BuildLoad_EscapesQuotesInSlotId()
    {
        using var document = JsonDocument.Parse(HarmonyOSAdsProtocol.BuildLoad(1, AdType.RewardVideo, "slot\"with\\quotes", testAds: false));
        Assert.Equal("slot\"with\\quotes", document.RootElement.GetProperty("slotId").GetString());
    }

    [Fact]
    public void TryParseEvent_ParsesFullPayload()
    {
        bool parsed = HarmonyOSAdsProtocol.TryParseEvent(
            "{\"v\":1,\"id\":5,\"type\":\"show-result\",\"ok\":true,\"reward\":true,\"adType\":\"reward\"}",
            out HarmonyOSAdsEvent @event);

        Assert.True(parsed);
        Assert.Equal(5, @event.Id);
        Assert.Equal("show-result", @event.Type);
        Assert.True(@event.Ok);
        Assert.True(@event.Reward);
        Assert.True(@event.IsAdType(AdType.RewardVideo));
        Assert.False(@event.IsAdType(AdType.Interstitial));
    }

    [Fact]
    public void TryParseEvent_DefaultsMissingFields()
    {
        bool parsed = HarmonyOSAdsProtocol.TryParseEvent("{\"v\":1,\"type\":\"state\",\"state\":\"show-start\"}", out HarmonyOSAdsEvent @event);

        Assert.True(parsed);
        Assert.Equal(-1, @event.Id);
        Assert.Equal("state", @event.Type);
        Assert.Equal("show-start", @event.State);
        Assert.False(@event.Ok);
        Assert.False(@event.Reward);
        Assert.Equal(string.Empty, @event.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"no-type\":true}")]
    public void TryParseEvent_RejectsMalformedPayloads(string json)
    {
        Assert.False(HarmonyOSAdsProtocol.TryParseEvent(json, out _));
    }
}
