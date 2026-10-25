namespace XEngine.Ai.Protocol;

// ★ Gen-P1.2 — gen_submit / gen_status / gen_fetch tool results. States travel as strings
// (pending | running | succeeded | failed | dryRun) so the protocol stays decoupled from the
// gateway's enum.

/// <summary>Result of gen_submit: the durable task handle (+ dedupe/dry-run flags).</summary>
public sealed record GenSubmitToolResult(
    /// <summary>Provider task id — poll with gen_status, ingest with gen_fetch. Empty for dryRun.</summary>
    string TaskId,
    string Provider,
    string Kind,
    /// <summary>Canonical recipe hash — the idempotency key that deduped (or would dedupe) this request.</summary>
    string InputHash,
    string State,
    double? EstimatedCredits,
    /// <summary>True when an existing non-failed task with the same InputHash was reused instead of
    /// resubmitting (no double billing).</summary>
    bool Deduplicated,
    bool DryRun);

/// <summary>★ Gen-P3.2 — One project style anchor as advertised to agents.</summary>
public sealed record StyleAnchorInfo(
    string Id,
    string? LoraModelId,
    IReadOnlyList<string> Palette,
    string? NegativePrompt,
    IReadOnlyList<string> ReferenceImageGuids);

/// <summary>★ Gen-P3.2 — All style anchors in <c>.xengine/style-anchors.json</c>.</summary>
public sealed record StyleAnchorListResult(IReadOnlyList<StyleAnchorInfo> Anchors);

/// <summary>★ Gen-P4.1 — One imported generation candidate for the editor gallery.</summary>
public sealed record GenCandidateInfo(
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

/// <summary>★ Gen-P4.1 — Filtered gallery listing (+ currently chosen task).</summary>
public sealed record GenCandidateListResult(
    IReadOnlyList<GenCandidateInfo> Candidates,
    string? ChosenTaskId);

/// <summary>Result of gen_status: normalized provider progress.</summary>
public sealed record GenStatusToolResult(
    string TaskId,
    string Provider,
    string State,
    /// <summary>0-100 when the provider reports progress.</summary>
    int? Progress,
    string? Message);

/// <summary>One artifact gen_fetch imported into the project.</summary>
public sealed record GenFetchedAsset(
    /// <summary>Project-relative asset path (e.g. Assets/GenArt/rusty-sword.glb).</summary>
    string AssetPath,
    /// <summary>The asset's stable GUID (preserved across re-roll overwrites).</summary>
    string Guid,
    /// <summary>Artifact role from the provider: model | texture | skybox | audio | thumbnail.</summary>
    string Role,
    string ImporterType,
    /// <summary>For model files: the derived sub-assets (meshes/materials/clips) with their
    /// deterministic GUIDs, ready for scene wiring. Null for non-model artifacts.</summary>
    IReadOnlyList<SubAssetInfo>? SubAssets);

/// <summary>Result of gen_fetch: everything downloaded + imported for one finished task.</summary>
public sealed record GenFetchToolResult(
    string TaskId,
    string State,
    IReadOnlyList<GenFetchedAsset> Assets,
    IReadOnlyList<string> Warnings);
