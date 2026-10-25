using System.Reflection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Single source of truth for which MCP tool TYPES register in a given edition. Tool classes carry
/// an optional <see cref="AiToolEditionAttribute"/>; unmarked classes serve every edition. The MCP
/// registration path (<c>WithTools(Type[])</c>) and the capability snapshot both go through this
/// catalog, so the advertised surface can never claim tools that aren't registered (the drift
/// problem the CapabilityCatalog rewrite already fixed for descriptions).
/// </summary>
internal static class ToolTypeCatalog
{
    /// <summary>Collects the [McpServerToolType]-marked classes visible to <paramref name="edition"/>.</summary>
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

    /// <summary>Tool method names ([McpServerTool]) across the given tool types — used by tests and
    /// the capability snapshot to reason about names without instantiating anything.</summary>
    public static IEnumerable<string> ToolNames(IEnumerable<Type> toolTypes)
    {
        foreach (Type type in toolTypes)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                McpServerToolAttribute? attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null) continue;
                yield return !string.IsNullOrEmpty(attr.Name) ? attr.Name! : method.Name;
            }
        }
    }
}
