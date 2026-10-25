namespace XEngine.Ai.Protocol;

/// <summary>
/// Stable identity of the target XEngine project. Engine-agnostic (no Unity version);
/// <c>EngineVersion</c> reads from the project's Build settings, <c>SolutionPath</c> locates
/// XEngine.sln for the Roslyn code index. Fingerprint is a 16-char SHA-256 truncation
/// (XMcp.ProjectIdentity pattern) so the same logical project yields a stable id across runs.
/// </summary>
public sealed record ProjectDescriptor(
    string ProjectId,
    string ProjectRoot,
    string? EngineVersion,
    string? SolutionPath,
    string? GitBranch,
    string Fingerprint);

/// <summary>★ P2-4 — One workspace folder entry (the server-side equivalent of MCP Roots, which are
/// natively a client capability). Lets the AI enumerate the project's top-level directories so it
/// knows where Assets/, ProjectSettings/, .xengine/, etc. live.</summary>
public sealed record WorkspaceFolder(
    /// <summary>Folder/file name (e.g. "Assets", "Tags & Layers.yaml").</summary>
    string Name,
    /// <summary>Project-relative path.</summary>
    string RelativePath,
    /// <summary>Absolute filesystem path.</summary>
    string AbsolutePath,
    /// <summary>True when this entry is a directory, false when a file.</summary>
    bool IsDirectory);

/// <summary>★ P2-4 — A listing of a workspace folder's children (or the project root's top-level
/// entries when no folder is given). Mirrors MCP Roots' intent (workspace awareness) as a server tool,
/// since the MCP Roots primitive is client-side (the client publishes roots TO the server).</summary>
public sealed record WorkspaceListing(
    string Root,
    IReadOnlyList<WorkspaceFolder> Entries,
    /// <summary>True when more entries exist beyond the returned page.</summary>
    bool HasMore);
