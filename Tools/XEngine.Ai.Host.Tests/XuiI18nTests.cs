using System;
using System.Collections.Generic;
using System.Linq;

using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// Translation: the <c>.po</c> format, what counts as a translatable string, and — the part that
/// matters most — that re-extracting never destroys a translator's work.
/// </summary>
public sealed class XuiI18nTests
{
    private const string Sample = """
        msgid ""
        msgstr ""
        "Project-Id-Version: XUI\n"
        "Language: zh-Hans\n"
        "Content-Type: text/plain; charset=UTF-8\n"

        #. a note from the translator
        #: Assets/UI/login/Login.xui:8
        msgid "Sign in"
        msgstr "登录"

        #: Assets/UI/login/Login.xui:12
        #, fuzzy
        msgid "Account"
        msgstr "帐号"

        #~ msgid "Gone"
        #~ msgstr "没了"
        """;

    // ------------------------------------------------------------------
    //  The file format
    // ------------------------------------------------------------------

    [Fact]
    public void AllThreeEntryStatesRoundTrip()
    {
        PoCatalog catalog = PoCatalog.Parse(Sample, "zh-Hans.po");

        Assert.Equal("zh-Hans", catalog.Header.Single(h => h.Key == "Language").Value);
        Assert.Equal(3, catalog.Entries.Count);

        PoEntry signIn = catalog.Entries[0];
        Assert.Equal("登录", signIn.Translation);
        Assert.False(signIn.IsFuzzy);
        Assert.Equal("Assets/UI/login/Login.xui:8", signIn.References.Single().ToString());
        Assert.Equal("a note from the translator", signIn.TranslatorComments.Single());

        Assert.True(catalog.Entries[1].IsFuzzy);
        Assert.True(catalog.Entries[2].IsObsolete);
        Assert.Equal("没了", catalog.Entries[2].Translation);
    }

    [Fact]
    public void WritingThenReadingPreservesEverything()
    {
        PoCatalog written = PoCatalog.Parse(PoCatalog.Parse(Sample, "a.po").Write(), "b.po");

        Assert.Equal(3, written.Entries.Count);
        Assert.Equal("登录", written.Find(null, "Sign in")!.Translation);
        Assert.True(written.Find(null, "Account")!.IsFuzzy);
        Assert.True(written.Find(null, "Gone")!.IsObsolete);
        Assert.Equal("zh-Hans", written.Header.Single(h => h.Key == "Language").Value);
    }

    [Fact]
    public void QuotesNewlinesAndBackslashesSurviveTheRoundTrip()
    {
        var catalog = new PoCatalog();
        catalog.Add(new PoEntry("Say \"hi\"\nor \\ don't") { Translation = "说\"你好\"\n或者 \\ 不说" });

        PoEntry read = PoCatalog.Parse(catalog.Write(), "t.po").Entries.Single();

        Assert.Equal("Say \"hi\"\nor \\ don't", read.Id);
        Assert.Equal("说\"你好\"\n或者 \\ 不说", read.Translation);
    }

    [Fact]
    public void ALongStringSplitAcrossLinesIsRejoined()
    {
        PoCatalog catalog = PoCatalog.Parse("""
            msgid ""
            "The quick brown fox "
            "jumps over the lazy dog"
            msgstr ""
            "敏捷的棕色狐狸"
            "跳过懒狗"
            """, "t.po");

        PoEntry entry = catalog.Entries.Single();
        Assert.Equal("The quick brown fox jumps over the lazy dog", entry.Id);
        Assert.Equal("敏捷的棕色狐狸跳过懒狗", entry.Translation);
    }

