using System;
using System.Diagnostics;

namespace XHub.Platform;

/// <summary>
/// Brings an existing editor window to the front (§4.4 已运行项目优先激活原编辑器窗口).
/// Best-effort: callers fall back to normal behavior when activation is not possible.
/// </summary>
public static class WindowActivation
{
    public static bool ActivateMainWindow(int processId)
    {
        if (OperatingSystem.IsWindows())
            return ActivateWindows(processId);
        if (OperatingSystem.IsMacOS())
            return false; // NSRunningApplication activation needs AppKit; N4 ships Windows-complete
        return false;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ActivateWindows(int processId)
    {
        try
        {
            using Process? process = Process.GetProcessById(processId);
            // The window may take a moment after startup; retry a few times.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SW_RESTORE);
                    if (SetForegroundWindow(handle))
                        return true;
                }
                System.Threading.Thread.Sleep(200);
                process.Refresh();
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private const int SW_RESTORE = 9;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
