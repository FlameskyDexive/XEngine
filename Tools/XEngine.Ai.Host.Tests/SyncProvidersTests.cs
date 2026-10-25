using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Gateway;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Gen-P1.4 — PixelLab + ElevenLabs (synchronous, cache-backed) and Blockade (async)
/// adapters: request shapes, auth header styles (Bearer / xi-api-key / x-api-key), the
/// submit-time cache lifecycle, status mapping, and fetch artifact sets. All HTTP is faked.</summary>
public sealed class SyncProvidersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-sync-prov-" + Guid.NewGuid().ToString("N"));

    private string CacheRoot => Path.Combine(_root, "cache");
    private string DownloadDir => Path.Combine(_root, "downloads");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- PixelLab ------------------------------------------------------------------------------

    private PixelLabGenProvider PixelLab(FakeHttpHandler handler, string? key = "pl_key")
        => new(() => key, CacheRoot, handler);

    private static string PixelLabImageJson()
        => """{"image":{"type":"base64","base64":"__B64__"}}"""
            .Replace("__B64__", Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

    [Fact]
    public async Task PixelLab_Submit_GeneratesSynchronously_AndCaches()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, PixelLabImageJson());

        var recipe = new GenRecipe("pixellab", "sprite", "tiny knight idle", Seed: 5,
            Extra: new Dictionary<string, string> { ["width"] = "64", ["height"] = "64" });
        PixelLabGenProvider provider = PixelLab(handler);
        GenSubmitResult submitted = await provider.SubmitAsync(recipe, CancellationToken.None);

        Assert.StartsWith("pixellab-", submitted.TaskId);

        FakeHttpHandler.Recorded request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.pixellab.ai/v2/create-image-pixflux", request.Uri);
        Assert.Equal("Bearer pl_key", request.Authorization);
        JsonNode body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("tiny knight idle", (string?)body["description"]);
        Assert.Equal(64, (int?)body["image_size"]?["width"]);
        Assert.Equal(64, (int?)body["image_size"]?["height"]);
        Assert.Equal(true, (bool?)body["no_background"]); // sprite default
        Assert.Equal(5, (int?)body["seed"]);

        // Cached → poll succeeds and fetch copies the PNG without further HTTP.
        GenTaskStatus status = await provider.PollAsync(submitted.TaskId, CancellationToken.None);
        Assert.Equal(GenTaskState.Succeeded, status.State);

        IReadOnlyList<GenArtifact> artifacts = await provider
            .FetchAsync(submitted.TaskId, recipe, DownloadDir, CancellationToken.None);
        GenArtifact sprite = Assert.Single(artifacts);
        Assert.Equal("sprite", sprite.Role);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(sprite.LocalPath));
        Assert.Single(handler.Requests); // no HTTP after submit
    }

    [Fact]
    public async Task PixelLab_Resubmit_SameRecipe_ServesFromCache_NoSecondBill()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, PixelLabImageJson());

        var recipe = new GenRecipe("pixellab", "sprite", "tiny knight idle");
        PixelLabGenProvider provider = PixelLab(handler);

        GenSubmitResult first = await provider.SubmitAsync(recipe, CancellationToken.None);
        GenSubmitResult second = await provider.SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Single(handler.Requests); // one API call total
    }

    [Fact]
    public async Task PixelLab_TextureKind_KeepsBackgroundByDefault()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, PixelLabImageJson());

        var recipe = new GenRecipe("pixellab", "texture", "mossy stone bricks");
        await PixelLab(handler).SubmitAsync(recipe, CancellationToken.None);

        JsonNode body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal(false, (bool?)body["no_background"]);
    }

    [Fact]
    public async Task PixelLab_MissingKey_FailsWithGuidance()
    {
        var recipe = new GenRecipe("pixellab", "sprite", "a slime");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PixelLab(new FakeHttpHandler(), key: null).SubmitAsync(recipe, CancellationToken.None));
        Assert.Contains("PIXELLAB_API_KEY", ex.Message);
    }

    [Fact]
    public async Task PixelLab_PollWithoutCache_ReportsFailedWithExplanation()
    {
        PixelLabGenProvider provider = PixelLab(new FakeHttpHandler());
        GenTaskStatus status = await provider.PollAsync("pixellab-0123456789abcdef", CancellationToken.None);

        Assert.Equal(GenTaskState.Failed, status.State);
        Assert.Contains("Resubmit", status.Message);
    }

    // ---- ElevenLabs ----------------------------------------------------------------------------

    private ElevenLabsGenProvider ElevenLabs(FakeHttpHandler handler, string? key = "xi_key")
        => new(() => key, CacheRoot, handler);

    [Fact]
    public async Task ElevenLabs_Submit_PostsWithXiApiKeyHeader_AndCachesMp3()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 0xFF, 0xFB, 0x90, 0x00 }); // mp3 frame header

        var recipe = new GenRecipe("elevenlabs", "sfx", "rusty sword clang",
            Extra: new Dictionary<string, string> { ["durationSeconds"] = "1.5", ["promptInfluence"] = "0.7" });
        ElevenLabsGenProvider provider = ElevenLabs(handler);
        GenSubmitResult submitted = await provider.SubmitAsync(recipe, CancellationToken.None);

        FakeHttpHandler.Recorded request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.elevenlabs.io/v1/sound-generation", request.Uri);
        Assert.Null(request.Authorization);            // NOT Bearer
        Assert.Equal("xi_key", request.Header("xi-api-key"));
        JsonNode body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("rusty sword clang", (string?)body["text"]);
        Assert.Equal(1.5, (double?)body["duration_seconds"]);
        Assert.Equal(0.7, (double?)body["prompt_influence"]);

        IReadOnlyList<GenArtifact> artifacts = await provider
            .FetchAsync(submitted.TaskId, recipe, DownloadDir, CancellationToken.None);
        GenArtifact audio = Assert.Single(artifacts);
        Assert.Equal("audio", audio.Role);
        Assert.EndsWith(".mp3", audio.LocalPath);
        Assert.Equal(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, File.ReadAllBytes(audio.LocalPath));
    }

    [Fact]
    public async Task ElevenLabs_HttpError_SurfacesBody()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, Encoding.UTF8.GetBytes("""{"detail":"text too long"}"""));

        var recipe = new GenRecipe("elevenlabs", "sfx", "boom");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ElevenLabs(handler).SubmitAsync(recipe, CancellationToken.None));

        Assert.Contains("422", ex.Message);
        Assert.Contains("text too long", ex.Message);
    }

    // ---- Blockade ------------------------------------------------------------------------------

    private BlockadeGenProvider Blockade(FakeHttpHandler handler, string? key = "bl_key")
        => new(() => key, handler);

    [Fact]
    public async Task Blockade_Submit_PostsPromptAndStyle_WithXApiKeyHeader()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"id":98765,"status":"pending","file_url":""}""");

        var recipe = new GenRecipe("blockade", "skybox", "alien desert at dusk", Seed: 11,
            Extra: new Dictionary<string, string> { ["styleId"] = "155", ["negativeText"] = "text, watermark" });
        GenSubmitResult submitted = await Blockade(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal("blockade:98765", submitted.TaskId);
        FakeHttpHandler.Recorded request = Assert.Single(handler.Requests);
        Assert.Equal("https://backend.blockadelabs.com/api/v1/skybox", request.Uri);
        Assert.Equal("bl_key", request.Header("x-api-key"));
        JsonNode body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("alien desert at dusk", (string?)body["prompt"]);
        Assert.Equal(155, (int?)body["skybox_style_id"]);
        Assert.Equal("text, watermark", (string?)body["negative_text"]);
        Assert.Equal(11, (int?)body["seed"]);
    }

    [Theory]
    [InlineData("pending", GenTaskState.Pending)]
    [InlineData("dispatched", GenTaskState.Running)]
    [InlineData("processing", GenTaskState.Running)]
    [InlineData("complete", GenTaskState.Succeeded)]
    [InlineData("abort", GenTaskState.Failed)]
    [InlineData("error", GenTaskState.Failed)]
    public async Task Blockade_Poll_MapsStatusChain(string blockadeStatus, GenTaskState expected)
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"request":{"id":98765,"status":"__S__","error_message":null}}""".Replace("__S__", blockadeStatus));

        GenTaskStatus status = await Blockade(handler).PollAsync("blockade:98765", CancellationToken.None);

        Assert.Equal(expected, status.State);
        Assert.Equal("https://backend.blockadelabs.com/api/v1/imagine/requests/98765", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task Blockade_Fetch_DownloadsSkybox_AndOptionalDepthMap()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"request":{"id":98765,"status":"complete",
              "file_url":"https://images.blockadelabs.com/images/w.jpg?ver=1",
              "depth_map_url":"https://images.blockadelabs.com/depths/w.png?ver=1",
              "thumb_url":"https://images.blockadelabs.com/thumbs/w.jpg?ver=1"}}
            """);
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 1 });
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 2 });

        var recipe = new GenRecipe("blockade", "skybox", "alien desert",
            Extra: new Dictionary<string, string> { ["fetchDepthMap"] = "true" });
        IReadOnlyList<GenArtifact> artifacts = await Blockade(handler)
            .FetchAsync("blockade:98765", recipe, DownloadDir, CancellationToken.None);

        Assert.Equal(new[] { "skybox", "depth" }, artifacts.Select(a => a.Role).ToArray());
        Assert.EndsWith(".jpg", artifacts[0].LocalPath);
        Assert.EndsWith(".png", artifacts[1].LocalPath);
        Assert.Null(handler.Requests[1].Authorization); // signed URL: no auth
        Assert.Null(handler.Requests[1].Header("x-api-key"));
    }

    [Fact]
    public async Task Blockade_FetchIncomplete_Fails()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"request":{"id":9,"status":"processing"}}""");

        var recipe = new GenRecipe("blockade", "skybox", "x");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Blockade(handler).FetchAsync("blockade:9", recipe, DownloadDir, CancellationToken.None));

        Assert.Contains("processing", ex.Message);
    }

    [Fact]
    public async Task Blockade_MissingKey_FailsWithGuidance()
    {
        var recipe = new GenRecipe("blockade", "skybox", "x");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Blockade(new FakeHttpHandler(), key: null).SubmitAsync(recipe, CancellationToken.None));
        Assert.Contains("BLOCKADE_API_KEY", ex.Message);
    }

    // ---- registry ------------------------------------------------------------------------------

    [Fact]
    public void CreateDefault_RegistersAllP14Providers_WithExpectedKinds()
    {
        GenProviderRegistry registry = GenProviderRegistry.CreateDefault();

        Assert.True(registry.TryGet("pixellab", out IGenProvider pixellab));
        Assert.Equal(new[] { "sprite", "texture" }, pixellab.SupportedKinds);

        Assert.True(registry.TryGet("blockade", out IGenProvider blockade));
        Assert.Equal(new[] { "skybox" }, blockade.SupportedKinds);

        Assert.True(registry.TryGet("elevenlabs", out IGenProvider elevenlabs));
        Assert.Equal(new[] { "sfx" }, elevenlabs.SupportedKinds);
    }

    // ---- fake HTTP -----------------------------------------------------------------------------

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public sealed record Recorded(HttpMethod Method, string Uri, string? Authorization, string? Body,
            IReadOnlyDictionary<string, string> Headers)
        {
            public string? Header(string name) => Headers.TryGetValue(name, out string? v) ? v : null;
        }

        private readonly Queue<(HttpStatusCode Status, byte[] Body)> _responses = new();

        public List<Recorded> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string json)
            => _responses.Enqueue((status, Encoding.UTF8.GetBytes(json)));

        public void Enqueue(HttpStatusCode status, byte[] bytes)
            => _responses.Enqueue((status, bytes));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(), body, headers));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            }

            (HttpStatusCode status, byte[] payload) = _responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new ByteArrayContent(payload) };
        }
    }
}
