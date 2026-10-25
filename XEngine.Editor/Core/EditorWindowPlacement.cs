using System;
using System.Runtime.InteropServices;

using Silk.NET.Windowing;

using XEngine.Vector;

namespace XEngine.Editor.Core;

/// <summary>Reads stable monitor and per-window DPI placement metadata for workspace persistence.</summary>
internal static class EditorWindowPlacement
{
    private const uint MonitorDefaultToNearest = 2;

    public static void Apply(EditorSurfaceHost host, IWindow window)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(window);

        host.MonitorId = string.Empty;
        host.DpiScale = 1f;
        if (!OperatingSystem.IsWindows())
            return;

        nint handle = window.Native.Win32?.Item1 ?? window.Handle;
        if (handle == 0)
            return;

        nint monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor != 0)
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && !string.IsNullOrWhiteSpace(info.DeviceName))
                host.MonitorId = info.DeviceName.TrimEnd('\0');
        }

        uint dpi = GetDpiForWindow(handle);
        if (dpi != 0)
            host.DpiScale = Math.Clamp(dpi / 96f, 0.5f, 8f);
    }

    public static bool TryGetVirtualDesktopBounds(out XEngine.Vector.Rect bounds)
    {
        bounds = default;
        if (!OperatingSystem.IsWindows())
            return false;

        int x = GetSystemMetrics(VirtualDesktopX);
        int y = GetSystemMetrics(VirtualDesktopY);
        int width = GetSystemMetrics(VirtualDesktopWidth);
        int height = GetSystemMetrics(VirtualDesktopHeight);
        if (width <= 0 || height <= 0)
            return false;

        bounds = new XEngine.Vector.Rect(x, y, x + width, y + height);
        return true;
    }

    private const int VirtualDesktopX = 76;
    private const int VirtualDesktopY = 77;
    private const int VirtualDesktopWidth = 78;
    private const int VirtualDesktopHeight = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
