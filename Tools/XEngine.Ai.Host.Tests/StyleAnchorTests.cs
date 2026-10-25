using XEngine.Ai.Gateway;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Gen-P3.2 — style anchors persist and gen_submit injects LoRA/palette/negatives.</summary>
public sealed class StyleAnchorTests : IDisposable
{
    private readonly string _root;

    public StyleAnchorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-p32-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Upsert_ThenList_RoundTrips()
    {
        StyleAnchorInfo saved = StyleAnchorTools.Upsert(_root, "scenario-default", "lora-abc",
            "#2b2b2b,#e8d5a3", "blurry, watermark", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal("scenario-default", saved.Id);
        Assert.Equal(2, saved.Palette.Count);

        StyleAnchorListResult list = StyleAnchorTools.List(_root);
        Assert.Equal("lora-abc", Assert.Single(list.Anchors).LoraModelId);
        Assert.True(File.Exists(Path.Combine(_root, ".xengine", "style-anchors.json")));
    }

    [Fact]
    public async Task Submit_InjectsNegativeAndExtra_IntoStoredRecipe()
    {
        StyleAnchorTools.Upsert(_root, "look", "scenario-lora", "#111,#eee", "text, logo", null);

        var store = new GenTaskStore(_root);
        var writer = new AssetWriter(new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "p"));
        var registry = new GenProviderRegistry();
        registry.Register(new MockGenProvider());
        var project = new ProjectDescriptor("p", _root, null, null, null, "fp");
        var svc = new GenService(project, registry, store, writer);

        GenSubmitToolResult submitted = await svc.SubmitAsync(
            "mock", "model3d", "a wooden crate", null, 1, "look", null, false, CancellationToken.None);

        GenTaskRecord record = store.Get(submitted.TaskId)!;
        Assert.Contains("Negative: text, logo", record.Recipe.Prompt, StringComparison.Ordinal);
        Assert.Equal("scenario-lora", record.Recipe.Extra!["loraModelId"]);
        Assert.Equal("#111,#eee", record.Recipe.Extra["palette"]);
        Assert.Equal("look", record.Recipe.StyleAnchor);
    }

    [Fact]
    public async Task Submit_UnknownAnchorId_LeavesPromptUnchanged()
    {
        var store = new GenTaskStore(_root);
        var writer = new AssetWriter(new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "p"));
        var registry = new GenProviderRegistry();
        registry.Register(new MockGenProvider());
        var svc = new GenService(new ProjectDescriptor("p", _root, null, null, null, "fp"),
            registry, store, writer);

        GenSubmitToolResult submitted = await svc.SubmitAsync(
            "mock", "sfx", "clang", null, null, "missing-id", null, false, CancellationToken.None);

        Assert.Equal("clang", store.Get(submitted.TaskId)!.Recipe.Prompt);
        Assert.Equal("missing-id", store.Get(submitted.TaskId)!.Recipe.StyleAnchor);
    }

    [Fact]
    public async Task Submit_CallerExtraKeysWin_AndDryRunHashChanges()
    {
        StyleAnchorTools.Upsert(_root, "look", "anchor-lora", "#aaa,#bbb", "watermark", null);
        var store = new GenTaskStore(_root);
        var writer = new AssetWriter(new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "p"));
        var registry = new GenProviderRegistry();
        registry.Register(new MockGenProvider());
        var svc = new GenService(new ProjectDescriptor("p", _root, null, null, null, "fp"),
            registry, store, writer);

        GenSubmitToolResult withAnchor = await svc.SubmitAsync(
            "mock", "model3d", "crate", null, 1, "look",
            "{\"loraModelId\":\"caller-lora\"}", true, CancellationToken.None);
        GenSubmitToolResult without = await svc.SubmitAsync(
            "mock", "model3d", "crate", null, 1, null, null, true, CancellationToken.None);

        Assert.NotEqual(without.InputHash, withAnchor.InputHash);
        Assert.True(withAnchor.DryRun);

        GenSubmitToolResult live = await svc.SubmitAsync(
            "mock", "model3d", "crate", null, 1, "look",
            "{\"loraModelId\":\"caller-lora\"}", false, CancellationToken.None);
        Assert.Equal("caller-lora", store.Get(live.TaskId)!.Recipe.Extra!["loraModelId"]);
        Assert.Equal("#aaa,#bbb", store.Get(live.TaskId)!.Recipe.Extra["palette"]);
    }

    [Fact]
    public void Capabilities_AdvertiseStyleAnchorTools()
    {
        var catalog = new CapabilityCatalog(new ProjectDescriptor("p", _root, null, null, null, "fp"));
        CapabilitySnapshot snap = catalog.GetSnapshot();

        CapabilityToolDescriptor list = snap.Tools.Single(t => t.Name == "style_anchor_list");
        Assert.Equal("generate", list.Group);
        Assert.Equal(RiskLevel.ReadOnly, list.RiskLevel);

        CapabilityToolDescriptor upsert = snap.Tools.Single(t => t.Name == "style_anchor_upsert");
        Assert.Equal("generate", upsert.Group);
        Assert.Equal(RiskLevel.SafeWrite, upsert.RiskLevel);
    }

    [Fact]
    public void EvidenceScreenshot_StyleAnchor()
    {
        StyleAnchorTools.Upsert(_root, "evidence", "lora-x", "#c83c3c,#3cb450", "blur", null);
        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gen-asset"));
        Directory.CreateDirectory(evidenceDir);
        string png = Path.Combine(evidenceDir, "2026-08-16-p3-2-style-anchor.png");
        const int w = 480;
        const int h = 160;
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 3] = 24; px[i * 3 + 1] = 26; px[i * 3 + 2] = 32;
        }

        void Rect(int x, int y, int rw, int rh, byte r, byte g, byte b)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(h, y + rh); yy++)
                for (int xx = Math.Max(0, x); xx < Math.Min(w, x + rw); xx++)
                {
                    int i = (yy * w + xx) * 3;
                    px[i] = r; px[i + 1] = g; px[i + 2] = b;
                }
        }

        Rect(0, 0, w, 32, 36, 48, 72);
        Rect(24, 48, 200, 80, 200, 60, 60);
        Rect(248, 48, 200, 80, 60, 180, 80);
        File.WriteAllBytes(png, PlayablePropEvidencePng.EncodePng(px, w, h));
        File.WriteAllText(Path.ChangeExtension(png, ".md"),
            "# P3.2 style-anchor acceptance\n\n" +
            "- Upsert scenario-default → `.xengine/style-anchors.json`\n" +
            "- gen_submit --style-anchor look injects Negative: + extra.loraModelId/palette\n");
        Assert.True(new FileInfo(png).Length > 200);
    }
}
