// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;
using XEngine.Ai.Server;
using Xunit;

namespace XEngine.Ai.Host.Tests;

// Fixture tool classes living in THIS test assembly — WithEditionTools scans assemblies handed to
// it, so dummy tools here prove the in-process server's edition filter without engine dependencies.

[McpServerToolType]
public class EditionFilterBothTestTools
{
    [McpServerTool(Name = "testtool_both_editions")]
    [System.ComponentModel.Description("fixture")]
    public static int Both() => 1;
}

[McpServerToolType]
[AiToolEdition(AiEdition.EngineDev)]
public class EditionFilterEngineOnlyTestTools
{
    [McpServerTool(Name = "testtool_engine_dev_only")]
    [System.ComponentModel.Description("fixture")]
    public static int EngineOnly() => 2;
}

/// <summary>
/// ★ In-process server (editor --serve) edition filter — the same ToolTypeCatalog semantics as the
/// offline Host, applied to every scanned assembly (server core + editor + package tool assemblies).
/// Pins: unmarked tool classes register in both editions; [AiToolEdition(EngineDev)] classes never
/// leak into the user surface; the registered factory count matches the visible tool-method count.
/// </summary>
public class ServerToolEditionFilterTests
{
    private static readonly Assembly TestAssembly = typeof(ServerToolEditionFilterTests).Assembly;

    [Fact]
    public void Catalog_EngineDev_IncludesBothFixtureClasses()
    {
        Type[] types = ToolTypeCatalog.Collect(TestAssembly, AiEdition.EngineDev);
        Assert.Contains(types, t => t == typeof(EditionFilterBothTestTools));
        Assert.Contains(types, t => t == typeof(EditionFilterEngineOnlyTestTools));
    }

    [Fact]
    public void Catalog_User_ExcludesEngineOnlyFixtureClass()
    {
        Type[] types = ToolTypeCatalog.Collect(TestAssembly, AiEdition.User);
        Assert.Contains(types, t => t == typeof(EditionFilterBothTestTools));
        Assert.DoesNotContain(types, t => t == typeof(EditionFilterEngineOnlyTestTools));
    }

    [Fact]
    public void WithEditionTools_User_RegistersOnlyVisibleToolFactories()
    {
        int factories = CollectRegisteredToolFactories(AiEdition.User);

        // Exactly the visible fixture tool: testtool_both_editions.
        Assert.Equal(1, factories);
    }

    [Fact]
    public void WithEditionTools_EngineDev_RegistersEveryToolFactory()
    {
        int factories = CollectRegisteredToolFactories(AiEdition.EngineDev);

        // Both fixture tools.
        Assert.Equal(2, factories);
    }

    private static int CollectRegisteredToolFactories(AiEdition edition)
    {
        // WithEditionTools registers each visible tool as AddSingleton(typeof(McpServerTool),
        // factory) — the SDK's own WithTools(IEnumerable<Type>) pattern.
        var services = new ServiceCollection();
        services.AddMcpServer().WithEditionTools(TestAssembly, edition);
        return services.Count(d => d.ServiceType == typeof(McpServerTool) && d.ImplementationFactory is not null);
    }
}
