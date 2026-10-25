namespace XEngine.Ai.Gateway;

/// <summary>★ Gen-P4.1 — One generation candidate as shown in the editor gallery / gen_candidates.</summary>
public sealed record GenCandidate(
    string TaskId,
    string Provider,
    string Kind,
    string Prompt,
    string State,
    string InputHash,
    IReadOnlyList<string> AssetPaths,
    DateTimeOffset CreatedAtUtc,
    bool Chosen,
    bool Discarded);

/// <summary>On-disk gallery picks at <c>.xengine/gen-gallery.json</c>.</summary>
public sealed record GenGalleryDocument(string? ChosenTaskId, IReadOnlyList<string> DiscardedTaskIds);

/// <summary>
/// ★ Gen-P4.1 — Project-level candidate gallery over the gen-tasks ledger: filter imported
/// results, persist the chosen task, and hide discarded ones without deleting assets.
/// </summary>
public sealed class GenCandidateCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _projectRoot;
    private readonly string _galleryPath;
    private readonly GenTaskStore _store;

    public GenCandidateCatalog(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _projectRoot = projectRoot;
        _galleryPath = Path.Combine(projectRoot, ".xengine", "gen-gallery.json");
        _store = new GenTaskStore(projectRoot);
    }

    public string? ChosenTaskId => LoadDoc().ChosenTaskId;

    public IReadOnlyList<GenCandidate> List(
        string? kind = null, string? query = null, bool importedOnly = true, bool includeDiscarded = false)
    {
        GenGalleryDocument doc = LoadDoc();
        var discarded = new HashSet<string>(doc.DiscardedTaskIds, StringComparer.Ordinal);
        var cards = new List<GenCandidate>();
        foreach (GenTaskRecord task in _store.All())
        {
            if (importedOnly && (task.ImportedAssets is null || task.ImportedAssets.Count == 0))
                continue;
            if (!includeDiscarded && discarded.Contains(task.TaskId))
                continue;
            if (!string.IsNullOrWhiteSpace(kind)
                && !string.Equals(task.Recipe.Kind, kind, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(query)
                && task.Recipe.Prompt.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && task.TaskId.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && task.Provider.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            cards.Add(ToCandidate(task, doc.ChosenTaskId, discarded.Contains(task.TaskId)));
        }

        cards.Sort(static (a, b) => b.CreatedAtUtc.CompareTo(a.CreatedAtUtc));
        return cards;
    }

    public GenCandidate? TryGet(string taskId)
    {
        foreach (GenCandidate c in List(importedOnly: false, includeDiscarded: true))
        {
            if (string.Equals(c.TaskId, taskId, StringComparison.Ordinal))
                return c;
        }

        return null;
    }

    public GenCandidate Choose(string taskId)
    {
        GenTaskRecord record = _store.Get(taskId)
            ?? throw new KeyNotFoundException($"Unknown generation task '{taskId}'.");
        GenGalleryDocument doc = LoadDoc();
        var discarded = doc.DiscardedTaskIds.Where(id => !string.Equals(id, taskId, StringComparison.Ordinal)).ToList();
        SaveDoc(new GenGalleryDocument(taskId, discarded));
        return ToCandidate(record, taskId, discarded: false);
    }

    public GenCandidate Discard(string taskId)
    {
        GenTaskRecord record = _store.Get(taskId)
            ?? throw new KeyNotFoundException($"Unknown generation task '{taskId}'.");
        GenGalleryDocument doc = LoadDoc();
        var discarded = doc.DiscardedTaskIds.ToList();
        if (!discarded.Contains(taskId, StringComparer.Ordinal))
            discarded.Add(taskId);
        string? chosen = string.Equals(doc.ChosenTaskId, taskId, StringComparison.Ordinal) ? null : doc.ChosenTaskId;
        SaveDoc(new GenGalleryDocument(chosen, discarded));
        return ToCandidate(record, chosen, discarded: true);
    }

    public static GenCandidateGalleryLayout ComputeLayout(float width, float height)
    {
        bool compact = width < 720f;
        float toolbar = 40f;
        float footer = 24f;
        float workspace = Math.Max(80f, height - toolbar - footer);
        float list = compact ? Math.Max(200f, width * 0.48f) : Math.Max(240f, width * 0.42f);
        float detail = Math.Max(200f, width - list - 1f);
        return new GenCandidateGalleryLayout(compact, toolbar, workspace, footer, list, detail);
    }

    private static GenCandidate ToCandidate(GenTaskRecord task, string? chosen, bool discarded)
        => new(
            task.TaskId,
            task.Provider,
            task.Recipe.Kind,
            task.Recipe.Prompt,
            StateName(task.State),
            task.InputHash,
            task.ImportedAssets ?? new List<string>(),
            task.CreatedAtUtc,
            Chosen: string.Equals(chosen, task.TaskId, StringComparison.Ordinal),
            Discarded: discarded);

    private static string StateName(GenTaskState state) => state switch
    {
        GenTaskState.Pending => "pending",
        GenTaskState.Running => "running",
        GenTaskState.Succeeded => "succeeded",
        GenTaskState.Failed => "failed",
        _ => state.ToString().ToLowerInvariant(),
    };

    private GenGalleryDocument LoadDoc()
    {
        if (!File.Exists(_galleryPath))
            return new GenGalleryDocument(null, Array.Empty<string>());
        string text = File.ReadAllText(_galleryPath);
        return JsonSerializer.Deserialize<GenGalleryDocument>(text, JsonOptions)
            ?? new GenGalleryDocument(null, Array.Empty<string>());
    }

    private void SaveDoc(GenGalleryDocument doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_galleryPath)!);
        string tmp = _galleryPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
        File.Move(tmp, _galleryPath, overwrite: true);
    }
}

/// <summary>Editor gallery split: list | detail. Compact under 720px.</summary>
public readonly record struct GenCandidateGalleryLayout(
    bool Compact, float ToolbarHeight, float WorkspaceHeight, float FooterHeight, float ListWidth, float DetailWidth);
