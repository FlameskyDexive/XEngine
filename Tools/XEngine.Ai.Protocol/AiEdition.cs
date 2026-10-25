using System;
using System.IO;

namespace XEngine.Ai.Protocol;

/// <summary>
/// Which audience a tool (or a running AI host) serves. The SAME xengine-cli binary ships
/// everywhere; the edition only decides the registered tool surface + the agent scaffolding
/// (harness) behavior:
/// <list type="bullet">
/// <item><see cref="EngineDev"/> — developing the engine repository itself. Full surface, including
/// engine-source tools (solution code index, engine test suite) and future engine-internal tools.</item>
/// <item><see cref="User"/> — game development in a user project created against a distributed
/// engine. Game-content tools only; engine-source tools are filtered out server-side.</item>
/// </list>
/// Resolution: an explicit <c>--edition</c> flag wins; otherwise auto-detection
/// (see <see cref="AiEditionDetector"/>).
/// </summary>
public enum AiEdition
{
    EngineDev,
    User,
}

/// <summary>
/// Marks an MCP tool class as belonging to one edition only. Tool classes without this attribute
/// serve every edition. Checked by the tool-type scanners in the offline Host and the in-process
/// server, so the registered surface and the capability snapshot never drift apart.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AiToolEditionAttribute(AiEdition edition) : Attribute
{
    public AiEdition Edition { get; } = edition;
}

/// <summary>
/// Auto-detects the edition from the project root layout. The engine repository is the only
/// place where <c>XEngine.sln</c> and <c>Tools/</c> sit side by side at the project root;
/// a user game project has a <c>*.xengine</c> file + <c>Assets/</c> instead.
/// </summary>
public static class AiEditionDetector
{
    public static AiEdition Detect(string projectRoot)
    {
        return File.Exists(Path.Combine(projectRoot, "XEngine.sln"))
            && Directory.Exists(Path.Combine(projectRoot, "Tools"))
            ? AiEdition.EngineDev
            : AiEdition.User;
    }
}

/// <summary>String forms used on the wire (capabilities/health snapshots, error messages).</summary>
public static class AiEditionNames
{
    public static string ToWireName(this AiEdition edition) => edition switch
    {
        AiEdition.EngineDev => "engine-dev",
        AiEdition.User => "user",
        _ => edition.ToString(),
    };

    /// <summary>
    /// Parses a <c>--edition</c> CLI value. Returns null for null/"auto" (= resolve by detection);
    /// throws for unknown values so a typo fails fast instead of silently serving the wrong surface.
    /// </summary>
    public static AiEdition? TryParse(string? value) => value?.ToLowerInvariant() switch
    {
        null or "auto" => null,
        "engine-dev" or "engine" or "enginedev" => AiEdition.EngineDev,
        "user" => AiEdition.User,
        _ => throw new ArgumentException(
            $"Unknown --edition value '{value}'. Expected engine-dev, user, or auto."),
    };
}
