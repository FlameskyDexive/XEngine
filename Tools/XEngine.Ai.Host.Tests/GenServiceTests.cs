using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Gateway;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ Gen-P1.2 — the gen_submit → gen_status → gen_fetch chain against the mock provider:
/// idempotent submission, dry runs without side effects, poll state persistence, and a fetch that
/// journals the import, embeds the recipe into .meta, derives model sub-assets, and preserves the
/// GUID across re-roll overwrites.
/// </summary>
public sealed class GenServiceTests : IDisposable
{
    private readonly string _root;
    private readonly GenTaskStore _store;
    private readonly AssetWriter _assetWriter;

    public GenServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-gensvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        _store = new GenTaskStore(_root);
        _assetWriter = new AssetWriter(new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "test-project"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GenService Service(int succeedAfterPolls = 0)
    {
        var registry = new GenProviderRegistry();
        registry.Register(new MockGenProvider(succeedAfterPolls));
        var project = new ProjectDescriptor("test-project", _root, null, null, null, "fingerprint000000");
        return new GenService(project, registry, _store, _assetWriter);
    }

    // ---- gen_submit ---------------------------------------------------------------------

    [Fact]
    public async Task Submit_RecordsPendingTask_AndDedupesResubmission()
    {
        GenService service = Service();

        GenSubmitToolResult first = await service.SubmitAsync(
            "mock", "model3d", "a rusty sword", null, 7, null, null, dryRun: false, CancellationToken.None);

        Assert.False(first.Deduplicated);
        Assert.StartsWith("mock-", first.TaskId);
        Assert.Equal("pending", first.State);
        Assert.NotNull(_store.Get(first.TaskId));

        GenSubmitToolResult second = await service.SubmitAsync(
            "mock", "model3d", "a rusty sword", null, 7, null, null, dryRun: false, CancellationToken.None);

        Assert.True(second.Deduplicated);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Single(_store.All());
    }

    [Fact]
    public async Task Submit_DryRun_HasNoSideEffects()
    {
        GenService service = Service();

        GenSubmitToolResult result = await service.SubmitAsync(
            "mock", "sfx", "sword clang", null, null, null, null, dryRun: true, CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal("dryRun", result.State);
        Assert.Equal("", result.TaskId);
        Assert.Equal(64, result.InputHash.Length);
        Assert.Empty(_store.All());
    }

    [Fact]
    public async Task Submit_UnsupportedKind_Rejects()
    {
        GenService service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(
            "mock", "hologram", "x", null, null, null, null, false, CancellationToken.None));
    }

    // ---- gen_status ---------------------------------------------------------------------

    [Fact]
    public async Task Status_TracksProviderStateMachine_AndPersists()
    {
        GenService service = Service(succeedAfterPolls: 1);
        GenSubmitToolResult submitted = await service.SubmitAsync(
            "mock", "texture", "mossy bricks", null, null, null, null, false, CancellationToken.None);

        GenStatusToolResult running = await service.StatusAsync(submitted.TaskId, CancellationToken.None);
        Assert.Equal("running", running.State);
        Assert.Equal(GenTaskState.Running, _store.Get(submitted.TaskId)!.State);

        GenStatusToolResult done = await service.StatusAsync(submitted.TaskId, CancellationToken.None);
        Assert.Equal("succeeded", done.State);
        Assert.Equal(GenTaskState.Succeeded, _store.Get(submitted.TaskId)!.State);

        // Terminal → served from the ledger (mock's poll counter no longer matters).
        GenStatusToolResult again = await service.StatusAsync(submitted.TaskId, CancellationToken.None);
        Assert.Equal("succeeded", again.State);
    }

    [Fact]
    public async Task Status_UnknownTask_Throws()
    {
        GenService service = Service();
        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(
            () => service.StatusAsync("nope", CancellationToken.None));
    }

    // ---- gen_fetch ----------------------------------------------------------------------

    [Fact]
    public async Task Fetch_ImportsArtifact_EmbedsRecipe_DerivesSubAssets()
    {
        GenService service = Service();
        GenSubmitToolResult submitted = await service.SubmitAsync(
            "mock", "model3d", "A Rusty Sword!!", null, null, null, null, false, CancellationToken.None);

        GenFetchToolResult fetched = await service.FetchAsync(
            submitted.TaskId, "GenArt", null, false, "test fetch", "fetch-key-1", CancellationToken.None);

        GenFetchedAsset asset = Assert.Single(fetched.Assets);
        Assert.Equal("Assets/GenArt/a-rusty-sword.glb", asset.AssetPath);
        Assert.Equal("model", asset.Role);
        Assert.Equal("EditorModelImporter", asset.ImporterType);
        Assert.False(string.IsNullOrEmpty(asset.Guid));

        // The real file + meta exist; meta embeds the recipe for re-rolling.
        string absolute = Path.Combine(_root, "Assets", "GenArt", "a-rusty-sword.glb");
        Assert.True(File.Exists(absolute));
        string meta = File.ReadAllText(absolute + ".meta");
        Assert.Contains("genRecipe", meta);
        Assert.Contains("A Rusty Sword!!", meta);
        Assert.Contains(submitted.TaskId, meta);

        // Model sub-assets are derived inline, ready for scene wiring.
        Assert.NotNull(asset.SubAssets);
        Assert.Contains(asset.SubAssets!, s => s.StableId == "MockMesh/prim0" && s.Kind == "Mesh");
        Assert.Contains(asset.SubAssets!, s => s.StableId == "MockMaterial" && s.Kind == "Material");

        // Ledger updated for later sessions.
        GenTaskRecord record = _store.Get(submitted.TaskId)!;
        Assert.Equal(GenTaskState.Succeeded, record.State);
        Assert.Equal("Assets/GenArt/a-rusty-sword.glb", Assert.Single(record.ImportedAssets));

        // The import was journaled with provenance.
        Assert.True(Directory.Exists(Path.Combine(_root, ".xengine", "journal")));
    }

    [Fact]
    public async Task Fetch_BeforeReady_Rejects()
    {
        GenService service = Service(succeedAfterPolls: 5);
        GenSubmitToolResult submitted = await service.SubmitAsync(
            "mock", "model3d", "slow crate", null, null, null, null, false, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.FetchAsync(
            submitted.TaskId, "GenArt", null, false, null, "fetch-key-2", CancellationToken.None));
        Assert.Contains("not ready", ex.Message);
    }

    [Fact]
    public async Task Fetch_Overwrite_PreservesGuid_TheRerollPath()
    {
        GenService service = Service();
        GenSubmitToolResult submitted = await service.SubmitAsync(
            "mock", "model3d", "a crate", null, null, null, null, false, CancellationToken.None);

        GenFetchToolResult first = await service.FetchAsync(
            submitted.TaskId, "GenArt", "crate", false, null, "fetch-key-3", CancellationToken.None);
        GenFetchToolResult second = await service.FetchAsync(
            submitted.TaskId, "GenArt", "crate", true, "re-roll", "fetch-key-4", CancellationToken.None);

        Assert.Equal(first.Assets[0].Guid, second.Assets[0].Guid);
        Assert.Equal(first.Assets[0].AssetPath, second.Assets[0].AssetPath);
    }

    [Fact]
    public async Task Fetch_UnknownTask_Throws()
    {
        GenService service = Service();
        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(
            () => service.FetchAsync("nope", "GenArt", null, false, null, "k", CancellationToken.None));
    }

    // ---- slug ----------------------------------------------------------------------------

    [Theory]
    [InlineData("A Rusty Sword!!", "a-rusty-sword")]
    [InlineData("  moss  ", "moss")]
    [InlineData("!!!", "generated")]
    public void Slugify_ProducesFileSafeNames(string prompt, string expected)
    {
        Assert.Equal(expected, GenService.Slugify(prompt));
    }
}
