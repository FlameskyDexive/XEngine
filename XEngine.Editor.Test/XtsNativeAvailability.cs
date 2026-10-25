// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;

namespace XEngine.Editor.Test;

/// <summary>
/// The XTS hot-update native (QuickJS + xengine_xts ABI) is prebuilt for Windows and HarmonyOS
/// only — Packages/com.xengine.xts/Plugins has no osx payload. Tests that drive the in-process
/// JS runtime skip on hosts without the native (building osx-arm64/libxengine_xts.dylib is
/// tracked as macOS follow-up work).
/// </summary>
internal static class XtsNativeAvailability
{
    /// <summary>Early-return guard: `if (XtsNativeAvailability.ShouldSkip) return;`</summary>
    internal static bool ShouldSkip => !NativeLibrary.TryLoad("xengine_xts", out _);
}
