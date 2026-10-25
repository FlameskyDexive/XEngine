using System.IO;

using XEngine.ILInterpreter.Compatibility;
using XEngine.Editor.Projects.Scripting;

namespace XEngine.ILInterpreter.Editor;

internal static class ILInterpreterMenu
{
    private const string PackageName = "com.xengine.ilinterpreter";

    [XEngine.Editor.MenuItem("Assets/IL Interpreter/Create Hot-update Module", priority: 1000)]
    public static void CreateHotUpdateModule()
    {
        var project = XEngine.Editor.Projects.Project.Current;
        if (project is null) return;
        string dir = Path.Combine(project.AssetsPath, "Hotfix");
        Directory.CreateDirectory(dir);
        new AssemblyDefinition { Name = "Game.Hotfix", IsInterpreted = true, AutoReferenced = false }
            .WriteToFile(Path.Combine(dir, "Game.Hotfix.asmdef"));
        string sample = Path.Combine(dir, "HotModule.cs");
        if (!File.Exists(sample))
        {
            File.WriteAllText(sample,
                "public static class HotModule\n{\n    public static string Hello() => \"hello from interpreted code\";\n}\n");
        }
        XEngine.Runtime.Debug.Log("[ILInterpreter] Hot-update module created at Assets/Hotfix (Game.Hotfix).");
    }

    [XEngine.Editor.MenuItem("Assets/IL Interpreter/Check Compatibility", priority: 1001)]
    public static void CheckCompatibility()
    {
        var project = XEngine.Editor.Projects.Project.Current;
        if (project is null) return;
        string moduleRoot = Path.Combine(project.LibraryPath, "ILInterpreter", "Modules");
        if (!Directory.Exists(moduleRoot))
        {
            XEngine.Runtime.Debug.Log("[ILInterpreter] No compiled modules found. Compile scripts first.");
            return;
        }

        var bindings = ILModuleManifestBuilder.LoadProjectBindings(project.RootPath);
        foreach (var dll in Directory.EnumerateFiles(moduleRoot, "*.dll", SearchOption.AllDirectories))
        {
            var report = ILModuleScanner.Scan(dll, bindings);
            string verdict = report.IsCompatible ? "COMPATIBLE" : "INCOMPATIBLE";
            XEngine.Runtime.Debug.Log(
                $"[ILInterpreter] {report.ModuleName}: {verdict}, " +
                $"{report.RequiredBindings.Count} binding requirement(s), sha256={report.ModuleSha256[..12]}…");
            foreach (var f in report.Findings)
                XEngine.Runtime.Debug.LogWarning($"[ILInterpreter]   {f.Type}.{f.Method}@{f.IlOffset}: {f.Kind} {f.Detail}");
        }
    }

    [XEngine.Editor.MenuItem("Assets/IL Interpreter/Generate Binding Template", priority: 1002)]
    public static void GenerateBindingTemplate()
    {
        var project = XEngine.Editor.Projects.Project.Current;
        if (project is null) return;
        string path = Path.Combine(project.RootPath, "ILBindings.txt");
        if (File.Exists(path))
        {
            XEngine.Runtime.Debug.Log("[ILInterpreter] ILBindings.txt already exists.");
            return;
        }
        File.WriteAllLines(path,
        [
            "# IL Interpreter host bindings",
            "# One binding per line: Full.Type::Member/paramCount",
            "# Lines starting with # are comments. Modules touching unlisted members",
            "# are reported as incompatible by the build compatibility scan.",
            "# Example:",
            "# Probe.Contracts.HostMath::Add/2",
        ]);
        XEngine.Runtime.Debug.Log($"[ILInterpreter] Binding template written to {path}");
    }
}
