// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;

using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class TerrainComponentRegistryTests
{
    [Theory]
    [InlineData("TerrainComponent")]
    [InlineData("Terrain")]
    public void TerrainShortNames_ResolveToPackageAssembly(string shortName)
    {
        string? resolved = ComponentRegistry.ResolveTypeString(shortName);
        Assert.NotNull(resolved);
        Assert.StartsWith("XEngine.Terrain.TerrainComponent, XEngine.Terrain.Runtime,", resolved, StringComparison.Ordinal);
        Assert.DoesNotContain("XEngine.Runtime, Version", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void TerrainCollider_ResolvesToPackageAssembly()
    {
        string? resolved = ComponentRegistry.ResolveTypeString("TerrainCollider");
        Assert.NotNull(resolved);
        Assert.StartsWith("XEngine.Terrain.TerrainCollider, XEngine.Terrain.Runtime,", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void WindZone_ResolvesToPackageAssembly()
    {
        string? resolved = ComponentRegistry.ResolveTypeString("WindZone");
        Assert.NotNull(resolved);
        Assert.StartsWith("XEngine.Terrain.WindZone, XEngine.Terrain.Runtime,", resolved, StringComparison.Ordinal);
        Assert.DoesNotContain("XEngine.Runtime, Version", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreShortNames_StillUseRuntimeAssembly()
    {
        string? camera = ComponentRegistry.ResolveTypeString("Camera");
        Assert.Equal(
            "XEngine.Runtime.Camera, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            camera);
    }
}
