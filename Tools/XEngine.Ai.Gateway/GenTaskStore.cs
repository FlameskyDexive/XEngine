namespace XEngine.Ai.Gateway;

/// <summary>One tracked generation task: recipe + provider task id + lifecycle + downloaded artifacts.</summary>
public sealed class GenTaskRecord
{
    public required string TaskId { get; set; }
    public required string Provider { get; set; }
    public required string InputHash { get; set; }
    public required GenRecipe Recipe { get; set; }
    public GenTaskState State { get; set; } = GenTaskState.Pending;
    public double? EstimatedCredits { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<GenArtifact> Artifacts { get; set; } = new();
    /// <summary>Project-relative asset paths produced by gen_fetch's import step (P1.2).</summary>
    public List<string> ImportedAssets { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// ★ Gen-P1.1 — Durable task ledger at <c>{projectRoot}/.xengine/gen-tasks.json</c>. Every CLI
/// invocation is a separate process, so submit → poll → fetch can only work if the recipe and
/// provider task id survive on disk. Doubles as the idempotency index: gen_submit consults
/// <see cref="FindReusableByHash"/> before touching a paid API, so re-running a workflow with an
/// unchanged recipe reuses the original task instead of billing again.
/// </summary>
public sealed class GenTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;

    public GenTaskStore(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _path = Path.Combine(projectRoot, ".xengine", "gen-tasks.json");
    }

    public GenTaskRecord Add(GenRecipe recipe, string taskId, double? estimatedCredits)
    {
        List<GenTaskRecord> tasks = Load();
        var record = new GenTaskRecord
        {
            TaskId = taskId,
            Provider = recipe.Provider,
            InputHash = recipe.ComputeInputHash(),
            Recipe = recipe,
            State = GenTaskState.Pending,
            EstimatedCredits = estimatedCredits,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        tasks.RemoveAll(t => t.TaskId == taskId);
        tasks.Add(record);
        Save(tasks);
        return record;
    }

    public GenTaskRecord? Get(string taskId)
    {
        foreach (GenTaskRecord task in Load())
        {
            if (task.TaskId == taskId)
            {
                return task;
            }
        }

        return null;
    }

    /// <summary>The idempotency lookup: an existing non-failed task with the same input hash is the
    /// same request — reuse it instead of resubmitting. Failed tasks don't block a retry.</summary>
    public GenTaskRecord? FindReusableByHash(string inputHash)
    {
        foreach (GenTaskRecord task in Load())
        {
            if (task.InputHash == inputHash && task.State != GenTaskState.Failed)
            {
                return task;
            }
        }

        return null;
    }

    /// <summary>Reload-mutate-save so concurrent CLI invocations can't resurrect stale snapshots.</summary>
    public GenTaskRecord Update(string taskId, Action<GenTaskRecord> mutate)
    {
        List<GenTaskRecord> tasks = Load();
        GenTaskRecord? record = null;
        foreach (GenTaskRecord task in tasks)
        {
            if (task.TaskId == taskId)
            {
                record = task;
                break;
            }
        }

        if (record is null)
        {
            throw new InvalidOperationException($"Unknown generation task '{taskId}' — not in {_path}.");
        }

        mutate(record);
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        Save(tasks);
        return record;
    }

    public IReadOnlyList<GenTaskRecord> All() => Load();

    private List<GenTaskRecord> Load()
    {
        if (!File.Exists(_path))
        {
            return new List<GenTaskRecord>();
        }

        string json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<GenTaskRecord>();
        }

        return JsonSerializer.Deserialize<List<GenTaskRecord>>(json, JsonOptions) ?? new List<GenTaskRecord>();
    }

    private void Save(List<GenTaskRecord> tasks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Atomic temp+rename, matching the journal/meta writers: a crash never truncates the ledger.
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(tasks, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
