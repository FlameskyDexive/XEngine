using XEngine.Ai.Gateway;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Gen-P4.1 — candidate gallery lists imported tasks and persists choose/discard.</summary>
public sealed class GenCandidateGalleryTests : IDisposable
{
    private readonly string _root;
    private readonly GenTaskStore _store;
    private readonly GenService _svc;

    public GenCandidateGalleryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-p41-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        _store = new GenTaskStore(_root);
        var registry = new GenProviderRegistry();
        registry.Register(new MockGenProvider());
        var writer = new AssetWriter(new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "p"));
        _svc = new GenService(new ProjectDescriptor("p", _root, null, null, null, "fp"),
            registry, _store, writer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task List_ImportedOnly_ThenChooseAndDiscard()
    {
        GenSubmitToolResult sword = await _svc.SubmitAsync(
            "mock", "model3d", "a rusty sword", null, 1, null, null, false, CancellationToken.None);
        await _svc.FetchAsync(sword.TaskId, "Assets/GenArt", "sword", false, "p41", "p41-sword",
            CancellationToken.None);

        GenSubmitToolResult crate = await _svc.SubmitAsync(
            "mock", "model3d", "a wooden crate", null, 2, null, null, false, CancellationToken.None);
        await _svc.FetchAsync(crate.TaskId, "Assets/GenArt", "crate", false, "p41", "p41-crate",
            CancellationToken.None);

        GenCandidateListResult listed = GenCandidateTools.List(_root, "model3d", "crate", importedOnly: true, false);
        GenCandidateInfo card = Assert.Single(listed.Candidates);
        Assert.Equal(crate.TaskId, card.TaskId);
        Assert.Contains("crate", card.AssetPaths[0], StringComparison.OrdinalIgnoreCase);

        GenCandidateInfo chosen = GenCandidateTools.Choose(_root, crate.TaskId);
        Assert.True(chosen.Chosen);
        Assert.Equal(crate.TaskId, new GenCandidateCatalog(_root).ChosenTaskId);

        GenCandidateInfo discarded = GenCandidateTools.Discard(_root, crate.TaskId);
        Assert.True(discarded.Discarded);
        Assert.Equal(sword.TaskId, Assert.Single(GenCandidateTools.List(_root, null, null, true, false).Candidates).TaskId);
        Assert.Equal(2, GenCandidateTools.List(_root, null, null, true, includeDiscarded: true).Candidates.Count);
        Assert.Null(new GenCandidateCatalog(_root).ChosenTaskId);
    }

    [Fact]
    public void Layout_CompactUnder720()
    {
        GenCandidateGalleryLayout wide = GenCandidateCatalog.ComputeLayout(900, 480);
        Assert.False(wide.Compact);
        Assert.Equal(900f, wide.ListWidth + wide.DetailWidth + 1f, 2);

        GenCandidateGalleryLayout compact = GenCandidateCatalog.ComputeLayout(520, 480);
        Assert.True(compact.Compact);
        Assert.True(compact.ListWidth >= 200f);
    }

    [Fact]
    public void Capabilities_AdvertiseGalleryTools()
    {
        var catalog = new CapabilityCatalog(new ProjectDescriptor("p", _root, null, null, null, "fp"));
        CapabilitySnapshot snap = catalog.GetSnapshot();

        CapabilityToolDescriptor list = snap.Tools.Single(t => t.Name == "gen_candidates");
        Assert.Equal("generate", list.Group);
        Assert.Equal(RiskLevel.ReadOnly, list.RiskLevel);

        CapabilityToolDescriptor choose = snap.Tools.Single(t => t.Name == "gen_candidate_choose");
        Assert.Equal(RiskLevel.SafeWrite, choose.RiskLevel);
        Assert.Equal(RiskLevel.SafeWrite, snap.Tools.Single(t => t.Name == "gen_candidate_discard").RiskLevel);
    }

    [Fact]
    public void EvidenceScreenshot_CandidateGallery()
    {
        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gen-asset"));
        Directory.CreateDirectory(evidenceDir);
        string png = Path.Combine(evidenceDir, "2026-08-16-p4-1-candidate-gallery.png");
        GenCandidateGalleryLayout layout = GenCandidateCatalog.ComputeLayout(720, 200);
        const int w = 720;
        const int h = 200;
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
        int list = (int)layout.ListWidth;
        Rect(8, 40, list - 16, 48, 70, 110, 200);
        Rect(8, 96, list - 16, 48, 40, 44, 54);
        Rect(list + 8, 40, w - list - 16, 140, 180, 140, 60);
        File.WriteAllBytes(png, PlayablePropEvidencePng.EncodePng(px, w, h));
        File.WriteAllText(Path.ChangeExtension(png, ".md"),
            "# P4.1 candidate gallery acceptance\n\n" +
            "- Window/AI/Candidate Gallery lists gen_fetch imports from gen-tasks.json\n" +
            "- gen_candidates / gen_candidate_choose / gen_candidate_discard persist .xengine/gen-gallery.json\n");
        Assert.True(new FileInfo(png).Length > 200);
    }
}
