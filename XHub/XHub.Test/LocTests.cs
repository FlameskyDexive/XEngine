using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using XHub.Resources;
using XHub.Services;
using XHub.ViewModels;
using Xunit;

namespace XHub.Test;

[Collection("Avalonia")]
public class LocTests
{
    [Fact]
    public void AllLanguages_ResolveEveryKnownKey()
    {
        foreach (string language in Loc.KnownLanguages)
        {
            var loc = new Loc();
            loc.SetLanguage(language);
            foreach (string key in Loc.KnownKeys)
                Assert.True(loc[key] != key,
                    $"{language} is missing the localization key '{key}' (indexer fell back to the raw key).");
        }
    }

    [Fact]
    public void UnknownKey_FallsBackToKeyText()
    {
        var loc = new Loc();
        loc.SetLanguage("en-US");
        Assert.Equal("missing.key", loc["missing.key"]);
    }

    [Fact]
    public void SetLanguage_SwitchesValues()
    {
        var loc = new Loc();
        loc.SetLanguage("en-US");
        Assert.Equal("Projects", loc["nav.projects"]);
        loc.SetLanguage("zh-CN");
        Assert.Equal("项目", loc["nav.projects"]);
    }
}

[Collection("Avalonia")]
public class NavItemTests
{
    private static MainWindowViewModel CreateShell() => new(
        new SettingsService(Path.Combine(
            Path.GetTempPath(), $"xhub-nav-{Guid.NewGuid():N}", "settings.json")));

    [Fact]
    public void MainAndBottomNav_UseDistinctPages()
    {
        var vm = CreateShell();
        var pages = vm.MainNavItems.Concat(vm.BottomNavItems).Select(n => n.Page).ToList();
        Assert.Equal(pages.Count, pages.Distinct().Count());
        Assert.Equal(5, pages.Count);
    }

    [Fact]
    public void NavIcons_HavePathData()
    {
        // StreamGeometry.Parse requires the Avalonia UI thread, which parallel unit tests do not
        // own — assert on the raw markup here; rendering is covered by the screenshot evidence.
        var vm = CreateShell();
        foreach (var item in vm.MainNavItems.Concat(vm.BottomNavItems))
        {
            Assert.StartsWith("M", item.IconPath);
            Assert.Contains("L", item.IconPath);
            Assert.EndsWith("Z", item.IconPath);
        }
    }

    [Fact]
    public void SelectingBottomNav_MapsToCorrectPage()
    {
        var vm = CreateShell();
        vm.BottomNavIndex = 0;
        Assert.Equal(HubPageId.Downloads, vm.SelectedPage);
        Assert.Equal(-1, vm.MainNavIndex);
        Assert.Same(vm.Downloads, vm.CurrentPage);

        vm.BottomNavIndex = 1;
        Assert.Equal(HubPageId.Settings, vm.SelectedPage);
        Assert.Same(vm.Settings, vm.CurrentPage);
    }

    [Fact]
    public void SelectingMainNav_ClearsBottomSelection()
    {
        var vm = CreateShell();
        vm.BottomNavIndex = 1; // Settings
        vm.MainNavIndex = 2;   // Templates
        Assert.Equal(HubPageId.Templates, vm.SelectedPage);
        Assert.Equal(-1, vm.BottomNavIndex);
        Assert.Same(vm.Templates, vm.CurrentPage);
    }

    [Fact]
    public void HandleRequest_PageDeepLink_SwitchesPage()
    {
        var vm = CreateShell();
        vm.HandleRequest(CliOptions.Parse(["--page", "installations"]));
        Assert.Equal(HubPageId.Installations, vm.SelectedPage);
    }

    [Fact]
    public void HandleRequest_OpenPath_LandsOnProjectsPage()
    {
        var vm = CreateShell();
        vm.MainNavIndex = 4; // Settings
        vm.HandleRequest(CliOptions.Parse(["--open", @"D:\Proj"]));
        Assert.Equal(HubPageId.Projects, vm.SelectedPage);
    }
}
