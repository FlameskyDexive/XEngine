using System.Linq;

using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The lint rules. Each test pins one rule's trigger and, just as importantly, a near-miss that must
/// NOT trigger it — a rule that fires on correct documents is worse than no rule, because it trains
/// people to ignore the output.
/// </summary>
public sealed class XuiLinterTests
{
    private static IReadOnlyList<XuiLintIssue> Lint(string xml)
        => XuiLinter.Lint(XuiDocumentParser.Parse(xml, "T.xui"));

    private static XuiLintIssue Single(string xml, string code)
    {
        IReadOnlyList<XuiLintIssue> issues = Lint(xml);
        return Assert.Single(issues.Where(i => i.Code == code));
    }

    private static void AssertNone(string xml, string code)
        => Assert.DoesNotContain(Lint(xml), i => i.Code == code);

    [Fact]
    public void CleanDocumentProducesNoErrors()
    {
        IReadOnlyList<XuiLintIssue> issues = Lint("""
            <XUI version="1">
              <Theme name="dark"><Color name="panel" value="#1E2433"/></Theme>
              <Screen name="A" reference="1920x1080">
                <VStack id="XVsRoot" color="panel" spacing="8" padding="16">
                  <Text id="XTxtTitle" font-size="24" align="center">Hi</Text>
                  <Btn id="XBtnGo" size="_x44">Go</Btn>
                </VStack>
              </Screen>
            </XUI>
            """);

        Assert.False(XuiLinter.HasErrors(issues), string.Join("; ", issues.Select(i => i.ToString())));
    }

