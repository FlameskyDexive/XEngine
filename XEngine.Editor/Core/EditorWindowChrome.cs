// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Silk.NET.Windowing;

namespace XEngine.Editor.Core;

/// <summary>
/// Win32 chrome: light DWM caption matching the in-editor menu bar (#F3F3F3 / #202020),
/// plus a #383838 class erase brush so idle-skip presents and HWND recreate do not flash
/// the default white GDI background over the dark editor body.
/// </summary>
internal static class EditorWindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int WmSetIcon = 0x0080;
    private const int WmEraseBkgnd = 0x0014;
    private const nint IconSmall = 0;
    private const nint IconBig = 1;
    private const int GclpHbrBackground = -10;
    private const int GwlpWndProc = -4;

    // COLORREF 0x00BBGGRR for Unity Dark body #383838.
    internal const int ClientEraseColor = unchecked((int)0x00383838);

    // Light caption matches EditorTheme.MenuBar (#FFFFFF) + MenuBarInk (#202020) so the
    // native title bar reads as one continuous light strip with the menu bar below it.
    // Windows 10 ignores the explicit caption/text colors; darkMode=0 still yields the
    // standard light caption (white bg / black text) there.
    internal const int CaptionColor = unchecked((int)0x00FFFFFF);
    internal const int CaptionTextColor = unchecked((int)0x00202020);

    private static nint s_iconLarge;
    private static nint s_iconSmall;
    private static bool s_iconResolved;
    private static nint s_clientEraseBrush;
    private static WndProc? s_eraseProc;
    private static readonly Dictionary<nint, nint> s_prevWndProcs = new();

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    public static void Apply(IWindow? window)
    {
        if (window == null) return;
        ApplyCaption(window);
        ApplyClientEraseBrush(window);
        HookEraseBackground(window);
        XEngine.Runtime.Window.ApplyEditorIcon(window);
        ApplyWin32Icon(window);
    }

    public static void ApplyCaption(IWindow window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        nint hwnd = window.Native?.Win32?.Hwnd ?? 0;
        if (hwnd == 0) return;

        // 0 = light caption regardless of the OS dark-app personalization, so the title
        // bar never flips between white and black depending on where styling lands.
        int darkMode = 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
        int caption = CaptionColor;
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        int text = CaptionTextColor;
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
    }

    private static void ApplyClientEraseBrush(IWindow window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        nint hwnd = window.Native?.Win32?.Hwnd ?? 0;
        if (hwnd == 0) return;
        if (s_clientEraseBrush == 0)
            s_clientEraseBrush = CreateSolidBrush(ClientEraseColor);
        if (s_clientEraseBrush == 0) return;
        SetClassLongPtr(hwnd, GclpHbrBackground, s_clientEraseBrush);
    }

    private static void HookEraseBackground(IWindow window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        nint hwnd = window.Native?.Win32?.Hwnd ?? 0;
        if (hwnd == 0 || s_prevWndProcs.ContainsKey(hwnd)) return;
        s_eraseProc ??= EraseWndProc;
        nint prev = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(s_eraseProc));
        if (prev != 0)
            s_prevWndProcs[hwnd] = prev;
    }

    private static nint EraseWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmEraseBkgnd)
            return 1;
        if (!s_prevWndProcs.TryGetValue(hWnd, out nint prev) || prev == 0)
            return DefWindowProc(hWnd, msg, wParam, lParam);
        return CallWindowProc(prev, hWnd, msg, wParam, lParam);
    }

    private static void ApplyWin32Icon(IWindow window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        nint hwnd = window.Native?.Win32?.Hwnd ?? 0;
        if (hwnd == 0) return;
        EnsureProcessIcons();
        if (s_iconSmall != 0)
            SendMessage(hwnd, WmSetIcon, IconSmall, s_iconSmall);
        if (s_iconLarge != 0)
            SendMessage(hwnd, WmSetIcon, IconBig, s_iconLarge);
    }

    private static void EnsureProcessIcons()
    {
        if (s_iconResolved) return;
        s_iconResolved = true;
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return;
        ExtractIconEx(path, 0, out s_iconLarge, out s_iconSmall, 1);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern nint SetClassLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(int crColor);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);
}
