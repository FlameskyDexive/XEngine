// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime.UI;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class XguiPlayerDependencyAuditTests : EditorTestHarness
{
    [Fact]
    [Trait("Category", "Build")]
    public void PlayerAssembliesIncludeXguiRuntimeAndExcludeEditor()
    {
        string sourceRoot = FindRepositoryRoot();
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(sourceRoot, "Packages");
            PackageManager.Invalidate();
            ScriptAssemblyManager.LoadAssemblies(Project);

            string[] platforms =
            [
                BuildPlatforms.Windows,
                BuildPlatforms.Android,
                BuildPlatforms.HarmonyOS,
            ];
            for (int i = 0; i < platforms.Length; i++)
            {
                var assemblies = ScriptCompiler.GetBuildAssemblies(Project, platforms[i]);
                var names = new List<string>(assemblies.Count);
                for (int a = 0; a < assemblies.Count; a++)
                    names.Add(assemblies[a].Name);
                Assert.True(PlayerXguiAssemblyPolicy.TryValidatePlayerAssemblies(names, out string error), error);
                Assert.Contains(assemblies, assembly => assembly.Name == "XEngine.XGUI.Runtime");
                Assert.DoesNotContain(assemblies, assembly => assembly.Name == "XEngine.XGUI.Editor");
                Assert.DoesNotContain(assemblies, assembly => assembly.Name == "XEngine.Editor");
                PlayerAssemblyDependencyAudit.ValidateRuntimeClosure(assemblies);
            }
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
