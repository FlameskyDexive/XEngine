// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Text.Json;
using XEngine.Ai.Harness;
using XEngine.Ai.Protocol;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ Harness 三件套 — the user-edition agent scaffolding seeded into game projects: .mcp.json
/// (offline CLI + live editor wiring), AGENTS.md (guide from the embedded template) and the
/// manifest-curated skills under .xengine/harness/skills/, stamped by .xengine/harness.json.
/// Uses the real repo layout as the engine install (dev layout: cli/skills + dotnet-run CLI).
/// </summary>
public class HarnessInstallTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private readonly string _projectRoot;

    public HarnessInstallTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "xengine-harness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Install_SeedsThreePieces_AndStampsStatus()
    {
        HarnessInstallResult result = HarnessInstaller.Install(_projectRoot, RepoRoot);

        Assert.True(result.Success, result.Error);
        Assert.Equal(RepoRoot, result.EngineRoot);

        // .mcp.json — valid JSON, offline CLI wired via the dev layout shape.
        string mcp = File.ReadAllText(Path.Combine(_projectRoot, ".mcp.json"));
        using JsonDocument doc = JsonDocument.Parse(mcp);
        JsonElement xengine = doc.RootElement.GetProperty("mcpServers").GetProperty("xengine");
        Assert.Equal("dotnet", xengine.GetProperty("command").GetString());
        string[] args = xengine.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Contains("serve", args);
        Assert.Contains("--project", args);
        Assert.EndsWith("xengine-cli.dll", args[0]);

        // AGENTS.md — placeholders fully resolved.
        string agents = File.ReadAllText(Path.Combine(_projectRoot, "AGENTS.md"));
        Assert.DoesNotContain("{{", agents);
        Assert.Contains(RepoRoot, agents);

        // Skills — the manifest's game-dev set, each with a SKILL.md.
        string skillsDir = Path.Combine(_projectRoot, ".xengine", "harness", "skills");
        Assert.True(Directory.Exists(skillsDir));
        Assert.True(File.Exists(Path.Combine(skillsDir, "edit-scene", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(skillsDir, "build-project", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(skillsDir, "run-tests")),
            "engine-repo-only skill (run-tests) must not ship to user projects.");
        Assert.False(Directory.Exists(Path.Combine(skillsDir, "search-code")),
            "engine-repo-only skill (search-code) must not ship to user projects.");

        // Stamp — version + edition + engine root, and status reads back clean.
        Assert.True(File.Exists(Path.Combine(_projectRoot, ".xengine", "harness.json")));
        HarnessStatus status = HarnessInstaller.Status(_projectRoot);
        Assert.True(status.Installed);
        Assert.Equal(HarnessInstaller.HarnessVersion, status.HarnessVersion);
        Assert.Equal("user", status.Edition);
        Assert.True(status.UpToDate);
        Assert.Empty(status.MissingFiles);
        Assert.Empty(status.MissingSkills);
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        HarnessInstaller.Install(_projectRoot, RepoRoot);
        HarnessInstallResult second = HarnessInstaller.Install(_projectRoot, RepoRoot);

        Assert.True(second.Success, second.Error);
        Assert.True(HarnessInstaller.Status(_projectRoot).UpToDate);
    }

    [Fact]
    public void Install_WalksUpFromProjectInsideEngineRepo()
    {
        // A project created INSIDE the repo (like ZonezeroTestProject) must resolve the engine
        // layout by walking up from the project root itself.
        string inner = Path.Combine(RepoRoot, "Temp", "harness-inner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inner);
        try
        {
            HarnessInstallResult result = HarnessInstaller.Install(inner);
            Assert.True(result.Success, result.Error);
            Assert.Equal(RepoRoot, result.EngineRoot);
            Assert.True(File.Exists(Path.Combine(inner, "AGENTS.md")));
        }
        finally
        {
            try { Directory.Delete(inner, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Locate_WalksUpFromMacEditorBundleToShippedInstallRoot()
    {
        string installRoot = Path.Combine(_projectRoot, "shipped-install");
        string editorBaseDirectory = Path.Combine(installRoot, "XEngine.app", "Contents", "MacOS");
        string cliName = OperatingSystem.IsWindows() ? "xengine-cli.exe" : "xengine-cli";
        string cliPath = Path.Combine(installRoot, "tools", cliName);
        string skillsPath = Path.Combine(installRoot, "harness", "skills");
        Directory.CreateDirectory(editorBaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(cliPath)!);
        Directory.CreateDirectory(skillsPath);
        File.WriteAllText(cliPath, string.Empty);

        EngineLayout? layout = EngineLocator.Locate(editorBaseDirectory);

        Assert.NotNull(layout);
        Assert.Equal(installRoot, layout.EngineRoot);
        Assert.Equal(skillsPath, layout.SkillsSource);
        Assert.Equal(cliPath, layout.Cli.Command);
        Assert.Empty(layout.Cli.BaseArgs);
    }

    [Fact]
    public void Install_WithoutEngineLayout_FailsWithActionableError()
    {
        string isolated = Path.Combine(_projectRoot, "nowhere");
        Directory.CreateDirectory(isolated);

        HarnessInstallResult result = HarnessInstaller.Install(_projectRoot, isolated);

        Assert.False(result.Success);
        Assert.Contains("Could not locate the engine installation", result.Error);
    }

    [Fact]
    public void Status_UnseededProject_IsNotInstalled()
    {
        HarnessStatus status = HarnessInstaller.Status(_projectRoot);
        Assert.False(status.Installed);
        Assert.False(status.UpToDate);
    }

    [Fact]
    public void ManifestSkills_AllExistInTheEngineRepo()
    {
        // Guards the manifest against drift: every shipped skill must exist in cli/skills.
        // (Install copies whole directories; this pin keeps the manifest honest even when
        // skills are renamed.)
        string skillsSource = Path.Combine(RepoRoot, "cli", "skills");
        var manifest = typeof(HarnessInstaller).Assembly
            .GetManifestResourceStream(HarnessInstaller.ManifestResource)!;
        using var reader = new StreamReader(manifest);
        var names = reader.ReadToEnd().Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        Assert.NotEmpty(names);
        foreach (string name in names)
        {
            Assert.True(File.Exists(Path.Combine(skillsSource, name, "SKILL.md")),
                $"manifest skill '{name}' has no SKILL.md in cli/skills.");
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XEngine.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "Tools")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (XEngine.sln + Tools/) not found from test base dir.");
    }
}
