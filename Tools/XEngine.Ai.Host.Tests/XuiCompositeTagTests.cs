using System;
using System.Linq;

using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The composite tags added in M8 — <c>SafeArea</c>, <c>Show</c>, <c>Progress</c>, <c>TabBar</c>,
/// <c>Tab</c> — and the lint rules that keep them honest.
/// </summary>
public sealed class XuiCompositeTagTests
{
    private static IReadOnlyList<XuiLintIssue> Lint(string xml)
        => XuiLinter.Lint(XuiDocumentParser.Parse(xml, "T.xui"));

    private static XuiLintIssue Single(string xml, string code)
        => Assert.Single(Lint(xml).Where(i => i.Code == code));

    private static void AssertNone(string xml, string code)
        => Assert.DoesNotContain(Lint(xml), i => i.Code == code);

    private const string Tabbed = """
        <XUI version="1">
          <Screen name="Settings">
            <TabBar id="XTabsMain" size="_x44" selected="1">
              <Tab id="XTabAudio" bind="XRectAudio">Audio</Tab>
              <Tab id="XTabVideo" bind="XRectVideo">Video</Tab>
            </TabBar>
            <Frame id="XRectAudio"/>
            <Frame id="XRectVideo"/>
          </Screen>
        </XUI>
        """;

