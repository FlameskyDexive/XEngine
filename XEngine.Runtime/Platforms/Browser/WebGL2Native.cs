// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#if XENGINE_WEB
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// P/Invoke into the emscripten WebGL2 shim linked by <c>NativeFileReference</c>
/// <c>native/xengine_webgl2.c</c>. Mono-WASM resolves the DllImport library name as the
/// C file stem, not <c>__Internal</c>. Strings are marshalled as UTF-8 bytes because
/// <c>LPUTF8Str</c> has no interp-to-native trampoline on browser-wasm (runtime abort).
/// </summary>
internal static unsafe class WebGL2Native
{
    private const string Lib = "xengine_webgl2";
    private const int MaxUtf8Bytes = 255;

    [DllImport(Lib, EntryPoint = "xengine_webgl2_create")]
    private static extern nint CreateContextUtf8(byte* selector);

    [DllImport(Lib, EntryPoint = "xengine_webgl2_make_current")]
    public static extern int MakeCurrent(nint context);

    [DllImport(Lib, EntryPoint = "xengine_webgl2_get_proc_address")]
    private static extern nint GetProcAddressUtf8(byte* name);

    [DllImport(Lib, EntryPoint = "xengine_webgl2_clear_rgba8")]
    public static extern void ClearRgba8(int r, int g, int b, int a);

    [DllImport(Lib, EntryPoint = "xengine_webgl2_canvas_width")]
    public static extern int CanvasWidth();

    [DllImport(Lib, EntryPoint = "xengine_webgl2_canvas_height")]
    public static extern int CanvasHeight();

    [DllImport(Lib, EntryPoint = "xengine_webgl2_start_main_loop")]
    public static extern void StartMainLoop();

    [DllImport(Lib, EntryPoint = "xengine_webgl2_frame_count")]
    public static extern int FrameCount();

    public static nint CreateContext(string selector) =>
        InvokeUtf8(string.IsNullOrWhiteSpace(selector) ? "#canvas" : selector, isCreate: true);

    public static nint GetProcAddress(string name) =>
        string.IsNullOrEmpty(name) ? nint.Zero : InvokeUtf8(name, isCreate: false);

    private static nint InvokeUtf8(string value, bool isCreate)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(value), "UTF-8 payload exceeds the native WebGL2 name buffer.");

        Span<byte> buffer = stackalloc byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, buffer);
        buffer[byteCount] = 0;
        fixed (byte* ptr = buffer)
            return isCreate ? CreateContextUtf8(ptr) : GetProcAddressUtf8(ptr);
    }
}
#endif
