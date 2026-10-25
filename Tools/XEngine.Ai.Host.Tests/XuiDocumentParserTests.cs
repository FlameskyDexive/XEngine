using XEngine.XUI;
using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// Contract tests for the <c>.xui</c> language front end. These run against the same
/// <c>Packages/com.xengine.xui/Runtime/Core</c> sources the engine compiles, so a rule proven here is
/// the rule the runtime assembler enforces — there is no second parser to drift from.
/// </summary>
public sealed class XuiDocumentParserTests
{
    private const string LoginScreen = """
        <?xml version="1.0" encoding="utf-8"?>
        <XUI version="1">
          <Theme name="dark">
            <Color name="panel" value="#1E2433"/>
            <Color name="accent" value="#4C8DFF"/>
          </Theme>
          <Screen name="Login" reference="1920x1080" layer="PopUp" scale-mode="pixel" canvas="camera">
            <Image id="XImgMask" anchor="stretch" color="#000000AA"/>
            <VStack id="XVsPanel" anchor="center" size="480x360" color="panel" spacing="12">
              <Text id="XTxtTitle" size="_x48" font-size="32" align="center">Sign in</Text>
              <InputField id="XInputAccount" size="400x44" placeholder="Account"/>
              <Btn id="XBtnLogin" size="400x44" color="accent">Start</Btn>
            </VStack>
          </Screen>
        </XUI>
        """;

    [Fact]
    public void ParsesScreenStructureAndTypedAttributes()
    {
        XuiDocument document = XuiDocumentParser.Parse(LoginScreen, "Login.xui");

        Assert.Equal(XUIRuntime.DocumentVersion, document.Version);
        Assert.Equal("Login.xui", document.SourcePath);

        ScreenDef screen = Assert.Single(document.Screens);
        Assert.Equal("Login", screen.Name);
        Assert.Equal(XuiLayerKind.PopUp, screen.Layer);
        Assert.Equal(XuiScaleMode.Pixel, screen.ScaleMode);
        Assert.Equal(XuiCanvasMode.Camera, screen.Canvas);
        Assert.NotNull(screen.Reference);
        Assert.Equal(1920f, screen.Reference!.Value.Width);
        Assert.Equal(1080f, screen.Reference!.Value.Height);

        // Root is the <Screen> element itself, so structural attributes survive for round-tripping.
        Assert.Equal("Screen", screen.Root.Tag);
        Assert.Equal("1920x1080", screen.Root.Attribute("reference"));
        Assert.Equal(2, screen.Root.Children.Count);

        ElementNode stack = screen.Root.Children[1];
        Assert.Equal("VStack", stack.Tag);
        Assert.Equal("XVsPanel", stack.Id);
        Assert.Equal(3, stack.Children.Count);
        Assert.Equal("Sign in", stack.Children[0].Text);
        Assert.Equal("Start", stack.Children[2].Text);
    }

    [Fact]
    public void ParsesThemeTokens()
    {
        XuiDocument document = XuiDocumentParser.Parse(LoginScreen, "Login.xui");

        ThemeBlock theme = Assert.Single(document.Themes);
        Assert.Equal("dark", theme.Name);
        Assert.Equal(2, theme.Colors.Count);
        Assert.Equal("panel", theme.Colors[0].Name);
        Assert.Equal("#1E2433", theme.Colors[0].Value);
    }

