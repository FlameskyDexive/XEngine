// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// ★ Agent harness seeding — Project.Create seeds the user-edition agent scaffolding
/// (三件套: .mcp.json + AGENTS.md + skills) so a fresh game project is immediately agent-operable.
/// Runs through the same headless Project.Create path the launcher uses.
/// </summary>
public class AgentHarnessSeedingTests : EditorTestHarness
{
    [Fact]
    public void NewProject_IsSeededWithAgentHarness()
    {
        Assert.True(File.Exists(Path.Combine(Project.RootPath, "AGENTS.md")), "AGENTS.md missing");
        Assert.True(File.Exists(Path.Combine(Project.RootPath, ".mcp.json")), ".mcp.json missing");
        Assert.True(File.Exists(Path.Combine(Project.RootPath, ".xengine", "harness.json")),
            "harness.json stamp missing");

        // Seeding is edition-correct: the guide talks to a user game project.
        string agents = File.ReadAllText(Path.Combine(Project.RootPath, "AGENTS.md"));
        Assert.DoesNotContain("{{", agents);

        // Dev layout (tests run from the repo): the manifest skills ship into the project.
        Assert.True(Directory.Exists(Path.Combine(Project.RootPath, ".xengine", "harness", "skills")),
            "harness skills not copied");
        Assert.True(File.Exists(Path.Combine(Project.RootPath, ".xengine", "harness", "skills",
            "edit-scene", "SKILL.md")));
    }
}
