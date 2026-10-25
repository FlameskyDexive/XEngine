using System;
using System.Diagnostics;

namespace XHub.Platform;

/// <summary>Thin wrappers over OS shell operations so UI code stays testable.</summary>
public static class Shell
{
    /// <summary>Opens a folder in the platform file manager (creates it when missing).</summary>
    public static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", path);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            Process.Start("xdg-open", path);
            return;
        }
        throw new PlatformNotSupportedException();
    }
}
