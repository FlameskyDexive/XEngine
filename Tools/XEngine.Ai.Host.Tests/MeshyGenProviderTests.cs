using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Gateway;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Gen-P1.3 — Meshy adapter contract: request routing/bodies per kind+extra, task-id
/// namespacing ("family:remoteId"), status mapping, signed-URL downloads without auth, and the
/// fail-fast paths (missing key, unknown extras, non-2xx, unfinished fetch). All HTTP is faked.</summary>
public sealed class MeshyGenProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "xe-meshy-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static MeshyGenProvider Provider(FakeHttpHandler handler, string? key = "msy_test_key")
        => new(() => key, handler);

    // ---- submit routing -----------------------------------------------------------------------

    [Fact]
    public async Task Submit_TextTo3dPreview_PostsPromptModelSeedAndCoercedOptions()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":"abc123"}""");

        var recipe = new GenRecipe("meshy", "model3d", "a rusty iron sword", Model: "meshy-6", Seed: 42,
            Extra: new Dictionary<string, string>
            {
                ["topology"] = "quad",
                ["targetPolycount"] = "30000",
                ["shouldRemesh"] = "true",
                ["symmetry"] = "on",
                ["artStyle"] = "realistic",
            });
        GenSubmitResult result = await Provider(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal("t23d:abc123", result.TaskId);
        FakeHttpHandler.Recorded request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.meshy.ai/openapi/v2/text-to-3d", request.Uri);
        Assert.Equal("Bearer msy_test_key", request.Authorization);

        JsonNode body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("preview", (string?)body["mode"]);
        Assert.Equal("a rusty iron sword", (string?)body["prompt"]);
        Assert.Equal("meshy-6", (string?)body["ai_model"]);
        Assert.Equal(42, (int?)body["seed"]);
        Assert.Equal("quad", (string?)body["topology"]);
        Assert.Equal(30000, (int?)body["target_polycount"]); // int, not string
        Assert.Equal(true, (bool?)body["should_remesh"]);    // bool, not string
        Assert.Equal("on", (string?)body["symmetry_mode"]);
        Assert.Equal("realistic", (string?)body["art_style"]);
    }

    [Fact]
    public async Task Submit_Refine_PostsPreviewTaskId_StrippingFamilyPrefix()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":"ref456"}""");

        var recipe = new GenRecipe("meshy", "model3d", "a rusty iron sword",
            Extra: new Dictionary<string, string>
            {
                ["mode"] = "refine",
                ["previewTaskId"] = "t23d:abc123",
                ["enablePbr"] = "true",
                ["textureResolution"] = "2k",
            });
        GenSubmitResult result = await Provider(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal("t23d:ref456", result.TaskId);
        JsonNode body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("refine", (string?)body["mode"]);
        Assert.Equal("abc123", (string?)body["preview_task_id"]); // prefix stripped
        Assert.Equal(true, (bool?)body["enable_pbr"]);
        Assert.Equal("2k", (string?)body["texture_resolution"]);
        Assert.Null(body["prompt"]); // refine carries no prompt
    }

    [Fact]
    public async Task Submit_ImageUrl_RoutesToImageTo3d()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":"img789"}""");

        var recipe = new GenRecipe("meshy", "model3d", "concept art prop",
            Extra: new Dictionary<string, string>
            {
                ["imageUrl"] = "https://example.com/concept.png",
                ["enablePbr"] = "true",
            });
        GenSubmitResult result = await Provider(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal("i23d:img789", result.TaskId);
        Assert.Equal("https://api.meshy.ai/openapi/v1/image-to-3d", handler.Requests[0].Uri);
        JsonNode body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("https://example.com/concept.png", (string?)body["image_url"]);
        Assert.Null(body["mode"]);
        Assert.Null(body["prompt"]); // image-to-3d has no prompt field
    }

    [Fact]
    public async Task Submit_Remesh_RoutesToRemesh_WithCsvTargetFormats()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":"rm1"}""");

        var recipe = new GenRecipe("meshy", "model3d", "n/a",
            Extra: new Dictionary<string, string>
            {
                ["mode"] = "remesh",
                ["inputTaskId"] = "t23d:abc123",
                ["targetFormats"] = "glb,fbx",
                ["targetPolycount"] = "5000",
            });
        GenSubmitResult result = await Provider(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal("remesh:rm1", result.TaskId);
        Assert.Equal("https://api.meshy.ai/openapi/v1/remesh", handler.Requests[0].Uri);
        JsonNode body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("abc123", (string?)body["input_task_id"]);
        Assert.Equal(5000, (int?)body["target_polycount"]);
        JsonArray formats = Assert.IsType<JsonArray>(body["target_formats"]);
        Assert.Equal(new[] { "glb", "fbx" }, formats.Select(n => (string?)n).ToArray());
    }

    [Fact]
    public async Task Submit_MotionRig_RoutesToRigging()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":"rig1"}""");

        var recipe = new GenRecipe("meshy", "motion", "rig my character",
            Extra: new Dictionary<string, string>
            {
                ["inputTaskId"] = "t23d:abc123",
                ["heightMeters"] = "1.8",
            });
        GenSubmitResult result = await Provider(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal("rig:rig1", result.TaskId);
        Assert.Equal("https://api.meshy.ai/openapi/v1/rigging", handler.Requests[0].Uri);
        JsonNode body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("abc123", (string?)body["input_task_id"]);
        Assert.Equal(1.8, (double?)body["height_meters"]);
    }

    [Fact]
    public async Task Submit_MotionAnimation_RequiresActionId_AndRoutesToAnimations()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":"anim1"}""");

        var recipe = new GenRecipe("meshy", "motion", "walk cycle",
            Extra: new Dictionary<string, string>
            {
                ["rigTaskId"] = "rig:rig1",
                ["actionId"] = "17",
            });
        GenSubmitResult result = await Provider(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal("anim:anim1", result.TaskId);
        Assert.Equal("https://api.meshy.ai/openapi/v1/animations", handler.Requests[0].Uri);
        JsonNode body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("rig1", (string?)body["rig_task_id"]);
        Assert.Equal(17, (int?)body["action_id"]);

        // Missing actionId fails before any HTTP.
        var missing = new GenRecipe("meshy", "motion", "walk",
            Extra: new Dictionary<string, string> { ["rigTaskId"] = "rig:rig1" });
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new FakeHttpHandler()).SubmitAsync(missing, CancellationToken.None));
        Assert.Contains("actionId", ex.Message);
    }

    [Fact]
    public async Task Submit_UnknownExtraKey_FailsListingSupportedKeys()
    {
        var recipe = new GenRecipe("meshy", "model3d", "a sword",
            Extra: new Dictionary<string, string> { ["targetPolyCount"] = "5000" }); // typo'd casing
        var handler = new FakeHttpHandler();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(handler).SubmitAsync(recipe, CancellationToken.None));

        Assert.Contains("targetPolyCount", ex.Message);
        Assert.Contains("targetPolycount", ex.Message); // the supported spelling is suggested
        Assert.Empty(handler.Requests); // failed before spending credits
    }

    [Fact]
    public async Task Submit_WithoutApiKey_FailsWithSetupGuidance()
    {
        var recipe = new GenRecipe("meshy", "model3d", "a sword");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(new FakeHttpHandler(), key: null).SubmitAsync(recipe, CancellationToken.None));

        Assert.Contains("MESHY_API_KEY", ex.Message);
        Assert.Contains("secrets.json", ex.Message);
    }

    [Fact]
    public async Task Submit_HttpError_SurfacesStatusAndBody()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.PaymentRequired, """{"message":"insufficient credits"}""");

        var recipe = new GenRecipe("meshy", "model3d", "a sword");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(handler).SubmitAsync(recipe, CancellationToken.None));

        Assert.Contains("402", ex.Message);
        Assert.Contains("insufficient credits", ex.Message);
    }

    // ---- poll ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("PENDING", GenTaskState.Pending)]
    [InlineData("IN_PROGRESS", GenTaskState.Running)]
    [InlineData("SUCCEEDED", GenTaskState.Succeeded)]
    [InlineData("FAILED", GenTaskState.Failed)]
    [InlineData("CANCELED", GenTaskState.Failed)]
    public async Task Poll_MapsMeshyStatuses(string meshyStatus, GenTaskState expected)
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"id":"abc123","status":"__S__","progress":55,"task_error":{"message":""}}"""
                .Replace("__S__", meshyStatus));

        GenTaskStatus status = await Provider(handler).PollAsync("t23d:abc123", CancellationToken.None);

        Assert.Equal(expected, status.State);
        Assert.Equal(55, status.Progress);
        Assert.Equal("https://api.meshy.ai/openapi/v2/text-to-3d/abc123", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task Poll_FailedTask_CarriesTaskErrorMessage()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"id":"abc","status":"FAILED","progress":0,"task_error":{"message":"prompt rejected"}}""");

        GenTaskStatus status = await Provider(handler).PollAsync("t23d:abc", CancellationToken.None);

        Assert.Equal(GenTaskState.Failed, status.State);
        Assert.Equal("prompt rejected", status.Message);
    }

    [Fact]
    public async Task Poll_RoutesFamiliesToTheirEndpoints()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"status":"PENDING"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"status":"PENDING"}""");

        await Provider(handler).PollAsync("rig:r1", CancellationToken.None);
        await Provider(handler).PollAsync("anim:a1", CancellationToken.None);

        // Provider() creates a fresh HttpClient per instance but both used the same handler queue.
        Assert.Equal("https://api.meshy.ai/openapi/v1/rigging/r1", handler.Requests[0].Uri);
        Assert.Equal("https://api.meshy.ai/openapi/v1/animations/a1", handler.Requests[1].Uri);
    }

    [Fact]
    public async Task Poll_UnnamespacedTaskId_Fails()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new FakeHttpHandler()).PollAsync("abc123", CancellationToken.None));
    }

    // ---- fetch ---------------------------------------------------------------------------------

    [Fact]
    public async Task Fetch_DownloadsGlb_WithoutAuthHeaderOnSignedUrl()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"abc","status":"SUCCEEDED","progress":100,
             "model_urls":{"glb":"https://assets.meshy.ai/tasks/abc/model.glb?Expires=1"},
             "thumbnail_url":"https://assets.meshy.ai/tasks/abc/preview.png?Expires=1"}
            """);
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 0x67, 0x6C, 0x54, 0x46 }); // "glTF"

        var recipe = new GenRecipe("meshy", "model3d", "a sword");
        IReadOnlyList<GenArtifact> artifacts = await Provider(handler)
            .FetchAsync("t23d:abc", recipe, _tempDir, CancellationToken.None);

        GenArtifact model = Assert.Single(artifacts); // thumbnail NOT fetched by default
        Assert.Equal("model", model.Role);
        Assert.Equal(new byte[] { 0x67, 0x6C, 0x54, 0x46 }, File.ReadAllBytes(model.LocalPath));

        Assert.Equal("Bearer msy_test_key", handler.Requests[0].Authorization); // API call: auth
        Assert.Null(handler.Requests[1].Authorization);                          // signed URL: no auth
    }

    [Fact]
    public async Task Fetch_WithFetchThumbnail_AddsThumbnailArtifact()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"abc","status":"SUCCEEDED",
             "model_urls":{"glb":"https://assets.meshy.ai/m.glb"},
             "thumbnail_url":"https://assets.meshy.ai/p.png"}
            """);
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 1 });
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 2 });

        var recipe = new GenRecipe("meshy", "model3d", "a sword",
            Extra: new Dictionary<string, string> { ["fetchThumbnail"] = "true" });
        IReadOnlyList<GenArtifact> artifacts = await Provider(handler)
            .FetchAsync("t23d:abc", recipe, _tempDir, CancellationToken.None);

        Assert.Equal(2, artifacts.Count);
        Assert.Equal(new[] { "model", "thumbnail" }, artifacts.Select(a => a.Role).ToArray());
    }

    [Fact]
    public async Task Fetch_RigTask_DownloadsCharacterAndBasicAnimations()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"r1","status":"SUCCEEDED","result":{
               "rigged_character_glb_url":"https://assets.meshy.ai/rigged.glb",
               "basic_animations":{
                 "walking_glb_url":"https://assets.meshy.ai/walk.glb",
                 "running_glb_url":"https://assets.meshy.ai/run.glb"}}}
            """);
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 1 });
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 2 });
        handler.Enqueue(HttpStatusCode.OK, new byte[] { 3 });

        var recipe = new GenRecipe("meshy", "motion", "rig it");
        IReadOnlyList<GenArtifact> artifacts = await Provider(handler)
            .FetchAsync("rig:r1", recipe, _tempDir, CancellationToken.None);

        Assert.Equal(new[] { "model", "walk", "run" }, artifacts.Select(a => a.Role).ToArray());
        Assert.All(artifacts, a => Assert.True(File.Exists(a.LocalPath)));
    }

    [Fact]
    public async Task Fetch_UnfinishedTask_Fails()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"abc","status":"IN_PROGRESS","progress":40}""");

        var recipe = new GenRecipe("meshy", "model3d", "a sword");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(handler).FetchAsync("t23d:abc", recipe, _tempDir, CancellationToken.None));

        Assert.Contains("IN_PROGRESS", ex.Message);
    }

    [Fact]
    public async Task Fetch_SucceededWithoutGlb_Fails()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"abc","status":"SUCCEEDED","model_urls":{"fbx":"https://x/f.fbx"}}""");

        var recipe = new GenRecipe("meshy", "model3d", "a sword");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(handler).FetchAsync("t23d:abc", recipe, _tempDir, CancellationToken.None));

        Assert.Contains("no GLB", ex.Message);
    }

    // ---- registry ------------------------------------------------------------------------------

    [Fact]
    public void CreateDefault_RegistersMeshyAlongsideMock()
    {
        GenProviderRegistry registry = GenProviderRegistry.CreateDefault();

        Assert.True(registry.TryGet("meshy", out IGenProvider meshy));
        Assert.Equal(new[] { "model3d", "motion" }, meshy.SupportedKinds);
        Assert.True(registry.TryGet("mock", out _));
    }

    // ---- fake HTTP -----------------------------------------------------------------------------

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public sealed record Recorded(HttpMethod Method, string Uri, string? Authorization, string? Body);

        private readonly Queue<(HttpStatusCode Status, byte[] Body, string ContentType)> _responses = new();

        public List<Recorded> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string json)
            => _responses.Enqueue((status, System.Text.Encoding.UTF8.GetBytes(json), "application/json"));

        public void Enqueue(HttpStatusCode status, byte[] bytes)
            => _responses.Enqueue((status, bytes, "application/octet-stream"));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(), body));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            }

            (HttpStatusCode status, byte[] payload, string contentType) = _responses.Dequeue();
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(status) { Content = content };
        }
    }
}
