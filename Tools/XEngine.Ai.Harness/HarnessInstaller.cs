using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Harness;

/// <summary>Outcome of one <see cref="HarnessInstaller.Install"/> run.</summary>
public sealed record HarnessInstallResult(
    bool Success,
    string? EngineRoot,
    string Edition,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public static HarnessInstallResult Failure(string error) =>
        new(false, null, AiEditionNames.ToWireName(AiEdition.User), [], [], [], error);
}

/// <summary>Current harness state of a project (harness status verb / editor diagnostics).</summary>
public sealed record HarnessStatus(
    bool Installed,
    string? HarnessVersion,
    string? Edition,
    string? EngineRoot,
    bool UpToDate,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> MissingSkills);

/// <summary>
/// Seeds the user-edition agent harness (三件套 + 技能包) into a game project:
/// <c>.mcp.json</c> (offline CLI + live editor wiring), <c>AGENTS.md</c> (agent guide, from the
/// embedded template), and the curated skills copied to <c>.xengine/harness/skills/</c>; stamped
/// by <c>.xengine/harness.json</c> for later <c>status</c>/<c>upgrade</c>. Idempotent: re-running
/// overwrites template outputs and re-copies skills.
/// </summary>
public static class HarnessInstaller
{
    public const string HarnessVersion = "0.1.0";
    public const string AgentsTemplateResource = "XEngine.Ai.Harness.Templates.AGENTS.md.tmpl";
    public const string ManifestResource = "XEngine.Ai.Harness.Templates.skills.manifest";

