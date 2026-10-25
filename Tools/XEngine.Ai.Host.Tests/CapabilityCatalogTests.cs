// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ Gen-P0.1 — The capability snapshot is what agents use to gate risky operations. Asset file ops
/// (import/move/delete) mutate disk, so they must never be advertised as ReadOnly, and must not claim
/// dryRun support they don't have. These tests pin the classification.
/// </summary>
public class CapabilityCatalogTests
{
    private static CapabilitySnapshot Snapshot() =>
        new CapabilityCatalog(new ProjectDescriptor(
            "test-project", "C:/tmp/nonexistent", null, null, null, "fingerprint")).GetSnapshot();

    private static CapabilityToolDescriptor Tool(CapabilitySnapshot snapshot, string name)
    {
        CapabilityToolDescriptor? tool = snapshot.Tools.FirstOrDefault(t => t.Name == name);
        Assert.NotNull(tool);
        return tool!;
    }

    [Theory]
    [InlineData("asset_import", RiskLevel.SafeWrite)]
    [InlineData("asset_move", RiskLevel.SafeWrite)]
    [InlineData("asset_delete", RiskLevel.Destructive)]
    [InlineData("asset_search", RiskLevel.ReadOnly)]
    [InlineData("asset_inspect", RiskLevel.ReadOnly)]
    [InlineData("asset_data_inspect", RiskLevel.ReadOnly)]
    [InlineData("asset_data_patch", RiskLevel.SafeWrite)]
    public void AssetTools_AdvertiseCorrectRisk(string name, RiskLevel expected)
    {
        Assert.Equal(expected, Tool(Snapshot(), name).RiskLevel);
    }

    [Theory]
    [InlineData("asset_import")]
    [InlineData("asset_move")]
    [InlineData("asset_delete")]
    public void AssetFileOps_DoNotAdvertiseDryRun(string name)
    {
        // The underlying AssetWriter ops have no dryRun parameter — the flag must not lie.
        Assert.False(Tool(Snapshot(), name).SupportsDryRun);
    }

    [Fact]
    public void SceneWrites_StillAdvertiseDryRun()
    {
        // Guard: the dry-run heuristic exclusion is scoped to asset file ops only.
        CapabilitySnapshot snapshot = Snapshot();
        Assert.True(Tool(snapshot, "scene_object_patch").SupportsDryRun);
        Assert.True(Tool(snapshot, "scene_component_add").SupportsDryRun);
    }
}
