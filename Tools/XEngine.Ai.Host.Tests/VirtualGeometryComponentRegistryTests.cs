// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;

using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class VirtualGeometryComponentRegistryTests
{
    [Fact]
    public void VirtualGeometryRenderer_ResolvesToRuntimeAssembly()
    {
        string? resolved = ComponentRegistry.ResolveTypeString("VirtualGeometryRenderer");
        Assert.NotNull(resolved);
        Assert.StartsWith("XEngine.Runtime.VirtualGeometryRenderer, XEngine.Runtime,", resolved, StringComparison.Ordinal);
    }
}
