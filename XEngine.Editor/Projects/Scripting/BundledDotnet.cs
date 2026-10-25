using System;
using System.IO;

namespace XEngine.Editor.Projects.Scripting;

/// <summary>Use the SDK shipped with the editor for all inherited build subprocesses.</summary>
internal static class BundledDotnet
{
    internal static string? FindRoot(string editorDirectory)
    {
        string root = Path.Combine(editorDirectory, "dotnet");
        if (IsSdkRoot(root)) return root;

        // SDK manifests and version folders are data, not nested macOS code bundles.
        // Keeping them below MacOS makes codesign --deep misclassify these folders.
        var editor = new DirectoryInfo(editorDirectory);
        if (editor.Name != "MacOS" || editor.Parent?.Name != "Contents") return null;
        root = Path.Combine(editor.Parent.FullName, "Resources", "dotnet");
        return IsSdkRoot(root) ? root : null;
    }

    private static bool IsSdkRoot(string root)
    {
        string host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(host) && Directory.Exists(Path.Combine(root, "sdk"));
    }

    internal static void Configure(string? editorDirectory = null)
    {
        string? root = FindRoot(editorDirectory ?? AppContext.BaseDirectory);
        if (root == null) return; // Source checkouts retain the developer's SDK.
        string host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
        Environment.SetEnvironmentVariable("DOTNET_ROOT_" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant(), root);
        Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }
}
