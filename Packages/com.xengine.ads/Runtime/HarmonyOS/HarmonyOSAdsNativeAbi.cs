// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Ads.HarmonyOS;

/// <summary>
/// Seam over the <c>libxengine_ads.so</c> C ABI so the provider can be unit-tested with a
/// fake bridge. The real implementation (<see cref="HarmonyOSAdsNativeAbi"/>) P/Invokes the
/// library deployed from this package's Plugins tree (see Native/native-manifest.json).
/// </summary>
internal interface IHarmonyOSAdsNative
{
    /// <summary>
    /// Registers the C# event sink (invoked on the ArkTS main thread for every
    /// <c>ads_emit_event</c>). Returns 0 on success.
    /// </summary>
    int Start(Action<string> onEvent);

    /// <summary>Unregisters the event sink.</summary>
    void Stop();

    /// <summary>
    /// Posts a JSON request to the ArkTS glue. Returns 0 when posted, -1 invalid payload,
    /// -2 no ArkTS bridge registered, -3 out of memory, -4 queue full.
    /// </summary>
    int Request(string json);
}

/// <summary>
/// Real binding to <c>xengine_ads</c>. Everything above this file speaks managed strings
/// only. Events arrive on the ArkTS main thread; per the XTask contract, ad completions may
/// resume continuations there — marshal explicitly when game-thread affinity matters.
/// </summary>
internal sealed unsafe class HarmonyOSAdsNativeAbi : IHarmonyOSAdsNative
{
    internal const string LibraryName = "xengine_ads";
    internal const int AbiVersion = 1;

    private static readonly object Gate = new();
    private static Action<string>? _eventSink;

    [System.Runtime.InteropServices.DllImport(LibraryName)]
    private static extern int xengine_ads_abi_version();

    [System.Runtime.InteropServices.DllImport(LibraryName)]
    private static extern int xengine_ads_start(delegate* unmanaged[Cdecl]<nint, void> onEvent);

    [System.Runtime.InteropServices.DllImport(LibraryName)]
    private static extern void xengine_ads_stop();

    [System.Runtime.InteropServices.DllImport(LibraryName)]
    private static extern int xengine_ads_request(nint jsonUtf8);

    public int Start(Action<string> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        lock (Gate)
        {
            _eventSink = onEvent;
            return xengine_ads_start(&OnEventTrampoline);
        }
    }

    public void Stop()
    {
        lock (Gate)
        {
            _eventSink = null;
            xengine_ads_stop();
        }
    }

    public int Request(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        int byteCount = System.Text.Encoding.UTF8.GetMaxByteCount(json.Length);
        // Include the terminating NUL the native side expects.
        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(byteCount + 1);
        try
        {
            unsafe
            {
                fixed (char* source = json)
                {
                    int written = System.Text.Encoding.UTF8.GetBytes(source, json.Length, (byte*)buffer, byteCount);
                    ((byte*)buffer)[written] = 0;
                }
            }
            return xengine_ads_request(buffer);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnEventTrampoline(nint jsonUtf8)
    {
        Action<string>? sink;
        string payload;
        try
        {
            payload = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(jsonUtf8) ?? string.Empty;
        }
        catch
        {
            return; // A malformed pointer must never crash the ArkTS thread.
        }
        lock (Gate)
        {
            sink = _eventSink;
        }
        sink?.Invoke(payload);
    }
}