    [Fact]
    public void RejectsUnsupportedDocumentVersion()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<XUI version="99"><Screen name="A"><Frame id="XRectA"/></Screen></XUI>""", "A.xui"));

        Assert.Contains("version 99", ex.RawMessage, System.StringComparison.Ordinal);
        Assert.Contains("A.xui", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsWrongRootElement()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<PromptUGUI version="1"><Screen name="A"><Frame id="XRectA"/></Screen></PromptUGUI>"""));

        Assert.Contains("root element must be <XUI>", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownTopLevelElement()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<XUI version="1"><Frame id="XRectA"/></XUI>"""));

        Assert.Contains("not allowed at the top level", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateIdsParseCleanlyAndAreLeftToLint()
    {
        // Deliberately not a parse error: stopping here would hide every other problem in the file.
        // XuiLinterTests pins the XUI-ID-DUPLICATE rule that reports it.
        XuiDocument document = XuiDocumentParser.Parse("""
            <XUI version="1">
              <Screen name="A">
                <Frame id="XRectPanel"/>
                <Frame id="XRectPanel"/>
              </Screen>
            </XUI>
            """);

        Assert.Equal(2, document.Screens[0].Root.Children.Count);
    }

    [Fact]
    public void RejectsNonIdentifierId()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<XUI version="1"><Screen name="A"><Frame id="panel-root"/></Screen></XUI>"""));

        Assert.Contains("generated C# property name", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsTextMixedWithChildElements()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse("""
            <XUI version="1">
              <Screen name="A">
                <Btn id="XBtnGo">Go<Text id="XTxtSub">sub</Text></Btn>
              </Screen>
            </XUI>
            """));

        Assert.Contains("mixes inline text with child elements", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedXmlWithSourcePosition()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<XUI version="1"><Screen name="A"><Frame id="XRectA"></Screen></XUI>""", "Broken.xui"));

        Assert.Contains("Malformed XML", ex.RawMessage, System.StringComparison.Ordinal);
        Assert.True(ex.Span.HasValue);
    }

    [Fact]
    public void RejectsScreenWithoutName()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<XUI version="1"><Screen><Frame id="XRectA"/></Screen></XUI>"""));

        Assert.Contains("missing the required name attribute", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsBadLayerValueAndListsTheAllowedSet()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<XUI version="1"><Screen name="A" layer="popup"><Frame id="XRectA"/></Screen></XUI>"""));

        Assert.Contains("Normal, Fixed, PopUp, or Other", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsSingleAxisReferenceResolution()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<XUI version="1"><Screen name="A" reference="1920x_"><Frame id="XRectA"/></Screen></XUI>"""));

        Assert.Contains("Both axes are required", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesTemplateWithParameters()
    {
        XuiDocument document = XuiDocumentParser.Parse("""
            <XUI version="1">
              <Template name="TitledPanel">
                <Param name="title" default="Untitled"/>
                <VStack id="XVsRoot">
                  <Text id="XTxtHeading">{title}</Text>
                </VStack>
              </Template>
            </XUI>
            """);

        TemplateDef template = Assert.Single(document.Templates);
        Assert.Equal("TitledPanel", template.Name);
        ParamDef parameter = Assert.Single(template.Params);
        Assert.Equal("title", parameter.Name);
        Assert.Equal("Untitled", parameter.Default);
        Assert.Equal("VStack", template.Root.Tag);
    }

    [Fact]
    public void RejectsTemplateWithTwoRoots()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse("""
            <XUI version="1">
              <Template name="Pair">
                <Frame id="XRectA"/>
                <Frame id="XRectB"/>
              </Template>
            </XUI>
            """));

        Assert.Contains("more than one root element", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsThemeTokenThatReferencesAnotherToken()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse("""
            <XUI version="1">
              <Theme name="dark"><Color name="panel" value="accent"/></Theme>
              <Screen name="A"><Frame id="XRectA"/></Screen>
            </XUI>
            """));

        Assert.Contains("cannot reference another token", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDocumentThatDeclaresNothingOpenable()
    {
        var ex = Assert.Throws<XuiParseException>(() => XuiDocumentParser.Parse(
            """<XUI version="1"><Import src="common/Buttons"/></XUI>"""));

        Assert.Contains("neither a <Screen> nor a <Template>", ex.RawMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesImportsIncludingAlias()
    {
        XuiDocument document = XuiDocumentParser.Parse("""
            <XUI version="1">
              <Import src="common/Buttons" as="ui"/>
              <Screen name="A"><Frame id="XRectA"/></Screen>
            </XUI>
            """);

        ImportRef import = Assert.Single(document.Imports);
        Assert.Equal("common/Buttons", import.Src);
        Assert.Equal("ui", import.Alias);
    }

    [Fact]
    public void KeepsAttributesInAuthoredOrder()
    {
        XuiDocument document = XuiDocumentParser.Parse(LoginScreen, "Login.xui");
        ElementNode mask = document.Screens[0].Root.Children[0];

        Assert.Equal("id", mask.Attributes[0].Name);
        Assert.Equal("anchor", mask.Attributes[1].Name);
        Assert.Equal("color", mask.Attributes[2].Name);
    }
}
