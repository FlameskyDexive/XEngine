using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ M10g — OpenAI Images API adapter (https://developers.openai.com/api/docs/guides/image-generation).
/// Default model is <c>gpt-image-2</c>. Synchronous: POST /v1/images/generations (text) or
/// /v1/images/edits (multipart, when extra.inputImagePath is set) returns <c>data[].b64_json</c>,
/// so this rides <see cref="CachedResultGenProvider"/>.
///
/// Extra keys: model, size, quality, background, outputFormat, outputCompression, moderation,
/// inputImagePath, plus Spine atlas restyle keys (atlasPath/region/width/height/…).
/// gpt-image-2 rejects <c>background=transparent</c> and requires size edges that are multiples of
/// 16 with at least 655360 pixels — Host pack-crop resizes the result down to the unrotated region.
/// Auth: <c>OPENAI_API_KEY</c> or <c>.xengine/secrets.json</c> <c>openai</c>.
/// </summary>
public sealed class OpenAiImageGenProvider : CachedResultGenProvider
{
    public const string ProviderName = "openai";
    public const string DefaultModel = "gpt-image-2";
    public const string DefaultBaseUrl = "https://api.openai.com";

    internal const int MinPixels = 655_360;
    internal const int MaxPixels = 8_294_400;
    internal const int MaxEdge = 3840;
    internal const int MaxAspect = 3;

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _baseUrl;
    private readonly string? _projectRoot;

    public OpenAiImageGenProvider(
        Func<string?> apiKeyResolver,
        string cacheRoot,
        string? projectRoot = null,
        HttpMessageHandler? handler = null,
        string? baseUrl = null)
        : base(cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(apiKeyResolver);
        _apiKey = apiKeyResolver;
        _projectRoot = projectRoot;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public override string Name => ProviderName;

    public override IReadOnlyList<string> SupportedKinds { get; } =
        new[] { GenRecipe.Kinds.Texture, GenRecipe.Kinds.Sprite };

    protected override async Task<IReadOnlyList<(string, byte[], string, string)>> GenerateAsync(
        GenRecipe recipe, CancellationToken cancellationToken)
    {
        string key = _apiKey() ?? throw new InvalidOperationException(
            $"No OpenAI API key. Set the {SecretResolver.EnvVarName(ProviderName)} environment variable " +
            "or add {{\"openai\": \"...\"}} to .xengine/secrets.json.");

        var extra = new ExtraReader(recipe.Extra);
        extra.Take("loraModelId");
        extra.Take("referenceImageGuids");
        string? palette = extra.Take("palette");
        string prompt = recipe.Prompt;
        if (!string.IsNullOrWhiteSpace(palette))
        {
            prompt += " palette: " + palette;
        }

        string model = recipe.Model ?? extra.Take("model") ?? DefaultModel;
        extra.Take("model");
        string? quality = extra.Take("quality") ?? "medium";
        string? background = extra.Take("background");
        string outputFormat = extra.Take("outputFormat") ?? "png";
        string? outputCompression = extra.Take("outputCompression");
        string? moderation = extra.Take("moderation");
        string? requestedSize = extra.Take("size");
        string? inputImagePath = extra.Take("inputImagePath");
        int? width = extra.TakeInt("width");
        int? height = extra.TakeInt("height");
        SpineAtlasGenExtras.Consume(extra.Take);
        extra.ThrowIfUnconsumed();

        if (string.Equals(model, DefaultModel, StringComparison.OrdinalIgnoreCase)
            && string.Equals(background, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "gpt-image-2 does not support extra.background=transparent. Omit background or use an older gpt-image-1 model.");
        }

        string size = ChooseSize(width, height, requestedSize);
        byte[] png = string.IsNullOrWhiteSpace(inputImagePath)
            ? await GenerateAsync(key, model, prompt, size, quality, background, outputFormat,
                outputCompression, moderation, cancellationToken).ConfigureAwait(false)
            : await EditAsync(key, model, prompt, size, quality, background, outputFormat,
                outputCompression, moderation, inputImagePath, cancellationToken).ConfigureAwait(false);

        string role = recipe.Kind == GenRecipe.Kinds.Sprite ? "sprite" : "texture";
        return new[] { (role + ".png", png, "image/png", role) };
    }

    private async Task<byte[]> GenerateAsync(
        string key, string model, string prompt, string size, string? quality, string? background,
        string outputFormat, string? outputCompression, string? moderation, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["n"] = 1,
            ["size"] = size,
            ["output_format"] = outputFormat,
        };
        PutOptional(body, "quality", quality);
        PutOptional(body, "background", background);
        PutOptional(body, "moderation", moderation);
        if (outputCompression is not null)
        {
            body["output_compression"] = ParseInt("outputCompression", outputCompression);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/images/generations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendImageAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> EditAsync(
        string key, string model, string prompt, string size, string? quality, string? background,
        string outputFormat, string? outputCompression, string? moderation, string imagePath,
        CancellationToken cancellationToken)
    {
        string absolute = Path.IsPathRooted(imagePath)
            ? imagePath
            : Path.Combine(_projectRoot ?? Environment.CurrentDirectory,
                imagePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
        {
            throw new FileNotFoundException($"OpenAI edit image not found: {absolute}");
        }

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(prompt), "prompt");
        content.Add(new StringContent(size), "size");
        content.Add(new StringContent(outputFormat), "output_format");
        if (!string.IsNullOrWhiteSpace(quality))
            content.Add(new StringContent(quality), "quality");
        if (!string.IsNullOrWhiteSpace(background))
            content.Add(new StringContent(background), "background");
        if (!string.IsNullOrWhiteSpace(moderation))
            content.Add(new StringContent(moderation), "moderation");
        if (outputCompression is not null)
            content.Add(new StringContent(outputCompression), "output_compression");

        byte[] bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "image", Path.GetFileName(absolute));

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/images/edits")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await SendImageAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> SendImageAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI Images API failed ({(int)response.StatusCode}): {Truncate(payload)}");
        }

        JsonNode json = JsonNode.Parse(payload)
            ?? throw new InvalidOperationException("OpenAI Images API returned empty JSON.");
        JsonNode? first = json["data"] is JsonArray { Count: > 0 } data ? data[0] : null;
        string? b64 = first?["b64_json"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(b64))
        {
            return Convert.FromBase64String(b64);
        }

        string? url = first?["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("OpenAI Images API response had no data[0].b64_json or url: " + Truncate(payload));
        }

        using var download = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage image = await _http.SendAsync(download, cancellationToken).ConfigureAwait(false);
        if (!image.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI image URL download failed ({(int)image.StatusCode}).");
        }

        return await image.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pick a gpt-image-2-legal <c>WxH</c>. Small atlas regions scale up (min 655360 px,
    /// edges multiples of 16); Host pack-crop scales the result back down.</summary>
    internal static string ChooseSize(int? width, int? height, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim();

        if (width is not int w || height is not int h || w <= 0 || h <= 0)
            return "1024x1024";

        if (IsValidSize(w, h))
            return w + "x" + h;

        double aspect = (double)w / h;
        if (aspect > MaxAspect)
            aspect = MaxAspect;
        if (aspect < 1.0 / MaxAspect)
            aspect = 1.0 / MaxAspect;

        double scale = Math.Sqrt(MinPixels / (double)((long)w * h));
        if (scale < 1)
            scale = 1;

        int rw = Snap16((int)Math.Ceiling(w * scale));
        int rh = Snap16((int)Math.Ceiling(h * scale));
        if (rw < 16)
            rw = 16;
        if (rh < 16)
            rh = 16;

        // Re-apply aspect if snap drifted too far.
        if (rw >= rh)
            rh = Snap16(Math.Max(16, (int)Math.Round(rw / aspect)));
        else
            rw = Snap16(Math.Max(16, (int)Math.Round(rh * aspect)));

        while ((long)rw * rh < MinPixels)
        {
            if (rw <= rh)
                rw += 16;
            else
                rh += 16;
        }

        if (rw > MaxEdge || rh > MaxEdge)
        {
            double shrink = Math.Min(MaxEdge / (double)rw, MaxEdge / (double)rh);
            rw = Snap16((int)(rw * shrink));
            rh = Snap16((int)(rh * shrink));
        }

        if (!IsValidSize(rw, rh))
            return "1024x1024";

        return rw + "x" + rh;
    }

    private static bool IsValidSize(int w, int h)
    {
        if (w <= 0 || h <= 0 || w % 16 != 0 || h % 16 != 0)
            return false;
        if (w > MaxEdge || h > MaxEdge)
            return false;
        long pixels = (long)w * h;
        if (pixels < MinPixels || pixels > MaxPixels)
            return false;
        int longEdge = Math.Max(w, h);
        int shortEdge = Math.Min(w, h);
        return longEdge <= shortEdge * MaxAspect;
    }

    private static int Snap16(int value)
    {
        int snapped = (value + 8) / 16 * 16;
        return snapped < 16 ? 16 : snapped;
    }

    private static void PutOptional(JsonObject body, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            body[field] = value;
    }

    private static int ParseInt(string key, string raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ArgumentException($"extra.{key} must be an integer, got '{raw}'.");

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";

    private sealed class ExtraReader
    {
        private readonly Dictionary<string, string> _remaining;

        public ExtraReader(IReadOnlyDictionary<string, string>? extra)
            => _remaining = extra is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(extra, StringComparer.Ordinal);

        public string? Take(string key) => _remaining.Remove(key, out string? value) ? value : null;

        public int? TakeInt(string key)
        {
            if (Take(key) is not string raw)
                return null;
            return ParseInt(key, raw);
        }

        public void ThrowIfUnconsumed()
        {
            if (_remaining.Count > 0)
            {
                throw new ArgumentException(
                    $"Unknown openai extra key(s): {string.Join(", ", _remaining.Keys.OrderBy(k => k, StringComparer.Ordinal))}. " +
                    "Supported: model, size, quality, background, outputFormat, outputCompression, moderation, " +
                    "inputImagePath, loraModelId, palette, referenceImageGuids, " +
                    "atlasPath, region, sourcePagePath, pageName, mode, tintR, tintG, tintB, pma, width, height.");
            }
        }
    }
}
