// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using XEngine.Runtime;

namespace XEngine.Editor.Core;

/// <summary>
/// Acceptance/debug helper: captures the whole editor window — chrome included (status bar, modals,
/// corner cards, startup screen) — to a PNG. The panel-screenshot automation renders individual
/// DockPanels offscreen and <c>runtime_screenshot</c> only reads the GameView RT, so editor-chrome
/// features use this path for acceptance evidence. Captures by BitBlting the window's screen
/// rectangle from the screen DC: PrintWindow/PW_RENDERFULLCONTENT reads the window's redirection
/// surface, which a Vulkan flip-model swapchain never updates (verified: pumped-frame toasts were
/// missing from PrintWindow shots). Screen capture needs the window on-screen and unoccluded —
/// foreground after a normal launch. Windows-only; returns null elsewhere or on failure. Main thread only.
/// </summary>
internal static class EditorWindowCapture
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BiRgb = 0;
    private const int DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height, nint hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint usage);

    // ─── macOS: GLFW window → NSWindow.windowNumber → screencapture -l ───

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, CharSet = CharSet.Ansi)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector);

    /// <summary>
    /// glfwGetCocoaWindow bound by hand: the dylib lives in runtimes/&lt;rid&gt;/native with a
    /// version-suffixed name (libglfw.3.dylib) that a bare DllImport("glfw") cannot resolve on
    /// macOS, while the Silk.NET GLFW binding loads it through its own resolution.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GlfwGetCocoaWindowDelegate(nint glfwWindow);

    private static nint? _glfwGetCocoaWindow;
    private static nint GetGlfwGetCocoaWindow()
    {
        if (_glfwGetCocoaWindow.HasValue)
            return _glfwGetCocoaWindow.Value;

        string baseDir = System.AppContext.BaseDirectory;
        string[] nativeDirs =
        [
            System.IO.Path.Combine(baseDir, "runtimes", "osx-arm64", "native"),
            System.IO.Path.Combine(baseDir, "runtimes", "osx-x64", "native"),
            baseDir,
        ];
        foreach (string dir in nativeDirs)
        {
            string candidate = System.IO.Path.Combine(dir, "libglfw.3.dylib");
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out nint handle))
            {
                _glfwGetCocoaWindow = System.Runtime.InteropServices.NativeLibrary.GetExport(handle, "glfwGetCocoaWindow");
                return _glfwGetCocoaWindow.Value;
            }
        }
        _glfwGetCocoaWindow = 0;
        return 0;
    }

    private static nint? _windowNumberSelector;
    private static nint GetWindowNumberSelector()
    {
        // sel_registerName interns the selector permanently, so caching is cosmetic.
        _windowNumberSelector ??= sel_registerName("windowNumber");
        return _windowNumberSelector.Value;
    }

    /// <summary>Captures the editor's main window to <paramref name="path"/> (PNG). Returns the path, or null on failure.</summary>
    public static string? Capture(string path)
    {
        if (OperatingSystem.IsWindows())
            return CaptureWindows(path);
        if (OperatingSystem.IsMacOS())
            return CaptureMacOS(path);
        return null;
    }

    /// <summary>
    /// macOS capture via <c>screencapture -l&lt;windowNumber&gt;</c> (whole window incl. chrome).
    /// First invocation triggers the Screen Recording TCC prompt for the editor process; the
    /// capture returns an empty/black image until the user grants it.
    /// </summary>
    private static string? CaptureMacOS(string path)
    {
        var window = Window.InternalWindow;
        // On macOS, Silk IWindow.Handle IS the GLFWwindow* (Win32 handles are Windows-only).
        nint glfwWindow = window?.Handle ?? 0;
        if (glfwWindow == 0)
            return null;

        nint glfwGetCocoaWindow = GetGlfwGetCocoaWindow();
        if (glfwGetCocoaWindow == 0)
            return null;
        nint cocoaWindow = Marshal.GetDelegateForFunctionPointer<GlfwGetCocoaWindowDelegate>(glfwGetCocoaWindow)(glfwWindow);
        if (cocoaWindow == 0)
            return null;

        nint windowNumber = objc_msgSend(cocoaWindow, GetWindowNumberSelector());
        if (windowNumber == 0)
            return null;

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        // -x silences the capture shutter sound; -l targets one window (shadow-free).
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/usr/sbin/screencapture",
            ArgumentList = { "-x", "-l", windowNumber.ToString(), path },
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
            return null;
        process.WaitForExit(5000);
        return process.ExitCode == 0 && System.IO.File.Exists(path) ? path : null;
    }

    private static string? CaptureWindows(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var window = Window.InternalWindow;
        nint hwnd = window?.Native?.Win32?.Hwnd ?? 0;
        if (hwnd == 0 || !GetWindowRect(hwnd, out RECT rect))
            return null;

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
            return null;

        nint hdcScreen = GetDC(0);
        if (hdcScreen == 0) return null;
        nint hdcMem = CreateCompatibleDC(hdcScreen);
        nint hBmp = CreateCompatibleBitmap(hdcScreen, w, h);
        nint hBmpOld = hdcMem != 0 && hBmp != 0 ? SelectObject(hdcMem, hBmp) : 0;
        if (hdcMem == 0 || hBmp == 0 || hBmpOld == 0)
        {
            Cleanup(hdcScreen, hdcMem, hBmp, hBmpOld, hasSelection: hBmpOld != 0);
            return null;
        }

        try
        {
            if (!BitBlt(hdcMem, 0, 0, w, h, hdcScreen, rect.Left, rect.Top, SRCCOPY | CAPTUREBLT))
                return null;

            byte[] bgra = new byte[w * h * 4];
            BITMAPINFOHEADER header = new()
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down rows, matches Magick PixelReadSettings order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            };
            if (GetDIBits(hdcMem, hBmp, 0, (uint)h, bgra, ref header, DibRgbColors) == 0)
                return null;

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            var settings = new ImageMagick.PixelReadSettings((uint)w, (uint)h,
                ImageMagick.StorageType.Char, ImageMagick.PixelMapping.BGRA);
            using var img = new ImageMagick.MagickImage();
            img.ReadPixels(bgra, settings);
            img.Write(path, ImageMagick.MagickFormat.Png32);
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            Cleanup(hdcScreen, hdcMem, hBmp, hBmpOld, hasSelection: true);
        }
    }

    private static void Cleanup(nint hdcScreen, nint hdcMem, nint hBmp, nint hBmpOld, bool hasSelection)
    {
        if (hasSelection && hBmpOld != 0)
            SelectObject(hdcMem, hBmpOld);
        if (hBmp != 0) DeleteObject(hBmp);
        if (hdcMem != 0) DeleteDC(hdcMem);
        if (hdcScreen != 0) ReleaseDC(0, hdcScreen);
    }
}
