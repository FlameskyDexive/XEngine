// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

namespace XEngine.PackageRegistry.Tests;

/// <summary>Builds throwaway package directories shaped like the repo's com.xengine.* packages.</summary>
public static class TestPackageDir
{
    public static string Create(string root, string name, string version, string? dependency = null)
    {
        string dir = Path.Combine(root, name.Replace('.', '-'));
        Directory.CreateDirectory(Path.Combine(dir, "Runtime"));
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""
            {
              "name": "{{name}}",
              "version": "{{version}}",
              "displayName": "Test package",
              {{(dependency is null ? "" : $"\"dependencies\": {{ \"{dependency}\": \"0.1.0\" }},")}}
              "description": "A package created for tests"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Runtime", "Marker.cs"),
            $"// {name} {version}\npublic static class Marker {{ }}\n");
        return dir;
    }
}
