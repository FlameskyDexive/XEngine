using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

using XEngine.Ai.Gateway;
using XEngine.Ai.Host;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ M10g — OpenAI GPT Image 2 adapter: generations vs edits, size pick, fail-fast extras.</summary>
public sealed class OpenAiImageGenProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "xe-openai-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private OpenAiImageGenProvider Provider(FakeHttpHandler handler, string? key = "sk-test")
        => new(() => key, _tempDir, _tempDir, handler);

    [Fact]
    public async Task Submit_Generations_PostsPromptSizeAndModel()
    {
        byte[] png = PngRgba.Encode([1, 2, 3, 255], 1, 1);
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK,
            "{\"created\":1,\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}");

        var recipe = new GenRecipe("openai", "texture", "brick wall", Model: "gpt-image-2",
            Extra: new Dictionary<string, string>
            {
                ["quality"] = "low",
                ["size"] = "1024x1024",
            });
        GenSubmitResult result = await Provider(handler).SubmitAsync(recipe, CancellationToken.None);

        Assert.StartsWith("openai-", result.TaskId);
        FakeHttpHandler.Recorded request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/images/generations", request.Uri);
        Assert.Equal("Bearer sk-test", request.Authorization);
        JsonNode body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("gpt-image-2", (string?)body["model"]);
        Assert.Equal("brick wall", (string?)body["prompt"]);
        Assert.Equal("1024x1024", (string?)body["size"]);
        Assert.Equal("low", (string?)body["quality"]);
        Assert.Equal("png", (string?)body["output_format"]);
    }

    [Fact]
    public async Task Submit_InputImage_PostsEditsMultipart()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllBytes(Path.Combine(_tempDir, "crop.png"), PngRgba.Encode([9, 8, 7, 255], 1, 1));
        byte[] png = PngRgba.Encode([4, 5, 6, 255], 1, 1);
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK,
            "{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}");

        await Provider(handler).SubmitAsync(
            new GenRecipe("openai", "texture", "moss goblin head",
                Extra: new Dictionary<string, string>
                {
                    ["inputImagePath"] = "crop.png",
                    ["atlasPath"] = "Assets/Spine/hero.atlas",
                    ["region"] = "head",
                    ["width"] = "4",
                    ["height"] = "3",
                    ["mode"] = "auto",
                }),
            CancellationToken.None);

        FakeHttpHandler.Recorded request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/images/edits", request.Uri);
        Assert.Contains("multipart", request.ContentType ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_UnknownExtra_FailsFast()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new FakeHttpHandler()).SubmitAsync(
                new GenRecipe("openai", "texture", "x",
                    Extra: new Dictionary<string, string> { ["typoKey"] = "1" }),
                CancellationToken.None));
        Assert.Contains("typoKey", ex.Message);
        Assert.Contains("inputImagePath", ex.Message);
    }

    [Fact]
    public async Task Submit_MissingKey_FailsBeforeHttp()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(new FakeHttpHandler(), key: null).SubmitAsync(
                new GenRecipe("openai", "texture", "x"), CancellationToken.None));
    }

    [Fact]
    public async Task Submit_TransparentOnGptImage2_FailsFast()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new FakeHttpHandler()).SubmitAsync(
                new GenRecipe("openai", "sprite", "icon",
                    Extra: new Dictionary<string, string> { ["background"] = "transparent" }),
                CancellationToken.None));
        Assert.Contains("transparent", ex.Message);
    }

    [Fact]
    public void ChooseSize_SmallRegion_MeetsGptImage2Constraints()
    {
        string size = OpenAiImageGenProvider.ChooseSize(103, 66, requested: null);
        string[] parts = size.Split('x');
        int w = int.Parse(parts[0]);
        int h = int.Parse(parts[1]);
        Assert.Equal(0, w % 16);
        Assert.Equal(0, h % 16);
        Assert.True((long)w * h >= OpenAiImageGenProvider.MinPixels);
        Assert.True(Math.Max(w, h) <= Math.Min(w, h) * OpenAiImageGenProvider.MaxAspect);
    }

    [Fact]
    public void ChooseSize_RequestedWins()
        => Assert.Equal("1024x1536", OpenAiImageGenProvider.ChooseSize(4, 3, "1024x1536"));

    [Fact]
    public void ChooseSize_MissingDims_DefaultsSquare()
        => Assert.Equal("1024x1024", OpenAiImageGenProvider.ChooseSize(null, null, null));

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public sealed record Recorded(HttpMethod Method, string Uri, string? Authorization, string? Body, string? ContentType);

        private readonly Queue<(HttpStatusCode Status, byte[] Body, string ContentType)> _responses = new();

        public List<Recorded> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string json)
            => _responses.Enqueue((status, Encoding.UTF8.GetBytes(json), "application/json"));

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

            (HttpStatusCode status, byte[] payload, string type) = _responses.Dequeue();
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return new HttpResponseMessage(status) { Content = content };
        }
    }
}
