// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Reflection;

namespace XEngine.Runtime.Resources;

/// <summary>
/// Helper class for loading embedded resources from the XEngine.Runtime assembly
/// </summary>
internal static class EmbeddedResources
{
    private static readonly Assembly RuntimeAssembly = Assembly.GetExecutingAssembly();

    /// <summary>
    /// Gets an embedded resource stream by path
    /// </summary>
    public static Stream GetStream(string resourcePath)
    {
        if (!TryGetResourceName(resourcePath, out string? resourceName) || resourceName == null)
            throw new FileNotFoundException($"Embedded resource '{resourcePath}' not found.");

        return RuntimeAssembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Failed to load embedded resource '{resourceName}'.");
    }

    /// <summary>
    /// Reads an embedded resource as text
    /// </summary>
    public static string ReadAllText(string resourcePath)
    {
        using Stream stream = GetStream(resourcePath);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Checks if an embedded resource exists
    /// </summary>
    public static bool Exists(string resourcePath)
    {
        return TryGetResourceName(resourcePath, out _);
    }

    private static bool TryGetResourceName(string resourcePath, out string? resourceName)
    {
        resourceName = null;

        // Normalize path separators
        resourcePath = resourcePath.Replace('\\', '/');

        string[] resourceNames = RuntimeAssembly.GetManifestResourceNames();

        // Last path segment without LINQ (PR0001).
        int slash = resourcePath.LastIndexOf('/');
        string lastSegment = slash >= 0 ? resourcePath.Substring(slash + 1) : resourcePath;
        string dotLastSegment = "." + lastSegment;

        // Try exact match first (manual scan, no FirstOrDefault).
        for (int i = 0; i < resourceNames.Length; i++)
        {
            string r = resourceNames[i].Replace('\\', '/');
            if (r.EndsWith(resourcePath, StringComparison.OrdinalIgnoreCase) ||
                r.EndsWith(dotLastSegment, StringComparison.OrdinalIgnoreCase))
            {
                resourceName = resourceNames[i];
                return true;
            }
        }

        // Fallback: match by file name only.
        string fileName = Path.GetFileName(resourcePath);
        for (int i = 0; i < resourceNames.Length; i++)
        {
            if (resourceNames[i].EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                resourceName = resourceNames[i];
                return true;
            }
        }

        return resourceName != null;
    }
}
