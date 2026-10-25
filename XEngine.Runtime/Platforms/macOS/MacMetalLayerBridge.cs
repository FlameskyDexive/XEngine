// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

namespace XEngine.Runtime.Platforms.macOS;

/// <summary>
/// macOS desktop Metal presentation: attaches a <c>CAMetalLayer</c> to the GLFW window's
/// content view so <see cref="Backends.Metal.MetalGraphicsDevice"/> can present through it.
/// The iOS host owns its layer on the UIKit side; on macOS the GLFW window gives us an
/// <c>NSWindow*</c> via <c>glfwGetCocoaWindow</c> (hand-bound — the dylib lives in
/// <c>runtimes/&lt;rid&gt;/native</c> with a version-suffixed name a bare DllImport("glfw")
/// cannot resolve), and AppKit wants the layer on the content view with
/// <c>wantsLayer = YES</c>.
/// </summary>
internal static class MacMetalLayerBridge
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, CharSet = CharSet.Ansi)]
    private static extern nint sel_registerName(string name);

    [DllImport(ObjC, CharSet = CharSet.Ansi)]
    private static extern nint objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern nint objc_msgSend(nint receiver, nint selector);

    // libobjc exports only objc_msgSend; the typed variants are marshaling shapes, not symbols.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(nint receiver, nint selector, byte arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_nint(nint receiver, nint selector, nint arg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GlfwGetCocoaWindowDelegate(nint glfwWindow);

    private static nint? _glfwGetCocoaWindow;
    private static nint? _contentViewSelector;
    private static nint? _setWantsLayerSelector;
    private static nint? _setLayerSelector;
    private static nint? _metalLayerClass;

    /// <summary>
    /// Attach a <c>CAMetalLayer</c> to the window's content view and return the layer pointer
    /// (0 when the bridge cannot run — the device then stays offscreen).
    /// </summary>
    public static nint Attach(nint glfwWindow)
    {
        if (!OperatingSystem.IsMacOS() || glfwWindow == 0)
            return 0;

        nint cocoaWindow = GetCocoaWindow(glfwWindow);
        if (cocoaWindow == 0)
            return 0;

        nint contentView = objc_msgSend(cocoaWindow, _contentViewSelector ??= sel_registerName("contentView"));
        if (contentView == 0)
            return 0;

        nint metalLayerClass = _metalLayerClass ??= objc_getClass("CAMetalLayer");
        if (metalLayerClass == 0)
            return 0;

        nint layer = objc_msgSend(metalLayerClass, sel_registerName("new"));
        if (layer == 0)
            return 0;

        objc_msgSend_bool(contentView, _setWantsLayerSelector ??= sel_registerName("setWantsLayer:"), 1);
        objc_msgSend_nint(contentView, _setLayerSelector ??= sel_registerName("setLayer:"), layer);
        return layer;
    }

    private static nint GetCocoaWindow(nint glfwWindow)
    {
        if (!_glfwGetCocoaWindow.HasValue)
        {
            string baseDir = AppContext.BaseDirectory;
            string[] nativeDirs =
            [
                System.IO.Path.Combine(baseDir, "runtimes", "osx-arm64", "native"),
                System.IO.Path.Combine(baseDir, "runtimes", "osx-x64", "native"),
                baseDir,
            ];
            foreach (string dir in nativeDirs)
            {
                string candidate = System.IO.Path.Combine(dir, "libglfw.3.dylib");
                if (NativeLibrary.TryLoad(candidate, out nint handle))
                {
                    _glfwGetCocoaWindow = NativeLibrary.GetExport(handle, "glfwGetCocoaWindow");
                    break;
                }
            }
            _glfwGetCocoaWindow ??= 0;
        }

        return _glfwGetCocoaWindow.Value == 0
            ? 0
            : Marshal.GetDelegateForFunctionPointer<GlfwGetCocoaWindowDelegate>(_glfwGetCocoaWindow.Value)(glfwWindow);
    }
}
