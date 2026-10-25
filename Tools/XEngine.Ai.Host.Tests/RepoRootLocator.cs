using System;
using System.IO;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// Walks up from the test assembly to the repository root. Tests that read files the repo ships
/// (package samples, skills) need this because the test binary's location moves with the build's
/// output path.
/// </summary>
internal static class RepoRootLocator
{
    public static string Find()
    {
        string dir = AppContext.BaseDirectory;
        while (true)
        {
            if (File.Exists(Path.Combine(dir, "XEngine.sln"))) return dir;
            string parent = Path.GetFullPath(Path.Combine(dir, ".."));
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
            dir = parent;
        }
        throw new InvalidOperationException("Could not locate XEngine.sln from " + AppContext.BaseDirectory);
    }
}