    [Fact]
    public void PluralFormsAreRefusedRatherThanHalfRead()
    {
        // Silently keeping the singular would be a wrong translation in every language with plural
        // rules, and nothing downstream would ever report it.
        XuiParseException error = Assert.Throws<XuiParseException>(() => PoCatalog.Parse("""
            msgid "1 file"
            msgid_plural "%d files"
            msgstr[0] "1 个文件"
            """, "t.po"));

        Assert.Contains("plural", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AContextSeparatesTwoIdenticalStrings()
    {
        PoCatalog catalog = PoCatalog.Parse("""
            msgctxt "verb"
            msgid "Post"
            msgstr "发布"

            msgctxt "noun"
            msgid "Post"
            msgstr "帖子"
            """, "t.po");

        Assert.Equal(2, catalog.Entries.Count);

        XuiCatalog lookup = XuiCatalog.FromPo(catalog, "zh-Hans");
        Assert.Equal("发布", lookup.Translate("Post", "verb"));
        Assert.Equal("帖子", lookup.Translate("Post", "noun"));
        Assert.Equal("Post", lookup.Translate("Post"));
    }

    // ------------------------------------------------------------------
    //  Lookup
    // ------------------------------------------------------------------

    [Fact]
    public void AMissingTranslationFallsBackToTheSourceString()
    {
        XuiCatalog catalog = XuiCatalog.FromPo(PoCatalog.Parse(Sample, "t.po"), "zh-Hans");

        // Never a marker like ??key??: an untranslated build has to stay shippable.
        Assert.Equal("Not in the catalog", catalog.Translate("Not in the catalog"));
        Assert.Equal("登录", catalog.Translate("Sign in"));
    }

    [Fact]
    public void ObsoleteAndUntranslatedEntriesAreNotLoaded()
    {
        var po = new PoCatalog();
        po.Add(new PoEntry("Gone") { Translation = "没了", IsObsolete = true });
        po.Add(new PoEntry("Blank") { Translation = string.Empty });
        po.Add(new PoEntry("Live") { Translation = "在" });

        XuiCatalog catalog = XuiCatalog.FromPo(po, "zh-Hans");

        Assert.Equal(1, catalog.Count);
        Assert.Equal("Gone", catalog.Translate("Gone"));
        Assert.Equal("在", catalog.Translate("Live"));
    }

    [Fact]
    public void AFuzzyTranslationIsStillUsed()
    {
        // A slightly stale translation beats an untranslated string, and fuzzy is a signal for the
        // translator's tool, not a reason to show the player English.
        XuiCatalog catalog = XuiCatalog.FromPo(PoCatalog.Parse(Sample, "t.po"), "zh-Hans");
        Assert.Equal("帐号", catalog.Translate("Account"));
    }

    // ------------------------------------------------------------------
    //  Extraction
    // ------------------------------------------------------------------

    private static List<XuiExtractedString> Extract(string xml)
        => XuiTextExtractor.Extract(XuiDocumentParser.Parse(xml, "T.xui"), "T.xui");

    [Fact]
    public void OnlyTheStringsAPlayerReadsAreExtracted()
    {
        List<XuiExtractedString> found = Extract("""
            <XUI version="1">
              <Screen name="Login">
                <VStack id="XVsPanel" color="#1E2433" spacing="12">
                  <Text id="XTxtTitle" font-size="32">Sign in</Text>
                  <InputField id="XInputAccount" placeholder="Account" text="guest"/>
                  <Btn id="XBtnGo" color="accent">Start</Btn>
                  <Image id="XImgLogo" sprite="UI/logo.png"/>
                </VStack>
              </Screen>
            </XUI>
            """);

        // Ids, colours, sprite paths, and font sizes are not prose and must never reach a translator.
        Assert.Equal(new[] { "Sign in", "Account", "guest", "Start" }, found.Select(f => f.Text));
        Assert.All(found, f => Assert.Equal("T.xui", f.SourcePath));
        Assert.Equal(4, found[0].Line);
    }

    [Fact]
    public void TranslateFalseOptsOutTheWholeSubtree()
    {
        List<XuiExtractedString> found = Extract("""
            <XUI version="1">
              <Screen name="Debug">
                <VStack id="XVsStats" translate="false">
                  <Text id="XTxtFps">FPS</Text>
                  <Text id="XTxtDraws">Draw calls</Text>
                  <Text id="XTxtHelp" translate="true">Press F1 for help</Text>
                </VStack>
                <Text id="XTxtTitle">Diagnostics</Text>
              </Screen>
            </XUI>
            """);

        // One decision covers a panel, and a child can opt back in for the one label that is prose.
        Assert.Equal(new[] { "Press F1 for help", "Diagnostics" }, found.Select(f => f.Text));
    }

    [Fact]
    public void TemplateStringsAreExtractedAfterExpansion()
    {
        List<XuiExtractedString> found = Extract("""
            <XUI version="1">
              <Template name="Tile">
                <Param name="rootId"/>
                <Param name="label"/>
                <VStack id="{rootId}">
                  <Text id="{rootId}Label">{label}</Text>
                  <Text id="{rootId}Hint">Tap to open</Text>
                </VStack>
              </Template>
              <Screen name="Home">
                <Tile rootId="XVsLeft" label="Play"/>
                <Tile rootId="XVsRight" label="Quit"/>
              </Screen>
            </XUI>
            """);

        // "{label}" is not a string anybody reads — the value passed at the usage site is, and it only
        // becomes text once the two are combined. The template's own literal comes through per usage
        // and merges into one catalog entry with two references.
        Assert.Equal(
            new[] { "Play", "Tap to open", "Quit", "Tap to open" },
            found.Select(f => f.Text));
    }

    [Fact]
    public void APlaceholderInATemplateParameterIsNeverACatalogEntry()
    {
        // Whatever survives expansion is real text; a raw "{param}" reaching the catalog would send a
        // translator a string that never appears on screen.
        List<XuiExtractedString> found = Extract("""
            <XUI version="1">
              <Template name="Row">
                <Param name="rootId"/>
                <Param name="hint"/>
                <InputField id="{rootId}" placeholder="{hint}"/>
              </Template>
              <Screen name="Login">
                <Row rootId="XInputAccount" hint="Account"/>
              </Screen>
            </XUI>
            """);

        Assert.Equal(new[] { "Account" }, found.Select(f => f.Text));
    }

    [Theory]
    [InlineData("""Xui.Tr("Saved")""", "Saved")]
    [InlineData("""var s = Tr("Deleted");""", "Deleted")]
    [InlineData("""Xui.Tr("Say \"hi\"")""", "Say \"hi\"")]
    public void TrCallsAreFoundInCSharp(string code, string expected)
        => Assert.Equal(expected, XuiTextExtractor.ExtractFromCSharp(code, "A.cs").Single().Text);

    [Theory]
    [InlineData("""Xui.Tr($"Hello {name}")""")]      // interpolated: nothing fixed to extract
    [InlineData("""Xui.Tr(key)""")]                   // a variable
    [InlineData("""Other.Tr("x")""")]                 // not ours
    [InlineData("""somethingTr("x")""")]              // a method that merely ends in Tr
    public void OnlyLiteralTrCallsAreExtracted(string code)
        => Assert.Empty(XuiTextExtractor.ExtractFromCSharp(code, "A.cs"));

    [Fact]
    public void LineNumbersPointAtTheCall()
    {
        List<XuiExtractedString> found = XuiTextExtractor.ExtractFromCSharp(
            "class A\n{\n    void M()\n    {\n        Xui.Tr(\"Saved\");\n    }\n}\n", "A.cs");

        Assert.Equal(5, found.Single().Line);
    }

    // ------------------------------------------------------------------
    //  Merge — the part that must never lose work
    // ------------------------------------------------------------------

    private static XuiExtractedString At(string text, int line = 1)
        => new(text, "Assets/UI/A.xui", line, "XTxtA");

    [Fact]
    public void AnExistingTranslationSurvivesReExtraction()
    {
        var catalog = new PoCatalog();
        catalog.Add(new PoEntry("Sign in") { Translation = "登录" });

        XuiMergeReport report = XuiTextExtractor.Merge(catalog, new[] { At("Sign in"), At("New", 2) });

        Assert.Equal("登录", catalog.Find(null, "Sign in")!.Translation);
        Assert.Equal(1, report.Added);
        Assert.Equal(0, report.Obsoleted);
        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Translated);
    }

    [Fact]
    public void AStringNoSourceMentionsIsMarkedNotDeleted()
    {
        var catalog = new PoCatalog();
        catalog.Add(new PoEntry("Old") { Translation = "旧的" });

        XuiMergeReport report = XuiTextExtractor.Merge(catalog, new[] { At("New") });

        PoEntry old = catalog.Find(null, "Old")!;
        Assert.True(old.IsObsolete);
        Assert.Equal("旧的", old.Translation);   // the work is still there
        Assert.Empty(old.References);
        Assert.Equal(1, report.Obsoleted);
    }

    [Fact]
    public void AStringThatComesBackKeepsItsTranslation()
    {
        // The reason obsolete entries are kept: a rename, a reverted commit, or a temporarily
        // commented-out screen must not cost a translation someone was paid for.
        var catalog = new PoCatalog();
        catalog.Add(new PoEntry("Sign in") { Translation = "登录", IsObsolete = true });

        XuiMergeReport report = XuiTextExtractor.Merge(catalog, new[] { At("Sign in") });

        PoEntry entry = catalog.Find(null, "Sign in")!;
        Assert.False(entry.IsObsolete);
        Assert.Equal("登录", entry.Translation);
        Assert.Equal(1, report.Revived);
        Assert.Equal(0, report.Added);
    }

    [Fact]
    public void ReferencesAreRewrittenNotAccumulated()
    {
        var catalog = new PoCatalog();
        PoEntry entry = new("Sign in");
        entry.References.Add(new PoReference("Assets/UI/Old.xui", 99));
        catalog.Add(entry);

        XuiTextExtractor.Merge(catalog, new[] { At("Sign in", 8), At("Sign in", 20) });

        // Both current sites, and nothing from the run before — a stale reference sends a translator
        // to a file that no longer contains the string.
        Assert.Equal(
            new[] { "Assets/UI/A.xui:8", "Assets/UI/A.xui:20" },
            entry.References.Select(r => r.ToString()));
    }

    [Fact]
    public void MergingTwiceChangesNothingTheSecondTime()
    {
        var catalog = new PoCatalog();
        XuiExtractedString[] strings = { At("Sign in"), At("Account", 2) };

        XuiTextExtractor.Merge(catalog, strings);
        catalog.Find(null, "Sign in")!.Translation = "登录";
        string first = catalog.Write();

        XuiMergeReport second = XuiTextExtractor.Merge(catalog, strings);

        Assert.Equal(first, catalog.Write());
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Obsoleted);
    }

    [Fact]
    public void TheSameStringInTwoPlacesIsOneEntry()
    {
        var catalog = new PoCatalog();

        XuiMergeReport report = XuiTextExtractor.Merge(
            catalog, new[] { At("OK"), new XuiExtractedString("OK", "Assets/UI/B.xui", 4, "XBtnB") });

        Assert.Single(catalog.Entries);
        Assert.Equal(1, report.Total);
        Assert.Equal(2, catalog.Entries[0].References.Count);
    }
}
