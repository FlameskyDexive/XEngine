using System.Text.Json.Nodes;

namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.4 — ElevenLabs sound-effects adapter (POST /v1/sound-generation, xi-api-key header).
/// Synchronous: the response body IS the MP3, so this rides <see cref="CachedResultGenProvider"/>.
///
/// Extra keys: durationSeconds (0.5–30; omitted → model picks), promptInfluence (0–1).
/// </summary>
public sealed class ElevenLabsGenProvider : CachedResultGenProvider
{
    public const string ProviderName = "elevenlabs";
    public const string DefaultBaseUrl = "https://api.elevenlabs.io";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _baseUrl;

    public ElevenLabsGenProvider(Func<string?> apiKeyResolver, string cacheRoot,
        HttpMessageHandler? handler = null, string? baseUrl = null)
        : base(cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(apiKeyResolver);
        _apiKey = apiKeyResolver;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public override string Name => ProviderName;

    public override IReadOnlyList<string> SupportedKinds { get; } = new[] { GenRecipe.Kinds.Sfx };

    protected override async Task<IReadOnlyList<(string, byte[], string, string)>> GenerateAsync(
        GenRecipe recipe, CancellationToken cancellationToken)
    {
        string key = _apiKey() ?? throw new InvalidOperationException(
            $"No ElevenLabs API key. Set the {SecretResolver.EnvVarName(ProviderName)} environment variable " +
            "or add {\"elevenlabs\": \"...\"} to .xengine/secrets.json.");

        IReadOnlyDictionary<string, string> extra =
            recipe.Extra ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var body = new JsonObject { ["text"] = recipe.Prompt };
        if (extra.TryGetValue("durationSeconds", out string? duration))
        {
            body["duration_seconds"] = ParseDouble("durationSeconds", duration);
        }

        if (extra.TryGetValue("promptInfluence", out string? influence))
        {
            body["prompt_influence"] = ParseDouble("promptInfluence", influence);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/sound-generation");
        request.Headers.Add("xi-api-key", key); // ElevenLabs uses its own header, not Bearer auth
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ElevenLabs API failed ({(int)response.StatusCode}): {(error.Length <= 500 ? error : error[..500] + "…")}");
        }

        byte[] audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (audio.Length == 0)
        {
            throw new InvalidOperationException("ElevenLabs returned an empty audio body.");
        }

        return new[] { ("audio.mp3", audio, "audio/mpeg", "audio") };
    }

    private static double ParseDouble(string key, string raw)
        => double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ArgumentException($"extra.{key} must be a number, got '{raw}'.");
}
