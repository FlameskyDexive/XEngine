using System;
using System.IO;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Harness;

/// <summary>Where the engine's shippable pieces (CLI, skills) were found.</summary>
public sealed record EngineLayout(
    string EngineRoot,
    /// <summary>Directory containing the per-skill folders (each with a SKILL.md).</summary>
    string SkillsSource,
    /// <summary>How the offline CLI should be invoked from this layout (dev: dotnet + repo dll;
    /// shipped: the self-contained tools/xengine-cli binary).</summary>
    CliInvocation Cli);

/// <summary>How to spawn `xengine-cli serve` for an MCP client config.</summary>
public sealed record CliInvocation(string Command, string[] BaseArgs)
{
    public string[] ServeArgs(string projectRoot) =>
        [.. BaseArgs, "serve", "--project", projectRoot];
}

/// <summary>
/// Locates the engine installation relative to a starting directory, in either layout:
/// <list type="bullet">
/// <item>Engine-repo (dev): up-walk finds <c>XEngine.sln</c> + <c>Tools/</c>; skills live in
/// <c>cli/skills/</c>; the CLI runs via <c>dotnet Tools/XEngine.Ai.Host/bin/Debug/net10.0/xengine-cli.dll</c>
/// (the canonical Debug path — the dll may not exist yet on a fresh clone, the wiring still points there).</item>
/// <item>Shipped install: a directory holding <c>harness/skills/</c> (release zips place it at the
/// install root next to the editor, with the CLI under <c>tools/</c>).</item>
/// </list>
/// Note the edition is NOT inferred here: the harness installed INTO a game project is always the
/// user-edition harness (the engine repository itself carries its own engine-dev AGENTS.md/.mcp.json).
/// </summary>
public static class EngineLocator
{
    /// <summary>
    /// Locates the engine layout for a project: up-walk from the project root first (project may
    /// live inside the engine repo), then from the running process's base directory (a dev editor/
    /// CLI running from the repo, or a shipped install where the process sits next to harness/).
    /// </summary>
    public static EngineLayout? LocateFromProject(string projectRoot, string? explicitEngineRoot)
    {
        if (explicitEngineRoot is not null) return Describe(explicitEngineRoot);
        return Locate(projectRoot) ?? Locate(AppContext.BaseDirectory);
    }

    /// <summary>Locates the engine layout by up-walking from <paramref name="startDir"/>
    /// (typically the editor/CLI process base directory, or the project root).</summary>
    public static EngineLayout? Locate(string startDir)
    {
        DirectoryInfo? dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            EngineLayout? layout = Describe(dir.FullName);
            if (layout is not null) return layout;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Recognizes an engine layout at exactly <paramref name="candidateRoot"/>, or null.</summary>
    public static EngineLayout? Describe(string candidateRoot)
    {
        if (!Directory.Exists(candidateRoot)) return null;

        // Dev layout: the engine repository itself.
        if (File.Exists(Path.Combine(candidateRoot, "XEngine.sln"))
            && Directory.Exists(Path.Combine(candidateRoot, "Tools")))
        {
            string devSkills = Path.Combine(candidateRoot, "cli", "skills");
            if (!Directory.Exists(devSkills)) return null;
            string devDll = Path.Combine(candidateRoot, "Tools", "XEngine.Ai.Host", "bin", "Debug", "net10.0", "xengine-cli.dll");
            return new EngineLayout(candidateRoot, devSkills,
                new CliInvocation("dotnet", [devDll]));
        }

        // Shipped layout: harness/skills at the install root, CLI under tools/.
        string skills = Path.Combine(candidateRoot, "harness", "skills");
        if (!Directory.Exists(skills)) return null;
        string cliExe = Path.Combine(candidateRoot, "tools", OperatingSystem.IsWindows() ? "xengine-cli.exe" : "xengine-cli");
        return new EngineLayout(candidateRoot, skills,
            new CliInvocation(cliExe, []));
    }
}
