using System.Text.Json.Nodes;

namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.4 — Blockade Labs Skybox adapter (https://api-documentation.blockadelabs.com).
/// Genuinely asynchronous, so it maps 1:1 onto submit → poll → fetch:
///   POST /api/v1/skybox {prompt, skybox_style_id, ...}   (x-api-key header) → numeric id
///   GET  /api/v1/imagine/requests/{id}                    → {request:{status, file_url, ...}}
/// Status chain: pending → dispatched → processing → complete (or abort/error).
///
/// Extra keys: styleId (int), negativeText, enhancePrompt (bool); fetch-time: fetchDepthMap,
/// fetchThumbnail (both bool) add the depth-map / thumbnail artifacts beside the equirect skybox.
/// </summary>
public sealed class BlockadeGenProvider : IGenProvider
{
    public const string ProviderName = "blockade";
    public const string DefaultBaseUrl = "https://backend.blockadelabs.com";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _baseUrl;

    public BlockadeGenProvider(Func<string?> apiKeyResolver, HttpMessageHandler? handler = null, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(apiKeyResolver);
        _apiKey = apiKeyResolver;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public string Name => ProviderName;

    public IReadOnlyList<string> SupportedKinds { get; } = new[] { GenRecipe.Kinds.Skybox };

    public async Task<GenSubmitResult> SubmitAsync(GenRecipe recipe, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> extra =
            recipe.Extra ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var body = new JsonObject { ["prompt"] = recipe.Prompt };
        if (extra.TryGetValue("styleId", out string? styleId))
        {
            body["skybox_style_id"] = int.TryParse(styleId, out int id)
                ? id
                : throw new ArgumentException($"extra.styleId must be an integer, got '{styleId}'.");
        }

        if (extra.TryGetValue("negativeText", out string? negative))
        {
            body["negative_text"] = negative;
        }

        if (extra.TryGetValue("enhancePrompt", out string? enhance))
        {
            body["enhance_prompt"] = enhance switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ArgumentException($"extra.enhancePrompt must be true or false, got '{enhance}'."),
            };
        }

        if (recipe.Seed is int seed)
        {
            body["seed"] = seed;
        }

        JsonNode response = await SendAsync(HttpMethod.Post, "/api/v1/skybox", body, cancellationToken).ConfigureAwait(false);
        // POST /skybox answers with the object directly; some deployments wrap it in {"request": ...}.
        JsonNode? request = response["request"] ?? response;
        int? remoteId = request?["id"]?.GetValue<int>();
        if (remoteId is null)
        {
            throw new InvalidOperationException($"Blockade submit response had no id: {response.ToJsonString()}");
        }

        return new GenSubmitResult($"blockade:{remoteId}");
    }

    public async Task<GenTaskStatus> PollAsync(string taskId, CancellationToken cancellationToken)
    {
        JsonNode request = await GetRequestAsync(taskId, cancellationToken).ConfigureAwait(false);
        string status = request["status"]?.GetValue<string>() ?? "unknown";
        string? error = request["error_message"]?.GetValue<string>();

        GenTaskState state = status switch
        {
            "pending" => GenTaskState.Pending,
            "dispatched" or "processing" => GenTaskState.Running,
            "complete" => GenTaskState.Succeeded,
            "abort" or "error" => GenTaskState.Failed,
            _ => GenTaskState.Failed,
        };

        string message = state == GenTaskState.Failed && !string.IsNullOrEmpty(error)
            ? error!
            : $"blockade: {status}";
        return new GenTaskStatus(taskId, state, state == GenTaskState.Succeeded ? 100 : null, message);
    }

    public async Task<IReadOnlyList<GenArtifact>> FetchAsync(
        string taskId, GenRecipe recipe, string downloadDirectory, CancellationToken cancellationToken)
    {
        JsonNode request = await GetRequestAsync(taskId, cancellationToken).ConfigureAwait(false);
        string status = request["status"]?.GetValue<string>() ?? "unknown";
        if (status != "complete")
        {
            throw new InvalidOperationException($"Blockade task '{taskId}' is {status}; only complete tasks can be fetched.");
        }

        string? fileUrl = request["file_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(fileUrl))
        {
            throw new InvalidOperationException($"Blockade task '{taskId}' is complete but has no file_url.");
        }

        Directory.CreateDirectory(downloadDirectory);
        IReadOnlyDictionary<string, string> extra =
            recipe.Extra ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var artifacts = new List<GenArtifact>();

        async Task DownloadAsync(string url, string role, string mime)
        {
            string extension = GuessExtension(url, mime);
            string path = Path.Combine(downloadDirectory, role + extension);
            using var download = new HttpRequestMessage(HttpMethod.Get, url); // signed URL: no auth header
            using HttpResponseMessage response = await _http.SendAsync(download, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Download failed ({(int)response.StatusCode}) for {url}");
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            artifacts.Add(new GenArtifact(role, path, mime));
        }

        await DownloadAsync(fileUrl!, "skybox", "image/jpeg").ConfigureAwait(false);

        if (extra.TryGetValue("fetchDepthMap", out string? depth) && depth == "true"
            && request["depth_map_url"]?.GetValue<string>() is string depthUrl && depthUrl.Length > 0)
        {
            await DownloadAsync(depthUrl, "depth", "image/png").ConfigureAwait(false);
        }

        if (extra.TryGetValue("fetchThumbnail", out string? thumb) && thumb == "true"
            && request["thumb_url"]?.GetValue<string>() is string thumbUrl && thumbUrl.Length > 0)
        {
            await DownloadAsync(thumbUrl, "thumbnail", "image/jpeg").ConfigureAwait(false);
        }

        return artifacts;
    }

    private async Task<JsonNode> GetRequestAsync(string taskId, CancellationToken cancellationToken)
    {
        string remoteId = taskId.StartsWith("blockade:", StringComparison.Ordinal)
            ? taskId["blockade:".Length..]
            : throw new ArgumentException($"'{taskId}' is not a blockade task id (expected 'blockade:<id>').");

        JsonNode response = await SendAsync(HttpMethod.Get, $"/api/v1/imagine/requests/{remoteId}", body: null, cancellationToken)
            .ConfigureAwait(false);
        return response["request"] ?? response;
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        string key = _apiKey() ?? throw new InvalidOperationException(
            $"No Blockade Labs API key. Set the {SecretResolver.EnvVarName(ProviderName)} environment variable " +
            "or add {\"blockade\": \"...\"} to .xengine/secrets.json.");

        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Add("x-api-key", key); // Blockade uses x-api-key, not Bearer auth
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Blockade API {method} {path} failed ({(int)response.StatusCode}): " +
                (payload.Length <= 500 ? payload : payload[..500] + "…"));
        }

        return JsonNode.Parse(payload) ?? throw new InvalidOperationException($"Blockade API {path} returned empty JSON.");
    }

    private static string GuessExtension(string url, string fallbackMime)
    {
        string path = url;
        int query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        string ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext))
        {
            return ext;
        }

        return fallbackMime == "image/png" ? ".png" : ".jpg";
    }
}
