namespace XEngine.Ai.Protocol;

/// <summary>★ Layer 1 (B1) — Code index status.</summary>
public sealed record CodeIndexStatus(
    bool Available,
    string SolutionPath,
    int ProjectCount,
    int DocumentCount,
    string? Error);

/// <summary>One symbol found by the code_symbol tool.</summary>
public sealed record CodeSymbol(
    string Name,
    string Kind,
    string ContainingType,
    string Project,
    string FilePath,
    int Line,
    int Column,
    string DisplayString);

/// <summary>Result of a code_symbol query.</summary>
public sealed record CodeSymbolResult(
    CodeIndexStatus Status,
    int Offset,
    int Limit,
    int TotalAvailable,
    IReadOnlyList<CodeSymbol> Symbols);

// ---- find_in_file (regex content search) ----

/// <summary>★ One regex match in a file: line number + the matching excerpt.</summary>
public sealed record FileSearchMatch(
    string RelativePath,
    int LineNumber,
    /// <summary>The matching line (trimmed to 300 chars to keep responses bounded).</summary>
    string Excerpt);

/// <summary>★ Result of a find_in_file query: files scanned, matches, hasMore.</summary>
public sealed record FileSearchResult(
    /// <summary>The regex pattern that was searched.</summary>
    string Pattern,
    /// <summary>The glob/path that was searched.</summary>
    string Scope,
    int FilesScanned,
    IReadOnlyList<FileSearchMatch> Matches,
    /// <summary>True when more matches exist beyond the returned page.</summary>
    bool HasMore);

// ---- script management (create/delete/apply_edits) ----

/// <summary>★ One structured text edit for script_apply_edits: find oldText, replace with newText.
/// <c>Occurrence</c> controls multi-match behavior: "first" (default, requires uniqueness), "all".</summary>
public sealed record ScriptTextEdit(
    /// <summary>The exact text to find (case-sensitive, whitespace-significant).</summary>
    string OldText,
    /// <summary>The replacement text.</summary>
    string NewText,
    /// <summary>"first" (replace the first match; error if OldText appears more than once) or "all".
    /// Default "first".</summary>
    string Occurrence = "first");

/// <summary>★ Result of script_create/script_delete/script_apply_edits: the target path, the new
/// content SHA-256 (for get_sha / change tracking), and a summary.</summary>
public sealed record ScriptEditResult(
    string TargetAsset,
    /// <summary>SHA-256 of the file content after the edit (empty for delete).</summary>
    string ContentSha,
    /// <summary>For apply_edits: how many edits were applied.</summary>
    int EditsApplied,
    string Summary);
