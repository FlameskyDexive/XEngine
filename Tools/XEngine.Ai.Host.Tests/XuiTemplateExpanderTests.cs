using System.Linq;

using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// Template expansion. The rule under test throughout: parameters are the only way a template's inner
/// ids change, so what an agent reads in the source is what the generated binding will be called.
/// </summary>
public sealed class XuiTemplateExpanderTests
{
    private static ElementNode ExpandFirstScreen(string xml)
    {
        XuiDocument document = XuiDocumentParser.Parse(xml, "T.xui");
        var table = new XuiTemplateTable();
        table.Add(document);
        return XuiTemplateExpander.Expand(document.Screens[0].Root, table, "T.xui");
    }

    [Fact]
    public void ExpandsUsageIntoTheTemplateBody()
    {
        ElementNode root = ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Badge">
                <Frame id="XRectBadge">
                  <Text id="XTxtBadge">99</Text>
                </Frame>
              </Template>
              <Screen name="A">
                <Badge/>
              </Screen>
            </XUI>
            """);

        ElementNode badge = Assert.Single(root.Children);
        Assert.Equal("Frame", badge.Tag);
        Assert.Equal("XRectBadge", badge.Id);
        Assert.Equal("XTxtBadge", Assert.Single(badge.Children).Id);
    }

    [Fact]
    public void ParametersSubstituteIntoAttributesTextAndIds()
    {
        ElementNode root = ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Tile">
                <Param name="rootId"/>
                <Param name="label" default="Untitled"/>
                <Frame id="{rootId}" color="#101010">
                  <Text id="{rootId}Label">{label}</Text>
                </Frame>
              </Template>
              <Screen name="A">
                <Tile rootId="XRectLeft" label="Left"/>
                <Tile rootId="XRectRight"/>
              </Screen>
            </XUI>
            """);

        Assert.Equal(2, root.Children.Count);

        Assert.Equal("XRectLeft", root.Children[0].Id);
        Assert.Equal("XRectLeftLabel", root.Children[0].Children[0].Id);
        Assert.Equal("Left", root.Children[0].Children[0].Text);

        Assert.Equal("XRectRight", root.Children[1].Id);
        Assert.Equal("Untitled", root.Children[1].Children[0].Text);
    }

    [Fact]
    public void SlotReceivesTheUsageSiteChildren()
    {
        ElementNode root = ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Panel">
                <VStack id="XVsPanel">
                  <Text id="XTxtHeader">Header</Text>
                  <Slot/>
                </VStack>
              </Template>
              <Screen name="A">
                <Panel>
                  <Btn id="XBtnOne">1</Btn>
                  <Btn id="XBtnTwo">2</Btn>
                </Panel>
              </Screen>
            </XUI>
            """);

        ElementNode panel = Assert.Single(root.Children);
        Assert.Equal(3, panel.Children.Count);
        Assert.Equal("XTxtHeader", panel.Children[0].Id);
        Assert.Equal("XBtnOne", panel.Children[1].Id);
        Assert.Equal("XBtnTwo", panel.Children[2].Id);
    }

    [Fact]
    public void NestedTemplatesExpand()
    {
        ElementNode root = ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Inner"><Text id="XTxtInner">inner</Text></Template>
              <Template name="Outer">
                <Frame id="XRectOuter"><Inner/></Frame>
              </Template>
              <Screen name="A"><Outer/></Screen>
            </XUI>
            """);

        ElementNode outer = Assert.Single(root.Children);
        Assert.Equal("XRectOuter", outer.Id);
        Assert.Equal("XTxtInner", Assert.Single(outer.Children).Id);
    }

    [Fact]
    public void MissingRequiredParameterIsRejected()
    {
        var ex = Assert.Throws<XuiParseException>(() => ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Tile"><Param name="rootId"/><Frame id="{rootId}"/></Template>
              <Screen name="A"><Tile/></Screen>
            </XUI>
            """));

        Assert.Contains("missing the required template parameter \"rootId\"", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void UndeclaredArgumentIsRejectedRatherThanSilentlyDropped()
    {
        var ex = Assert.Throws<XuiParseException>(() => ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Tile"><Frame id="XRectTile"/></Template>
              <Screen name="A"><Tile colour="#fff"/></Screen>
            </XUI>
            """));

        Assert.Contains("does not declare as a <Param>", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPlaceholderIsRejected()
    {
        var ex = Assert.Throws<XuiParseException>(() => ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Tile"><Frame id="XRectTile" color="{missing}"/></Template>
              <Screen name="A"><Tile/></Screen>
            </XUI>
            """));

        Assert.Contains("does not match any <Param>", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SelfReferentialTemplateStopsAtTheDepthLimit()
    {
        var ex = Assert.Throws<XuiParseException>(() => ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Loop"><Frame id="XRectLoop"><Loop/></Frame></Template>
              <Screen name="A"><Loop/></Screen>
            </XUI>
            """));

        Assert.Contains("most likely uses itself", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ImportedTemplatesResolveUnderTheirAlias()
    {
        XuiDocument common = XuiDocumentParser.Parse("""
            <XUI version="1">
              <Template name="Close"><Btn id="XBtnClose">X</Btn></Template>
            </XUI>
            """, "common.xui");

        XuiDocument screen = XuiDocumentParser.Parse("""
            <XUI version="1">
              <Import src="common" as="ui"/>
              <Screen name="A"><ui.Close/></Screen>
            </XUI>
            """, "A.xui");

        var table = new XuiTemplateTable();
        table.Add(screen);
        table.Add(common, alias: "ui");

        ElementNode root = XuiTemplateExpander.Expand(screen.Screens[0].Root, table, "A.xui");

        Assert.Equal("XBtnClose", Assert.Single(root.Children).Id);
        Assert.Contains("ui.Close", table.Names.ToArray());
    }

    [Fact]
    public void DoubledBracesSurviveAsLiterals()
    {
        ElementNode root = ExpandFirstScreen("""
            <XUI version="1">
              <Template name="Lit"><Text id="XTxtLit">{{braced}}</Text></Template>
              <Screen name="A"><Lit/></Screen>
            </XUI>
            """);

        Assert.Equal("{braced}", Assert.Single(root.Children).Text);
    }

    [Fact]
    public void NonTemplateTagsPassThroughUntouched()
    {
        ElementNode root = ExpandFirstScreen("""
            <XUI version="1">
              <Screen name="A">
                <VStack id="XVsRoot"><Btn id="XBtnGo">Go</Btn></VStack>
              </Screen>
            </XUI>
            """);

        ElementNode stack = Assert.Single(root.Children);
        Assert.Equal("VStack", stack.Tag);
        Assert.Equal("XBtnGo", Assert.Single(stack.Children).Id);
    }
}
