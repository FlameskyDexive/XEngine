using System;
using System.Collections.Generic;

namespace XEngine.ILInterpreter.Runtime;

/// <summary>
/// N1 package marker. Later nodes add the module loader, VM, host bindings and
/// behaviour integration on top of this assembly; the classification plumbing
/// in the editor (interpreted asmdef support) is inert unless this package is
/// installed, and this assembly never ships without it.
/// </summary>
public static class ILInterpreterPackage
{
    public const string PackageName = "com.xengine.ilinterpreter";
    public const string Version = "0.1.0";

    /// <summary>Folder (under Library) where interpreted modules compile to.</summary>
    public const string ModuleRootFolder = "ILInterpreter/Modules";
}
