using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Gateway;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Gen-P1.1 — recipe hashing: the idempotency/billing-dedupe key must be deterministic
/// and insensitive to Extra-dictionary ordering, and must change when any shaping field changes.</summary>
public sealed class GenRecipeTests
{
    [Fact]
    public void InputHash_IsDeterministic_AndExtraOrderInsensitive()
    {
        var a = new GenRecipe("mock", "model3d", "a rusty sword", Model: "m-5", Seed: 42,
            Extra: new Dictionary<string, string> { ["topology"] = "quad", ["polycount"] = "3000" });
        var b = new GenRecipe("mock", "model3d", "a rusty sword", Model: "m-5", Seed: 42,
            Extra: new Dictionary<string, string> { ["polycount"] = "3000", ["topology"] = "quad" });

        Assert.Equal(a.ComputeInputHash(), b.ComputeInputHash());
        Assert.Equal(64, a.ComputeInputHash().Length); // sha256 hex
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("seed")]
    [InlineData("model")]
    [InlineData("styleAnchor")]
    [InlineData("extra")]
    public void InputHash_ChangesWhenAnyFieldChanges(string field)
    {
        var baseline = new GenRecipe("mock", "model3d", "a rusty sword", Model: "m-5", Seed: 42,
            StyleAnchor: "anchor-1", Extra: new Dictionary<string, string> { ["topology"] = "quad" });
        GenRecipe changed = field switch
        {
            "prompt" => baseline with { Prompt = "a shiny sword" },
            "seed" => baseline with { Seed = 43 },
            "model" => baseline with { Model = "m-6" },
            "styleAnchor" => baseline with { StyleAnchor = "anchor-2" },
            _ => baseline with { Extra = new Dictionary<string, string> { ["topology"] = "tri" } },
        };

        Assert.NotEqual(baseline.ComputeInputHash(), changed.ComputeInputHash());
    }
}

/// <summary>★ Gen-P1.1 — secret resolution order: env var first, then .xengine/secrets.json
/// (string or {"apiKey": ...} form); missing everywhere → null.</summary>
public sealed class SecretResolverTests : IDisposable
{
    private readonly string _root;

