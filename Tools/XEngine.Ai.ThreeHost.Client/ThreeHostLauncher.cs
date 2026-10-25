using System;
using System.IO;

namespace XEngine.Ai.ThreeHost.Client;

/// <summary>
/// Locates the Windows authoring host executable. Resolution order: the
/// <c>XENGINE_THREE_HOST_EXE</c> override, the repo-local build outputs, then the packaged
/// publish layout under <c>Build/Tools</c>.
/// </summary>
public static class ThreeHostLauncher
{
    public const string EngineRootEnvVar = "XENGINE_ENGINE_ROOT";
    public const string HostExeEnvVar = "XENGINE_THREE_HOST_EXE";

    /// <summary>Walks up from <paramref name="startDir"/> looking for <c>XEngine.sln</c>.</summary>
    public static bool TryFindEngineRoot(string startDir, out string engineRoot)
    {
        string dir = Path.GetFullPath(startDir);
        while (true)
        {
            if (File.Exists(Path.Combine(dir, "XEngine.sln")))
            {
                engineRoot = dir;
                return true;
            }

            string? parent = Path.GetDirectoryName(dir);
            if (parent is null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
            {
                engineRoot = string.Empty;
                return false;
            }
            dir = parent;
        }
    }

    public static bool TryResolve(string? engineRoot, out string hostExe, out string? error)
    {
        string? overridePath = Environment.GetEnvironmentVariable(HostExeEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath))
            {
                hostExe = Path.GetFullPath(overridePath);
                error = null;
                return true;
            }

            hostExe = string.Empty;
            error = $"{HostExeEnvVar} points at a missing file: {overridePath}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(engineRoot) && !TryFindEngineRoot(Directory.GetCurrentDirectory(), out engineRoot))
        {
            hostExe = string.Empty;
            error = "Could not locate the XEngine repository root (no XEngine.sln found) and "
                + $"{HostExeEnvVar} is not set.";
            return false;
        }

        string[] candidates =
        [
            Path.Combine(engineRoot, "Build", "Tools", "ThreeHost", "XEngine.Ai.ThreeHost.exe"),
            Path.Combine(engineRoot, "Tools", "XEngine.Ai.ThreeHost", "bin", "Release", "net10.0-windows", "XEngine.Ai.ThreeHost.exe"),
            Path.Combine(engineRoot, "Tools", "XEngine.Ai.ThreeHost", "bin", "Debug", "net10.0-windows", "XEngine.Ai.ThreeHost.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                hostExe = candidate;
                error = null;
                return true;
            }
        }

        hostExe = string.Empty;
        error = "Authoring host executable not found. Build Tools/XEngine.Ai.ThreeHost first "
            + "(looked at: " + string.Join("; ", candidates) + ").";
        return false;
    }

    /// <summary>Builds the argument list for one host process scoped to a project root.</summary>
    public static string BuildArguments(
        string protocolVersion,
        string projectRoot,
        string pageRoot,
        long ownerHwnd,
        bool showWindow,
        int width,
        int height)
    {
        return "--protocol " + Quote(protocolVersion)
            + " --project-root " + Quote(Path.GetFullPath(projectRoot))
            + " --page-root " + Quote(Path.GetFullPath(pageRoot))
            + (ownerHwnd != 0 ? " --owner-hwnd " + ownerHwnd.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
            + " --window " + (showWindow ? "show" : "hidden")
            + " --width " + width.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " --height " + height.ToString(System.Globalization.CultureInfo.InvariantCulture);

        static string Quote(string value) => '"' + value + '"';
    }
}
