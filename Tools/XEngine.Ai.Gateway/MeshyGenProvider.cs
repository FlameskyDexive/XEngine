using System.Text.Json.Nodes;

namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.3 — Meshy (https://docs.meshy.ai) adapter: text-to-3d (preview/refine), image-to-3d,
/// remesh, auto-rigging, and animation-library application, normalized to submit → poll → fetch.
///
/// Meshy splits these across five REST task families with identical lifecycle semantics
/// (POST → {"result": id}; GET /:id → {status, progress, task_error, *_urls}). The provider is
/// stateless between CLI invocations, so returned task ids are namespaced "family:remoteId" and
/// decoded on poll/fetch — the <see cref="GenTaskStore"/> only ever sees an opaque string.
///
/// Routing (all inputs travel in <see cref="GenRecipe.Extra"/>, camelCase keys):
///   kind=model3d            → text-to-3d preview (prompt, ai_model=Model, seed, topology, ...)
///     + extra.mode=refine   → text-to-3d refine  (previewTaskId required; enablePbr, textureResolution)
///     + extra.mode=remesh   → remesh             (inputTaskId required; targetFormats, ...)
///     + extra.imageUrl      → image-to-3d        (image_url; same geometry options)
///   kind=motion             → rigging            (inputTaskId or modelUrl; heightMeters)
///     + extra.rigTaskId     → animations         (rigTaskId + actionId required)
///
/// Downloads use the signed *_urls without the Authorization header (presigned URLs reject it).
/// </summary>
public sealed class MeshyGenProvider : IGenProvider
{
    public const string ProviderName = "meshy";
    public const string DefaultBaseUrl = "https://api.meshy.ai";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _baseUrl;

    /// <param name="apiKeyResolver">Called per request; return null to fail with setup guidance.
    /// Lazy so the provider can always be registered and only errors when actually used.</param>
    /// <param name="handler">Test seam — a fake <see cref="HttpMessageHandler"/> intercepts all HTTP.</param>
    public MeshyGenProvider(Func<string?> apiKeyResolver, HttpMessageHandler? handler = null, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(apiKeyResolver);
        _apiKey = apiKeyResolver;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public string Name => ProviderName;

    public IReadOnlyList<string> SupportedKinds { get; } = new[] { GenRecipe.Kinds.Model3d, GenRecipe.Kinds.Motion };

    // ---- task-family namespacing --------------------------------------------------------------

    private const string TextTo3d = "t23d";
    private const string ImageTo3d = "i23d";
    private const string Remesh = "remesh";
    private const string Rig = "rig";
    private const string Anim = "anim";

    private static string Endpoint(string family) => family switch
    {
        TextTo3d => "/openapi/v2/text-to-3d",
        ImageTo3d => "/openapi/v1/image-to-3d",
        Remesh => "/openapi/v1/remesh",
        Rig => "/openapi/v1/rigging",
        Anim => "/openapi/v1/animations",
        _ => throw new InvalidOperationException($"Not a meshy task id family: '{family}'."),
    };

    private static (string Family, string RemoteId) Split(string taskId)
    {
        int colon = taskId.IndexOf(':');
        if (colon <= 0 || colon == taskId.Length - 1)
        {
            throw new ArgumentException(
                $"'{taskId}' is not a meshy task id (expected 'family:remoteId', e.g. 't23d:018a...').");
        }

        return (taskId[..colon], taskId[(colon + 1)..]);
    }

    /// <summary>Chained ids (previewTaskId, inputTaskId, rigTaskId) accept our namespaced form and
    /// raw Meshy ids alike — agents naturally pass back what gen_submit returned.</summary>
    private static string StripFamily(string taskId)
        => taskId.IndexOf(':') is int colon && colon > 0 ? taskId[(colon + 1)..] : taskId;

    // ---- submit --------------------------------------------------------------------------------

    public async Task<GenSubmitResult> SubmitAsync(GenRecipe recipe, CancellationToken cancellationToken)
    {
        var extra = new ExtraReader(recipe.Extra);
        extra.Take("fetchThumbnail"); // fetch-time option; travels in the recipe but shapes no request
        // ★ Gen-P3.2 — style-anchor injects these into Extra for Scenario/ComfyUI; Meshy ignores them.
        extra.Take("loraModelId");
        extra.Take("palette");
        extra.Take("referenceImageGuids");
        (string family, JsonObject body) = recipe.Kind switch
        {
            GenRecipe.Kinds.Model3d => BuildModel3dRequest(recipe, extra),
            GenRecipe.Kinds.Motion => BuildMotionRequest(recipe, extra),
            _ => throw new InvalidOperationException($"Meshy cannot generate kind '{recipe.Kind}'."),
        };
        extra.ThrowIfUnconsumed();

        JsonNode response = await SendAsync(HttpMethod.Post, Endpoint(family), body, cancellationToken).ConfigureAwait(false);
        string? remoteId = response["result"]?.GetValue<string>();
        if (string.IsNullOrEmpty(remoteId))
        {
            throw new InvalidOperationException($"Meshy submit response had no 'result' task id: {response.ToJsonString()}");
        }

        return new GenSubmitResult($"{family}:{remoteId}");
    }

    private static (string, JsonObject) BuildModel3dRequest(GenRecipe recipe, ExtraReader extra)
    {
        string? mode = extra.Take("mode");
        if (mode is "refine")
        {
            var refine = new JsonObject
            {
                ["mode"] = "refine",
                ["preview_task_id"] = StripFamily(extra.Require("previewTaskId", "refine mode")),
            };
            extra.PutBool(refine, "enablePbr", "enable_pbr");
            extra.PutString(refine, "textureResolution", "texture_resolution");
            extra.PutString(refine, "texturePrompt", "texture_prompt");
            extra.PutBool(refine, "moderation", "moderation");
            if (recipe.Model is not null)
            {
                refine["ai_model"] = recipe.Model;
            }

            return (TextTo3d, refine);
        }

        if (mode is "remesh")
        {
            var remesh = new JsonObject
            {
                ["input_task_id"] = StripFamily(extra.Require("inputTaskId", "remesh mode")),
            };
            extra.PutString(remesh, "targetFormats", "target_formats", csvToArray: true);
            extra.PutString(remesh, "topology", "topology");
            extra.PutInt(remesh, "targetPolycount", "target_polycount");
            extra.PutBool(remesh, "convertFormatOnly", "convert_format_only");
            return (Remesh, remesh);
        }

        if (mode is not (null or "preview"))
        {
            throw new ArgumentException($"Unknown meshy mode '{mode}'. Expected preview (default), refine, or remesh.");
        }

        // Geometry options are shared between text-to-3d preview and image-to-3d.
        string? imageUrl = extra.Take("imageUrl");
        var body = new JsonObject();
        if (imageUrl is not null)
        {
            body["image_url"] = imageUrl;
            extra.PutBool(body, "enablePbr", "enable_pbr");
        }
        else
        {
            body["mode"] = "preview";
            body["prompt"] = recipe.Prompt;
            extra.PutString(body, "artStyle", "art_style");
        }

        if (recipe.Model is not null)
        {
            body["ai_model"] = recipe.Model;
        }

        if (recipe.Seed is int seed)
        {
            body["seed"] = seed;
        }

        extra.PutString(body, "modelType", "model_type");
        extra.PutString(body, "topology", "topology");
        extra.PutInt(body, "targetPolycount", "target_polycount");
        extra.PutBool(body, "shouldRemesh", "should_remesh");
        extra.PutString(body, "symmetry", "symmetry_mode");
        extra.PutString(body, "poseMode", "pose_mode");
        extra.PutBool(body, "moderation", "moderation");
        return (imageUrl is not null ? ImageTo3d : TextTo3d, body);
    }

    private static (string, JsonObject) BuildMotionRequest(GenRecipe recipe, ExtraReader extra)
    {
        string? rigTaskId = extra.Take("rigTaskId");
        if (rigTaskId is not null)
        {
            var anim = new JsonObject
            {
                ["rig_task_id"] = StripFamily(rigTaskId),
                ["action_id"] = extra.RequireInt("actionId", "animation tasks"),
            };
            return (Anim, anim);
        }

        var rig = new JsonObject();
        string? inputTaskId = extra.Take("inputTaskId");
        string? modelUrl = extra.Take("modelUrl");
        if (inputTaskId is not null)
        {
            rig["input_task_id"] = StripFamily(inputTaskId);
        }
        else if (modelUrl is not null)
        {
            rig["model_url"] = modelUrl;
        }
        else
        {
            throw new ArgumentException(
                "Meshy rigging needs extra.inputTaskId (a succeeded meshy model task) or extra.modelUrl (GLB url). " +
                "To apply a library animation instead, pass extra.rigTaskId + extra.actionId.");
        }

        extra.PutDouble(rig, "heightMeters", "height_meters");
        extra.PutString(rig, "textureImageUrl", "texture_image_url");
        return (Rig, rig);
    }

    // ---- poll ----------------------------------------------------------------------------------

    public async Task<GenTaskStatus> PollAsync(string taskId, CancellationToken cancellationToken)
    {
        (string family, string remoteId) = Split(taskId);
        JsonNode task = await SendAsync(HttpMethod.Get, $"{Endpoint(family)}/{remoteId}", body: null, cancellationToken)
            .ConfigureAwait(false);

        string status = task["status"]?.GetValue<string>() ?? "UNKNOWN";
        int? progress = task["progress"]?.GetValue<int>();
        string? error = task["task_error"]?["message"]?.GetValue<string>();

        GenTaskState state = status switch
        {
            "PENDING" => GenTaskState.Pending,
            "IN_PROGRESS" => GenTaskState.Running,
            "SUCCEEDED" => GenTaskState.Succeeded,
            "FAILED" or "CANCELED" => GenTaskState.Failed,
            _ => GenTaskState.Failed,
        };

        string? message = state == GenTaskState.Failed
            ? (string.IsNullOrEmpty(error) ? $"meshy: {status}" : error)
            : $"meshy: {status}";
        return new GenTaskStatus(taskId, state, progress, message);
    }

    // ---- fetch ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<GenArtifact>> FetchAsync(
        string taskId, GenRecipe recipe, string downloadDirectory, CancellationToken cancellationToken)
    {
        (string family, string remoteId) = Split(taskId);
        JsonNode task = await SendAsync(HttpMethod.Get, $"{Endpoint(family)}/{remoteId}", body: null, cancellationToken)
            .ConfigureAwait(false);

        string status = task["status"]?.GetValue<string>() ?? "UNKNOWN";
        if (status != "SUCCEEDED")
        {
            throw new InvalidOperationException($"Meshy task '{taskId}' is {status}; only SUCCEEDED tasks can be fetched.");
        }

        Directory.CreateDirectory(downloadDirectory);
        var artifacts = new List<GenArtifact>();

        async Task DownloadAsync(string? url, string role, string extension, string mime)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            string path = Path.Combine(downloadDirectory, role + extension);
            byte[] bytes = await DownloadSignedAsync(url!, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            artifacts.Add(new GenArtifact(role, path, mime));
        }

        bool wantThumbnail = new ExtraReader(recipe.Extra).Take("fetchThumbnail") is "true";

        switch (family)
        {
            case TextTo3d or ImageTo3d or Remesh:
                string? glb = task["model_urls"]?["glb"]?.GetValue<string>();
                if (string.IsNullOrEmpty(glb))
                {
                    throw new InvalidOperationException(
                        $"Meshy task '{taskId}' has no GLB in model_urls: {task["model_urls"]?.ToJsonString() ?? "null"}");
                }

                await DownloadAsync(glb, "model", ".glb", "model/gltf-binary").ConfigureAwait(false);
                if (wantThumbnail)
                {
                    await DownloadAsync(task["thumbnail_url"]?.GetValue<string>(), "thumbnail", ".png", "image/png")
                        .ConfigureAwait(false);
                }

                break;

            case Rig:
                await DownloadAsync(task["result"]?["rigged_character_glb_url"]?.GetValue<string>()
                        ?? task["rigged_character_glb_url"]?.GetValue<string>(),
                    "model", ".glb", "model/gltf-binary").ConfigureAwait(false);
                JsonNode? basic = task["result"]?["basic_animations"] ?? task["basic_animations"];
                await DownloadAsync(basic?["walking_glb_url"]?.GetValue<string>(), "walk", ".glb", "model/gltf-binary")
                    .ConfigureAwait(false);
                await DownloadAsync(basic?["running_glb_url"]?.GetValue<string>(), "run", ".glb", "model/gltf-binary")
                    .ConfigureAwait(false);
                break;

            case Anim:
                await DownloadAsync(task["result"]?["animation_glb_url"]?.GetValue<string>()
                        ?? task["animation_glb_url"]?.GetValue<string>(),
                    "model", ".glb", "model/gltf-binary").ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown meshy task family '{family}'.");
        }

        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException($"Meshy task '{taskId}' (family {family}) exposed no downloadable outputs.");
        }

        return artifacts;
    }

    // ---- HTTP ----------------------------------------------------------------------------------

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        string key = _apiKey() ?? throw new InvalidOperationException(
            $"No Meshy API key. Set the {SecretResolver.EnvVarName(ProviderName)} environment variable " +
            "or add {\"meshy\": \"msy_...\"} to .xengine/secrets.json.");

        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Meshy API {method} {path} failed ({(int)response.StatusCode} {response.StatusCode}): {Truncate(payload, 500)}");
        }

        return JsonNode.Parse(payload) ?? throw new InvalidOperationException($"Meshy API {path} returned empty JSON.");
    }

    /// <summary>Signed asset URLs (S3-style) must be fetched WITHOUT the Authorization header —
    /// presigned signatures reject extra auth.</summary>
    private async Task<byte[]> DownloadSignedAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Download failed ({(int)response.StatusCode}) for {url}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    // ---- extras --------------------------------------------------------------------------------

    /// <summary>Curated extra-key consumption: every recognized key is marked consumed, and any
    /// leftover key fails the submit — typos ("targetPolyCount") surface immediately instead of
    /// being silently ignored by the remote API.</summary>
    private sealed class ExtraReader
    {
        private readonly Dictionary<string, string> _remaining;

        public ExtraReader(IReadOnlyDictionary<string, string>? extra)
            => _remaining = extra is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(extra, StringComparer.Ordinal);

        public string? Take(string key) => _remaining.Remove(key, out string? value) ? value : null;

        public string Require(string key, string context)
            => Take(key) ?? throw new ArgumentException($"Meshy {context} requires extra.{key}.");

        public int RequireInt(string key, string context)
        {
            string raw = Require(key, context);
            return int.TryParse(raw, out int value)
                ? value
                : throw new ArgumentException($"extra.{key} must be an integer, got '{raw}'.");
        }

        public void PutString(JsonObject body, string key, string field, bool csvToArray = false)
        {
            if (Take(key) is not string value)
            {
                return;
            }

            if (csvToArray)
            {
                var array = new JsonArray();
                foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    array.Add(part);
                }

                body[field] = array;
            }
            else
            {
                body[field] = value;
            }
        }

        public void PutInt(JsonObject body, string key, string field)
        {
            if (Take(key) is string raw)
            {
                body[field] = int.TryParse(raw, out int value)
                    ? value
                    : throw new ArgumentException($"extra.{key} must be an integer, got '{raw}'.");
            }
        }

        public void PutDouble(JsonObject body, string key, string field)
        {
            if (Take(key) is string raw)
            {
                body[field] = double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out double value)
                    ? value
                    : throw new ArgumentException($"extra.{key} must be a number, got '{raw}'.");
            }
        }

        public void PutBool(JsonObject body, string key, string field)
        {
            if (Take(key) is string raw)
            {
                body[field] = raw switch
                {
                    "true" => true,
                    "false" => false,
                    _ => throw new ArgumentException($"extra.{key} must be true or false, got '{raw}'."),
                };
            }
        }

        public void ThrowIfUnconsumed()
        {
            if (_remaining.Count > 0)
            {
                throw new ArgumentException(
                    $"Unknown meshy extra key(s): {string.Join(", ", _remaining.Keys.OrderBy(k => k, StringComparer.Ordinal))}. " +
                    "Supported: mode, previewTaskId, inputTaskId, rigTaskId, actionId, imageUrl, modelUrl, modelType, " +
                    "artStyle, topology, targetPolycount, shouldRemesh, symmetry, poseMode, moderation, enablePbr, " +
                    "textureResolution, texturePrompt, targetFormats, convertFormatOnly, heightMeters, textureImageUrl, " +
                    "fetchThumbnail, loraModelId, palette, referenceImageGuids.");
            }
        }
    }
}
