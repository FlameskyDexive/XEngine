using System.Text.Json;

namespace XEngine.Ai.Protocol;

/// <summary>★ Layer 1 (B4, foundation) — A declarative workflow definition (JSON DAG of tool calls).
/// Ported/shrunk from XMcp.WorkflowModels. Execution (workflow_run with DAG deps + failure policy)
/// lands with the operation/ticket store; this defines the stable contract + list tool now so
/// workflows can be authored ahead of the executor.</summary>
public sealed record WorkflowDefinition(
    string Name,
    string Description,
    JsonElement Inputs,
    IReadOnlyList<WorkflowStep> Steps);

/// <summary>One step in a workflow DAG.</summary>
public sealed record WorkflowStep(
    string Name,
    string Tool,
    JsonElement Arguments,
    /// <summary>Step names this one depends on (must complete first).</summary>
    IReadOnlyList<string> DependsOn,
    /// <summary>What to do if this step fails: Stop | Continue | Rollback | Compensate.</summary>
    string FailurePolicy = "Stop");

/// <summary>A workflow listed by the catalog tool.</summary>
public sealed record WorkflowDescriptor(
    string Name,
    string Description,
    int StepCount,
    IReadOnlyList<string> Steps,
    bool Available);

/// <summary>Result of the workflow_list tool.</summary>
public sealed record WorkflowListResult(
    IReadOnlyList<WorkflowDescriptor> Workflows,
    DateTimeOffset GeneratedAtUtc);

/// <summary>Result of one step in a workflow run.</summary>
public sealed record WorkflowStepResult(
    string Step,
    string Tool,
    bool Succeeded,
    bool Skipped,
    string? Error,
    JsonElement Data);

/// <summary>Result of the workflow_run tool (synchronous DAG execution).</summary>
public sealed record WorkflowRunResult(
    string Name,
    bool Succeeded,
    bool DryRun,
    IReadOnlyList<WorkflowStepResult> Steps);
