using System;
using System.Collections.Generic;

using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The deep-link grammar. Links arrive from outside the app — notifications, shared URLs, tutorial
/// steps — so the cases that matter most are the malformed ones: a bad link must fail cleanly, and a
/// single bad escape must not take navigation down.
/// </summary>
public sealed class XuiLinkTests
{
    private static XuiRouteQuery Parse(string link, string scheme = XuiLink.DefaultScheme)
    {
        Assert.True(XuiLink.TryParse(link, out XuiRouteQuery? query, scheme), "did not parse: " + link);
        return query!;
    }

    [Fact]
    public void ParsesScreenAndQuery()
    {
        XuiRouteQuery query = Parse("xui://Login?redirect=Home&level=3");

        Assert.Equal("Login", query.ScreenName);
        Assert.Equal("Home", query.GetString("redirect"));
        Assert.Equal(3, query.GetInt("level"));
    }

    [Fact]
    public void ABareScreenNameIsAValidLink()
    {
        // A hand-written call passes this; rejecting it would only invite string concatenation.
        Assert.Equal("Login", Parse("Login").ScreenName);
    }

    [Fact]
    public void TrailingSlashIsTolerated()
        => Assert.Equal("Login", Parse("xui://Login/").ScreenName);

    [Fact]
    public void ABareFlagReadsAsTrue()
    {
        XuiRouteQuery query = Parse("xui://Shop?featured");

        Assert.True(query.GetBool("featured"));
        Assert.Equal("true", query.Get("featured"));
    }

    [Fact]
    public void PercentEncodingIsDecodedInKeysAndValues()
    {
        XuiRouteQuery query = Parse("xui://Chat?room%20id=general%20chat");

        Assert.Equal("general chat", query.GetString("room id"));
    }

    [Fact]
    public void AMalformedEscapeDoesNotTakeNavigationDown()
    {
        // "%zz" is not valid percent-encoding. A link from outside the app should not be able to throw.
        XuiRouteQuery query = Parse("xui://Chat?tag=%zz");

        Assert.Equal("%zz", query.GetString("tag"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("://Login")]
    [InlineData("xui://")]
    [InlineData("xui://not-an-identifier")]
    [InlineData("xui://9Login")]
    public void MalformedLinksAreRejected(string link)
        => Assert.False(XuiLink.TryParse(link, out _));

    [Fact]
    public void AForeignSchemeIsRejected()
    {
        Assert.False(XuiLink.TryParse("https://Login", out _));
        Assert.True(XuiLink.TryParse("myapp://Login", out _, scheme: "myapp"));
    }

    [Fact]
    public void SchemeMatchingIsCaseInsensitive()
        => Assert.True(XuiLink.TryParse("XUI://Login", out _));

    [Fact]
    public void EmptyKeysAreDropped()
    {
        XuiRouteQuery query = Parse("xui://Login?=orphan&real=1");

        Assert.False(query.Has(""));
        Assert.Equal("1", query.Get("real"));
    }

    [Fact]
    public void LaterDuplicateKeyWins()
        => Assert.Equal("second", Parse("xui://Login?a=first&a=second").GetString("a"));

    [Fact]
    public void TypedAccessorsFallBackRatherThanThrowOnBadInput()
    {
        XuiRouteQuery query = Parse("xui://Login?count=notanumber");

        Assert.Equal(7, query.GetInt("count", 7));
        Assert.Equal(1.5f, query.GetFloat("missing", 1.5f));
        Assert.True(query.GetBool("missing", true));
        Assert.Equal("fallback", query.GetString("missing", "fallback"));
    }

    [Fact]
    public void RoundTripsThroughToLink()
    {
        XuiRouteQuery original = Parse("xui://Chat?room=general&muted=true");
        XuiRouteQuery again = Parse(original.ToLink());

        Assert.Equal(original.ScreenName, again.ScreenName);
        Assert.Equal("general", again.GetString("room"));
        Assert.True(again.GetBool("muted"));
    }

    [Fact]
    public void ToLinkEscapesValuesSoTheResultReparses()
    {
        var query = new XuiRouteQuery("Chat", new Dictionary<string, string>
        {
            ["room"] = "general chat&more",
        });

        XuiRouteQuery again = Parse(query.ToLink());

        Assert.Equal("general chat&more", again.GetString("room"));
    }

    [Fact]
    public void ToLinkUsesTheGivenScheme()
        => Assert.StartsWith("myapp://Login", new XuiRouteQuery("Login").ToLink("myapp"), StringComparison.Ordinal);
}
