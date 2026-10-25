using System.Text.Json;

namespace XEngine.Ai.Protocol;

/// <summary>
/// ★ v2 — Provenance attached to every recorded change (Layer 0). This is the core of the
/// "process-aware asset" layer: without it, an AI can read current state but not WHY a scene
/// evolved to it. Carried both on the on-disk operation journal (A2) and on enriched undo
/// records (A0/A1). See roadmap §4.
/// </summary>
public sealed record ChangeProvenance(
    /// <summary>Who made the change: a username, "editor", or an AI agent/tool name.</summary>
    string Author,
    /// <summary>Natural-language intent/note ("darken the scene to dusk for cutscene X").</summary>
    string? Intent,
    /// <summary>What produced the change: "editor.gizmo", "xengine-cli.scene-object-patch", ...</summary>
    string Source,
    DateTimeOffset TimestampUtc,
    /// <summary>Optional stable id of the parent change this one builds on (delta chain link).</summary>
    string? ParentDeltaId = null,
    /// <summary>For AI-driven writes, the idempotency key that produced this change.</summary>
    string? IdempotencyKey = null);

/// <summary>
/// One structured change operation persisted to the project journal (A2). The delta is stored as
/// its serialized text form so any consumer (Host, AI agent) can read it without an Echo dependency,
/// and replay it via the scene-write tools. Carries flattened provenance fields.
/// </summary>
public sealed record ChangeRecord(
    string ChangeId,
    string ProjectId,
    /// <summary>Asset path or guid the change targets (".scene" file, prefab, asset).</summary>
    string TargetAsset,
    /// <summary>SHA-256 of the target's serialized form before the change (drift detection).</summary>
    string BeforeHash,
    /// <summary>SHA-256 after the change.</summary>
    string AfterHash,
    /// <summary>The Echo delta payload, serialized to text (stable, replayable via ApplyDelta).</summary>
    string DeltaText,
    string Author,
    string? Intent,
    string Source,
    DateTimeOffset TimestampUtc,
    DateTimeOffset RecordedAtUtc);

/// <summary>Filters for the journal-query read tool (B1).</summary>
public sealed record JournalQuery(
    string? TargetAsset = null,
    DateTimeOffset? SinceUtc = null,
    DateTimeOffset? UntilUtc = null,
    string? ByAuthor = null,
    string? IntentContains = null,
    int Offset = 0,
    int Limit = 100);

/// <summary>Paged journal-query result. ★ P0-3: <c>HasMore</c> tells the AI whether more records
/// exist beyond the returned page.</summary>
public sealed record JournalPage(
    string? TargetAsset,
    int Offset,
    int Limit,
    int TotalAvailable,
    IReadOnlyList<ChangeRecord> Changes,
    bool HasMore = false);