    public SecretResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ".xengine"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnvVar_WinsOverSecretsFile()
    {
        // Unique provider name per test run — env vars are process-global.
        string provider = "envtest" + Guid.NewGuid().ToString("N")[..8];
        string envVar = SecretResolver.EnvVarName(provider);
        File.WriteAllText(Path.Combine(_root, ".xengine", "secrets.json"), $$"""{"{{provider}}": "from-file"}""");
        Environment.SetEnvironmentVariable(envVar, "from-env");
        try
        {
            Assert.Equal("from-env", SecretResolver.Resolve(provider, _root));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void SecretsFile_SupportsStringAndObjectForms()
    {
        File.WriteAllText(Path.Combine(_root, ".xengine", "secrets.json"),
            """{"stringy": "sk-plain", "objecty": {"apiKey": "sk-nested"}}""");

        Assert.Equal("sk-plain", SecretResolver.Resolve("stringy", _root));
        Assert.Equal("sk-nested", SecretResolver.Resolve("objecty", _root));
        Assert.Null(SecretResolver.Resolve("absent", _root));
    }

    [Fact]
    public void MissingEverywhere_ReturnsNull()
    {
        Assert.Null(SecretResolver.Resolve("nokey" + Guid.NewGuid().ToString("N")[..8], _root));
    }

    [Fact]
    public void EnvVarName_SanitizesNonAlphanumerics()
    {
        Assert.Equal("PIXEL_LAB_API_KEY", SecretResolver.EnvVarName("pixel-lab"));
        Assert.Equal("MESHY_API_KEY", SecretResolver.EnvVarName("meshy"));
    }
}

/// <summary>★ Gen-P1.1 — the durable task ledger: round-trips across store instances (the CLI is
/// one process per command), dedupes by input hash, and lets failed tasks be retried.</summary>
public sealed class GenTaskStoreTests : IDisposable
{
    private readonly string _root;

    public GenTaskStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-gentasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static GenRecipe Recipe(string prompt = "a barrel") =>
        new("mock", "model3d", prompt, Extra: new Dictionary<string, string> { ["polycount"] = "500" });

    [Fact]
    public void Add_ThenGet_AcrossStoreInstances()
    {
        var store = new GenTaskStore(_root);
        GenTaskRecord added = store.Add(Recipe(), "task-1", estimatedCredits: 5);

        var reloaded = new GenTaskStore(_root);
        GenTaskRecord? found = reloaded.Get("task-1");

        Assert.NotNull(found);
        Assert.Equal(added.InputHash, found!.InputHash);
        Assert.Equal("a barrel", found.Recipe.Prompt);
        Assert.Equal("500", found.Recipe.Extra!["polycount"]);
        Assert.Equal(GenTaskState.Pending, found.State);
        Assert.Equal(5, found.EstimatedCredits);
    }

    [Fact]
    public void FindReusableByHash_DedupesActiveTasks_ButNotFailed()
    {
        var store = new GenTaskStore(_root);
        GenRecipe recipe = Recipe();
        store.Add(recipe, "task-1", null);

        Assert.NotNull(store.FindReusableByHash(recipe.ComputeInputHash()));

        store.Update("task-1", t => { t.State = GenTaskState.Failed; t.Error = "boom"; });
        Assert.Null(store.FindReusableByHash(recipe.ComputeInputHash()));
    }

    [Fact]
    public void Update_PersistsStateArtifactsAndImports()
    {
        var store = new GenTaskStore(_root);
        store.Add(Recipe(), "task-1", null);

        store.Update("task-1", t =>
        {
            t.State = GenTaskState.Succeeded;
            t.Artifacts.Add(new GenArtifact("model", @"C:\downloads\task-1.glb", "model/gltf-binary"));
            t.ImportedAssets.Add("GenArt/barrel.glb");
        });

        GenTaskRecord? found = new GenTaskStore(_root).Get("task-1");
        Assert.Equal(GenTaskState.Succeeded, found!.State);
        Assert.Single(found.Artifacts);
        Assert.Equal("model", found.Artifacts[0].Role);
        Assert.Equal("GenArt/barrel.glb", found.ImportedAssets[0]);
    }

    [Fact]
    public void Update_UnknownTask_Throws()
    {
        var store = new GenTaskStore(_root);
        Assert.Throws<InvalidOperationException>(() => store.Update("nope", _ => { }));
    }
}

/// <summary>★ Gen-P1.1 — the mock provider end-to-end: deterministic task ids, poll state machine,
/// artifacts that the real ingestion path accepts (valid GLB parsed by asset_subassets' Clay path).</summary>
public sealed class MockGenProviderTests : IDisposable
{
    private readonly string _root;

    public MockGenProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-mockgen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Submit_SameRecipe_SameTaskId()
    {
        var provider = new MockGenProvider();
        var recipe = new GenRecipe("mock", "model3d", "a crate");

        GenSubmitResult first = await provider.SubmitAsync(recipe, CancellationToken.None);
        GenSubmitResult second = await provider.SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal(first.TaskId, second.TaskId);
        Assert.StartsWith("mock-", first.TaskId);
    }

    [Fact]
    public async Task Poll_RunsThenSucceeds()
    {
        var provider = new MockGenProvider(succeedAfterPolls: 2);
        GenSubmitResult submitted = await provider.SubmitAsync(new GenRecipe("mock", "sfx", "sword clang"), CancellationToken.None);

        GenTaskStatus p1 = await provider.PollAsync(submitted.TaskId, CancellationToken.None);
        GenTaskStatus p2 = await provider.PollAsync(submitted.TaskId, CancellationToken.None);
        GenTaskStatus p3 = await provider.PollAsync(submitted.TaskId, CancellationToken.None);

        Assert.Equal(GenTaskState.Running, p1.State);
        Assert.Equal(GenTaskState.Running, p2.State);
        Assert.Equal(GenTaskState.Succeeded, p3.State);
        Assert.True(p1.Progress < p3.Progress);
    }

    [Theory]
    [InlineData("model3d", ".glb", "model")]
    [InlineData("texture", ".png", "texture")]
    [InlineData("skybox", ".png", "skybox")]
    [InlineData("sfx", ".wav", "audio")]
    public async Task Fetch_WritesArtifactPerKind(string kind, string extension, string role)
    {
        var provider = new MockGenProvider();
        var recipe = new GenRecipe("mock", kind, "anything");
        GenSubmitResult submitted = await provider.SubmitAsync(recipe, CancellationToken.None);

        IReadOnlyList<GenArtifact> artifacts =
            await provider.FetchAsync(submitted.TaskId, recipe, _root, CancellationToken.None);

        GenArtifact artifact = Assert.Single(artifacts);
        Assert.Equal(role, artifact.Role);
        Assert.EndsWith(extension, artifact.LocalPath);
        Assert.True(new FileInfo(artifact.LocalPath).Length > 0);
    }

    [Fact]
    public async Task MockGlb_IsAcceptedByTheRealModelPipeline()
    {
        // The fabricated GLB must be indistinguishable from provider output for the ingestion
        // chain: run it through asset_subassets' Clay-backed derivation.
        var provider = new MockGenProvider();
        var recipe = new GenRecipe("mock", "model3d", "a crate");
        GenSubmitResult submitted = await provider.SubmitAsync(recipe, CancellationToken.None);
        IReadOnlyList<GenArtifact> artifacts =
            await provider.FetchAsync(submitted.TaskId, recipe, _root, CancellationToken.None);

        string glb = artifacts[0].LocalPath;
        File.WriteAllText(glb + ".meta", MetaFor(Guid.NewGuid()));

        XEngine.Ai.Protocol.SubAssetList derived = SubAssetResolver.Resolve(glb, Path.GetFileName(glb));

        Assert.Contains(derived.SubAssets, s => s.StableId == "MockMesh/prim0" && s.Kind == "Mesh");
        Assert.Contains(derived.SubAssets, s => s.StableId == "MockMaterial" && s.Kind == "Material");
        Assert.Empty(derived.Warnings);
    }

    [Fact]
    public void DefaultRegistry_HasMock()
    {
        GenProviderRegistry registry = GenProviderRegistry.CreateDefault();
        Assert.Equal("mock", registry.Get("mock").Name);
        Assert.True(registry.TryGet("MOCK", out _)); // case-insensitive
        Assert.Throws<InvalidOperationException>(() => registry.Get("no-such-provider"));
    }

    private static string MetaFor(Guid guid)
    {
        XEngine.Echo.EchoObject meta = XEngine.Echo.EchoObject.NewCompound();
        meta["guid"] = new XEngine.Echo.EchoObject(guid.ToString("N"));
        meta["importer"] = new XEngine.Echo.EchoObject("EditorModelImporter");
        meta["importerVersion"] = new XEngine.Echo.EchoObject(6);
        return meta.WriteToString();
    }
}