    [Fact]
    public void UnknownTagIsAnError()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1"><Screen name="A"><Marquee id="XImgM"/></Screen></XUI>
            """, XuiLinter.Codes.UnknownTag);

        Assert.Equal(XuiLintSeverity.Error, issue.Severity);
        Assert.Contains("neither a built-in tag nor a template", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredTemplateIsNotAnUnknownTag()
        => AssertNone("""
            <XUI version="1">
              <Template name="Badge"><Text id="XTxtBadge">9</Text></Template>
              <Screen name="A"><Badge/></Screen>
            </XUI>
            """, XuiLinter.Codes.UnknownTag);

    [Fact]
    public void IdMustCarryTheTagPrefix()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1"><Screen name="A"><Btn id="XImgGo">Go</Btn></Screen></XUI>
            """, XuiLinter.Codes.IdPrefix);

        Assert.Contains("must start with \"XBtn\"", issue.Message, System.StringComparison.Ordinal);
        Assert.Equal("XImgGo", issue.ElementId);
    }

    [Fact]
    public void DuplicateIdAfterExpansionIsAnErrorNamingBothLines()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Template name="Row"><Text id="XTxtRow">row</Text></Template>
              <Screen name="A">
                <Row/>
                <Row/>
              </Screen>
            </XUI>
            """, XuiLinter.Codes.DuplicateId);

        Assert.Contains("already used at line", issue.Message, System.StringComparison.Ordinal);
        Assert.Contains("parameterise", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterisedTemplateIdsDoNotCollide()
        => AssertNone("""
            <XUI version="1">
              <Template name="Row"><Param name="rowId"/><Text id="{rowId}">row</Text></Template>
              <Screen name="A">
                <Row rowId="XTxtOne"/>
                <Row rowId="XTxtTwo"/>
              </Screen>
            </XUI>
            """, XuiLinter.Codes.DuplicateId);

    [Fact]
    public void UnknownAttributeIsAnErrorBecauseItWouldBeDropped()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1"><Screen name="A"><Text id="XTxtA" colour="#fff">x</Text></Screen></XUI>
            """, XuiLinter.Codes.UnknownAttribute);

        Assert.Contains("silently dropped", issue.Message, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<Image id="XImgA" size="480"/>""")]
    [InlineData("""<Image id="XImgA" anchor="middle"/>""")]
    [InlineData("""<Image id="XImgA" pos="12"/>""")]
    [InlineData("""<Text id="XTxtA" font-size="big">x</Text>""")]
    [InlineData("""<Image id="XImgA" type="cover"/>""")]
    [InlineData("""<Image id="XImgA" mask="circle"/>""")]
    [InlineData("""<Btn id="XBtnA" interactable="yes">x</Btn>""")]
    public void BadAttributeValuesAreErrors(string element)
    {
        XuiLintIssue issue = Single(
            "<XUI version=\"1\"><Screen name=\"A\">" + element + "</Screen></XUI>",
            XuiLinter.Codes.BadAttributeValue);

        Assert.Contains("expected", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownColorTokenIsAnError()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1"><Screen name="A"><Image id="XImgA" color="panel"/></Screen></XUI>
            """, XuiLinter.Codes.UnknownColorToken);

        Assert.Contains("not a hex literal", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredTokenResolves()
        => AssertNone("""
            <XUI version="1">
              <Theme name="d"><Color name="panel" value="#111"/></Theme>
              <Screen name="A"><Image id="XImgA" color="panel"/></Screen>
            </XUI>
            """, XuiLinter.Codes.UnknownColorToken);

    [Fact]
    public void AnchorOnALayoutChildIsADeadAttributeWarning()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Screen name="A">
                <VStack id="XVsRoot"><Btn id="XBtnGo" anchor="center">Go</Btn></VStack>
              </Screen>
            </XUI>
            """, XuiLinter.Codes.LayoutChildRect);

        Assert.Equal(XuiLintSeverity.Warning, issue.Severity);
        Assert.Contains("the container writes its children's rects", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorOnALayoutContainerItselfIsFine()
        => AssertNone("""
            <XUI version="1">
              <Screen name="A">
                <VStack id="XVsRoot" anchor="center"><Btn id="XBtnGo">Go</Btn></VStack>
              </Screen>
            </XUI>
            """, XuiLinter.Codes.LayoutChildRect);

    [Fact]
    public void SizeOnALayoutChildIsAllowed()
        => AssertNone("""
            <XUI version="1">
              <Screen name="A">
                <VStack id="XVsRoot"><Btn id="XBtnGo" size="_x44">Go</Btn></VStack>
              </Screen>
            </XUI>
            """, XuiLinter.Codes.LayoutChildRect);

    [Fact]
    public void ControlWithoutAnIdIsWarnedAsUnreachable()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1"><Screen name="A"><Btn>Go</Btn></Screen></XUI>
            """, XuiLinter.Codes.MissingId);

        Assert.Equal(XuiLintSeverity.Warning, issue.Severity);
        Assert.Contains("no code can reach it", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void PlainImageWithoutAnIdIsNotWarned()
        => AssertNone("""
            <XUI version="1"><Screen name="A"><Image anchor="stretch" color="#000"/></Screen></XUI>
            """, XuiLinter.Codes.MissingId);

    [Fact]
    public void ChildrenUnderAControlThatBuildsItsOwnTreeAreRejected()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Screen name="A"><Slider id="XSliderA"><Image id="XImgB"/></Slider></Screen>
            </XUI>
            """, XuiLinter.Codes.ChildrenNotAllowed);

        Assert.Contains("would be discarded", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeInheritingAnUndeclaredThemeIsAnError()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Theme name="dark" base="light"><Color name="panel" value="#111"/></Theme>
              <Screen name="A"><Frame id="XRectA"/></Screen>
            </XUI>
            """, XuiLinter.Codes.UnknownTheme);

        Assert.Contains("which this document does not declare", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyScreenIsWarned()
        => Assert.Equal(
            XuiLintSeverity.Warning,
            Single("""<XUI version="1"><Screen name="A"></Screen></XUI>""", XuiLinter.Codes.EmptyScreen).Severity);

    [Fact]
    public void UnusedTemplateIsWarned()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Template name="Dead"><Text id="XTxtDead">x</Text></Template>
              <Screen name="A"><Frame id="XRectA"/></Screen>
            </XUI>
            """, XuiLinter.Codes.UnusedTemplate);

        Assert.Contains("never used", issue.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateUsedOnlyByAnotherTemplateCountsAsUsed()
        => AssertNone("""
            <XUI version="1">
              <Template name="Inner"><Text id="XTxtInner">x</Text></Template>
              <Template name="Outer"><Frame id="XRectOuter"><Inner/></Frame></Template>
              <Screen name="A"><Outer/></Screen>
            </XUI>
            """, XuiLinter.Codes.UnusedTemplate);

    [Fact]
    public void EveryIssueCarriesAUsableSourcePosition()
    {
        IReadOnlyList<XuiLintIssue> issues = Lint("""
            <XUI version="1">
              <Screen name="A">
                <Btn id="XImgGo" colour="x">Go</Btn>
              </Screen>
            </XUI>
            """);

        Assert.NotEmpty(issues);
        foreach (XuiLintIssue issue in issues)
        {
            Assert.True(issue.Span.HasValue, issue.Code + " has no source position");
            Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        }
    }

    [Fact]
    public void TheShippedSampleLintsClean()
    {
        string path = Path.Combine(
            RepoRootLocator.Find(), "Packages", "com.xengine.xui", "Samples~", "LoginScreen", "Login.xui");

        IReadOnlyList<XuiLintIssue> issues = XuiLinter.Lint(XuiDocumentParser.ParseFile(path));

        Assert.False(
            XuiLinter.HasErrors(issues),
            "The shipped sample must lint clean: " + string.Join("; ", issues.Select(i => i.ToString())));
    }
}
