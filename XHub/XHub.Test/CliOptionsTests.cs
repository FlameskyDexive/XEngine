using System;
using System.IO;
using XHub.Models;
using XHub.Services;
using Xunit;

namespace XHub.Test;

public class CliOptionsTests
{
    [Fact]
    public void Parse_EmptyArgs_YieldsNoRequests()
    {
        var options = CliOptions.Parse([]);
        Assert.False(options.HasForwardableRequest);
        Assert.Null(options.Page);
        Assert.Null(options.OpenPath);
    }

    [Fact]
    public void Parse_Page_AcceptsAliases()
    {
        Assert.Equal(HubPageId.Projects, CliOptions.Parse(["--page", "projects"]).Page);
        Assert.Equal(HubPageId.Installations, CliOptions.Parse(["--page", "engines"]).Page);
        Assert.Equal(HubPageId.Templates, CliOptions.Parse(["--page", "templates"]).Page);
        Assert.Equal(HubPageId.Downloads, CliOptions.Parse(["--page", "downloads"]).Page);
        Assert.Equal(HubPageId.Settings, CliOptions.Parse(["--page", "设置"]).Page);
    }

    [Fact]
    public void Parse_Open_SetsForwardableRequest()
    {
        var options = CliOptions.Parse(["--open", @"D:\Projects\MyGame"]);
        Assert.True(options.HasForwardableRequest);
        Assert.Equal(@"D:\Projects\MyGame", options.OpenPath);
    }

    [Fact]
    public void Parse_UnknownArg_IsIgnored()
    {
        var options = CliOptions.Parse(["--definitely-not-a-flag", "value", "--page", "settings"]);
        Assert.Equal(HubPageId.Settings, options.Page);
    }

    [Fact]
    public void Parse_Language_MapsCommonCodes()
    {
        Assert.Equal(HubLanguage.SimplifiedChinese, CliOptions.Parse(["--lang", "zh"]).Language);
        Assert.Equal(HubLanguage.English, CliOptions.Parse(["--lang", "en-US"]).Language);
        Assert.Equal(HubLanguage.System, CliOptions.Parse(["--lang", "klingon"]).Language);
    }

    [Fact]
    public void Parse_Delay_IsClamped()
    {
        Assert.Equal(100, CliOptions.Parse(["--delay-ms", "100"]).ScreenshotDelayMs);
        Assert.Equal(60_000, CliOptions.Parse(["--delay-ms", "999999"]).ScreenshotDelayMs);
    }
}
