using System;
using System.IO;

namespace XEngine.Editor.Core;

/// <summary>Default captures belong to the project or user data, never the installation CWD.</summary>
internal static class EditorScreenshotPaths
{
    internal static string ResolveOutputPath(string fileName, string? projectRoot, string? outputPath = null)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
            return Path.GetFullPath(outputPath);

        string directory;
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            directory = Path.Combine(projectRoot, ".xengine", "screenshots");
        }
        else
        {
            string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(userData))
                userData = Path.GetTempPath();
            directory = Path.Combine(userData, "XEngine", "Screenshots");
        }

        return Path.GetFullPath(Path.Combine(directory, fileName));
    }
}
