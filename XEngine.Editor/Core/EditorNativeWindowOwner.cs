// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;

namespace XEngine.Editor.Core;

/// <summary>
/// Win32 owned-window helpers so floating editor HWNDs stay above the main editor
/// (Unity-style). Uses <c>GWLP_HWNDPARENT</c> (owner), never <c>SetParent</c> (child)
/// and never <c>WS_EX_TOPMOST</c>. Callers must pass GLFW HWNDs from
/// <see cref="EditorNativeSurfaceManager.GetOsWindowHandle"/> — never DXGI/GL
/// <c>IWindowSurface.NativeHandle</c>.
/// </summary>
internal static class EditorNativeWindowOwner
{
    // GWLP_HWNDPARENT (-8): sets owner for top-level windows, not WS_CHILD parent.
    private const int GwlpHwndParent = -8;

    /// <summary>Rejects null/zero handles and self-ownership before touching Win32.</summary>
    public static bool CanAssignOwner(nint child, nint owner) =>
        child != nint.Zero && owner != nint.Zero && child != owner;

    /// <summary>
    /// Assigns <paramref name="owner"/> as the Win32 owner of <paramref name="child"/>.
    /// No-op on non-Windows. Call once while the child is still hidden and again after
    /// show — GLFW may overwrite <c>GWLP_HWNDPARENT</c> when making the window visible.
    /// </summary>
    public static bool TryAssignOwner(nint child, nint owner)
    {
        if (!CanAssignOwner(child, owner))
            return false;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        SetWindowLongPtr(child, GwlpHwndParent, owner);
        return GetWindowLongPtr(child, GwlpHwndParent) == owner;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
}
