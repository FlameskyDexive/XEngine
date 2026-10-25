using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P3.3 — Local ComfyUI HTTP adapter (https://docs.comfy.org/developer-guide/rest).
/// Maps submit → poll → fetch onto:
///   POST /prompt {prompt, client_id}  → {prompt_id}
///   GET  /history/{prompt_id}         → outputs + status
///   GET  /queue                       → running vs pending when history is empty
///   GET  /view?filename&amp;subfolder&amp;type → artifact bytes
///
/// Workflows are ComfyUI <c>API Format</c> graphs (node-id → {class_type, inputs}), not the
/// UI <c>nodes[]</c> export. Built-in presets <c>trellis2</c> / <c>hunyuan3d</c> are minimal
/// text-to-GLB graphs (CLIPTextEncode + generator + SaveGLB) so TRELLIS.2 / Hunyuan3D 2.1
/// custom-node installs can run without a hand-exported JSON; override with extra.workflow.
///
/// Extra keys: workflow | workflowJson | preset, promptNode/promptField, negativeNode/negativeField,
/// outputNode, loraNode/loraField/loraModelId, seedNode/seedField, filenameNode/filenamePrefix,
/// baseUrl, clientId, palette, referenceImageGuids, inputImageNode/inputImageField/inputImagePath,
/// plus Spine atlas restyle keys (atlasPath/region/…).
/// API key is optional (local default is open); <c>COMFYUI_API_KEY</c> is sent as Bearer when set.
/// </summary>
public sealed class ComfyUiGenProvider : IGenProvider
{
    public const string ProviderName = "comfyui";
    public const string DefaultBaseUrl = "http://127.0.0.1:8188";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _baseUrl;
    private readonly string? _projectRoot;

    public ComfyUiGenProvider(
        Func<string?> apiKeyResolver,
        string? projectRoot = null,
        HttpMessageHandler? handler = null,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(apiKeyResolver);
        _apiKey = apiKeyResolver;
        _projectRoot = projectRoot;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public string Name => ProviderName;

    public IReadOnlyList<string> SupportedKinds { get; } =
        new[] { GenRecipe.Kinds.Model3d, GenRecipe.Kinds.Texture, GenRecipe.Kinds.Sprite, GenRecipe.Kinds.Skybox };

    public async Task<GenSubmitResult> SubmitAsync(GenRecipe recipe, CancellationToken cancellationToken)
    {
        var extra = new ExtraReader(recipe.Extra);
        extra.Take("referenceImageGuids"); // style-anchor leftover; ComfyUI needs a disk path, not a GUID

        string baseUrl = (extra.Take("baseUrl") ?? _baseUrl).TrimEnd('/');
        string clientId = extra.Take("clientId") ?? "xengine-cli";
        string? preset = extra.Take("preset");
        JsonObject graph = LoadGraph(preset, extra.Take("workflowJson"), extra.Take("workflow"));

        (string positive, string? negativeFromPrompt) = SplitPrompt(recipe.Prompt);
        if (extra.Take("palette") is string palette && palette.Length > 0)
        {
            positive += " palette: " + palette;
        }

        string promptNode = extra.Take("promptNode") ?? (preset is null ? "6" : "1");
        string promptField = extra.Take("promptField") ?? "text";
        SetInput(graph, promptNode, promptField, positive);

        string? negativeNode = extra.Take("negativeNode") ?? (preset is null ? null : "2");
        string negativeField = extra.Take("negativeField") ?? "text";
        if (negativeNode is not null && negativeFromPrompt is not null)
        {
            SetInput(graph, negativeNode, negativeField, negativeFromPrompt);
        }

        string? seedNode = extra.Take("seedNode") ?? (preset is null ? null : "3");
        string seedField = extra.Take("seedField") ?? "seed";
        if (seedNode is not null && recipe.Seed is int seed)
        {
            SetInput(graph, seedNode, seedField, seed);
        }

        string? loraNode = extra.Take("loraNode");
        string loraField = extra.Take("loraField") ?? "lora_name";
        string? lora = extra.Take("loraModelId");
        if (loraNode is not null && !string.IsNullOrWhiteSpace(lora))
        {
            SetInput(graph, loraNode, loraField, lora);
        }

        string? filenameNode = extra.Take("filenameNode") ?? (preset is null ? null : "4");
        if (filenameNode is not null && extra.Take("filenamePrefix") is string prefix)
        {
            SetInput(graph, filenameNode, "filename_prefix", prefix);
        }
        else
        {
            extra.Take("filenamePrefix");
        }

        extra.Take("outputNode"); // fetch-time
        SpineAtlasGenExtras.Consume(extra.Take);

        string? inputImageNode = extra.Take("inputImageNode");
        string inputImageField = extra.Take("inputImageField") ?? "image";
        string? inputImagePath = extra.Take("inputImagePath");
        if (!string.IsNullOrWhiteSpace(inputImageNode))
        {
            if (string.IsNullOrWhiteSpace(inputImagePath))
            {
                throw new ArgumentException(
                    "ComfyUI extra.inputImageNode requires extra.inputImagePath (Host injects the unrotated atlas crop on kind=texture + atlasPath).");
            }

            string uploaded = await UploadInputImageAsync(baseUrl, inputImagePath, cancellationToken)
                .ConfigureAwait(false);
            SetInput(graph, inputImageNode, inputImageField, uploaded);
        }
        else if (!string.IsNullOrWhiteSpace(inputImagePath))
        {
            throw new ArgumentException(
                "ComfyUI extra.inputImagePath requires extra.inputImageNode (LoadImage node id in the API-format graph).");
        }

        extra.ThrowIfUnconsumed();

        var body = new JsonObject
        {
            ["prompt"] = graph,
            ["client_id"] = clientId,
        };
        JsonNode response = await SendJsonAsync(HttpMethod.Post, baseUrl, "/prompt", body, cancellationToken)
            .ConfigureAwait(false);

        if (response["node_errors"] is JsonObject errors && errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"ComfyUI rejected the workflow (node_errors): {errors.ToJsonString()}");
        }

        string? promptId = response["prompt_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(promptId))
        {
            throw new InvalidOperationException($"ComfyUI /prompt had no prompt_id: {response.ToJsonString()}");
        }

        return new GenSubmitResult(PackTaskId(promptId, baseUrl));
    }

    public async Task<GenTaskStatus> PollAsync(string taskId, CancellationToken cancellationToken)
    {
        (string baseUrl, string promptId) = Unpack(taskId);
        JsonNode history = await SendJsonAsync(HttpMethod.Get, baseUrl, "/history/" + Uri.EscapeDataString(promptId),
            body: null, cancellationToken).ConfigureAwait(false);

        if (TryHistoryEntry(history, promptId, out JsonObject? entry))
        {
            string status = entry["status"]?["status_str"]?.GetValue<string>() ?? "";
            bool completed = entry["status"]?["completed"]?.GetValue<bool>() ?? false;
            if (status is "error" or "exception" || status.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                string? detail = entry["status"]?["messages"]?.ToJsonString();
                return new GenTaskStatus(taskId, GenTaskState.Failed, null, detail ?? "comfyui: error");
            }

            if (completed || entry["outputs"] is JsonObject { Count: > 0 })
            {
                return new GenTaskStatus(taskId, GenTaskState.Succeeded, 100, "comfyui: done");
            }
        }

        JsonNode queue = await SendJsonAsync(HttpMethod.Get, baseUrl, "/queue", body: null, cancellationToken)
            .ConfigureAwait(false);
        if (QueueContains(queue["queue_running"], promptId))
        {
            return new GenTaskStatus(taskId, GenTaskState.Running, null, "comfyui: running");
        }

        if (QueueContains(queue["queue_pending"], promptId))
        {
            return new GenTaskStatus(taskId, GenTaskState.Pending, null, "comfyui: queued");
        }

        return new GenTaskStatus(taskId, GenTaskState.Pending, null, "comfyui: waiting");
    }

    public async Task<IReadOnlyList<GenArtifact>> FetchAsync(
        string taskId, GenRecipe recipe, string downloadDirectory, CancellationToken cancellationToken)
    {
        (string baseUrl, string promptId) = Unpack(taskId);
        JsonNode history = await SendJsonAsync(HttpMethod.Get, baseUrl, "/history/" + Uri.EscapeDataString(promptId),
            body: null, cancellationToken).ConfigureAwait(false);
        if (!TryHistoryEntry(history, promptId, out JsonObject? entry)
            || entry["outputs"] is not JsonObject outputs
            || outputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"ComfyUI task '{taskId}' has no outputs yet — poll gen_status until succeeded.");
        }

        string? outputNode = null;
        recipe.Extra?.TryGetValue("outputNode", out outputNode);

        Directory.CreateDirectory(downloadDirectory);
        var artifacts = new List<GenArtifact>();
        foreach (KeyValuePair<string, JsonNode?> node in outputs)
        {
            if (outputNode is { Length: > 0 } && !string.Equals(node.Key, outputNode, StringComparison.Ordinal))
            {
                continue;
            }

            if (node.Value is not JsonObject bag)
            {
                continue;
            }

            await CollectAsync(bag, "images", recipe.Kind, baseUrl, downloadDirectory, artifacts, cancellationToken)
                .ConfigureAwait(false);
            await CollectAsync(bag, "gifs", recipe.Kind, baseUrl, downloadDirectory, artifacts, cancellationToken)
                .ConfigureAwait(false);
            await CollectAsync(bag, "glb", GenRecipe.Kinds.Model3d, baseUrl, downloadDirectory, artifacts, cancellationToken)
                .ConfigureAwait(false);
            await CollectAsync(bag, "gltf", GenRecipe.Kinds.Model3d, baseUrl, downloadDirectory, artifacts, cancellationToken)
                .ConfigureAwait(false);
            await CollectAsync(bag, "mesh", GenRecipe.Kinds.Model3d, baseUrl, downloadDirectory, artifacts, cancellationToken)
                .ConfigureAwait(false);
            await CollectAsync(bag, "3d", GenRecipe.Kinds.Model3d, baseUrl, downloadDirectory, artifacts, cancellationToken)
                .ConfigureAwait(false);
            await CollectAsync(bag, "files", recipe.Kind, baseUrl, downloadDirectory, artifacts, cancellationToken)
                .ConfigureAwait(false);
        }

        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException($"ComfyUI task '{taskId}' outputs contained no downloadable files.");
        }

        return artifacts;
    }

    // ---- workflow ------------------------------------------------------------------------------

    private JsonObject LoadGraph(string? preset, string? workflowJson, string? workflowPath)
    {
        string json;
        if (!string.IsNullOrWhiteSpace(workflowJson))
        {
            json = workflowJson;
        }
        else if (!string.IsNullOrWhiteSpace(workflowPath))
        {
            string path = Path.IsPathRooted(workflowPath)
                ? workflowPath
                : Path.Combine(_projectRoot ?? Environment.CurrentDirectory, workflowPath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"ComfyUI workflow file not found: {path}");
            }

            json = File.ReadAllText(path);
        }
        else if (preset is "trellis2" or "hunyuan3d")
        {
            json = PresetGraph(preset);
        }
        else if (preset is not null)
        {
            throw new ArgumentException($"Unknown comfyui preset '{preset}'. Expected trellis2 or hunyuan3d.");
        }
        else
        {
            throw new ArgumentException(
                "ComfyUI requires extra.workflow (API-format JSON path), extra.workflowJson, or extra.preset=trellis2|hunyuan3d.");
        }

        JsonNode root = JsonNode.Parse(json)
            ?? throw new ArgumentException("ComfyUI workflow JSON is empty.");
        if (root is JsonObject obj && obj["prompt"] is JsonObject nested)
        {
            return nested;
        }

        if (root is JsonObject graph && graph["nodes"] is JsonArray)
        {
            throw new ArgumentException(
                "ComfyUI workflow looks like the UI export (nodes[]). Re-export via Save (API Format).");
        }

        return root as JsonObject
            ?? throw new ArgumentException("ComfyUI workflow must be a JSON object of node-id → {class_type, inputs}.");
    }

    /// <summary>Minimal API-format graphs for local TRELLIS.2 / Hunyuan3D custom-node packs.
    /// Node ids are fixed so extra.promptNode defaults (1/2/3/4) line up.</summary>
    internal static string PresetGraph(string preset)
    {
        string generator = preset == "hunyuan3d" ? "Hunyuan3DTextTo3D" : "Trellis2TextTo3D";
        return """
            {
              "1": { "class_type": "CLIPTextEncode", "inputs": { "text": "" } },
              "2": { "class_type": "CLIPTextEncode", "inputs": { "text": "" } },
              "3": { "class_type": "GENERATOR", "inputs": { "prompt": ["1", 0], "negative": ["2", 0], "seed": 0 } },
              "4": { "class_type": "SaveGLB", "inputs": { "mesh": ["3", 0], "filename_prefix": "xengine" } }
            }
            """.Replace("GENERATOR", generator, StringComparison.Ordinal);
    }

    private static void SetInput(JsonObject graph, string nodeId, string field, JsonNode value)
    {
        if (!graph.TryGetPropertyValue(nodeId, out JsonNode? node) || node is not JsonObject obj)
        {
            throw new ArgumentException(
                $"ComfyUI workflow has no node '{nodeId}'. Nodes: {string.Join(", ", graph.Select(p => p.Key))}.");
        }

        if (obj["inputs"] is not JsonObject inputs)
        {
            inputs = new JsonObject();
            obj["inputs"] = inputs;
        }

        inputs[field] = value.DeepClone();
    }

    private static void SetInput(JsonObject graph, string nodeId, string field, string value)
        => SetInput(graph, nodeId, field, JsonValue.Create(value)!);

    private static void SetInput(JsonObject graph, string nodeId, string field, int value)
        => SetInput(graph, nodeId, field, JsonValue.Create(value));

    private static (string Positive, string? Negative) SplitPrompt(string prompt)
    {
        const string marker = "\nNegative: ";
        int index = prompt.IndexOf(marker, StringComparison.Ordinal);
        return index < 0
            ? (prompt, null)
            : (prompt[..index], prompt[(index + marker.Length)..]);
    }

    // ---- history / queue / view ----------------------------------------------------------------

    private static bool TryHistoryEntry(JsonNode history, string promptId, out JsonObject? entry)
    {
        entry = null;
        if (history is not JsonObject obj)
        {
            return false;
        }

        if (obj.TryGetPropertyValue(promptId, out JsonNode? found) && found is JsonObject direct)
        {
            entry = direct;
            return true;
        }

        // Some builds wrap once more; accept a single-entry object.
        if (obj.Count == 1)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in obj)
            {
                if (pair.Value is JsonObject only)
                {
                    entry = only;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool QueueContains(JsonNode? queue, string promptId)
    {
        if (queue is not JsonArray rows)
        {
            return false;
        }

        foreach (JsonNode? row in rows)
        {
            if (row is JsonArray cells)
            {
                foreach (JsonNode? cell in cells)
                {
                    if (cell is JsonValue value && value.TryGetValue(out string? id)
                        && string.Equals(id, promptId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private async Task CollectAsync(
        JsonObject bag, string key, string kind, string baseUrl, string downloadDirectory,
        List<GenArtifact> artifacts, CancellationToken cancellationToken)
    {
        if (bag[key] is not JsonArray files)
        {
            return;
        }

        foreach (JsonNode? file in files)
        {
            if (file is not JsonObject rec)
            {
                continue;
            }

            string? filename = rec["filename"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(filename))
            {
                continue;
            }

            string subfolder = rec["subfolder"]?.GetValue<string>() ?? "";
            string type = rec["type"]?.GetValue<string>() ?? "output";
            string view = "/view?filename=" + Uri.EscapeDataString(filename)
                + "&subfolder=" + Uri.EscapeDataString(subfolder)
                + "&type=" + Uri.EscapeDataString(type);

            byte[] bytes = await SendBytesAsync(HttpMethod.Get, baseUrl, view, cancellationToken).ConfigureAwait(false);
            string dest = Path.Combine(downloadDirectory, SanitizeFileName(filename));
            await File.WriteAllBytesAsync(dest, bytes, cancellationToken).ConfigureAwait(false);

            string ext = Path.GetExtension(filename);
            string role = RoleFor(kind, ext);
            artifacts.Add(new GenArtifact(role, dest, MimeFor(ext)));
        }
    }

    private static string RoleFor(string kind, string extension)
    {
        if (extension is ".glb" or ".gltf" or ".obj" or ".fbx")
        {
            return "model";
        }

        return kind switch
        {
            GenRecipe.Kinds.Sprite => "sprite",
            GenRecipe.Kinds.Skybox => "skybox",
            GenRecipe.Kinds.Sfx => "audio",
            _ => "texture",
        };
    }

    private static string MimeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".glb" => "model/gltf-binary",
        ".gltf" => "model/gltf+json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".exr" => "image/x-exr",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in Path.GetFileName(name))
        {
            sb.Append(c is ':' or '/' or '\\' or '?' or '*' or '"' or '<' or '>' or '|' ? '-' : c);
        }

        return sb.Length == 0 ? "artifact.bin" : sb.ToString();
    }

    // ---- task id -------------------------------------------------------------------------------

    private string PackTaskId(string promptId, string baseUrl)
        => string.Equals(baseUrl, _baseUrl, StringComparison.OrdinalIgnoreCase)
            ? "comfyui:" + promptId
            : "comfyui@" + baseUrl + "|" + promptId;

    private (string BaseUrl, string PromptId) Unpack(string taskId)
    {
        if (taskId.StartsWith("comfyui@", StringComparison.Ordinal))
        {
            int bar = taskId.IndexOf('|');
            if (bar < 0)
            {
                throw new ArgumentException($"'{taskId}' is not a comfyui task id (expected 'comfyui@base|promptId').");
            }

            return (taskId["comfyui@".Length..bar], taskId[(bar + 1)..]);
        }

        if (taskId.StartsWith("comfyui:", StringComparison.Ordinal))
        {
            return (_baseUrl, taskId["comfyui:".Length..]);
        }

        throw new ArgumentException($"'{taskId}' is not a comfyui task id (expected 'comfyui:promptId').");
    }

    // ---- input image (M10f) --------------------------------------------------------------------

    private async Task<string> UploadInputImageAsync(string baseUrl, string imagePath, CancellationToken cancellationToken)
    {
        string absolute = Path.IsPathRooted(imagePath)
            ? imagePath
            : Path.Combine(_projectRoot ?? Environment.CurrentDirectory,
                imagePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
        {
            throw new FileNotFoundException($"ComfyUI input image not found: {absolute}");
        }

        using var content = new MultipartFormDataContent();
        byte[] bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(MimeFor(Path.GetExtension(absolute)));
        content.Add(file, "image", Path.GetFileName(absolute));
        content.Add(new StringContent("true"), "overwrite");
        content.Add(new StringContent("input"), "type");

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/upload/image") { Content = content };
        AttachAuth(request);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ComfyUI API POST /upload/image failed ({(int)response.StatusCode}): " +
                (payload.Length <= 500 ? payload : payload[..500] + "…"));
        }

        JsonNode json = JsonNode.Parse(payload)
            ?? throw new InvalidOperationException("ComfyUI /upload/image returned empty JSON.");
        string? name = json["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"ComfyUI /upload/image had no name: {json.ToJsonString()}");
        }

        string subfolder = json["subfolder"]?.GetValue<string>() ?? "";
        return string.IsNullOrWhiteSpace(subfolder) ? name : subfolder.TrimEnd('/') + "/" + name;
    }

    // ---- HTTP ----------------------------------------------------------------------------------

    private async Task<JsonNode> SendJsonAsync(
        HttpMethod method, string baseUrl, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        string payload = await SendRawAsync(method, baseUrl, path,
            body is null ? null : body.ToJsonString(), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(payload) ?? new JsonObject();
    }

    private Task<byte[]> SendBytesAsync(HttpMethod method, string baseUrl, string path, CancellationToken cancellationToken)
        => SendBytesCoreAsync(method, baseUrl, path, cancellationToken);

    private async Task<string> SendRawAsync(
        HttpMethod method, string baseUrl, string path, string? jsonBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, baseUrl + path);
        AttachAuth(request);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ComfyUI API {method} {path} failed ({(int)response.StatusCode}): " +
                (payload.Length <= 500 ? payload : payload[..500] + "…"));
        }

        return payload;
    }

    private async Task<byte[]> SendBytesCoreAsync(
        HttpMethod method, string baseUrl, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, baseUrl + path);
        AttachAuth(request);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ComfyUI API {method} {path} failed ({(int)response.StatusCode}): " +
                (payload.Length <= 200 ? payload : payload[..200] + "…"));
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private void AttachAuth(HttpRequestMessage request)
    {
        string? key = _apiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
    }

    // ---- extras --------------------------------------------------------------------------------

    private sealed class ExtraReader
    {
        private readonly Dictionary<string, string> _remaining;

        public ExtraReader(IReadOnlyDictionary<string, string>? extra)
            => _remaining = extra is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(extra, StringComparer.Ordinal);

        public string? Take(string key) => _remaining.Remove(key, out string? value) ? value : null;

        public void ThrowIfUnconsumed()
        {
            if (_remaining.Count > 0)
            {
                throw new ArgumentException(
                    $"Unknown comfyui extra key(s): {string.Join(", ", _remaining.Keys.OrderBy(k => k, StringComparer.Ordinal))}. " +
                    "Supported: workflow, workflowJson, preset, promptNode, promptField, negativeNode, negativeField, " +
                    "outputNode, loraNode, loraField, loraModelId, seedNode, seedField, filenameNode, filenamePrefix, " +
                    "baseUrl, clientId, palette, referenceImageGuids, inputImageNode, inputImageField, inputImagePath, " +
                    "atlasPath, region, sourcePagePath, pageName, mode, tintR, tintG, tintB, pma, width, height.");
            }
        }
    }
}
