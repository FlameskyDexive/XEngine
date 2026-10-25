// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace XEngine.Editor.Utils;

/// <summary>
/// Sends files or folders to the OS recycle bin (Unity's delete semantics) instead of
/// unlinking them. Windows-only; callers fall back to a hard delete when this returns false.
/// </summary>
public static class RecycleBin
{
    /// <summary>Move a file or directory (recursively) to the recycle bin.
    /// Returns false when unsupported on this OS, the path is missing, or the shell refused.</summary>
    public static bool TryDelete(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return false;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath)) return false;

        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                // The shell API takes a double-null-terminated list; the marshaller adds one
                // terminator, the embedded '\0' provides the second.
                pFrom = Path.GetFullPath(absolutePath) + "\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperationW(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"Recycle bin delete failed for '{absolutePath}': {ex.Message}");
            return false;
        }
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);
}
