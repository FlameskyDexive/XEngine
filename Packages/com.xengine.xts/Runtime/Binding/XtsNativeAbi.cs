using System.Text;
using System;
using System.Text;
using System.Runtime.InteropServices;

namespace XEngine.XTS.Binding;

/// <summary>
/// P/Invoke surface of <c>xengine_xts</c> (Native/xengine_xts_abi). Internal:
/// everything above this file speaks managed types only. The native library is
/// deployed from this package's Plugins directory; see Native/native-manifest.
/// </summary>
internal static unsafe class XtsNativeAbi
{
    internal const string LibraryName = "xengine_xts";
    internal const int AbiVersion = 1;

    [DllImport(LibraryName)]
    internal static extern int xts_abi_version();

    [StructLayout(LayoutKind.Sequential)]
    internal struct XtsOptions
    {
        public uint StructSize;
        public uint AbiVersion;
        public ulong MemoryLimitBytes;
        public ulong MaxStackBytes;
    }

    [DllImport(LibraryName)]
    internal static extern IntPtr xts_env_create(ref XtsOptions options);

    [DllImport(LibraryName)]
    internal static extern void xts_env_destroy(IntPtr env);

    // Strings cross as UTF-8 byte arrays (NUL-terminated by the caller);
    // CharSet.Ansi would re-encode through the system codepage and corrupt
    // any non-ASCII source or identifier.
    [DllImport(LibraryName, EntryPoint = "xts_env_add_module_source", ExactSpelling = true)]
    internal static extern int xts_env_add_module_source(IntPtr env, byte[] name, byte[] source);

    [DllImport(LibraryName, EntryPoint = "xts_env_eval_module", ExactSpelling = true)]
    internal static extern int xts_env_eval_module(IntPtr env, byte[] name);

    internal enum XtsValueKind : int
    {
        Undefined = 0,
        Bool = 1,
        Int32 = 2,
        Float64 = 3,
        String = 4,
        Null = 5,
        BigInt = 6,
        Function = 7,
    }

    /// <summary>Mirror of xts_value (kind + union; 24 bytes, align 8).</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct XtsValue
    {
        public const int Size = 24;

        [FieldOffset(0)] public XtsValueKind Kind;
        [FieldOffset(8)] public int Int32Value;
        [FieldOffset(8)] public double Float64Value;
        [FieldOffset(8)] public IntPtr StringData;
        [FieldOffset(16)] public uint StringLength;
        [FieldOffset(8)] public long BigIntValue;
        [FieldOffset(8)] public int FunctionHandle;
    }

    [DllImport(LibraryName, EntryPoint = "xts_env_eval_expression", ExactSpelling = true)]
    internal static extern int xts_env_eval_expression(IntPtr env, byte[] code, XtsValue* outValue);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void XtsNativeCallback(IntPtr userData, int argc, XtsValue* argv, XtsValue* result);

    [DllImport(LibraryName, EntryPoint = "xts_env_register_callback", ExactSpelling = true)]
    internal static extern int xts_env_register_callback(IntPtr env, byte[] name, XtsNativeCallback fn, IntPtr userData);

    [DllImport(LibraryName, EntryPoint = "xts_env_invoke_function", ExactSpelling = true)]
    internal static extern int xts_env_invoke_function(IntPtr env, int handle, int argc, XtsValue* args, XtsValue* outValue);

    [DllImport(LibraryName, EntryPoint = "xts_env_release_function", ExactSpelling = true)]
    internal static extern int xts_env_release_function(IntPtr env, int handle);

    [DllImport(LibraryName, EntryPoint = "xts_env_get_last_exception", ExactSpelling = true)]
    internal static extern int xts_env_get_last_exception(IntPtr env, byte[] buffer, uint length);

    internal static byte[] Utf8(string text)
    {
        // single allocation carrying the NUL terminator the C side expects
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(text) + 1];
        var written = System.Text.Encoding.UTF8.GetBytes(text, bytes);
        bytes[written] = 0;
        return bytes;
    }

    /// <summary>HGlobal UTF-8 copy of <paramref name="text"/>; the caller frees.</summary>
    internal static IntPtr AllocUtf8(string text)
    {
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(text);
        var ptr = Marshal.AllocHGlobal(byteCount + 1);
        var bytes = new byte[byteCount + 1];
        System.Text.Encoding.UTF8.GetBytes(text, bytes);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    [DllImport(LibraryName)]
    internal static extern int xts_env_execute_pending_jobs(IntPtr env, uint maxJobs, out uint executed);

    [DllImport(LibraryName)]
    internal static extern int xts_env_set_memory_limit(IntPtr env, ulong bytes);

    [DllImport(LibraryName)]
    internal static extern int xts_env_set_execution_deadline_ms(IntPtr env, ulong ms);

    [DllImport(LibraryName)]
    internal static extern int xts_env_memory_used_bytes(IntPtr env, out ulong used);
}
