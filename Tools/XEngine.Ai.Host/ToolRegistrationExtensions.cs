using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Edition-aware MCP tool registration. This replicates the body of the SDK's own
/// <c>WithTools(IEnumerable&lt;Type&gt;)</c> (tool-method reflection → <see cref="McpServerTool.Create"/>
/// factories) with one addition: the <see cref="ToolTypeCatalog"/> edition filter.
///
/// ★ Why not call WithTools(Type[]) directly: passing a <c>Type[]</c> binds the generic overload
/// <c>WithTools&lt;TToolType&gt;(builder, TToolType target, ...)</c> (TToolType=Type[], an identity
/// conversion that beats IEnumerable&lt;Type&gt;), which scans the ARRAY TYPE for tool methods,
/// finds none, and silently registers zero tools. Building the factories explicitly keeps the
/// SDK-documented per-method construction while making the edition filter the only difference
/// from WithToolsFromAssembly.
/// </summary>
internal static class ToolRegistrationExtensions
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
