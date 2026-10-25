// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;

using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class NavigationComponentRegistryTests
{
    [Fact]
    public void NavMeshAgent_ResolvesToPackageAssembly()
    {
        string? resolved = ComponentRegistry.ResolveTypeString("NavMeshAgent");
        Assert.NotNull(resolved);
        Assert.StartsWith("XEngine.Runtime.Navigation.NavMeshAgent, XEngine.AI.Navigation.Runtime,", resolved, StringComparison.Ordinal);
        Assert.DoesNotContain("XEngine.Runtime, Version", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void NavMeshSurface_ResolvesToPackageAssembly()
    {
        string? resolved = ComponentRegistry.ResolveTypeString("NavMeshSurface");
        Assert.NotNull(resolved);
        Assert.StartsWith("XEngine.AI.Navigation.NavMeshSurface, XEngine.AI.Navigation.Runtime,", resolved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NavMeshObstacle", "XEngine.Runtime.Navigation.NavMeshObstacle")]
    [InlineData("OffMeshLink", "XEngine.Runtime.Navigation.OffMeshLink")]
    [InlineData("NavMeshModifier", "XEngine.AI.Navigation.NavMeshModifier")]
    [InlineData("NavMeshLink", "XEngine.AI.Navigation.NavMeshLink")]
    public void OtherNavShortNames_ResolveToPackageAssembly(string shortName, string typePrefix)
    {
        string? resolved = ComponentRegistry.ResolveTypeString(shortName);
        Assert.NotNull(resolved);
        Assert.StartsWith(typePrefix + ", XEngine.AI.Navigation.Runtime,", resolved, StringComparison.Ordinal);
    }
}
