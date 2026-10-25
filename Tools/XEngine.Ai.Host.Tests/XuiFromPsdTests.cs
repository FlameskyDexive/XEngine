using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using XEngine.Ai.Protocol;
using XEngine.Echo;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The PSD → XUI converter, against builder-made fixtures.
/// </summary>
/// <remarks>
/// These tests pin the contract the tool description promises: naming prefixes map to elements,
/// positions are document coordinates with Y negated, nothing is silently dropped, and disk writes
/// (sprites, metas, the .xui itself) behave exactly as dry-run flags say.
/// </remarks>
public sealed class XuiFromPsdTests : IDisposable
{
    private readonly string _root;
    private readonly XuiFromPsdService _service;

    public XuiFromPsdTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-xui-from-psd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Art"));
        var project = new ProjectDescriptor("psd-test-project", _root, null, null, null, "fingerprint");
        _service = new XuiFromPsdService(project);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteFixture(string name, byte[] psd)
    {
        string path = Path.Combine(_root, "Assets", "Art", name);
        File.WriteAllBytes(path, psd);
        return "Assets/Art/" + name;
    }

    /// <summary>
    /// One screen with every mapping: a button group with a baked-out label, a colored text, an
    /// input, an image, a plain group (frame), plus a hidden layer, a #skip layer, and an empty one.
    /// </summary>
    private static byte[] ScreenLikePsd()
        => new PsdFixtureBuilder(320, 200)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "bg #skip", Width = 320, Height = 200 })
            .Group("btn_login=登录")
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "bg", X = 40, Y = 150, Width = 120, Height = 36, Fill = (40, 80, 200, 255) })
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "txt_label=登录", X = 70, Y = 158, Width = 60, Height = 20, Fill = (255, 255, 255, 255) })
            .EndGroup()
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "txt_title=标题", X = 20, Y = 12, Width = 80, Height = 24, Fill = (200, 30, 30, 255) })
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "input_account=请输入账号", X = 20, Y = 60, Width = 200, Height = 30, Fill = (240, 240, 240, 255) })
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "img_avatar", X = 260, Y = 20, Width = 40, Height = 40, Fill = (10, 200, 30, 255) })
            .Group("panel")
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "cell", X = 30, Y = 40, Width = 50, Height = 25, Fill = (90, 90, 90, 255) })
            .EndGroup()
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "hidden_deco", X = 0, Y = 0, Width = 10, Height = 10, Visible = false })
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "empty", X = 5, Y = 5, Width = 0, Height = 0 })
            .Build();

    // ------------------------------------------------------------------
    //  Mapping
    // ------------------------------------------------------------------

    [Fact]
    public void NamingContractMapsLayersToElements()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        var result = _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: true);

        Assert.True(result.Valid, string.Join("\n", result.Issues.Select(i => i.Code + ": " + i.Message)));
        Assert.Equal("Login", result.ScreenName);
        Assert.Equal(320, result.Width);
        Assert.Equal(200, result.Height);

        // btn group → Btn (label child absorbed, no separate Text), txt → Text, input → InputField,
        // img → Image, plain group → Frame with its pixel child inside. Layers emit bottom-to-top,
        // and a group's children report before the group itself.
        Assert.Equal(new[]
        {
            ("cell", "Image"),
            ("panel", "Frame"),
            ("img_avatar", "Image"),
            ("input_account=请输入账号", "InputField"),
            ("txt_title=标题", "Text"),
            ("btn_login=登录", "Btn"),
        }, result.Elements.Select(e => (e.LayerName, e.Tag)).ToArray());

        Assert.Contains(result.Elements, e => e.Tag == "Btn" && e.Id == "XBtnLogin");
        Assert.Contains(result.Elements, e => e.Tag == "Text" && e.Id == "XTxtTitle");
        Assert.Contains(result.Elements, e => e.Tag == "InputField" && e.Id == "XInputAccount");
        Assert.Contains(result.Elements, e => e.Tag == "Image" && e.Id == "XImgAvatar");
        Assert.Contains(result.Elements, e => e.Tag == "Frame" && e.Id == "XRectPanel");
    }

    [Fact]
    public void SkippedLayersAreReportedNotDropped()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        var result = _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: true);

        Assert.Contains(result.Skipped, s => s.Contains("bg #skip") && s.Contains("#skip"));
        Assert.Contains(result.Skipped, s => s.Contains("hidden_deco") && s.Contains("hidden"));
        Assert.Contains(result.Skipped, s => s.Contains("empty") && s.Contains("no pixels"));
    }

    [Fact]
    public void IncludeHiddenKeepsHiddenLayersAsImages()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        var result = _service.Convert(psd, "Login", "UI/login", includeHidden: true, dryRun: true);

        Assert.DoesNotContain(result.Skipped, s => s.Contains("hidden_deco"));
        Assert.Contains(result.Elements, e => e.LayerName == "hidden_deco" && e.Tag == "Image");
    }

    // ------------------------------------------------------------------
    //  Document text
    // ------------------------------------------------------------------

    [Fact]
    public void TextLayerGetsDominantColorAndLayerHeightFontSize()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        var result = _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: true);

        string xui = ReadGeneratedXui(result);
        Assert.Contains("<Text id=\"XTxtTitle\"", xui);
        Assert.Contains("font-size=\"24\"", xui);          // layer height 24, inside the clamp
        Assert.Contains("color=\"#C81E1E\"", xui);         // dominant opaque pixel (200,30,30)
        Assert.Contains("标题", xui);                       // =content became the text body
    }

    [Fact]
    public void PositionsAreDocumentCoordinatesWithNegatedY()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        var result = _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: true);

        string xui = ReadGeneratedXui(result);

        // txt_title at document (20,12): top-left anchor means pos y = -docY.
        Assert.Contains("pos=\"20,-12\" size=\"80x24\"", xui);

        // Frame child coords are relative to the frame: cell at (30,40) inside panel at (30? no —
        // panel's rect is the union of its children, (30,40)). Local pos is therefore (0,0).
        Assert.Contains("pos=\"0,0\" size=\"50x25\"", xui);
    }

    [Fact]
    public void ButtonLabelIsInlineAndEscapedInputPlaceholderSurvives()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        var result = _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: true);

        string xui = ReadGeneratedXui(result);

        // btn content after '=' becomes the Btn's inline label, not a separate baked Text.
        Assert.Contains("<Btn id=\"XBtnLogin\"", xui);
        Assert.Contains(">登录</Btn>", xui);
        Assert.DoesNotContain("id=\"XTxtLabel\"", xui);

        Assert.Contains("placeholder=\"请输入账号\"", xui);
    }

    [Fact]
    public void DuplicateNamesGetUniqueIdsAndSpriteFiles()
    {
        byte[] psd = new PsdFixtureBuilder(100, 60)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "img_icon", X = 0, Y = 0, Width = 20, Height = 20, Fill = (255, 0, 0, 255) })
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "img_icon", X = 30, Y = 0, Width = 20, Height = 20, Fill = (0, 0, 255, 255) })
            .Build();
        string path = WriteFixture("dup.psd", psd);

        var result = _service.Convert(path, "Dup", "UI/dup", includeHidden: false, dryRun: true);

        Assert.Equal(new[] { "XImgIcon", "XImgIcon2" }, result.Elements.Select(e => e.Id).ToArray());
        var sprites = result.Elements.Select(e => Path.GetFileName(e.Sprite)).ToArray();
        Assert.Equal(new[] { "icon.png", "icon-2.png" }, sprites);
    }

    // ------------------------------------------------------------------
    //  Disk behaviour
    // ------------------------------------------------------------------

    [Fact]
    public void DryRunWritesNothing()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        var result = _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: true);

        Assert.True(result.DryRun);
        Assert.False(Directory.Exists(Path.Combine(_root, "Assets", "UI")));
    }

    [Fact]
    public void WriteProducesXuiSpritesAndSpriteModeSingleMetas()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        var result = _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: false);

        string screenFolder = Path.Combine(_root, "Assets", "UI", "login");
        string xuiPath = Path.Combine(screenFolder, "Login.xui");
        Assert.True(File.Exists(xuiPath), "screen .xui written");
        Assert.Equal("Assets/UI/login/Login.xui", result.XuiPath);

        // One sprite each for the button background (label pixels excluded), the input, the
        // avatar, and the frame child cell.
        string spritesFolder = Path.Combine(screenFolder, "sprites");
        Assert.Equal(result.SpritesWritten, Directory.GetFiles(spritesFolder, "*.png").Length);
        Assert.Equal(result.Elements.Count(e => e.Sprite != null), result.SpritesWritten);

        // Every PNG has a meta declaring sprite mode Single, so sprite= resolves to the sub-asset.
        foreach (string png in Directory.GetFiles(spritesFolder, "*.png"))
        {
            Assert.True(File.Exists(png + ".meta"), png + " has a meta");
            EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(png + ".meta"));
            Assert.Equal("TextureImporter", meta["importer"].StringValue);
            Assert.Equal(1L, meta["settings"]["sprite"]["Mode"].IntValue);
        }

        // The .xui on disk is the linted document the report claims.
        string xui = File.ReadAllText(xuiPath);
        Assert.Contains("sprite=\"UI/login/sprites/", xui);
        Assert.Matches(new Regex("reference=\"320x200\""), xui);
    }

    [Fact]
    public void RerunKeepsExistingSpriteMetaGuids()
    {
        string psd = WriteFixture("login.psd", ScreenLikePsd());
        _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: false);

        string metaPath = Path.Combine(_root, "Assets", "UI", "login", "sprites", "avatar.png.meta");
        string guidBefore = EchoObject.ReadFromString(File.ReadAllText(metaPath))["guid"].StringValue;

        _service.Convert(psd, "Login", "UI/login", includeHidden: false, dryRun: false);

        string guidAfter = EchoObject.ReadFromString(File.ReadAllText(metaPath))["guid"].StringValue;
        Assert.Equal(guidBefore, guidAfter);
        Assert.NotEqual(Guid.Empty.ToString(), guidAfter);
    }

    [Fact]
    public void NonPsdPathIsRejected()
    {
        string path = WriteFixture("doc.png", new byte[] { 1, 2, 3 });
        Assert.Throws<ArgumentException>(() => _service.Convert(path, null, null, false, false));
    }

    private string ReadGeneratedXui(XuiFromPsdResult result)
    {
        // Dry-run does not write to disk; the service returns the path it would write, so re-run
        // with dryRun:false and read the file — the document is linted identically either way.
        string psd = result.DocumentPath;
        _service.Convert(psd, result.ScreenName, "UI/" + result.ScreenName.ToLowerInvariant(), false, false);
        return File.ReadAllText(Path.Combine(_root, "Assets", "UI", result.ScreenName.ToLowerInvariant(), result.ScreenName + ".xui"));
    }
}
