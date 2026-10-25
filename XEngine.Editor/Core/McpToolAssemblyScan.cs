using System;
using System.Collections.Generic;
using System.Reflection;

using ModelContextProtocol.Server;
using XEngine.Editor.Projects.Scripting;

namespace XEngine.Editor.Core;

/// <summary>
/// Discovers extra MCP tool assemblies for the in-process server. Official optional packages
/// (e.g. <c>com.xengine.vfx</c> / <c>com.xengine.ai.navigation</c> Editor) register <c>[McpServerToolType]</c> on their own
/// assemblies; the editor scans loaded script DLLs instead of hard-coding each package.
/// </summary>
public static class McpToolAssemblyScan
{
    public static Assembly[] Collect()
    {
        var list = new List<Assembly> { typeof(EditorApplication).Assembly };
        foreach (Assembly asm in ScriptAssemblyManager.GetScriptAssemblies())
        {
            if (ReferenceEquals(asm, typeof(EditorApplication).Assembly))
                continue;
            if (ContainsMcpServerTools(asm))
                list.Add(asm);
        }
        return list.ToArray();
    }

    public static bool ContainsMcpServerTools(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = new List<Type>();
            Type?[] partial = ex.Types;
            for (int i = 0; i < partial.Length; i++)
            {
                if (partial[i] != null)
                    loaded.Add(partial[i]!);
            }
            types = loaded.ToArray();
        }
        catch
        {
            return false;
        }

        for (int i = 0; i < types.Length; i++)
        {
            if (types[i].IsDefined(typeof(McpServerToolTypeAttribute), inherit: false))
                return true;
        }
        return false;
    }
}
