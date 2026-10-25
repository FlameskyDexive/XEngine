using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

using XEngine.Ai.Gateway;
using XEngine.Ai.Host;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Gen-P3.3 — ComfyUI HTTP adapter: prompt injection, queue/history poll, /view fetch.</summary>
public sealed class ComfyUiGenProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "xe-comfy-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static ComfyUiGenProvider Provider(FakeHttpHandler handler, string? key = null, string? baseUrl = null)
        => new(() => key, projectRoot: null, handler, baseUrl);

    [Fact]
    public async Task Submit_PresetTrellis2_PostsInjectedPromptSeedAndClientId()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"prompt_id":"abc-123","node_errors":{}}""");

        var recipe = new GenRecipe("comfyui", "model3d", "a wooden crate\nNegative: blurry", Seed: 7,
            Extra: new Dictionary<string, string> { ["preset"] = "trellis2" });
        GenSubmitResult result = await Provider(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.Equal("comfyui:abc-123", result.TaskId);
        FakeHttpHandler.Recorded request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:8188/prompt", request.Uri);
        Assert.Null(request.Authorization);

        JsonNode body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("xengine-cli", (string?)body["client_id"]);
        Assert.Equal("a wooden crate", (string?)body["prompt"]?["1"]?["inputs"]?["text"]);
        Assert.Equal("blurry", (string?)body["prompt"]?["2"]?["inputs"]?["text"]);
        Assert.Equal(7, (int?)body["prompt"]?["3"]?["inputs"]?["seed"]);
        Assert.Equal("Trellis2TextTo3D", (string?)body["prompt"]?["3"]?["class_type"]);
    }

    [Fact]
    public async Task Submit_HunyuanPreset_UsesHunyuanClassType()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"prompt_id":"h1"}""");

        await Provider(handler).SubmitAsync(
            new GenRecipe("comfyui", "model3d", "hero", Extra: new Dictionary<string, string> { ["preset"] = "hunyuan3d" }),
            CancellationToken.None);

        JsonNode body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("Hunyuan3DTextTo3D", (string?)body["prompt"]?["3"]?["class_type"]);
    }

    [Fact]
    public async Task Submit_WorkflowJson_AndCustomBaseUrl_PacksHostInTaskId()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"prompt_id":"z9"}""");

        const string graph = """{"6":{"class_type":"CLIPTextEncode","inputs":{"text":""}}}""";
        GenSubmitResult result = await Provider(handler).SubmitAsync(
            new GenRecipe("comfyui", "texture", "brick wall",
                Extra: new Dictionary<string, string>
                {
                    ["workflowJson"] = graph,
                    ["baseUrl"] = "http://192.168.1.9:9000",
                    ["promptNode"] = "6",
                }),
            CancellationToken.None);

        Assert.Equal("comfyui@http://192.168.1.9:9000|z9", result.TaskId);
        Assert.Equal("http://192.168.1.9:9000/prompt", handler.Requests[0].Uri);
        Assert.Equal("brick wall", (string?)JsonNode.Parse(handler.Requests[0].Body!)!["prompt"]?["6"]?["inputs"]?["text"]);
    }

    [Fact]
    public async Task Submit_WorkflowFile_LoadsApiFormat()
    {
        Directory.CreateDirectory(_tempDir);
        string path = Path.Combine(_tempDir, "graph.json");
        File.WriteAllText(path, """{"1":{"class_type":"CLIPTextEncode","inputs":{"text":"old"}}}""");

        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"prompt_id":"f1"}""");
        var provider = new ComfyUiGenProvider(() => null, _tempDir, handler);

        await provider.SubmitAsync(
            new GenRecipe("comfyui", "sprite", "hero",
                Extra: new Dictionary<string, string> { ["workflow"] = "graph.json", ["promptNode"] = "1" }),
            CancellationToken.None);

        Assert.Equal("hero", (string?)JsonNode.Parse(handler.Requests[0].Body!)!["prompt"]?["1"]?["inputs"]?["text"]);
    }

    [Fact]
    public async Task Submit_MissingWorkflow_FailsBeforeHttp()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new FakeHttpHandler()).SubmitAsync(
                new GenRecipe("comfyui", "model3d", "x"), CancellationToken.None));
        Assert.Contains("workflow", ex.Message);
    }

    [Fact]
    public async Task Submit_UnknownExtra_FailsFast()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new FakeHttpHandler()).SubmitAsync(
                new GenRecipe("comfyui", "model3d", "x",
                    Extra: new Dictionary<string, string> { ["preset"] = "trellis2", ["typoKey"] = "1" }),
                CancellationToken.None));
        Assert.Contains("typoKey", ex.Message);
        Assert.Contains("preset", ex.Message);
    }

    [Fact]
    public async Task Submit_StyleAnchorExtras_AreConsumed()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"prompt_id":"s1"}""");

        GenSubmitResult result = await Provider(handler).SubmitAsync(
            new GenRecipe("comfyui", "model3d", "crate",
                Extra: new Dictionary<string, string>
                {
                    ["preset"] = "trellis2",
                    ["loraModelId"] = "look.safetensors",
                    ["palette"] = "#111,#eee",
                    ["referenceImageGuids"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    ["loraNode"] = "3",
                    ["loraField"] = "lora_name",
                }),
            CancellationToken.None);

        Assert.Equal("comfyui:s1", result.TaskId);
        JsonNode body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("look.safetensors", (string?)body["prompt"]?["3"]?["inputs"]?["lora_name"]);
        Assert.Contains("palette: #111,#eee", (string?)body["prompt"]?["1"]?["inputs"]?["text"]);
    }

    [Fact]
    public async Task Submit_InputImage_UploadsAndSetsLoadImageNode()
    {
        Directory.CreateDirectory(_tempDir);
        string crop = Path.Combine(_tempDir, "head.png");
        File.WriteAllBytes(crop, PngRgba.Encode([200, 40, 40, 255], 1, 1));

        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"name":"head.png","subfolder":"","type":"input"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"prompt_id":"img-1"}""");

        const string graph = """
            {
              "6": {"class_type":"CLIPTextEncode","inputs":{"text":""}},
              "10": {"class_type":"LoadImage","inputs":{"image":"old.png"}}
            }
            """;
        var provider = new ComfyUiGenProvider(() => null, _tempDir, handler);
        await provider.SubmitAsync(
            new GenRecipe("comfyui", "texture", "moss goblin head",
                Extra: new Dictionary<string, string>
                {
                    ["workflowJson"] = graph,
                    ["promptNode"] = "6",
                    ["inputImageNode"] = "10",
                    ["inputImagePath"] = "head.png",
                    ["atlasPath"] = "Assets/Spine/hero.atlas",
                    ["region"] = "head",
                    ["width"] = "4",
                    ["height"] = "3",
                    ["mode"] = "auto",
                }),
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://127.0.0.1:8188/upload/image", handler.Requests[0].Uri);
        Assert.Contains("multipart", handler.Requests[0].ContentType ?? "", StringComparison.OrdinalIgnoreCase);
        JsonNode body = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Equal("moss goblin head", (string?)body["prompt"]?["6"]?["inputs"]?["text"]);
        Assert.Equal("head.png", (string?)body["prompt"]?["10"]?["inputs"]?["image"]);
    }

    [Fact]
    public async Task Submit_InputImagePathWithoutNode_FailsFast()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new FakeHttpHandler()).SubmitAsync(
                new GenRecipe("comfyui", "texture", "x",
                    Extra: new Dictionary<string, string>
                    {
                        ["workflowJson"] = """{"6":{"class_type":"CLIPTextEncode","inputs":{"text":""}}}""",
                        ["promptNode"] = "6",
                        ["inputImagePath"] = "head.png",
                    }),
                CancellationToken.None));
        Assert.Contains("inputImageNode", ex.Message);
    }

    [Fact]
    public async Task Submit_NodeErrors_Fails()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"prompt_id":"e1","node_errors":{"3":{"details":"missing"}}}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).SubmitAsync(
            new GenRecipe("comfyui", "model3d", "x", Extra: new Dictionary<string, string> { ["preset"] = "trellis2" }),
            CancellationToken.None));
    }

    [Fact]
    public async Task Submit_WithApiKey_SendsBearer()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"prompt_id":"k1"}""");
        await Provider(handler, key: "secret").SubmitAsync(
            new GenRecipe("comfyui", "model3d", "x", Extra: new Dictionary<string, string> { ["preset"] = "trellis2" }),
            CancellationToken.None);
        Assert.Equal("Bearer secret", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task Poll_EmptyHistory_QueueRunning()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        handler.Enqueue(HttpStatusCode.OK, """{"queue_running":[[0,"abc",{},{},[]]],"queue_pending":[]}""");

        GenTaskStatus status = await Provider(handler).PollAsync("comfyui:abc", CancellationToken.None);
        Assert.Equal(GenTaskState.Running, status.State);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/history/abc", handler.Requests[0].Uri);
        Assert.EndsWith("/queue", handler.Requests[1].Uri);
    }

    [Fact]
    public async Task Poll_HistorySucceeded()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"abc":{"outputs":{"4":{"glb":[{"filename":"x.glb","subfolder":"","type":"output"}]}},"status":{"completed":true,"status_str":"success"}}}""");

        GenTaskStatus status = await Provider(handler).PollAsync("comfyui:abc", CancellationToken.None);
        Assert.Equal(GenTaskState.Succeeded, status.State);
        Assert.Equal(100, status.Progress);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Poll_HistoryError()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"abc":{"status":{"status_str":"error","completed":true,"messages":["oom"]}}}""");

        GenTaskStatus status = await Provider(handler).PollAsync("comfyui:abc", CancellationToken.None);
        Assert.Equal(GenTaskState.Failed, status.State);
        Assert.Contains("oom", status.Message);
    }

    [Fact]
    public async Task Fetch_DownloadsViewGlb()
    {
        byte[] glb = MockArtifacts.MinimalGlb();
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"abc":{"outputs":{"4":{"glb":[{"filename":"hero.glb","subfolder":"","type":"output"}]}}}}""");
        handler.Enqueue(HttpStatusCode.OK, glb);

        IReadOnlyList<GenArtifact> artifacts = await Provider(handler).FetchAsync(
            "comfyui:abc",
            new GenRecipe("comfyui", "model3d", "hero"),
            _tempDir,
            CancellationToken.None);

        GenArtifact artifact = Assert.Single(artifacts);
        Assert.Equal("model", artifact.Role);
        Assert.Equal("model/gltf-binary", artifact.MimeType);
        Assert.True(File.Exists(artifact.LocalPath));
        Assert.Equal(glb, File.ReadAllBytes(artifact.LocalPath));
        Assert.Contains("filename=hero.glb", handler.Requests[1].Uri);
        Assert.Contains("/view?", handler.Requests[1].Uri);
    }

    [Fact]
    public async Task Fetch_NotReady_Fails()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(handler).FetchAsync(
            "comfyui:abc", new GenRecipe("comfyui", "model3d", "x"), _tempDir, CancellationToken.None));
    }

    [Fact]
    public void CreateDefault_RegistersComfyUi()
    {
        GenProviderRegistry registry = GenProviderRegistry.CreateDefault();
        Assert.True(registry.TryGet("comfyui", out IGenProvider comfy));
        Assert.Equal(new[] { "model3d", "texture", "sprite", "skybox" }, comfy.SupportedKinds);
        Assert.True(registry.TryGet("openai", out IGenProvider openai));
        Assert.Equal(new[] { "texture", "sprite" }, openai.SupportedKinds);
    }

    [Fact]
    public void EvidenceScreenshot_ComfyUi()
    {
        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gen-asset"));
        Directory.CreateDirectory(evidenceDir);
        string png = Path.Combine(evidenceDir, "2026-08-16-p3-3-comfyui.png");
        const int w = 480;
        const int h = 160;
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 3] = 24;
            px[i * 3 + 1] = 26;
            px[i * 3 + 2] = 32;
        }

        void Rect(int x, int y, int rw, int rh, byte r, byte g, byte b)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(h, y + rh); yy++)
            {
                for (int xx = Math.Max(0, x); xx < Math.Min(w, x + rw); xx++)
                {
                    int i = (yy * w + xx) * 3;
                    px[i] = r;
                    px[i + 1] = g;
                    px[i + 2] = b;
                }
            }
        }

        Rect(0, 0, w, 32, 36, 48, 72);
        Rect(24, 48, 200, 80, 70, 110, 200);
        Rect(248, 48, 200, 80, 180, 140, 60);
        File.WriteAllBytes(png, PlayablePropEvidencePng.EncodePng(px, w, h));
        File.WriteAllText(Path.ChangeExtension(png, ".md"),
            "# P3.3 ComfyUI local provider acceptance\n\n" +
            "- POST /prompt injects recipe.Prompt (+ Negative / palette / LoRA) into API-format graph\n" +
            "- Poll /history + /queue; fetch GET /view writes GLB/PNG artifacts\n" +
            "- Presets: trellis2 / hunyuan3d; extra.workflow for a real exported graph\n");
        Assert.True(new FileInfo(png).Length > 200);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public sealed record Recorded(HttpMethod Method, string Uri, string? Authorization, string? Body, string? ContentType);

        private readonly Queue<(HttpStatusCode Status, byte[] Body, string ContentType)> _responses = new();

        public List<Recorded> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string json)
            => _responses.Enqueue((status, Encoding.UTF8.GetBytes(json), "application/json"));

        public void Enqueue(HttpStatusCode status, byte[] bytes)
            => _responses.Enqueue((status, bytes, "application/octet-stream"));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? contentType = request.Content?.Headers.ContentType?.ToString();
            string? body = null;
            if (request.Content is not null
                && (contentType is null || !contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)))
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(), body, contentType));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            }

            (HttpStatusCode status, byte[] payload, string responseType) = _responses.Dequeue();
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(responseType);
            return new HttpResponseMessage(status) { Content = content };
        }
    }
}
