using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;

using Xunit;

using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The offline <c>xui_validate</c> / <c>xui_inspect</c> / <c>xui_patch</c> surface, against a real
/// temp project and a real journal. The behaviours pinned here are the ones an agent depends on to
/// edit a screen safely: patches keep the file readable, drift is refused, a patch that would break
/// the document never reaches disk, and every write is journalled.
/// </summary>
public sealed class XuiToolsTests : IDisposable
{
    private const string DocumentPath = "UI/login/Login.xui";

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <XUI version="1">
          <!-- palette -->
          <Theme name="dark">
            <Color name="panel" value="#1E2433"/>
            <Color name="accent" value="#4C8DFF"/>
          </Theme>

          <Template name="Field">
            <Param name="rootId"/>
            <Param name="hint"/>
            <InputField id="{rootId}" size="_x44" placeholder="{hint}"/>
          </Template>

          <Screen name="Login" reference="1920x1080" layer="Normal">
            <Image id="XImgMask" anchor="stretch" color="#000000AA"/>
            <VStack id="XVsPanel" anchor="center" size="480x_" color="panel" spacing="12">
              <Text id="XTxtTitle" font-size="32" align="center">Sign in</Text>
              <Field rootId="XInputAccount" hint="Account"/>
              <Btn id="XBtnLogin" size="_x44" color="accent">Start</Btn>
            </VStack>
          </Screen>
        </XUI>
        """;

    private readonly string _root;
    private readonly XuiService _service;
    private readonly ChangeJournal _journal;

    public XuiToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-xui-test-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(_root, "Assets", "UI", "login");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Login.xui"), Document);

        string journalRoot = Path.Combine(_root, ".xengine", "journal");
        Directory.CreateDirectory(journalRoot);
        _journal = new ChangeJournal(journalRoot, "xui-test-project");

        var project = new ProjectDescriptor("xui-test-project", _root, null, null, null, "fingerprint");
        _service = new XuiService(project, _journal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* test cleanup */ }
    }

    private string Absolute => Path.Combine(_root, "Assets", "UI", "login", "Login.xui");

    private static ChangeProvenance Provenance(string key)
        => ChangeProvenance.ForTool("test.xui-patch", "test", key);

    /// <summary>Document lines, split by the framework so the assertions do not depend on whether git
    /// checked the fixture out with CRLF.</summary>
    private string[] SourceLines() => File.ReadAllLines(Absolute);

    // ------------------------------------------------------------------
    //  Validate
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateAcceptsAGoodDocument()
    {
        XuiValidateResult result = _service.Validate(DocumentPath, includeWarnings: true);

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(i => i.Code + " " + i.Message)));
        Assert.Equal(0, result.Errors);
        Assert.False(string.IsNullOrEmpty(result.ContentHash));
    }

    [Fact]
    public void ValidateReportsEveryFindingAtOnceRatherThanTheFirst()
    {
        File.WriteAllText(Absolute, """
            <XUI version="1">
              <Screen name="A">
                <Btn id="XImgGo" colour="x">Go</Btn>
                <Image id="XImgBad" size="480"/>
                <Image id="XImgBad" color="nope"/>
              </Screen>
            </XUI>
            """);

        XuiValidateResult result = _service.Validate(DocumentPath, includeWarnings: true);

        Assert.False(result.Valid);
        // id prefix, unknown attribute, bad size, duplicate id, unknown token — a single-error parser
        // would have stopped at the first and hidden the rest.
        Assert.True(result.Errors >= 4,
            "expected several errors, got " + result.Errors + ": "
            + string.Join("; ", result.Issues.Select(i => i.Code)));
        Assert.Contains(result.Issues, i => i.Code == "XUI-ID-PREFIX");
        Assert.Contains(result.Issues, i => i.Code == "XUI-ATTR-UNKNOWN");
        Assert.Contains(result.Issues, i => i.Code == "XUI-ID-DUPLICATE");
    }

    [Fact]
    public void ValidateTurnsAParseFailureIntoOneBlockingIssueWithAPosition()
    {
        File.WriteAllText(Absolute, """<XUI version="1"><Screen name="A"><Frame id="XRectA"></Screen></XUI>""");

        XuiValidateResult result = _service.Validate(DocumentPath, includeWarnings: true);

        Assert.False(result.Valid);
        XuiIssueInfo issue = Assert.Single(result.Issues);
        Assert.Equal("XUI-PARSE", issue.Code);
        Assert.True(issue.Line > 0);
    }

    [Fact]
    public void ErrorsOnlySuppressesWarningsButStillCountsThem()
    {
        File.WriteAllText(Absolute, """
            <XUI version="1">
              <Screen name="A"><VStack id="XVsRoot"><Btn id="XBtnGo" anchor="center">Go</Btn></VStack></Screen>
            </XUI>
            """);

        XuiValidateResult result = _service.Validate(DocumentPath, includeWarnings: false);

        Assert.True(result.Valid);
        Assert.True(result.Warnings > 0);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidateRejectsANonXuiPath()
        => Assert.Throws<ArgumentException>(() => _service.Validate("UI/login/Login.xml", true));

    [Fact]
    public void ValidateReportsAMissingFileAsNotFound()
        => Assert.Throws<FileNotFoundException>(() => _service.Validate("UI/login/Missing.xui", true));

    // ------------------------------------------------------------------
    //  Inspect
    // ------------------------------------------------------------------

    [Fact]
    public void InspectReportsScreensThemesTemplatesAndTheExpandedTree()
    {
        XuiInspectResult result = _service.Inspect(DocumentPath, screenName: null);

        Assert.Equal(1, result.Version);
        XuiScreenInfo screen = Assert.Single(result.Screens);
        Assert.Equal("Login", screen.Name);
        Assert.Equal("Normal", screen.Layer);
        Assert.Equal("1920x1080", screen.Reference);

        Assert.Contains("Field", result.Templates);
        Assert.Equal("dark", Assert.Single(result.Themes).Name);

        // The template usage is gone; what it produced is what is listed.
        Assert.DoesNotContain(screen.Elements, e => e.Tag == "Field");
        XuiElementInfo account = Assert.Single(screen.Elements.Where(e => e.Id == "XInputAccount"));
        Assert.Equal("InputField", account.Tag);
        Assert.Equal("InputField", account.BindingType);
        Assert.Equal("InputAccount", account.BindingProperty);
    }

    [Fact]
    public void InspectReportsTheBindingNameEachIdWillGenerate()
    {
        XuiInspectResult result = _service.Inspect(DocumentPath, screenName: "Login");

        XuiElementInfo button = Assert.Single(result.Screens[0].Elements.Where(e => e.Id == "XBtnLogin"));
        Assert.Equal("Button", button.BindingType);
        Assert.Equal("BtnLogin", button.BindingProperty);
        Assert.Equal("Start", button.Text);
    }

    [Fact]
    public void InspectNamesTheScreensItHasWhenAskedForOneItDoesNot()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => _service.Inspect(DocumentPath, "Missing"));
        Assert.Contains("without a screen name to list what is", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectHashMatchesValidateHash()
        => Assert.Equal(
            _service.Validate(DocumentPath, true).ContentHash,
            _service.Inspect(DocumentPath, null).ContentHash);

    // ------------------------------------------------------------------
    //  Patch
    // ------------------------------------------------------------------

    [Fact]
    public async Task PatchSetsAnAttributeAndJournalsTheChange()
    {
        SceneWriteResult result = await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@color", "#FF0000") },
            null, Provenance("p1"), dryRun: false, expectedBeforeHash: null, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.ChangeId));
        Assert.NotEqual(result.BeforeHash, result.AfterHash);
        Assert.Contains("color = \"#FF0000\"", result.Summary, StringComparison.Ordinal);
        Assert.Contains("color=\"#FF0000\"", File.ReadAllText(Absolute), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchPreservesCommentsAndIndentation()
    {
        await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XTxtTitle/#text", "Welcome") },
            null, Provenance("p2"), dryRun: false, null, CancellationToken.None);

        string after = File.ReadAllText(Absolute);
        Assert.Contains("<!-- palette -->", after, StringComparison.Ordinal);
        Assert.Contains(">Welcome<", after, StringComparison.Ordinal);
        // Indentation intact: the panel line still starts where it did.
        Assert.Contains("\n    <VStack id=\"XVsPanel\"", after.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchCanPatchAScreenAttribute()
    {
        await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("screen:Login/@layer", "PopUp") },
            null, Provenance("p3"), dryRun: false, null, CancellationToken.None);

        Assert.Equal("PopUp", _service.Inspect(DocumentPath, "Login").Screens[0].Layer);
    }

    [Fact]
    public async Task RemoveClearsADeadAttribute()
    {
        await _service.PatchAsync(
            DocumentPath,
            Array.Empty<SceneFieldPatch>(),
            new[] { "XImgMask/@color" },
            Provenance("p4"), dryRun: false, null, CancellationToken.None);

        Assert.DoesNotContain("#000000AA", File.ReadAllText(Absolute), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunReportsTheChangeWithoutTouchingTheFile()
    {
        string before = File.ReadAllText(Absolute);

        SceneWriteResult result = await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@color", "#00FF00") },
            null, Provenance("p5"), dryRun: true, null, CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.NotEqual(result.BeforeHash, result.AfterHash);
        Assert.Equal(before, File.ReadAllText(Absolute));
    }

    [Fact]
    public async Task StaleExpectedHashIsRefused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@color", "#00FF00") },
            null, Provenance("p6"), dryRun: false,
            expectedBeforeHash: new string('0', 64), CancellationToken.None));

        Assert.Contains("changed since it was read", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchingExpectedHashIsAccepted()
    {
        string hash = _service.Inspect(DocumentPath, null).ContentHash;

        SceneWriteResult result = await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@color", "#00FF00") },
            null, Provenance("p7"), dryRun: false, hash, CancellationToken.None);

        Assert.Equal(hash, result.BeforeHash);
    }

    [Fact]
    public async Task APatchThatWouldBreakTheDocumentIsRejectedBeforeAnythingIsWritten()
    {
        string before = File.ReadAllText(Absolute);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@id", "not an identifier") },
            null, Provenance("p8"), dryRun: false, null, CancellationToken.None));

        Assert.Contains("was not written", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(Absolute));
    }

    [Fact]
    public async Task UnknownIdNamesTheToolThatListsValidOnes()
    {
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnNope/@color", "#fff") },
            null, Provenance("p9"), dryRun: false, null, CancellationToken.None));

        Assert.Contains("xui_inspect", ex.Message, StringComparison.Ordinal);
        Assert.Contains("template instead", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIdThatOnlyExistsAfterExpansionIsNotPatchable()
    {
        // XInputAccount is produced by the Field template, so it is in inspect's output but not in the
        // file. Saying so plainly is better than silently patching nothing.
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XInputAccount/@placeholder", "User") },
            null, Provenance("p10"), dryRun: false, null, CancellationToken.None));

        Assert.Contains("template expansion are not patchable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedPatchPathIsRejected()
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin", "#fff") },
            null, Provenance("p11"), dryRun: false, null, CancellationToken.None));

    [Fact]
    public async Task IdenticalPatchIsReportedAsNoChangeWithoutAJournalEntry()
    {
        string before = File.ReadAllText(Absolute);

        SceneWriteResult result = await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@color", "accent") },
            null, Provenance("p12"), dryRun: false, null, CancellationToken.None);

        // Byte-identical, not merely equivalent. Re-serialising the tree would reformat every
        // self-closing tag in the file, so "no change" has to mean the text really did not move.
        Assert.Equal(before, File.ReadAllText(Absolute));
        Assert.Equal(string.Empty, result.ChangeId);
        Assert.Equal(result.BeforeHash, result.AfterHash);
        Assert.Contains("No change", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APatchTouchesOnlyTheLineItNames()
    {
        string[] before = SourceLines();

        await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@color", "#FF0000") },
            null, Provenance("p14"), dryRun: false, null, CancellationToken.None);

        string[] after = SourceLines();

        Assert.Equal(before.Length, after.Length);
        int changed = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal)) changed++;
        }
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task AddingAnAbsentAttributeDoesNotDisturbTheRestOfTheStartTag()
    {
        await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XTxtTitle/@color", "#FFFFFF") },
            null, Provenance("p15"), dryRun: false, null, CancellationToken.None);

        string after = File.ReadAllText(Absolute);
        Assert.Contains(
            "<Text id=\"XTxtTitle\" font-size=\"32\" align=\"center\" color=\"#FFFFFF\">",
            after,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextWithMarkupCharactersIsEscaped()
    {
        await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XTxtTitle/#text", "Tom & Jerry <3") },
            null, Provenance("p16"), dryRun: false, null, CancellationToken.None);

        Assert.Contains("Tom &amp; Jerry &lt;3", File.ReadAllText(Absolute), StringComparison.Ordinal);
        Assert.Equal(
            "Tom & Jerry <3",
            Assert.Single(_service.Inspect(DocumentPath, "Login").Screens[0].Elements
                .Where(e => e.Id == "XTxtTitle")).Text);
    }

    [Fact]
    public async Task ReplayingTheSameIdempotencyKeyDoesNotWriteTwice()
    {
        SceneWriteResult first = await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@color", "#123456") },
            null, Provenance("replay-1"), dryRun: false, null, CancellationToken.None);

        SceneWriteResult second = await _service.PatchAsync(
            DocumentPath,
            new[] { new SceneFieldPatch("XBtnLogin/@color", "#654321") },
            null, Provenance("replay-1"), dryRun: false, null, CancellationToken.None);

        Assert.Equal(first.ChangeId, second.ChangeId);
        Assert.Contains("Idempotent replay", second.Summary, StringComparison.Ordinal);
        Assert.Contains("#123456", File.ReadAllText(Absolute), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchedDocumentStillValidatesAndStillBuildsAnEquivalentTree()
    {
        await _service.PatchAsync(
            DocumentPath,
            new[]
            {
                new SceneFieldPatch("XTxtTitle/#text", "Welcome back"),
                new SceneFieldPatch("XBtnLogin/@color", "panel"),
            },
            null, Provenance("p13"), dryRun: false, null, CancellationToken.None);

        XuiValidateResult validated = _service.Validate(DocumentPath, includeWarnings: true);
        Assert.True(validated.Valid, string.Join("; ", validated.Issues.Select(i => i.Message)));

        XuiInspectResult inspected = _service.Inspect(DocumentPath, "Login");
        Assert.Equal(
            "Welcome back",
            Assert.Single(inspected.Screens[0].Elements.Where(e => e.Id == "XTxtTitle")).Text);
    }
}
