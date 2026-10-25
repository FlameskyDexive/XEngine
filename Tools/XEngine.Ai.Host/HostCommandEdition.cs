using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// CLI-verb ↔ edition gate. Mirrors the MCP-side tool filtering in <see cref="ToolTypeCatalog"/>:
/// verbs backed by engine-repo-only tools (<c>EngineDevTools</c>) are refused outside the
/// engine-dev edition with a structured <see cref="AiError.Codes.EditionForbidden"/> envelope,
/// so a user-project agent gets "not in your edition" instead of a misleading FileNotFound from
/// deep inside the tool.
/// </summary>
internal static class HostCommandEdition
{
    /// <summary>The edition a verb requires, or null when it runs in every edition.</summary>
    public static AiEdition? RequiredEdition(HostCommand command) => command switch
    {
        HostCommand.CodeSymbols or HostCommand.RunTests => AiEdition.EngineDev,
        _ => null,
    };
}
