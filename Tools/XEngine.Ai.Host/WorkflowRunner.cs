using System.Text.Json;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B4-cont) — Executes a declarative workflow as a synchronous DAG. XEngine has no Domain
/// Reload, so (unlike Unity/XMcp) a long operation does NOT need a ticket/polling store — each step
/// runs to completion inline and the workflow returns when all steps finish. Dependencies are
/// topo-sorted; a failed step applies its failurePolicy (Stop | Continue | Rollback | Compensate —
/// only Stop/Continue implemented for the MVP; Rollback/Compensate are no-ops that stop the run).
///
/// <para>Step arguments support <c>${inputs.x}</c> / <c>${steps.y.field}</c> templating. Tools are
/// resolved via the injected <see cref="WorkflowToolDispatcher"/> (name -> result).</para>
/// </summary>
internal sealed class WorkflowRunner
{
    private readonly WorkflowToolDispatcher _dispatcher;

    public WorkflowRunner(WorkflowToolDispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition definition,
        JsonElement inputs,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeInputs(definition, inputs);
        var order = TopoSort(definition.Steps);
        var results = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var stepResults = new List<WorkflowStepResult>();

        foreach (string stepName in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowStep step = definition.Steps.First(s => s.Name == stepName);

            JsonElement args = Template(step.Arguments, normalized, results);

            if (dryRun)
            {
                stepResults.Add(new WorkflowStepResult(step.Name, step.Tool, Succeeded: true, Skipped: true,
                    Error: null, JsonSerializer.SerializeToElement(new { dryRun = true, args })));
                results[step.Name] = JsonSerializer.SerializeToElement(new { dryRun = true });
                continue;
            }

            bool succeeded;
            JsonElement data;
            string? error = null;
            try
            {
                object result = await _dispatcher.InvokeAsync(step.Tool, args, cancellationToken);
                data = JsonSerializer.SerializeToElement(result, result.GetType(), JsonOutput.Options);
                succeeded = true;
            }
            catch (Exception ex)
            {
                succeeded = false;
                data = JsonSerializer.SerializeToElement(new { });
                error = ex.Message;
            }

            results[step.Name] = data;
            stepResults.Add(new WorkflowStepResult(step.Name, step.Tool, succeeded, Skipped: false, error, data));

            if (!succeeded && step.FailurePolicy is "Stop")
            {
                break;
            }
            if (!succeeded && step.FailurePolicy is "Rollback" or "Compensate")
            {
                // ★ P1-2: Rollback/Compensate are NOT implemented (reversing prior steps requires an
                // inverse-op registry that doesn't exist yet). Refuse loudly with an actionable error
                // instead of silently stopping — the AI should know it can't rely on these policies.
                throw new NotImplementedException(
                    $"Workflow step '{step.Name}' failed with FailurePolicy='{step.FailurePolicy}', but " +
                    "Rollback/Compensate are not implemented (no inverse-op registry). Use 'Stop' (halt) or " +
                    "'Continue' (proceed). Compensating logic must be expressed as explicit later steps.");
            }
            // FailurePolicy == "Continue": proceed to next step.
        }

        bool anyFailed = stepResults.Any(r => !r.Succeeded && !r.Skipped);
        return new WorkflowRunResult(definition.Name, !anyFailed, dryRun, stepResults);
    }

    private static Dictionary<string, JsonElement> NormalizeInputs(WorkflowDefinition def, JsonElement inputs)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (inputs.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return dict;
        }

        foreach (JsonProperty p in inputs.EnumerateObject())
        {
            dict[p.Name] = p.Value.Clone();
        }

