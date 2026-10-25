namespace XEngine.Ai.Protocol;

/// <summary>
/// Stable protocol constants shared between the CLI Host, the in-process MCP server,
/// and any external tooling that speaks the XEngine AI protocol. Version-pinned so a
/// client and server can detect drift (XMcp.Pattern).
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Wire-protocol version. Bumped on breaking DTO/semantic changes.</summary>
    public const string CurrentVersion = "0.1.0";

    /// <summary>Process-exit codes (the JSON-vs-error contract, Unity CLI style).</summary>
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int Error = 1;
        public const int Canceled = 130;
    }

    /// <summary>Default response/request size limits enforced everywhere.</summary>
    public static class Limits
    {
        public const int MaxRequestBytes = 1 * 1024 * 1024;       // 1 MiB
        public const int MaxResponseBytes = 4 * 1024 * 1024;       // 4 MiB
        public const int MaxIdempotencyKeyLength = 128;
        public const int DefaultOperationTimeoutSeconds = 12 * 60 * 60; // 12 h
        public const int OperationRetentionDays = 7;
        /// <summary>★ P0-3: hard ceiling on the number of root nodes scene_hierarchy /
        /// prefab_hierarchy will return in a single response, regardless of the caller's maxObjects.
        /// Keeps a pathological scene from blowing the 4 MiB response budget.</summary>
        public const int MaxObjectsPerHierarchy = 2000;
    }
}
