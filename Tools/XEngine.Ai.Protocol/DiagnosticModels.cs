using System.Text.Json;

namespace XEngine.Ai.Protocol;

/// <summary>Status of a single doctor diagnostic check.</summary>
public enum DoctorStatus
{
    Passed,
    Warning,
    Failed,
    Skipped,
}

/// <summary>One actionable environment diagnostic result.</summary>
public sealed record DoctorCheck(
    string Name,
    DoctorStatus Status,
    string Message,
    string? SuggestedAction = null,
    JsonElement? Details = null);

/// <summary>Aggregate doctor report across Host, project, tooling and client config.</summary>
public sealed record DoctorReport(
    DoctorStatus Status,
    string ProjectId,
    IReadOnlyList<DoctorCheck> Checks,
    DateTimeOffset CheckedAtUtc);

/// <summary>
/// Health snapshot of the AI tooling layer + its connection to a live engine instance.
/// <c>EngineOnline</c> is false for the offline CLI path and true once an in-process MCP
/// server (Layer 2) is attached.
/// </summary>
public sealed record HostHealthSnapshot(
    string Status,
    string HostVersion,
    string ProtocolVersion,
    string Edition,
    ProjectDescriptor Project,
    bool EngineOnline,
    EngineInstanceDescriptor? EngineInstance,
    DateTimeOffset CheckedAtUtc);

/// <summary>Describes a discoverable running engine instance (Layer 2 in-process server).</summary>
public sealed record EngineInstanceDescriptor(
    string ServerId,
    int? Port,
    string ProjectId,
    string CapabilitiesHash);
