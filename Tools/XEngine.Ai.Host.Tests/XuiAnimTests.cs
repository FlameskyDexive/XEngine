using System;
using System.Linq;

using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The <c>&lt;Anim&gt;</c> tag and its easing curves. Motion is the one part of a document you cannot
/// check by reading, so what is pinned here is everything decidable without a frame: the grammar, the
/// curve shapes, and the lint rules that catch a transition which would play and do nothing.
/// </summary>
public sealed class XuiAnimTests
{
    private static IReadOnlyList<XuiLintIssue> Lint(string xml)
        => XuiLinter.Lint(XuiDocumentParser.Parse(xml, "T.xui"));

    private static XuiLintIssue Single(string xml, string code)
        => Assert.Single(Lint(xml).Where(i => i.Code == code));

    // ------------------------------------------------------------------
    //  Easing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(XuiEase.Linear)]
    [InlineData(XuiEase.OutQuad)]
    [InlineData(XuiEase.InQuad)]
    [InlineData(XuiEase.InOutQuad)]
    [InlineData(XuiEase.OutBack)]
    public void EveryCurveStartsAtZeroAndEndsAtOne(XuiEase ease)
    {
        // A curve that does not land exactly on 1 leaves the element permanently short of its target.
        Assert.Equal(0f, XuiEasing.Evaluate(ease, 0f));
        Assert.Equal(1f, XuiEasing.Evaluate(ease, 1f));
    }

    [Theory]
    [InlineData(XuiEase.Linear)]
    [InlineData(XuiEase.OutQuad)]
    [InlineData(XuiEase.InQuad)]
    [InlineData(XuiEase.InOutQuad)]
    public void CurvesAreMonotonic(XuiEase ease)
    {
        float previous = -1f;
        for (int i = 0; i <= 20; i++)
        {
            float value = XuiEasing.Evaluate(ease, i / 20f);
            Assert.True(value >= previous, ease + " went backwards at t=" + (i / 20f));
            previous = value;
        }
    }

    [Fact]
    public void OutQuadDecelerates()
    {
        // Past halfway it should already be more than halfway there — that is what "slows into place"
        // means, and it is the default because it is what UI motion almost always wants.
        Assert.True(XuiEasing.Evaluate(XuiEase.OutQuad, 0.5f) > 0.5f);
        Assert.True(XuiEasing.Evaluate(XuiEase.InQuad, 0.5f) < 0.5f);
    }

    [Fact]
    public void OutBackOvershootsBeforeSettling()
    {
        bool overshot = false;
        for (int i = 1; i < 20; i++)
        {
            if (XuiEasing.Evaluate(XuiEase.OutBack, i / 20f) > 1f) overshot = true;
        }
        Assert.True(overshot, "OutBack never exceeded 1, so it does not overshoot");
    }

    [Fact]
    public void OutOfRangeTimeIsClamped()
    {
        Assert.Equal(0f, XuiEasing.Evaluate(XuiEase.OutQuad, -5f));
        Assert.Equal(1f, XuiEasing.Evaluate(XuiEase.OutQuad, 5f));
    }

    [Theory]
    [InlineData("linear", XuiEase.Linear)]
    [InlineData("out-quad", XuiEase.OutQuad)]
    [InlineData("in-quad", XuiEase.InQuad)]
    [InlineData("in-out-quad", XuiEase.InOutQuad)]
    [InlineData("out-back", XuiEase.OutBack)]
    public void EaseNamesParse(string text, XuiEase expected)
    {
        Assert.True(XuiEasing.TryParse(text, out XuiEase ease));
        Assert.Equal(expected, ease);
    }

    [Fact]
    public void AnUnknownEaseNameIsRejectedRatherThanSilentlyDefaulting()
        => Assert.False(XuiEasing.TryParse("bouncy", out _));

    // ------------------------------------------------------------------
    //  Document
    // ------------------------------------------------------------------

