using System.Text.Json;

namespace XEngine.Ai.Protocol;

/// <summary>
/// Stable lifecycle state of a long-running operation. Any write tool that may outlive a
/// single request (compile, bake, build, import) returns a ticket and transitions through these.
/// </summary>
public enum OperationState
{
    Queued,
    Running,
    WaitingReload,
    Succeeded,
    Failed,
    Canceled,
    TimedOut,
    Interrupted,
}

/// <summary>Final business status of a tool invocation.</summary>
public enum ToolExecutionStatus
{
    Succeeded,
    Failed,
    Pending,
    Canceled,
    TimedOut,
}

/// <summary>Outcome of a cancel request against an operation.</summary>
public enum OperationCancelStatus
{
    Canceled,
    CancelRequested,
    NotCancelable,
    AlreadyTerminal,
    Unavailable,
}

/// <summary>A log line, report, or file artifact produced by a tool invocation.</summary>
public sealed record ArtifactRef(
    string Kind,
    string Path,
    string? Description = null);

/// <summary>Structured result of a tool call.</summary>
public sealed record ToolExecutionResult(
    ToolExecutionStatus Status,
    string Summary,
    JsonElement? Data,
    string? OperationTicket,
    AiError? Error,
    IReadOnlyList<ArtifactRef> Artifacts,
    long DurationMs);

/// <summary>
/// A long-task record that survives process and engine restarts (journal-backed).
/// Ported from XMcp.OperationRecord; the on-disk journal (Layer 0 A2) is the store of these.
/// </summary>
public sealed record OperationRecord(
    string Ticket,
    string ProjectId,
    string ToolName,
    OperationState State,
    string? CurrentStep,
    double? Progress,
    JsonElement? Checkpoint,
    ToolExecutionResult? Result,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? IdempotencyKey = null,
    DateTimeOffset? DeadlineUtc = null,
    DateTimeOffset? CancelRequestedAtUtc = null);

/// <summary>Stable return contract for operation cancel.</summary>
public sealed record OperationCancelResult(
    OperationCancelStatus Status,
    string Message,
    OperationRecord Operation);
