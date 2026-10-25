using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Produces the Host health snapshot. <c>EngineOnline</c> is false for the offline CLI path
/// (Layer 1); it flips true once an in-process MCP server (Layer 2) attaches and reports an
/// <see cref="EngineInstanceDescriptor"/>. Mirrors XMcp.HealthService.
/// </summary>
internal sealed class HealthService
{
    private const string HostVersion = "0.1.0";
    private readonly ProjectDescriptor _project;
    private readonly AiEdition _edition;

    public HealthService(ProjectDescriptor project, AiEdition edition)
    {
        _project = project;
        _edition = edition;
    }

    public HostHealthSnapshot GetSnapshot()
    {
        // Layer 2 not wired yet: the Host is always offline-capable for now.
        return new HostHealthSnapshot(
            Status: "healthy",
            HostVersion: HostVersion,
            ProtocolVersion: ProtocolConstants.CurrentVersion,
            Edition: _edition.ToWireName(),
            Project: _project,
            EngineOnline: false,
            EngineInstance: null,
            CheckedAtUtc: DateTimeOffset.UtcNow);
    }
}