    private const string AgentsFileName = "AGENTS.md";
    private const string McpFileName = ".mcp.json";
    private const string StampFileName = "harness.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static HarnessInstallResult Install(string projectRoot, string? engineRoot = null)
    {
        string root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
        {
            return HarnessInstallResult.Failure($"Project root does not exist: '{root}'.");
        }

        EngineLayout? layout = EngineLocator.LocateFromProject(root, engineRoot);
        if (layout is null)
        {
            return HarnessInstallResult.Failure(
                "Could not locate the engine installation (looked up-walk for the engine repo " +
                "layout 'XEngine.sln + Tools/', then for a shipped 'harness/skills/' install). " +
                "Pass the engine root explicitly.");
        }

        var warnings = new List<string>();
        var files = new List<string>();

        // 1. Skills: copy every manifest-listed skill folder into the project.
        List<string> skills = ReadManifest();
        string skillsDest = Path.Combine(root, ".xengine", "harness", "skills");
        Directory.CreateDirectory(skillsDest);
        foreach (string skill in skills)
        {
            string source = Path.Combine(layout.SkillsSource, skill);
            if (!Directory.Exists(source))
            {
                warnings.Add($"skill '{skill}' not found in '{layout.SkillsSource}' — skipped.");
                continue;
            }
            CopyDirectory(source, Path.Combine(skillsDest, skill));
        }
        if (Directory.Exists(skillsDest))
        {
            files.Add(Path.Combine(".xengine", "harness", "skills"));
        }

        // 2. .mcp.json — offline CLI + optional live editor wiring, paths into the engine layout.
        var servers = new Dictionary<string, object>
        {
            ["xengine"] = Server(layout.Cli.Command, layout.Cli.ServeArgs(root)),
        };
        string? liveCommand = FindEditorBinary(layout);
        if (liveCommand is not null)
        {
            servers["xengine-live"] = Server(liveCommand, ["--serve", "--project", root]);
        }
        else
        {
            warnings.Add("editor binary not found — .mcp.json wired for the offline CLI only.");
        }
        string mcpPath = Path.Combine(root, McpFileName);
        File.WriteAllText(mcpPath, JsonSerializer.Serialize(new { mcpServers = servers }, JsonOptions));
        files.Add(McpFileName);

        // 3. AGENTS.md — the agent guide, placeholders resolved.
        string agentsPath = Path.Combine(root, AgentsFileName);
        string template = ReadEmbeddedResource(AgentsTemplateResource);
        File.WriteAllText(agentsPath, template
            .Replace("{{PROJECT_NAME}}", Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)))
            .Replace("{{ENGINE_ROOT}}", layout.EngineRoot)
            .Replace("{{HARNESS_VERSION}}", HarnessVersion));
        files.Add(AgentsFileName);

        // 4. Stamp for status/upgrade.
        string stampPath = Path.Combine(root, ".xengine", StampFileName);
        File.WriteAllText(stampPath, JsonSerializer.Serialize(new
        {
            harnessVersion = HarnessVersion,
            edition = AiEditionNames.ToWireName(AiEdition.User),
            engineRoot = layout.EngineRoot,
            installedAtUtc = DateTimeOffset.UtcNow,
            files,
            skills = skills.Where(s => !warnings.Any(w => w.StartsWith($"skill '{s}' "))).ToArray(),
        }, JsonOptions));
        files.Add(Path.Combine(".xengine", StampFileName));

        return new HarnessInstallResult(true, layout.EngineRoot,
            AiEditionNames.ToWireName(AiEdition.User), skills, files, warnings, null);
    }

    public static HarnessStatus Status(string projectRoot)
    {
        string root = Path.GetFullPath(projectRoot);
        string stampPath = Path.Combine(root, ".xengine", StampFileName);
        if (!File.Exists(stampPath))
        {
            return new HarnessStatus(false, null, null, null, false, [], []);
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(stampPath));
        JsonElement stamp = doc.RootElement;
        string version = stamp.GetProperty("harnessVersion").GetString() ?? "";
        string edition = stamp.GetProperty("edition").GetString() ?? "";
        string engineRoot = stamp.GetProperty("engineRoot").GetString() ?? "";

        var missingFiles = new List<string>();
        foreach (JsonElement f in stamp.GetProperty("files").EnumerateArray())
        {
            string rel = f.GetString() ?? "";
            if (rel.EndsWith("skills", StringComparison.Ordinal) ? !Directory.Exists(Path.Combine(root, rel))
                : !File.Exists(Path.Combine(root, rel)))
            {
                missingFiles.Add(rel);
            }
        }

        var missingSkills = new List<string>();
        string skillsDest = Path.Combine(root, ".xengine", "harness", "skills");
        foreach (JsonElement s in stamp.GetProperty("skills").EnumerateArray())
        {
            string skill = s.GetString() ?? "";
            if (!File.Exists(Path.Combine(skillsDest, skill, "SKILL.md"))) missingSkills.Add(skill);
        }

        return new HarnessStatus(true, version, edition, engineRoot,
            UpToDate: version == HarnessVersion && missingFiles.Count == 0 && missingSkills.Count == 0,
            missingFiles, missingSkills);
    }

    // ---- helpers ----

    private static Dictionary<string, object> Server(string command, string[] args) => new()
    {
        ["command"] = command,
        ["args"] = args,
    };

    private static List<string> ReadManifest()
    {
        return ReadEmbeddedResource(ManifestResource)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }

    private static string ReadEmbeddedResource(string name)
    {
        using Stream stream = typeof(HarnessInstaller).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded harness resource '{name}' missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? FindEditorBinary(EngineLayout layout)
    {
        string[] candidates = OperatingSystem.IsMacOS()
            ? [Path.Combine(layout.EngineRoot, "XEngine.app", "Contents", "MacOS", "XEngine.Editor")]
            :
            [
                Path.Combine(layout.EngineRoot, OperatingSystem.IsWindows() ? "XEngine.Editor.exe" : "XEngine.Editor"),
                // Dev repo build output, when seeding from a source checkout.
                Path.Combine(layout.EngineRoot, "Build", "Editor", "Debug", "net10.0",
                    OperatingSystem.IsWindows() ? "XEngine.Editor.exe" : "XEngine.Editor"),
            ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string sub in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
    }
}