        return dict;
    }

    /// <summary>Kahn's topo-sort over DependsOn. Throws on a cycle.</summary>
    private static List<string> TopoSort(IReadOnlyList<WorkflowStep> steps)
    {
        var byName = steps.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var indegree = steps.ToDictionary(s => s.Name, s => 0, StringComparer.Ordinal);
        var dependents = steps.ToDictionary(s => s.Name, s => new List<string>(), StringComparer.Ordinal);

        foreach (WorkflowStep s in steps)
        {
            foreach (string dep in s.DependsOn)
            {
                if (!byName.ContainsKey(dep)) throw new InvalidOperationException($"Step '{s.Name}' depends on unknown step '{dep}'.");
                indegree[s.Name]++;
                dependents[dep].Add(s.Name);
            }
        }

        var queue = new Queue<string>(steps.Where(s => indegree[s.Name] == 0).Select(s => s.Name));
        var order = new List<string>();
        while (queue.Count > 0)
        {
            string n = queue.Dequeue();
            order.Add(n);
            foreach (string d in dependents[n])
            {
                if (--indegree[d] == 0) queue.Enqueue(d);
            }
        }

        if (order.Count != steps.Count)
        {
            throw new InvalidOperationException("Workflow has a dependency cycle.");
        }

        return order;
    }

    /// <summary>Substitute ${inputs.x} / ${steps.y.field} tokens in string values within the args tree.
    /// ${steps.y.field} navigates a prior step's JSON result (e.g. ${steps.build.afterHash}).</summary>
    private static JsonElement Template(JsonElement args, Dictionary<string, JsonElement> inputs, Dictionary<string, JsonElement?> stepResults)
    {
        string json = JsonSerializer.Serialize(args);

        foreach (var (k, v) in inputs)
        {
            json = Inject(json, "${inputs." + k + "}", v);
        }

        // ${steps.<stepName>.<fieldPath>} → navigate the step's result JSON. fieldPath may be dotted.
        foreach (var (stepName, result) in stepResults)
        {
            if (result is null || result.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            // Walk the result object's top-level fields AND allow dotted deeper paths by scanning all
            // ${steps.<stepName>. ...} tokens present in the json and resolving their suffix path.
            json = InjectStepTokens(json, stepName, result.Value);
        }

        return JsonSerializer.SerializeToElement(JsonDocument.Parse(json).RootElement.Clone());
    }

    private static string Inject(string json, string token, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return json.Replace(token, value.GetString() ?? "");
        }

        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.False or JsonValueKind.True)
        {
            return json.Replace("\"" + token + "\"", value.GetRawText()); // unquote so numbers/bools inject
        }

        return json; // objects/arrays not injected as scalars
    }

    private static string InjectStepTokens(string json, string stepName, JsonElement result)
    {
        string prefix = "${steps." + stepName + ".";
        int i = 0;
        // Find every occurrence of ${steps.<stepName>.<path>} and resolve <path> against the result.
        while ((i = json.IndexOf(prefix, i, StringComparison.Ordinal)) >= 0)
        {
            int end = json.IndexOf('}', i);
            if (end < 0) break;
            string token = json[i..(end + 1)];                // ${steps.x.afterHash}
            string path = json[(i + prefix.Length)..end];     // afterHash   (may contain dots)
            if (TryNavigate(result, path, out JsonElement leaf))
            {
                json = Inject(json, token, leaf);
                // Inject may shorten the string; don't advance i (the replacement shifted everything).
            }
            else
            {
                i = end + 1; // unresolvable token: skip past it to avoid infinite loop
            }
        }

        return json;
    }

    /// <summary>Navigate a dotted path (a.b.c) through a JSON tree. Numeric segments index into
    /// arrays (★ Gen-P1.5: e.g. <c>assets.0.subAssets.1.guid</c> reaches into gen_fetch results).
    /// Returns false if absent.</summary>
    private static bool TryNavigate(JsonElement root, string dottedPath, out JsonElement leaf)
    {
        leaf = root;
        foreach (string seg in dottedPath.Split('.'))
        {
            if (leaf.ValueKind == JsonValueKind.Array && int.TryParse(seg, out int index))
            {
                if (index < 0 || index >= leaf.GetArrayLength())
                {
                    return false;
                }

                leaf = leaf[index];
                continue;
            }

            if (leaf.ValueKind != JsonValueKind.Object || !leaf.TryGetProperty(seg, out leaf))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Resolves a tool name + args to a result object. The Host wires this to its tool services.</summary>
internal interface WorkflowToolDispatcher
{
    Task<object> InvokeAsync(string tool, JsonElement arguments, CancellationToken cancellationToken);
}
