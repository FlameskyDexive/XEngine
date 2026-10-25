using System;
using System.Runtime.InteropServices;

using XEngine.Editor.Core;
using XEngine.OrigamiUI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorPopupChromeTests
{
    [Fact]
    public void DetachedHostStaysVisibleSoGpuPresentIsNotSkipped()
    {
        // RenderSecondaryHosts skips hosts with IsVisible=false. Secondary HWNDs start hidden
        // for chrome; attaching must not latch that hidden state onto the host.
        var host = new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf()), new Rect(0, 0, 420, 280));
        Assert.True(host.IsVisible);
    }

    [Fact]
    public void NativeHostsDoNotShowTabBarLeadingMark()
    {
        var main = new EditorSurfaceHost(0, new DockSpace(DockNode.Leaf()), new Rect(0, 0, 200, 200));
        var detached = new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf()), new Rect(220, 0, 420, 200));

        Assert.False(main.DockSpace.ShowTabBarLeadingMark);
        Assert.False(detached.DockSpace.ShowTabBarLeadingMark);
    }

    [Fact]
    public void ReplaceDockSpaceKeepsDetachedLeadingMarkOff()
    {
        var host = new EditorSurfaceHost(2, new DockSpace(DockNode.Leaf()), new Rect(0, 0, 200, 200));
        var replacement = new DockSpace(DockNode.Leaf());
        Assert.False(replacement.ShowTabBarLeadingMark);

        host.ReplaceDockSpace(replacement);

        Assert.False(host.DockSpace.ShowTabBarLeadingMark);
    }

    [Fact]
    public void MapHostsConvertsPaperPointsThroughScreenSpace()
    {
        var source = new EditorSurfaceHost(0, new DockSpace(DockNode.Leaf()), new Rect(0, 0, 200, 200));
        source.SetDockingScreenBounds(new Rect(0, 0, 200, 200));
        source.DpiScale = 1f;

        var target = new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf()), new Rect(100, 0, 300, 200));
        target.SetDockingScreenBounds(new Rect(100, 0, 300, 200));
        target.DpiScale = 1f;

        EditorPopupMapping.MapHosts(source, 150f, 40f, target, out float x, out float y);

        Assert.Equal(50f, x, 2);
        Assert.Equal(40f, y, 2);
    }

    [Fact]
    public void WindowGroupStaysActiveWhenSilkFocusSaysAFloaterIsFocused()
    {
        // GetForegroundWindow returns the GLFW HWND. Presentation NativeHandle may be a
        // different DXGI/GL handle, so HWND compare fails. Silk FocusChanged is the fallback
        // that keeps Preferences / Package Manager / Build Project interactive.
        nint[] handles = [0x100, 0x200];
        Assert.True(EditorNativeSurfaceManager.ComputeWindowGroupActive(
            foreground: 0x999, hostOsHandles: handles, mainFocused: false, anySecondaryFocused: true));
        Assert.False(EditorNativeSurfaceManager.ComputeWindowGroupActive(
            foreground: 0x999, hostOsHandles: handles, mainFocused: false, anySecondaryFocused: false));
    }

    [Fact]
    public void PointerOverMainHwndNeverCountsAsSecondary()
    {
        nint main = 0x100;
        nint[] floaters = [main, 0x200];
        Assert.False(EditorNativeSurfaceManager.IsPointerOverSecondary(
            windowFromPointRoot: main, mainOsHandle: main, secondaryOsHandles: floaters));
        Assert.True(EditorNativeSurfaceManager.IsPointerOverSecondary(
            windowFromPointRoot: 0x200, mainOsHandle: main, secondaryOsHandles: floaters));
    }

    [Fact]
    public void PointerOverMainOrFloaterKeepsEditorUiLive()
    {
        nint main = 0x100;
        nint[] floaters = [0x200];
        Assert.True(EditorNativeSurfaceManager.IsPointerOverAnyEditor(
            windowFromPointRoot: main, mainOsHandle: main, secondaryOsHandles: floaters));
        Assert.True(EditorNativeSurfaceManager.IsPointerOverAnyEditor(
            windowFromPointRoot: 0x200, mainOsHandle: main, secondaryOsHandles: floaters));
        Assert.False(EditorNativeSurfaceManager.IsPointerOverAnyEditor(
            windowFromPointRoot: 0x999, mainOsHandle: main, secondaryOsHandles: floaters));
    }

    [Fact]
    public void WindowGroupIsActiveWhenForegroundMatchesAnOsHandle()
    {
        nint[] handles = [0x100, 0x200];
        Assert.True(EditorNativeSurfaceManager.ComputeWindowGroupActive(
            foreground: 0x200, hostOsHandles: handles, mainFocused: false, anySecondaryFocused: false));
    }

    [Fact]
    public void CanAssignOwnerRejectsZeroAndSelf()
    {
        // Never assign GWLP_HWNDPARENT with a DXGI/GL surface handle or the same HWND.
        Assert.False(EditorNativeWindowOwner.CanAssignOwner(0, 0x100));
        Assert.False(EditorNativeWindowOwner.CanAssignOwner(0x100, 0));
        Assert.False(EditorNativeWindowOwner.CanAssignOwner(0x100, 0x100));
        Assert.True(EditorNativeWindowOwner.CanAssignOwner(0x200, 0x100));
    }

    [Fact]
    public void TryAssignOwnerSetsGwlpHwndParentOnRealWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        nint owner = CreateTopLevelWindow("XEngineOwnerSmoke");
        nint child = CreateTopLevelWindow("XEngineChildSmoke");
        try
        {
            Assert.NotEqual(nint.Zero, owner);
            Assert.NotEqual(nint.Zero, child);
            Assert.True(EditorNativeWindowOwner.TryAssignOwner(child, owner));
            Assert.Equal(owner, GetWindowLongPtr(child, GwlpHwndParent));
        }
        finally
        {
            if (child != nint.Zero) DestroyWindow(child);
            if (owner != nint.Zero) DestroyWindow(owner);
        }
    }

    [Fact]
    public void SceneViewIconPngsAreEmbedded()
    {
        string[] names = typeof(XEngine.Editor.Core.EditorApplication).Assembly.GetManifestResourceNames();
        Assert.Contains(names, name => name.EndsWith("d_MoveTool.png", StringComparison.Ordinal));
        Assert.Contains(names, name => name.EndsWith("d_SceneViewAudio.png", StringComparison.Ordinal));
        Assert.Contains(names, name => name.EndsWith("d_ToolHandleLocal.png", StringComparison.Ordinal));
    }

    private const int GwlpHwndParent = -8;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;

    private static nint CreateTopLevelWindow(string className)
    {
        var wndClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_defWndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref wndClass);
        return CreateWindowEx(0, className, className, WsPopup, 0, 0, 64, 64, nint.Zero, nint.Zero,
            wndClass.hInstance, nint.Zero);
    }

    private static readonly WndProc s_defWndProc = DefWindowProc;

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
}
