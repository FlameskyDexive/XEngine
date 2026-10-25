// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.UI;

/// <summary>
/// Player assembly-name policy for XGUI. Desktop, Android, and HarmonyOS Players must ship
/// <c>XEngine.XGUI.Runtime</c> and must not include <c>XEngine.XGUI.Editor</c> or
/// <c>XEngine.Editor</c>. The Editor PE-closure audit still walks DLL references.
/// </summary>
public static class PlayerXguiAssemblyPolicy
{
    public const string RuntimeAssemblyName = "XEngine.XGUI.Runtime";
    public const string EditorPackageAssemblyName = "XEngine.XGUI.Editor";
    public const string EngineEditorAssemblyName = "XEngine.Editor";

    public static bool IsForbiddenEditorAssembly(string assemblyName)
        => string.Equals(assemblyName, EditorPackageAssemblyName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(assemblyName, EngineEditorAssemblyName, StringComparison.OrdinalIgnoreCase);

    public static bool TryValidatePlayerAssemblies(IReadOnlyList<string> assemblyNames, out string error)
    {
        error = string.Empty;
        if (assemblyNames == null)
        {
            error = "Player assembly list is missing.";
            return false;
        }

        bool hasRuntime = false;
        for (int i = 0; i < assemblyNames.Count; i++)
        {
            string name = assemblyNames[i];
            if (string.Equals(name, RuntimeAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                hasRuntime = true;
                continue;
            }

            if (IsForbiddenEditorAssembly(name))
            {
                error = "Player runtime closure must not include " + name + ".";
                return false;
            }
        }

        if (!hasRuntime)
        {
            error = "Player runtime closure must include " + RuntimeAssemblyName + ".";
            return false;
        }

        return true;
    }
}
