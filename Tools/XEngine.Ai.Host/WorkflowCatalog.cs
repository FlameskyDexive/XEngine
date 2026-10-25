using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B4 foundation) — Loads declarative workflow definitions from
/// <c>{projectRoot}/.xengine/workflows/*.json</c> and lists them. Workflow execution
/// (<c>workflow_run</c> with DAG dependencies + failure policy + ticketed operations) is a follow-up
/// that lands with the operation store; this catalog + the stable DTO contract let workflows be
/// authored now. (Shrunk from XMcp.WorkflowCatalog.)
/// </summary>
internal sealed class WorkflowCatalog
{
    private readonly string _projectRoot;

    public WorkflowCatalog(string projectRoot) => _projectRoot = projectRoot;

    public WorkflowListResult List()
    {
        return new WorkflowListResult(
            LoadAll().Select(kv => new WorkflowDescriptor(
                kv.Value.Name, kv.Value.Description, kv.Value.Steps.Count,
                kv.Value.Steps.Select(s => s.Name).ToArray(), Available: true))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Get a workflow definition by name, or null if absent.</summary>
    public WorkflowDefinition? Get(string name)
        => LoadAll().TryGetValue(name, out WorkflowDefinition? def) ? def : null;

    private Dictionary<string, WorkflowDefinition> LoadAll()
    {
        var dict = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        string dir = Path.Combine(_projectRoot, ".xengine", "workflows");
        if (!Directory.Exists(dir)) return dict;

        foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                WorkflowDefinition? def = ParseDefinition(file);
                if (def is not null) dict[def.Name] = def;
            }
            catch { /* skip malformed */ }
        }

        return dict;
    }

    private static WorkflowDefinition? ParseDefinition(string file)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = doc.RootElement;
        string name = root.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(file);
        string description = root.TryGetProperty("description", out JsonElement d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? "" : "";

        JsonElement inputs = root.TryGetProperty("inputs", out JsonElement i) ? i.Clone() : JsonSerializer.SerializeToElement(new { });
        JsonElement stepsEl = root.GetProperty("steps");

        var steps = new List<WorkflowStep>();
        foreach (JsonElement s in stepsEl.EnumerateArray())
        {
            steps.Add(new WorkflowStep(
                Name: s.GetProperty("name").GetString() ?? "",
                Tool: s.GetProperty("tool").GetString() ?? "",
                Arguments: s.TryGetProperty("arguments", out JsonElement a) ? a.Clone() : JsonSerializer.SerializeToElement(new { }),
                DependsOn: s.TryGetProperty("dependsOn", out JsonElement dep) && dep.ValueKind == JsonValueKind.Array
                    ? dep.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
                FailurePolicy: s.TryGetProperty("failurePolicy", out JsonElement fp) && fp.ValueKind == JsonValueKind.String
                    ? fp.GetString() ?? "Stop" : "Stop"));
        }

        return new WorkflowDefinition(name, description, inputs, steps);
    }
}