    // ------------------------------------------------------------------
    //  Catalog
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("SafeArea", "XSafe", "SafeAreaFitter")]
    [InlineData("Show", "XShow", "XuiShowGroup")]
    [InlineData("Progress", "XProg", "XuiProgress")]
    [InlineData("TabBar", "XTabs", "XuiTabBar")]
    [InlineData("Tab", "XTab", "XuiTab")]
    public void NewTagsAreRegisteredWithTheirPrefixAndBindingType(string tag, string prefix, string binding)
    {
        XuiTagInfo info = Assert.IsType<XuiTagInfo>(XuiTagLibrary.Find(tag));

        Assert.Equal(prefix, info.IdPrefix);
        Assert.Equal(binding, info.BindingType);
    }

    [Fact]
    public void ProgressTakesNoChildrenBecauseItBuildsItsOwnFill()
    {
        Assert.False(XuiTagLibrary.Find("Progress")!.AcceptsChildren);

        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Screen name="A"><Progress id="XProgA"><Frame id="XRectB"/></Progress></Screen>
            </XUI>
            """, XuiLinter.Codes.ChildrenNotAllowed);
        Assert.Contains("would be discarded", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATabBarCountsAsALayoutContainerSoAnchorOnATabIsDead()
    {
        Assert.True(XuiTagLibrary.Find("TabBar")!.IsLayoutGroup);

        Single("""
            <XUI version="1">
              <Screen name="A">
                <TabBar id="XTabsA"><Tab id="XTabB" anchor="center">B</Tab></TabBar>
                <Frame id="XRectB"/>
              </Screen>
            </XUI>
            """, XuiLinter.Codes.LayoutChildRect);
    }

    // ------------------------------------------------------------------
    //  Lint
    // ------------------------------------------------------------------

    [Fact]
    public void AWellFormedTabbedScreenLintsClean()
    {
        IReadOnlyList<XuiLintIssue> issues = Lint(Tabbed);
        Assert.False(XuiLinter.HasErrors(issues), string.Join("; ", issues.Select(i => i.ToString())));
    }

    [Fact]
    public void ATabBindingNothingIsAnError()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Screen name="A">
                <TabBar id="XTabsA"><Tab id="XTabB" bind="XRectMissing">B</Tab></TabBar>
              </Screen>
            </XUI>
            """, XuiLinter.Codes.UnknownBind);

        Assert.Contains("would reveal nothing", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATabMayBindAPanelDeclaredAfterTheBar()
        // Resolution happens after the whole screen is walked, so declaration order must not matter.
        => AssertNone(Tabbed, XuiLinter.Codes.UnknownBind);

    [Fact]
    public void ATabOutsideATabBarIsAnError()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Screen name="A"><Frame id="XRectA"><Tab id="XTabB">B</Tab></Frame></Screen>
            </XUI>
            """, XuiLinter.Codes.TabOutsideBar);

        Assert.Contains("direct child of <TabBar>", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<Progress id="XProgA" direction="sideways"/>""")]
    [InlineData("""<Progress id="XProgA" value="lots"/>""")]
    [InlineData("""<Show id="XShowA" visible="maybe"/>""")]
    [InlineData("""<SafeArea id="XSafeA" conform-y="1"/>""")]
    [InlineData("""<TabBar id="XTabsA" selected="first"/>""")]
    public void BadValuesOnTheNewAttributesAreErrors(string element)
        => Single("<XUI version=\"1\"><Screen name=\"A\">" + element + "</Screen></XUI>",
            XuiLinter.Codes.BadAttributeValue);

    [Theory]
    [InlineData("left-to-right", XuiProgressFill.LeftToRight)]
    [InlineData("right-to-left", XuiProgressFill.RightToLeft)]
    [InlineData("bottom-to-top", XuiProgressFill.BottomToTop)]
    [InlineData("top-to-bottom", XuiProgressFill.TopToBottom)]
    public void ProgressDirectionsParse(string text, XuiProgressFill expected)
    {
        Assert.True(XuiValueParser.TryParseProgressDirection(text, out XuiProgressFill direction));
        Assert.Equal(expected, direction);
    }

    [Fact]
    public void UnknownAttributesOnTheNewTagsAreStillRejected()
        => Single("""
            <XUI version="1"><Screen name="A"><Progress id="XProgA" speed="2"/></Screen></XUI>
            """, XuiLinter.Codes.UnknownAttribute);

    // ------------------------------------------------------------------
    //  Bindings
    // ------------------------------------------------------------------

    [Fact]
    public void ScaffoldBindsTheNewControlsWithTheirOwnTypes()
    {
        XuiDocument document = XuiDocumentParser.Parse(Tabbed, "T.xui");
        var templates = new XuiTemplateTable();
        templates.Add(document);
        ScreenDef screen = document.Screens[0];

        XuiScaffoldOutput output = XuiViewCodeGenerator.Generate(
            screen, XuiTemplateExpander.Expand(screen.Root, templates, "T.xui"), "Game.UI", "T.xui");

        Assert.Equal("XuiTabBar", Assert.Single(output.Members.Where(m => m.Property == "TabsMain")).Type);
        Assert.Equal("XuiTab", Assert.Single(output.Members.Where(m => m.Property == "TabAudio")).Type);
        // A Tab with inline text builds a label child, so it gets a label binding like Btn does.
        Assert.Contains(output.Members, m => m.Property == "TabAudioLabel");
    }

    [Fact]
    public void ControllerSkeletonDoesNotOfferHandlersForTheNonInteractiveOnes()
    {
        XuiDocument document = XuiDocumentParser.Parse("""
            <XUI version="1">
              <Screen name="A">
                <Progress id="XProgLoad"/>
                <Show id="XShowEmpty"><Text id="XTxtEmpty">none</Text></Show>
              </Screen>
            </XUI>
            """, "T.xui");
        var templates = new XuiTemplateTable();
        templates.Add(document);
        ScreenDef screen = document.Screens[0];

        XuiScaffoldOutput output = XuiViewCodeGenerator.Generate(
            screen, XuiTemplateExpander.Expand(screen.Root, templates, "T.xui"), "", "T.xui");

        Assert.DoesNotContain("View.ProgLoad.On", output.ControllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("View.ShowEmpty.On", output.ControllerSource, StringComparison.Ordinal);
        // Both are still reachable as bindings.
        Assert.Contains("XuiProgress ProgLoad", output.ViewSource, StringComparison.Ordinal);
        Assert.Contains("XuiShowGroup ShowEmpty", output.ViewSource, StringComparison.Ordinal);
    }
}
