// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Server;

/// <summary>
/// Edition-aware MCP tool registration for the in-process server. Deliberately mirrors
/// Tools/XEngine.Ai.Host/ToolRegistrationExtensions.cs (the two surfaces — offline Host and
/// in-process server — are sibling assemblies with no reference between them, so the ~20-line
/// registration body is duplicated on purpose).
///
/// ★ Same overload-resolution trap avoided: calling the SDK's WithTools(Type[]) directly binds the
/// generic WithTools&lt;TToolType&gt;(builder, TToolType target) overload (TToolType=Type[], identity
/// conversion wins), which scans the array type for tool methods and silently registers ZERO tools.
/// Building McpServerTool factories per method keeps the SDK-documented construction path.
/// </summary>
public static class ToolRegistrationExtensions
{
    public static IMcpServerBuilder WithEditionTools(this IMcpServerBuilder builder, Assembly toolsAssembly, AiEdition edition)
    {
        foreach (Type toolType in ToolTypeCatalog.Collect(toolsAssembly, edition))
        {
            foreach (MethodInfo method in toolType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;

                builder.Services.AddSingleton((Func<IServiceProvider, McpServerTool>)(method.IsStatic
                    ? services => McpServerTool.Create(method, options: new McpServerToolCreateOptions { Services = services })
                    : services => McpServerTool.Create(
                        method,
                        _ => ActivatorUtilities.CreateInstance(services, toolType),
                        new McpServerToolCreateOptions { Services = services })));
            }
        }

        return builder;
    }
}

/// <summary>
/// Collects the [McpServerToolType]-marked classes visible to an edition: tool classes carry an
/// optional <see cref="AiToolEditionAttribute"/>; unmarked classes serve every edition. Shared
/// filter semantics with the offline Host's ToolTypeCatalog.
/// </summary>
public static class ToolTypeCatalog
{
    public static Type[] Collect(Assembly toolsAssembly, AiEdition edition)
    {
        var types = new List<Type>();
        foreach (Type type in toolsAssembly.GetTypes())
        {
            if (!type.IsClass || !type.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false)) continue;

            AiToolEditionAttribute? restricted = type.GetCustomAttribute<AiToolEditionAttribute>(inherit: false);
            if (restricted is not null && restricted.Edition != edition) continue;

            types.Add(type);
        }

        types.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return types.ToArray();
    }
}
