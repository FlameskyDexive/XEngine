using System.Text.Json;

namespace XEngine.Ai.Protocol;

/// <summary>
/// Stable error envelope crossing every tool boundary (CLI, MCP, in-process).
/// Mirrors XMcp.XMcpError: a typed code, human message, retryability hint, and an
/// optional suggested next action so an agent can self-correct.
/// </summary>
public sealed record AiError(
    string Code,
    string Message,
    bool Retryable,
    string? SuggestedAction = null,
    JsonElement? Details = null)
{
    // Canonical error codes (used by both the CLI top-level catch and tool handlers).
    public static class Codes
    {
        public const string InvalidArguments = "invalid_arguments";
        public const string NotFound = "not_found";
        public const string Timeout = "timeout";
        public const string IoError = "io_error";
        public const string OperationFailed = "operation_failed";
        public const string OperationConflict = "operation_conflict";
        public const string StaleState = "stale_state";
        public const string Unauthorized = "unauthorized";
        public const string ProtocolMismatch = "protocol_mismatch";
        public const string ProjectMismatch = "project_mismatch";
        public const string ToolNotFound = "tool_not_found";
        public const string PayloadTooLarge = "payload_too_large";
        public const string EditionForbidden = "edition_forbidden";
        public const string InternalError = "internal_error";
    }
}