    [Fact]
    public void AnimIsRegisteredAsBehaviourAndTakesNoChildren()
    {
        XuiTagInfo anim = Assert.IsType<XuiTagInfo>(XuiTagLibrary.Find("Anim"));

        Assert.Equal(XuiTagKind.Behaviour, anim.Kind);
        Assert.False(anim.AcceptsChildren);
        Assert.False(anim.AcceptsText);
        Assert.Equal("XuiAnimator", anim.BindingType);
    }

    [Fact]
    public void AWellFormedTransitionLintsClean()
    {
        IReadOnlyList<XuiLintIssue> issues = Lint("""
            <XUI version="1">
              <Screen name="A">
                <Btn id="XBtnGo">
                  <Anim on="show" move="0,-8:0,0" fade="0:1" duration="0.2" ease="out-back"/>
                  <Anim on="hide" fade=":0" duration="0.1"/>
                  Go
                </Btn>
              </Screen>
            </XUI>
            """);

        Assert.False(XuiLinter.HasErrors(issues), string.Join("; ", issues.Select(i => i.ToString())));
    }

    [Fact]
    public void AnimAtTheTopOfAScreenHasNothingToAnimate()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1"><Screen name="A"><Anim on="show" fade="0:1"/></Screen></XUI>
            """, XuiLinter.Codes.AnimAtScreenRoot);

        Assert.Contains("nothing for it to animate", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnimThatChangesNothingIsWarned()
    {
        XuiLintIssue issue = Single("""
            <XUI version="1">
              <Screen name="A"><Frame id="XRectA"><Anim on="show" duration="0.3"/></Frame></Screen>
            </XUI>
            """, XuiLinter.Codes.AnimDoesNothing);

        Assert.Equal(XuiLintSeverity.Warning, issue.Severity);
        Assert.Contains("plays and changes nothing", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A single point with no colon is the one that matters: it reads as "move to here" and would
    // silently animate the wrong way, which no still frame would reveal.
    [InlineData("""<Anim on="show" move="0,-8" fade="0:1"/>""")]
    [InlineData("""<Anim on="show" move="8:0" fade="0:1"/>""")]
    [InlineData("""<Anim on="show" ease="springy" fade="0:1"/>""")]
    [InlineData("""<Anim on="show" duration="fast" fade="0:1"/>""")]
    [InlineData("""<Anim on="show" fade="opaque"/>""")]
    [InlineData("""<Anim on="show" fade="0:2"/>""")]
    [InlineData("""<Anim on="show" fade=":"/>""")]
    [InlineData("""<Anim on="show" scale="big:1"/>""")]
    public void BadTransitionValuesAreErrors(string element)
        => Single(
            "<XUI version=\"1\"><Screen name=\"A\"><Frame id=\"XRectA\">" + element + "</Frame></Screen></XUI>",
            XuiLinter.Codes.BadAttributeValue);

    [Fact]
    public void ToggleStillReadsOnAsACheckedState()
    {
        // "on" means a trigger name on <Anim> and a boolean on <Toggle>; the tag decides, and getting
        // that wrong would break every existing toggle in the language.
        Assert.Empty(Lint("""
            <XUI version="1"><Screen name="A"><Toggle id="XTogA" on="true">Sound</Toggle></Screen></XUI>
            """).Where(i => i.Code == XuiLinter.Codes.BadAttributeValue));

        Single("""
            <XUI version="1"><Screen name="A"><Toggle id="XTogA" on="show">Sound</Toggle></Screen></XUI>
            """, XuiLinter.Codes.BadAttributeValue);
    }

    // ------------------------------------------------------------------
    //  from:to grammar
    // ------------------------------------------------------------------

    [Fact]
    public void AnOffsetPairReadsBothEnds()
    {
        Assert.True(XuiValueParser.TryParseTweenOffset("0,-24:0,0", out XuiFloat2 from, out XuiFloat2 to));
        Assert.Equal(0f, from.X);
        Assert.Equal(-24f, from.Y);
        Assert.Equal(0f, to.Y);
    }

    [Fact]
    public void AnOmittedSideMeansRest()
    {
        // Rest differs per channel — no offset, no scaling, fully opaque — which is what makes
        // "0,-24:" an entrance and ":0,-24" an exit without either spelling out (0,0).
        Assert.True(XuiValueParser.TryParseTweenOffset("0,-24:", out _, out XuiFloat2 offsetTo));
        Assert.Equal(0f, offsetTo.X);
        Assert.Equal(0f, offsetTo.Y);

        Assert.True(XuiValueParser.TryParseTweenScale("0.9:", out XuiFloat2 scaleFrom, out XuiFloat2 scaleTo));
        Assert.Equal(0.9f, scaleFrom.X);
        Assert.Equal(1f, scaleTo.X);
        Assert.Equal(1f, scaleTo.Y);

        Assert.True(XuiValueParser.TryParseTweenAlpha("0:", out float alphaFrom, out float alphaTo));
        Assert.Equal(0f, alphaFrom);
        Assert.Equal(1f, alphaTo);
    }

    [Fact]
    public void AScaleSideMayBeOneNumberForBothAxes()
    {
        Assert.True(XuiValueParser.TryParseTweenScale("0.5:1.25", out XuiFloat2 from, out XuiFloat2 to));
        Assert.Equal(0.5f, from.X);
        Assert.Equal(0.5f, from.Y);
        Assert.Equal(1.25f, to.X);
        Assert.Equal(1.25f, to.Y);
    }

    [Theory]
    [InlineData("0,-24")]      // no colon at all
    [InlineData(":")]          // both sides empty says nothing
    [InlineData("0,0:0,0:0,0")]
    [InlineData("0,0:nope")]
    public void AMalformedPairIsRejected(string text)
        => Assert.False(XuiValueParser.TryParseTweenOffset(text, out _, out _));

    [Theory]
    [InlineData("-0.1:1")]
    [InlineData("0:1.5")]
    public void AlphaOutsideZeroToOneIsRejected(string text)
        => Assert.False(XuiValueParser.TryParseTweenAlpha(text, out _, out _));

    [Fact]
    public void SeveralTransitionsMayShareOneElement()
    {
        // They become several tracks on one animator rather than a stack of empty children, so the
        // element's layout is unaffected by how many transitions it has.
        XuiDocument document = XuiDocumentParser.Parse("""
            <XUI version="1">
              <Screen name="A">
                <Frame id="XRectA">
                  <Anim on="show" fade="0:1"/>
                  <Anim on="hide" fade=":0"/>
                  <Anim on="pulse" scale="1:1.1"/>
                </Frame>
              </Screen>
            </XUI>
            """, "T.xui");

        ElementNode frame = document.Screens[0].Root.Children[0];
        Assert.Equal(3, frame.Children.Count);
        Assert.All(frame.Children, c => Assert.Equal("Anim", c.Tag));
    }

    [Fact]
    public void AnimProducesNoBindingBecauseItIsNotAnElement()
    {
        XuiDocument document = XuiDocumentParser.Parse("""
            <XUI version="1">
              <Screen name="A"><Frame id="XRectA"><Anim id="XAnimA" on="show" fade="0:1"/></Frame></Screen>
            </XUI>
            """, "T.xui");
        var templates = new XuiTemplateTable();
        templates.Add(document);
        ScreenDef screen = document.Screens[0];

        XuiScaffoldOutput output = XuiViewCodeGenerator.Generate(
            screen, XuiTemplateExpander.Expand(screen.Root, templates, "T.xui"), "", "T.xui");

        // The animator lives on the parent element, so binding the Anim id would point at nothing.
        Assert.DoesNotContain(output.Members, m => m.Id == "XAnimA");
        Assert.Contains(output.Members, m => m.Id == "XRectA");
    }
}
