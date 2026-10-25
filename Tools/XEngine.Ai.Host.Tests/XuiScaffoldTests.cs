using System;
using System.IO;
using System.Linq;

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// Code generation and the <c>xui_scaffold</c> tool. The properties pinned here are what make the
/// generated binding trustworthy: it names every reachable id and nothing else, it never invents a
/// property for an element the assembler does not build, and re-running it cannot eat hand-written
/// logic.
/// </summary>
public sealed class XuiScaffoldTests : IDisposable
{
    private const string DocumentPath = "UI/home/Home.xui";

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <XUI version="1">
          <Template name="Row">
            <Param name="rowId"/>
            <Param name="caption"/>
            <Btn id="{rowId}" size="_x44">{caption}</Btn>
          </Template>

          <Screen name="Home" reference="1920x1080" layer="PopUp">
            <Image id="XImgScrim" anchor="stretch" color="#000000AA"/>
            <VStack id="XVsPanel" anchor="center" size="480x_" spacing="8">
              <Text id="XTxtTitle" font-size="28" align="center">Home</Text>
              <Row rowId="XBtnPlay" caption="Play"/>
              <Row rowId="XBtnQuit" caption="Quit"/>
              <Slider id="XSliderVolume" min="0" max="1" value="0.8"/>
              <Toggle id="XTogMute">Mute</Toggle>
            </VStack>
          </Screen>
        </XUI>
        """;

    private readonly string _root;
    private readonly XuiService _service;

    public XuiScaffoldTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-xui-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "UI", "home"));
        File.WriteAllText(Path.Combine(_root, "Assets", "UI", "home", "Home.xui"), Document);

        string journalRoot = Path.Combine(_root, ".xengine", "journal");
        Directory.CreateDirectory(journalRoot);

        var project = new ProjectDescriptor("xui-scaffold-test", _root, null, null, null, "fingerprint");
        _service = new XuiService(project, new ChangeJournal(journalRoot, "xui-scaffold-test"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* test cleanup */ }
    }

    private string Generated(string fileName)
        => Path.Combine(_root, "Assets", "Scripts", "UI", "Home", fileName);

    private XuiScaffoldResult Run(bool overwriteController = false, bool dryRun = false)
        => _service.Scaffold(DocumentPath, "Home", "Scripts/UI/Home", "Game.UI", overwriteController, dryRun);

    // ------------------------------------------------------------------
    //  Generator
    // ------------------------------------------------------------------

    [Fact]
    public void BindsEveryIdIncludingOnesTemplateExpansionProduced()
    {
        XuiScaffoldResult result = Run();

        string[] properties = result.Bindings.Select(b => b.Property).ToArray();
        Assert.Contains("ImgScrim", properties);
        Assert.Contains("VsPanel", properties);
        Assert.Contains("TxtTitle", properties);
        // These two only exist because the Row template was expanded twice with different ids.
        Assert.Contains("BtnPlay", properties);
        Assert.Contains("BtnQuit", properties);
        Assert.Contains("SliderVolume", properties);
        Assert.Contains("TogMute", properties);
    }

    [Fact]
    public void BindingTypeFollowsTheTagNotTheIdText()
    {
        XuiScaffoldResult result = Run();

        Assert.Equal("Button", Assert.Single(result.Bindings.Where(b => b.Property == "BtnPlay")).Type);
        Assert.Equal("Slider", Assert.Single(result.Bindings.Where(b => b.Property == "SliderVolume")).Type);
        Assert.Equal("XuiStack", Assert.Single(result.Bindings.Where(b => b.Property == "VsPanel")).Type);
        Assert.Equal("Image", Assert.Single(result.Bindings.Where(b => b.Property == "ImgScrim")).Type);
    }

    [Fact]
    public void ControlsThatBuildALabelChildGetALabelBinding()
    {
        XuiScaffoldResult result = Run();
        string[] properties = result.Bindings.Select(b => b.Property).ToArray();

        // Btn and Toggle put their inline text on a generated child.
        Assert.Contains("BtnPlayLabel", properties);
        Assert.Contains("TogMuteLabel", properties);

        // <Text> carries its own text, so a label binding would point at a child that is never built.
        Assert.DoesNotContain("TxtTitleLabel", properties);
    }

    [Fact]
    public void GeneratedViewCachesLazilyAndClearsOnRebind()
    {
        Run();
        string view = File.ReadAllText(Generated("HomeView.g.cs"));

        Assert.Contains("private Button? _btnPlay;", view, StringComparison.Ordinal);
        Assert.Contains("public Button BtnPlay => _btnPlay ??= Require<Button>(\"XBtnPlay\");", view, StringComparison.Ordinal);
        Assert.Contains("protected override void ClearCache()", view, StringComparison.Ordinal);
        Assert.Contains("_btnPlay = null;", view, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedViewCarriesTheCreateViewOverrideSoNoReflectionIsNeeded()
    {
        Run();
        string view = File.ReadAllText(Generated("HomeView.g.cs"));

        Assert.Contains(
            "protected override HomeView CreateView(XuiScreenInstance screen) => new HomeView(screen);",
            view,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedViewRegistersTheScreenWithTheLayerTheDocumentDeclares()
    {
        Run();
        string view = File.ReadAllText(Generated("HomeView.g.cs"));

        Assert.Contains("ModuleInitializer", view, StringComparison.Ordinal);
        Assert.Contains("Xui.Register<Home>(\"Home\", XuiLayerKind.PopUp);", view, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedViewIsMarkedAutoGeneratedAndNamesItsSource()
    {
        Run();
        string view = File.ReadAllText(Generated("HomeView.g.cs"));

        Assert.StartsWith("// <auto-generated />", view, StringComparison.Ordinal);
        Assert.Contains(DocumentPath, view, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerSkeletonListsTheControlsThatRaiseEvents()
    {
        Run();
        string controller = File.ReadAllText(Generated("Home.cs"));

        Assert.Contains("[XuiScreen(\"Home\")]", controller, StringComparison.Ordinal);
        Assert.Contains("public partial class Home : XuiController<HomeView>", controller, StringComparison.Ordinal);
        Assert.Contains("// View.BtnPlay.OnClick", controller, StringComparison.Ordinal);
        Assert.Contains("// View.SliderVolume.OnValueChanged", controller, StringComparison.Ordinal);
        Assert.Contains("// View.TogMute.OnValueChanged", controller, StringComparison.Ordinal);
        // A plain Image raises nothing, so it is not offered as a handler.
        Assert.DoesNotContain("View.ImgScrim.On", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void NamespaceIsOmittedWhenNotAsked()
    {
        _service.Scaffold(DocumentPath, "Home", "Scripts/UI/Home", null, false, false);

        Assert.DoesNotContain("namespace ", File.ReadAllText(Generated("HomeView.g.cs")), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Tool behaviour
    // ------------------------------------------------------------------

    [Fact]
    public void RerunningOverwritesTheBindingButNeverTheController()
    {
        Run();
        const string HandWritten = "// my logic\n";
        File.WriteAllText(Generated("Home.cs"), HandWritten);

        XuiScaffoldResult second = Run();

        Assert.Equal(HandWritten, File.ReadAllText(Generated("Home.cs")));
        Assert.Null(second.ControllerFile);
        Assert.Contains("already exists", second.Note, StringComparison.Ordinal);
        // The binding is derived, so it is rewritten every time.
        Assert.Contains("BtnPlay", File.ReadAllText(Generated("HomeView.g.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void OverwriteControllerIsOptInAndSaysSo()
    {
        Run();
        File.WriteAllText(Generated("Home.cs"), "// my logic");

        XuiScaffoldResult result = Run(overwriteController: true);

        Assert.NotNull(result.ControllerFile);
        Assert.Contains("XuiController<HomeView>", File.ReadAllText(Generated("Home.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunWritesNothing()
    {
        XuiScaffoldResult result = Run(dryRun: true);

        Assert.True(result.DryRun);
        Assert.NotEmpty(result.Bindings);
        Assert.False(File.Exists(Generated("HomeView.g.cs")));
    }

    [Fact]
    public void FrameBindingsImportTheNamespaceRectTransformLivesIn()
    {
        // A Frame binds to RectTransform, which lives in XEngine.Vector — without that using the
        // generated view does not compile. Found by building the M4 PSD demo project for real.
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Doc"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Doc", "Framed.xui"), """
            <?xml version="1.0" encoding="utf-8"?>
            <XUI version="1">
              <Screen name="Framed" reference="400x240" layer="Normal">
                <Frame id="XRectPanel" anchor="top-left" pos="10,-10" size="100x40">
                  <Image id="XImgCell" anchor="top-left" pos="0,0" size="100x40"/>
                </Frame>
              </Screen>
            </XUI>
            """);

        XuiScaffoldResult result = _service.Scaffold("Assets/Doc/Framed.xui", "Framed",
            "Scripts/UI/Framed", null, false, false);

        string view = File.ReadAllText(Path.Combine(_root, "Assets", "Scripts", "UI", "Framed", "FramedView.g.cs"));
        Assert.Contains("using XEngine.Vector;", view);
        Assert.Equal("RectTransform",
            Assert.Single(result.Bindings.Where(b => b.Property == "RectPanel")).Type);
    }

    [Fact]
    public void OutputLandsUnderAssetsNotTheProjectRoot()
    {
        XuiScaffoldResult result = Run();

        Assert.Equal("Assets/Scripts/UI/Home/HomeView.g.cs", result.ViewFile);
        Assert.True(File.Exists(Generated("HomeView.g.cs")));
    }

    [Fact]
    public void ScaffoldingFromADocumentWithLintErrorsIsRefused()
    {
        File.WriteAllText(
            Path.Combine(_root, "Assets", "UI", "home", "Home.xui"),
            """
            <XUI version="1">
              <Screen name="Home"><Btn id="XImgGo">Go</Btn></Screen>
            </XUI>
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Run());

        Assert.Contains("lint errors", ex.Message, StringComparison.Ordinal);
        Assert.Contains("xui_validate", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Generated("HomeView.g.cs")));
    }

    [Fact]
    public void UnknownScreenNamesTheToolThatListsThem()
    {
        var ex = Assert.Throws<KeyNotFoundException>(
            () => _service.Scaffold(DocumentPath, "Nope", "Scripts/UI/Home", null, false, false));

        Assert.Contains("xui_inspect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedSampleGeneratesABindingForEveryControlItDeclares()
    {
        string path = Path.Combine(
            RepoRootLocator.Find(), "Packages", "com.xengine.xui", "Samples~", "LoginScreen", "Login.xui");

        XuiDocument document = XuiDocumentParser.ParseFile(path);
        var templates = new XuiTemplateTable();
        templates.Add(document);
        ScreenDef screen = document.Screens[0];

        XuiScaffoldOutput output = XuiViewCodeGenerator.Generate(
            screen,
            XuiTemplateExpander.Expand(screen.Root, templates, path),
            "Game.UI",
            "Samples~/LoginScreen/Login.xui");

        string[] properties = output.Members.Select(m => m.Property).ToArray();
        Assert.Contains("BtnLogin", properties);
        Assert.Contains("InputAccount", properties);
        Assert.Contains("InputPassword", properties);
        Assert.Contains("TxtStatus", properties);
    }
}
