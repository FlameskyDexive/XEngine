namespace XEngine.Ai.Protocol;

/// <summary>
/// Risk classification of a tool, surfaced via the capabilities snapshot so an agent (or
/// client UI) can gate destructive actions. Same taxonomy as XMcp.
/// </summary>
public enum RiskLevel
{
    /// <summary>Read-only: no project state mutated.</summary>
    ReadOnly,

    /// <summary>Writes project state in a reversible/idempotent way (dry-run supported).</summary>
    SafeWrite,

    /// <summary>Irreversible mutation (delete, overwrite, force-reimport).</summary>
    Destructive,

    /// <summary>Controls engine process lifecycle (playmode, restart, build spawn).</summary>
    ProcessControl,
}

/// <summary>Capability metadata for one stable tool.</summary>
public sealed record CapabilityToolDescriptor(
    string Name,
    string Group,
    string Description,
    RiskLevel RiskLevel,
    bool RequiresEngine,     // true = needs a running engine instance; false = CLI/offline-capable
    bool SupportsDryRun,
    bool SupportsCancel,
    bool MayReloadScripts,   // XEngine analog of Unity's "MayReloadDomain"
    string Version);

/// <summary>An independently versioning capability pack.</summary>
public sealed record CapabilityPackDescriptor(
    string Name,
    string Version,
    string Status,
    IReadOnlyList<string> Tools);

/// <summary>
/// Full capability snapshot advertised to clients. <c>Edition</c> names the audience the serving
/// host resolved (engine-dev | user); <c>Tools</c> only ever contains tools visible to that edition.
/// </summary>
public sealed record CapabilitySnapshot(
    string ProtocolVersion,
    string HostVersion,
    string ProjectId,
    string Edition,
    IReadOnlyList<CapabilityToolDescriptor> Tools,
    IReadOnlyList<CapabilityPackDescriptor> Packs,
    IReadOnlyDictionary<string, int> Limits,
    DateTimeOffset GeneratedAtUtc);
