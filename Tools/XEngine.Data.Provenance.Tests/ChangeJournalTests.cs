// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Data.Provenance;
using XEngine.Echo;
using Xunit;

namespace XEngine.Data.Provenance.Tests;

/// <summary>
/// Proves A2 (Layer 0): the operation journal persists changes to disk and they survive a
/// "restart" (disposing and reopening the journal). This is the durability guarantee the AI
/// scene-write tools (B2) and the journal-query read tool (B1) depend on.
/// </summary>
public class ChangeJournalTests : IDisposable
{
    private readonly string _tempRoot;

    public ChangeJournalTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "xengine-journal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* test cleanup */ }
    }

    [Fact]
    public async Task Append_ThenQuery_ReturnsTheRecord()
    {
        using var journal = new ChangeJournal(_tempRoot, "project-1");

        EchoObject before = MakeGo("Cam", 0f, 1f, -5f);
        EchoObject after = MakeGo("Cam", 0f, 4f, -5f);
        EchoObject delta = ProvenancedDelta.Create(before, after,
            ChangeProvenance.ForEditor("raise camera"));

        await journal.AppendAsync("Assets/Scenes/Main.scene", delta,
            ProvenancedDelta.GetProvenance(delta)!,
            ProvenancedDelta.ContentHash(before),
            ProvenancedDelta.ContentHash(after));

        var records = await journal.QueryAsync(new JournalQuery());
        Assert.Single(records);
        Assert.Equal("Assets/Scenes/Main.scene", records[0].TargetAsset);
        Assert.Equal("editor", records[0].Provenance.Author);
        Assert.Equal("raise camera", records[0].Provenance.Intent);
    }

    [Fact]
    public async Task Journal_SurvivesRestart()
    {
        // Append in one journal instance, then open a fresh instance (simulating an engine restart)
        // and confirm the history is still queryable.
        string beforeHash;
        string afterHash;
        EchoObject delta;
        {
            using var j1 = new ChangeJournal(_tempRoot, "project-1");
            EchoObject b = MakeGo("Light", 0f, 5f, 0f);
            EchoObject a = MakeGo("Light", 0f, 8f, 0f);
            beforeHash = ProvenancedDelta.ContentHash(b);
            afterHash = ProvenancedDelta.ContentHash(a);
            delta = ProvenancedDelta.Create(b, a, ChangeProvenance.ForTool("scene-patch", "dusk", "k1"));
            await j1.AppendAsync("Assets/Scenes/Main.scene", delta, ProvenancedDelta.GetProvenance(delta)!, beforeHash, afterHash);
        }

        // "Restart": a brand-new journal instance over the same root.
        using var j2 = new ChangeJournal(_tempRoot, "project-1");
        var records = await j2.QueryAsync(new JournalQuery());

        Assert.Single(records);
        Assert.Equal("scene-patch", records[0].Provenance.Author);
        Assert.Equal(beforeHash, records[0].BeforeHash);
        Assert.Equal(afterHash, records[0].AfterHash);

        // And the stored delta is still replayable.
        EchoObject replayed = EchoObject.ApplyDelta(MakeGo("Light", 0f, 5f, 0f), records[0].Delta);
        Assert.Equal(MakeGo("Light", 0f, 8f, 0f).WriteToString(), replayed.WriteToString());
    }

    [Fact]
    public async Task Query_FiltersByAuthorAndIntent()
    {
        using var journal = new ChangeJournal(_tempRoot, "project-1");

        await Append(journal, "A.scene", ChangeProvenance.ForTool("tool-a", "raise camera", "k1"));
        await Append(journal, "A.scene", ChangeProvenance.ForTool("tool-b", "darken lights", "k2"));
        await Append(journal, "B.scene", ChangeProvenance.ForTool("tool-a", "move spawn", "k3"));

        var byAuthor = await journal.QueryAsync(new JournalQuery(ByAuthor: "tool-a"));
        Assert.Equal(2, byAuthor.Count);
        Assert.All(byAuthor, r => Assert.Equal("tool-a", r.Provenance.Author));

        var byIntent = await journal.QueryAsync(new JournalQuery(IntentContains: "darken"));
        Assert.Single(byIntent);
        Assert.Contains("darken", byIntent[0].Provenance.Intent);

        var byAsset = await journal.QueryAsync(new JournalQuery(TargetAsset: "B.scene"));
        Assert.Single(byAsset);
    }

    [Fact]
    public async Task IdempotencyKey_Deduplicates_SecondAppendReturnsFirst()
    {
        // ★ P0-1: same idempotencyKey + source submitted twice → second returns the first record,
        // and the journal has only ONE entry (no duplicate write).
        using var journal = new ChangeJournal(_tempRoot, "project-1");

        EchoObject b = MakeGo("Light", 0f, 5f, 0f);
        EchoObject a = MakeGo("Light", 0f, 8f, 0f);
        EchoObject delta = ProvenancedDelta.Create(b, a,
            ChangeProvenance.ForTool("xengine-cli.scene-object-patch", "raise", "idem-key-1"));
        var prov = ProvenancedDelta.GetProvenance(delta)!;

        var first = await journal.AppendAsync("A.scene", delta, prov, "h1", "h2");
        // Second append with the SAME key + source → should return the FIRST record (dedup).
        var second = await journal.AppendAsync("A.scene", delta, prov, "h1", "h2");

        Assert.Equal(first.ChangeId, second.ChangeId);
        var all = await journal.QueryAsync(new JournalQuery());
        Assert.Single(all); // only ONE entry, not two
    }

    [Fact]
    public async Task IdempotencyKey_DifferentKeys_BothAppended()
    {
        // Different idempotencyKeys → both records are appended (no false dedup).
        using var journal = new ChangeJournal(_tempRoot, "project-1");
        await Append(journal, "A.scene", ChangeProvenance.ForTool("tool", "a", "key-A"));
        await Append(journal, "A.scene", ChangeProvenance.ForTool("tool", "b", "key-B"));

        var all = await journal.QueryAsync(new JournalQuery());
        Assert.Equal(2, all.Count);
    }

    private static async Task Append(ChangeJournal journal, string asset, ChangeProvenance prov)
    {
        EchoObject b = MakeGo("Obj", 0f, 0f, 0f);
        EchoObject a = MakeGo("Obj", 1f, 0f, 0f);
        EchoObject delta = ProvenancedDelta.Create(b, a, prov);
        await journal.AppendAsync(asset, delta, prov,
            ProvenancedDelta.ContentHash(b), ProvenancedDelta.ContentHash(a));
    }

    private static EchoObject MakeGo(string name, float x, float y, float z)
    {
        EchoObject root = EchoObject.NewCompound();
        root["Name"] = new EchoObject(name);
        EchoObject transform = EchoObject.NewCompound();
        EchoObject position = EchoObject.NewCompound();
        position["X"] = new EchoObject(x);
        position["Y"] = new EchoObject(y);
        position["Z"] = new EchoObject(z);
        transform["Position"] = position;
        root["Transform"] = transform;
        return root;
    }
}
