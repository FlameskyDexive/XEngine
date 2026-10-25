using System.Text.Json.Nodes;

namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.4 — PixelLab (https://api.pixellab.ai/v2/docs) adapter for pixel-art sprites and
/// textures. The API is synchronous (POST create-image-pixflux → base64 PNG in the response), so
/// this rides <see cref="CachedResultGenProvider"/>: generation happens at submit, results land in
/// the gen cache, poll/fetch serve from it.
///
/// Extra keys: width, height (32–400, default 128), noBackground (default true for kind=sprite),
/// textGuidanceScale, outline, shading, detail, isometric, negativeDescription.
/// </summary>
public sealed class PixelLabGenProvider : CachedResultGenProvider
{
    public const string ProviderName = "pixellab";
    public const string DefaultBaseUrl = "https://api.pixellab.ai/v2";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _baseUrl;

    public PixelLabGenProvider(Func<string?> apiKeyResolver, string cacheRoot,
        HttpMessageHandler? handler = null, string? baseUrl = null)
        : base(cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(apiKeyResolver);
        _apiKey = apiKeyResolver;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public override string Name => ProviderName;

    public override IReadOnlyList<string> SupportedKinds { get; } =
        new[] { GenRecipe.Kinds.Sprite, GenRecipe.Kinds.Texture };

    protected override async Task<IReadOnlyList<(string, byte[], string, string)>> GenerateAsync(
        GenRecipe recipe, CancellationToken cancellationToken)
    {
        string key = _apiKey() ?? throw new InvalidOperationException(
            $"No PixelLab API key. Set the {SecretResolver.EnvVarName(ProviderName)} environment variable " +
            "or add {\"pixellab\": \"...\"} to .xengine/secrets.json.");

        IReadOnlyDictionary<string, string> extra =
            recipe.Extra ?? new Dictionary<string, string>(StringComparer.Ordinal);

        int width = ParseInt(extra, "width", 128);
        int height = ParseInt(extra, "height", 128);
        // Sprites want alpha by default; textures tile edge-to-edge and keep their background.
        bool noBackground = extra.TryGetValue("noBackground", out string? nb)
            ? ParseBool("noBackground", nb)
            : recipe.Kind == GenRecipe.Kinds.Sprite;

        var body = new JsonObject
        {
            ["description"] = recipe.Prompt,
            ["image_size"] = new JsonObject { ["width"] = width, ["height"] = height },
            ["no_background"] = noBackground,
        };
        if (recipe.Seed is int seed)
        {
            body["seed"] = seed;
        }

        PutDouble(body, extra, "textGuidanceScale", "text_guidance_scale");
        PutString(body, extra, "outline", "outline");
        PutString(body, extra, "shading", "shading");
        PutString(body, extra, "detail", "detail");
        PutBool(body, extra, "isometric", "isometric");
        PutString(body, extra, "negativeDescription", "negative_description");

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/create-image-pixflux");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"PixelLab API failed ({(int)response.StatusCode}): {Truncate(payload)}");
        }

        JsonNode json = JsonNode.Parse(payload)
            ?? throw new InvalidOperationException("PixelLab returned empty JSON.");
        string? base64 = json["image"]?["base64"]?.GetValue<string>();
        if (string.IsNullOrEmpty(base64))
        {
            throw new InvalidOperationException($"PixelLab response had no image.base64: {Truncate(payload)}");
        }

        string role = recipe.Kind == GenRecipe.Kinds.Sprite ? "sprite" : "texture";
        return new[] { (role + ".png", Convert.FromBase64String(base64!), "image/png", role) };
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> extra, string key, int fallback)
        => extra.TryGetValue(key, out string? raw)
            ? int.TryParse(raw, out int value)
                ? value
                : throw new ArgumentException($"extra.{key} must be an integer, got '{raw}'.")
            : fallback;

    private static bool ParseBool(string key, string raw) => raw switch
    {
        "true" => true,
        "false" => false,
        _ => throw new ArgumentException($"extra.{key} must be true or false, got '{raw}'."),
    };

    private static void PutString(JsonObject body, IReadOnlyDictionary<string, string> extra, string key, string field)
    {
        if (extra.TryGetValue(key, out string? value))
        {
            body[field] = value;
        }
    }

    private static void PutBool(JsonObject body, IReadOnlyDictionary<string, string> extra, string key, string field)
    {
        if (extra.TryGetValue(key, out string? raw))
        {
            body[field] = ParseBool(key, raw);
        }
    }

    private static void PutDouble(JsonObject body, IReadOnlyDictionary<string, string> extra, string key, string field)
    {
        if (extra.TryGetValue(key, out string? raw))
        {
            body[field] = double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value
                : throw new ArgumentException($"extra.{key} must be a number, got '{raw}'.");
        }
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
