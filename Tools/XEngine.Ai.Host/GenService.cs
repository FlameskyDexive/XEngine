using XEngine.Ai.Gateway;
using XEngine.Ai.Protocol;
using XEngine.Echo;

using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Gen-P1.2 — The gen_submit / gen_status / gen_fetch core. Bridges the gateway (providers +
/// durable task store) to the asset pipeline: fetch downloads a finished task's artifacts, imports
/// them through <see cref="AssetWriter"/> (journaled, GUID-preserving on overwrite), embeds the
/// recipe into the .meta settings, and derives model sub-asset GUIDs so the caller can wire scenes
/// immediately. Submit consults the task store's input-hash index before touching a paid API.
/// </summary>
internal sealed class GenService
{
    private static readonly JsonSerializerOptions RecipeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ProjectDescriptor _project;
    private readonly GenProviderRegistry _registry;
    private readonly GenTaskStore _store;
    private readonly AssetWriter _assetWriter;

    public GenService(ProjectDescriptor project, GenProviderRegistry registry, GenTaskStore store, AssetWriter assetWriter)
    {
        _project = project;
        _registry = registry;
        _store = store;
        _assetWriter = assetWriter;
    }

    // ---- gen_submit -------------------------------------------------------------------------

    public async Task<GenSubmitToolResult> SubmitAsync(
        string provider, string kind, string prompt, string? model, int? seed, string? styleAnchor,
        string? extraJson, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        IGenProvider genProvider = _registry.Get(provider);
        if (!genProvider.SupportedKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Provider '{genProvider.Name}' cannot generate kind '{kind}'. " +
                $"Supported: {string.Join(", ", genProvider.SupportedKinds)}.");
        }

        IReadOnlyDictionary<string, string>? extra = ParseExtra(extraJson);
        var anchors = new StyleAnchorStore(_project.ProjectRoot);
        StyleAnchor? anchor = anchors.TryGet(styleAnchor);
        if (anchor is not null)
        {
            (prompt, extra) = StyleAnchorStore.Apply(anchor, prompt, extra);
        }

        if (extra is not null
            && kind.Equals(GenRecipe.Kinds.Texture, StringComparison.OrdinalIgnoreCase)
            && extra.ContainsKey(SpineAtlasRestyleService.ExtraAtlasPath))
        {
            extra = SpineAtlasRestyleService.Enrich(extra, _project.ProjectRoot);
            if (SpineAtlasRestyleService.ProviderUsesInputCrop(genProvider.Name))
                extra = SpineAtlasRestyleService.AttachInputCrop(extra, _project.ProjectRoot);
        }

        var recipe = new GenRecipe(genProvider.Name, kind, prompt, model, seed, styleAnchor, extra);
        string inputHash = recipe.ComputeInputHash();

        if (dryRun)
        {
            // No provider call, no store write, no billing — just the hash + routing facts.
            return new GenSubmitToolResult("", genProvider.Name, kind, inputHash, "dryRun",
                EstimatedCredits: null, Deduplicated: _store.FindReusableByHash(inputHash) is not null, DryRun: true);
        }

        GenTaskRecord? existing = _store.FindReusableByHash(inputHash);
        if (existing is not null)
        {
            return new GenSubmitToolResult(existing.TaskId, existing.Provider, existing.Recipe.Kind, inputHash,
                StateName(existing.State), existing.EstimatedCredits, Deduplicated: true, DryRun: false);
        }

        GenSubmitResult submitted = await genProvider.SubmitAsync(recipe, cancellationToken).ConfigureAwait(false);
        GenTaskRecord record = _store.Add(recipe, submitted.TaskId, submitted.EstimatedCredits);
        return new GenSubmitToolResult(record.TaskId, record.Provider, kind, inputHash,
            StateName(record.State), record.EstimatedCredits, Deduplicated: false, DryRun: false);
    }

    // ---- gen_status -------------------------------------------------------------------------

    public async Task<GenStatusToolResult> StatusAsync(string taskId, CancellationToken cancellationToken)
    {
        GenTaskRecord record = Require(taskId);

        // Terminal states are served from the ledger — no provider round-trip.
        if (record.State is GenTaskState.Succeeded or GenTaskState.Failed)
        {
            return new GenStatusToolResult(taskId, record.Provider, StateName(record.State), 100, record.Error);
        }

        GenTaskStatus status = await _registry.Get(record.Provider)
            .PollAsync(taskId, cancellationToken).ConfigureAwait(false);
        _store.Update(taskId, t =>
        {
            t.State = status.State;
            if (status.State == GenTaskState.Failed)
            {
                t.Error = status.Message;
            }
        });

        return new GenStatusToolResult(taskId, record.Provider, StateName(status.State), status.Progress, status.Message);
    }

    // ---- gen_fetch --------------------------------------------------------------------------

    public async Task<GenFetchToolResult> FetchAsync(
        string taskId, string destinationFolder, string? baseName, bool overwrite,
        string? intent, string idempotencyKey, CancellationToken cancellationToken)
    {
        GenTaskRecord record = Require(taskId);
        IGenProvider provider = _registry.Get(record.Provider);

        if (record.State != GenTaskState.Succeeded)
        {
            GenTaskStatus status = await provider.PollAsync(taskId, cancellationToken).ConfigureAwait(false);
            _store.Update(taskId, t => t.State = status.State);
            if (status.State == GenTaskState.Failed)
            {
                throw new InvalidOperationException($"Generation task '{taskId}' failed: {status.Message ?? "no detail"}.");
            }

            if (status.State != GenTaskState.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Generation task '{taskId}' is not ready (state={StateName(status.State)}" +
                    (status.Progress is int p ? $", {p}%" : "") + "). Poll gen_status until succeeded.");
            }
        }

        string downloadDir = Path.Combine(_project.ProjectRoot, ".xengine", "gen-downloads", Sanitize(taskId));
        IReadOnlyList<GenArtifact> artifacts = await provider
            .FetchAsync(taskId, record.Recipe, downloadDir, cancellationToken).ConfigureAwait(false);
        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException($"Provider '{provider.Name}' returned no artifacts for task '{taskId}'.");
        }

        string folder = NormalizeFolder(destinationFolder);
        string slug = string.IsNullOrWhiteSpace(baseName) ? Slugify(record.Recipe.Prompt) : Sanitize(baseName!);

        var imported = new List<GenFetchedAsset>(artifacts.Count);
        var warnings = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < artifacts.Count; i++)
        {
            GenArtifact artifact = artifacts[i];
            string extension = Path.GetExtension(artifact.LocalPath);
            string name = artifacts.Count == 1 ? slug : $"{slug}-{Sanitize(artifact.Role)}";
            while (!usedNames.Add(name))
            {
                name += "-" + i;
            }

            string relative = $"{folder}/{name}{extension}";
            string absolute = Path.Combine(_project.ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar));

            if (SpineAtlasRestyleService.IsRequested(record.Recipe)
                && extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                SpineAtlasRestyleService.ApplyToArtifact(record.Recipe, _project.ProjectRoot, artifact.LocalPath);
            }

            AssetImportResult import = await _assetWriter.ImportAsync(
                artifact.LocalPath, absolute, relative,
                ChangeProvenance.ForTool("xengine-cli.gen-fetch",
                    intent ?? $"gen_fetch {taskId}: {record.Recipe.Kind} \"{record.Recipe.Prompt}\"",
                    $"{idempotencyKey}:{i}"),
                overwrite, animationType: null, cancellationToken).ConfigureAwait(false);

            EmbedRecipe(absolute + ".meta", record);

            IReadOnlyList<SubAssetInfo>? subAssets = null;
            if (extension is ".glb" or ".gltf" or ".obj" or ".fbx")
            {
                try
                {
                    subAssets = SubAssetResolver.Resolve(absolute, relative).SubAssets;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Sub-asset derivation failed for '{relative}': {ex.Message}");
                }
            }

            imported.Add(new GenFetchedAsset(relative, import.Guid, artifact.Role, import.ImporterType, subAssets));
        }

        _store.Update(taskId, t =>
        {
            t.State = GenTaskState.Succeeded;
            t.Artifacts = artifacts.ToList();
            t.ImportedAssets = imported.Select(a => a.AssetPath).ToList();
        });

        return new GenFetchToolResult(taskId, "succeeded", imported, warnings);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private GenTaskRecord Require(string taskId)
        => _store.Get(taskId) ?? throw new KeyNotFoundException(
            $"Unknown generation task '{taskId}'. Submit first with gen_submit (tasks persist in .xengine/gen-tasks.json).");

    /// <summary>Recipe → .meta settings (genRecipe/genTaskId/genInputHash) so the asset carries its
    /// own provenance for re-rolling. Note: an editor reimport may rewrite settings; the durable
    /// copies live in the journal and gen-tasks.json.</summary>
    private static void EmbedRecipe(string metaPath, GenTaskRecord record)
    {
        if (!File.Exists(metaPath))
        {
            return;
        }

        EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
        EchoObject settings;
        if (meta.TryGet("settings", out EchoObject? existing) && existing!.TagType == EchoType.Compound)
        {
            settings = existing;
        }
        else
        {
            settings = EchoObject.NewCompound();
            meta["settings"] = settings;
        }

        settings["genRecipe"] = new EchoObject(JsonSerializer.Serialize(record.Recipe, RecipeJsonOptions));
        settings["genTaskId"] = new EchoObject(record.TaskId);
        settings["genInputHash"] = new EchoObject(record.InputHash);

        string temp = metaPath + ".tmp";
        File.WriteAllText(temp, meta.WriteToString());
        File.Move(temp, metaPath, overwrite: true);
    }

    /// <summary>Destination folders always land under Assets/ ("GenArt" → "Assets/GenArt").</summary>
    private static string NormalizeFolder(string folder)
    {
        string trimmed = folder.Replace('\\', '/').Trim('/');
        if (trimmed.Length == 0)
        {
            trimmed = "Assets/GenArt";
        }

        return trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Assets", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : "Assets/" + trimmed;
    }

    /// <summary>Prompt → file slug: lowercase alphanumerics with single dashes, capped at 40 chars.</summary>
    internal static string Slugify(string prompt)
    {
        var sb = new StringBuilder(Math.Min(prompt.Length, 48));
        bool lastDash = true; // suppress leading dash
        foreach (char c in prompt)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }

            if (sb.Length >= 40)
            {
                break;
            }
        }

        string slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "generated" : slug;
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        return sb.Length == 0 ? "x" : sb.ToString();
    }

    private static IReadOnlyDictionary<string, string>? ParseExtra(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(extraJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("extra must be a JSON object of string values, e.g. {\"topology\":\"quad\"}.");
        }

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            extra[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : property.Value.GetRawText();
        }

        return extra;
    }

    private static string StateName(GenTaskState state) => state switch
    {
        GenTaskState.Pending => "pending",
        GenTaskState.Running => "running",
        GenTaskState.Succeeded => "succeeded",
        GenTaskState.Failed => "failed",
        _ => state.ToString(),
    };
}
